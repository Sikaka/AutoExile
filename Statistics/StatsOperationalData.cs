using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutoExile.Statistics
{
    public sealed record MapMetadataSnapshot(
        string MapName,
        IReadOnlyList<string> BossTiles,
        DateTime? LastScannedUtc,
        string? BossEntityPath,
        string? TransitionDetailName);

    public sealed record LabExitMemorySnapshot(
        string Day,
        string ZoneName,
        int ExitCount,
        float AngleDegrees,
        string DestinationName);

    /// <summary>Low-volume learned data stored beside statistics in the canonical SQLite database.</summary>
    public sealed partial class StatsService
    {
        public IReadOnlyList<MapMetadataSnapshot> ReadMapMetadata()
        {
            using var db = OpenReadConnection();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT map_name,boss_tiles_json,last_scanned_at_ms,boss_entity_path,transition_detail_name
FROM map_metadata ORDER BY map_name";
            var result = new List<MapMetadataSnapshot>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var tiles = reader.IsDBNull(1)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? new List<string>();
                result.Add(new MapMetadataSnapshot(
                    reader.GetString(0), tiles,
                    reader.IsDBNull(2) ? null : FromMs(reader.GetInt64(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
            return result;
        }

        public void UpsertMapMetadata(MapMetadataSnapshot value)
        {
            if (string.IsNullOrWhiteSpace(value.MapName)) return;
            Enqueue(db => Execute(db, null, @"
INSERT INTO map_metadata(map_name,boss_tiles_json,last_scanned_at_ms,boss_entity_path,transition_detail_name)
VALUES($name,$tiles,$scanned,$boss,$transition)
ON CONFLICT(map_name) DO UPDATE SET boss_tiles_json=excluded.boss_tiles_json,
last_scanned_at_ms=excluded.last_scanned_at_ms,boss_entity_path=excluded.boss_entity_path,
transition_detail_name=excluded.transition_detail_name",
                ("$name", value.MapName), ("$tiles", JsonSerializer.Serialize(value.BossTiles)),
                ("$scanned", value.LastScannedUtc.HasValue ? ToUnixMs(value.LastScannedUtc.Value) : DBNull.Value),
                ("$boss", (object?)value.BossEntityPath ?? DBNull.Value),
                ("$transition", (object?)value.TransitionDetailName ?? DBNull.Value)));
        }

        public IReadOnlyList<LabExitMemorySnapshot> ReadLabExitMemory(string day)
        {
            using var db = OpenReadConnection();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT day,zone_name,exit_count,angle_milli_degrees,destination_name
FROM lab_exit_memory WHERE day=$day ORDER BY zone_name,exit_count,angle_milli_degrees";
            cmd.Parameters.AddWithValue("$day", day);
            var result = new List<LabExitMemorySnapshot>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(new LabExitMemorySnapshot(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    reader.GetInt64(3) / 1000f, reader.GetString(4)));
            return result;
        }

        public bool ReplaceLabExitMemory(string day, IReadOnlyCollection<LabExitMemorySnapshot> values,
            Action<bool>? completed = null)
        {
            if (string.IsNullOrWhiteSpace(day))
            {
                completed?.Invoke(false);
                return false;
            }

            void Complete(bool success)
            {
                try { completed?.Invoke(success); }
                catch (Exception ex) { _log($"Lab exit-memory completion callback error: {ex.Message}"); }
            }

            var accepted = Enqueue(db =>
            {
                try
                {
                    using var tx = db.BeginTransaction();
                    Execute(db, tx, "DELETE FROM lab_exit_memory WHERE day=$day", ("$day", day));
                    var now = NowMs();
                    foreach (var value in values)
                        Execute(db, tx, @"INSERT INTO lab_exit_memory(day,zone_name,exit_count,angle_milli_degrees,destination_name,updated_at_ms)
VALUES($day,$zone,$count,$angle,$destination,$now)",
                            ("$day", day), ("$zone", value.ZoneName), ("$count", value.ExitCount),
                            ("$angle", (long)Math.Round(value.AngleDegrees * 1000f)),
                            ("$destination", value.DestinationName), ("$now", now));
                    tx.Commit();
                    Complete(true);
                }
                catch
                {
                    Complete(false);
                    throw;
                }
            });
            if (!accepted) Complete(false);
            return accepted;
        }

        private SqliteConnection OpenReadConnection()
        {
            var db = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Cache=Private;Pooling=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout=2000;";
            cmd.ExecuteNonQuery();
            return db;
        }

        private static void ImportOperationalFiles(SqliteConnection db, string pluginDirectory)
        {
            var dataDirectory = Path.Combine(pluginDirectory, "Data");
            var mapData = Path.Combine(dataDirectory, "map_data.json");
            ImportMapMetadata(db, File.Exists(mapData) ? mapData : Path.Combine(dataDirectory, "map_bosses.json"));
            ImportLabExitMemory(db, Path.Combine(pluginDirectory, "lab_memory.json"));
        }

        private static void ImportMapMetadata(SqliteConnection db, string path)
        {
            if (!ShouldImportOperationalFile(db, path)) return;
            var imported = 0;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                using var tx = db.BeginTransaction();
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    var value = property.Value;
                    var tiles = value.TryGetProperty("bossTiles", out var tileElement)
                        ? tileElement.GetRawText() : "[]";
                    object scanned = DBNull.Value;
                    if (value.TryGetProperty("lastScanned", out var scanElement) &&
                        scanElement.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(scanElement.GetString(), out var scanTime))
                        scanned = ToUnixMs(scanTime);
                    Execute(db, tx, @"INSERT OR REPLACE INTO map_metadata(map_name,boss_tiles_json,last_scanned_at_ms,boss_entity_path,transition_detail_name)
VALUES($name,$tiles,$scanned,$boss,$transition)",
                        ("$name", property.Name), ("$tiles", tiles), ("$scanned", scanned),
                        ("$boss", ReadNullableString(value, "bossEntityPath")),
                        ("$transition", ReadNullableString(value, "transitionDetailName")));
                    imported++;
                }
                RecordOperationalImport(db, tx, path, imported, "complete");
                tx.Commit();
            }
            catch
            {
                RecordFailedOperationalImport(db, path, imported);
            }
        }

        private static void ImportLabExitMemory(SqliteConnection db, string path)
        {
            if (!ShouldImportOperationalFile(db, path)) return;
            var imported = 0;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var day = root.TryGetProperty("date", out var dayElement) ? dayElement.GetString() ?? "" : "";
                using var tx = db.BeginTransaction();
                if (root.TryGetProperty("entries", out var entries))
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var zone = entry.GetProperty("zone").GetString() ?? "";
                        var exitCount = entry.GetProperty("exitCount").GetInt32();
                        foreach (var mapping in entry.GetProperty("mappings").EnumerateArray())
                        {
                            Execute(db, tx, @"INSERT OR REPLACE INTO lab_exit_memory(day,zone_name,exit_count,angle_milli_degrees,destination_name,updated_at_ms)
VALUES($day,$zone,$count,$angle,$destination,$now)",
                                ("$day", day), ("$zone", zone), ("$count", exitCount),
                                ("$angle", (long)Math.Round(mapping.GetProperty("angle").GetSingle() * 1000f)),
                                ("$destination", mapping.GetProperty("dest").GetString() ?? ""), ("$now", NowMs()));
                            imported++;
                        }
                    }
                }
                RecordOperationalImport(db, tx, path, imported, "complete");
                tx.Commit();
            }
            catch
            {
                RecordFailedOperationalImport(db, path, imported);
            }
        }

        private static bool ShouldImportOperationalFile(SqliteConnection db, string path)
        {
            if (!File.Exists(path)) return false;
            var hash = TryHashOperationalFile(path);
            if (hash == null) return true;
            return ScalarInt32(db, null, @"SELECT COUNT(*) FROM operational_imports
WHERE source_path=$path AND source_sha256=$hash AND status='complete'",
                ("$path", path), ("$hash", hash)) == 0;
        }

        private static void RecordFailedOperationalImport(SqliteConnection db, string path, int rows)
        {
            using var tx = db.BeginTransaction();
            RecordOperationalImport(db, tx, path, rows, "failed");
            tx.Commit();
        }

        private static void RecordOperationalImport(SqliteConnection db, SqliteTransaction tx,
            string path, int rows, string status)
        {
            // A failed/locked source must not make startup fail, and an empty hash
            // never suppresses a later retry once the source becomes readable.
            var hash = TryHashOperationalFile(path) ?? "";
            Execute(db, tx, @"INSERT OR REPLACE INTO operational_imports(source_path,source_sha256,imported_at_ms,rows_imported,status)
VALUES($path,$hash,$now,$rows,$status)",
                ("$path", path), ("$hash", hash), ("$now", NowMs()), ("$rows", rows), ("$status", status));
        }

        private static string? TryHashOperationalFile(string path)
        {
            try { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))); }
            catch { return null; }
        }

        private static object ReadNullableString(JsonElement value, string propertyName)
        {
            if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
                return DBNull.Value;
            return property.GetString() ?? (object)DBNull.Value;
        }

        private static long ToUnixMs(DateTime value) =>
            new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeMilliseconds();
    }
}
