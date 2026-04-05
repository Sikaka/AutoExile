using AutoExile.Recording;
using AutoExile.Replay;
using AutoExile.Systems;
using System.IO.Compression;
using System.Text.Json;

// ══════════════════════════════════════════════════════════════
// AutoExile Replay Tool
//
// Usage:
//   dotnet run -- analyze <directory|file.json.gz>  — Full decision analysis
//   dotnet run -- classify <recording.json.gz>
//   dotnet run -- compare <recording1.json.gz> <recording2.json.gz>
//   dotnet run -- list <directory>
//
// Future:
//   dotnet run -- replay <recording.json.gz> --strategy "Stacked Deck"
// ══════════════════════════════════════════════════════════════

if (args.Length == 0)
{
    PrintUsage();
    return;
}

var command = args[0].ToLower();

switch (command)
{
    case "analyze":
        if (args.Length < 2) { Console.WriteLine("Usage: analyze <directory|file.json.gz>"); return; }
        RunAnalyze(args[1]);
        break;

    case "classify":
        if (args.Length < 2) { Console.WriteLine("Usage: classify <recording.json.gz>"); return; }
        RunClassify(args[1]);
        break;

    case "compare":
        if (args.Length < 3) { Console.WriteLine("Usage: compare <recording1.json.gz> <recording2.json.gz>"); return; }
        RunCompare(args[1], args[2]);
        break;

    case "list":
        var dir = args.Length > 1 ? args[1] : ".";
        RunList(dir);
        break;

    default:
        Console.WriteLine($"Unknown command: {command}");
        PrintUsage();
        break;
}

// ══════════════════════════════════════════════════════════════

void PrintUsage()
{
    Console.WriteLine("AutoExile Replay Tool");
    Console.WriteLine("  analyze  <directory|file>                         — Full decision-making analysis");
    Console.WriteLine("  classify <recording.json.gz>                     — Classify actions in a recording");
    Console.WriteLine("  compare  <rec1.json.gz> <rec2.json.gz>           — Compare two classified recordings");
    Console.WriteLine("  list     [directory]                              — List recordings in directory");
}

void RunAnalyze(string pathOrDir)
{
    var recordings = new List<(GameplayRecording Recording, string Path)>();

    if (Directory.Exists(pathOrDir))
    {
        var files = Directory.GetFiles(pathOrDir, "*.json.gz", SearchOption.AllDirectories)
            .OrderBy(f => File.GetLastWriteTime(f));
        foreach (var file in files)
        {
            Console.Write($"Loading {Path.GetFileName(file)}... ");
            var rec = LoadRecording(file);
            if (rec != null)
            {
                Console.WriteLine($"{rec.Ticks.Count} ticks");
                recordings.Add((rec, file));
            }
        }
    }
    else if (File.Exists(pathOrDir))
    {
        Console.Write($"Loading {Path.GetFileName(pathOrDir)}... ");
        var rec = LoadRecording(pathOrDir);
        if (rec != null)
        {
            Console.WriteLine($"{rec.Ticks.Count} ticks");
            recordings.Add((rec, pathOrDir));
        }
    }
    else
    {
        Console.WriteLine($"Not found: {pathOrDir}");
        return;
    }

    if (recordings.Count == 0) { Console.WriteLine("No recordings loaded."); return; }
    Console.WriteLine();

    var (runs, summary) = MapRunAnalyzer.AnalyzeAll(recordings);

    // Print individual reports
    foreach (var run in runs)
    {
        Console.WriteLine(MapRunAnalyzer.FormatReport(run));
        Console.WriteLine();
    }

    // Print cross-run summary
    if (runs.Count > 1)
        Console.WriteLine(summary);
}

void RunClassify(string path)
{
    Console.WriteLine($"Loading: {path}");
    var recording = LoadRecording(path);
    if (recording == null) { Console.WriteLine("Failed to load recording"); return; }

    Console.WriteLine($"Recording: {recording.AreaName}, {recording.TickCount} ticks, {recording.DurationSeconds:F1}s");
    Console.WriteLine();

    var timeline = ActionClassifier.Classify(recording);

    // Summary
    Console.WriteLine(ActionClassifier.Summarize(timeline, recording.TickCount));
    Console.WriteLine();

    // Timeline
    Console.WriteLine("── Timeline ──");
    foreach (var action in timeline)
    {
        var duration = action.Duration;
        var durationMs = duration * 16; // ~16ms per tick at 60fps
        Console.WriteLine($"  tick {action.StartTick,5}-{action.EndTick,5} ({durationMs,5}ms): {action.Type,-20} {action.Detail}");
    }
}

void RunCompare(string path1, string path2)
{
    Console.WriteLine($"Loading recording 1: {path1}");
    var rec1 = LoadRecording(path1);
    Console.WriteLine($"Loading recording 2: {path2}");
    var rec2 = LoadRecording(path2);

    if (rec1 == null || rec2 == null) { Console.WriteLine("Failed to load recordings"); return; }

    Console.WriteLine($"Recording 1: {rec1.AreaName}, {rec1.TickCount} ticks, {rec1.DurationSeconds:F1}s");
    Console.WriteLine($"Recording 2: {rec2.AreaName}, {rec2.TickCount} ticks, {rec2.DurationSeconds:F1}s");
    Console.WriteLine();

    var timeline1 = ActionClassifier.Classify(rec1);
    var timeline2 = ActionClassifier.Classify(rec2);

    // Compare using the shorter recording's length
    var totalTicks = Math.Min(rec1.TickCount, rec2.TickCount);
    var report = ComparisonReport.Compare(timeline1, timeline2, totalTicks);

    Console.WriteLine(report.ToText());
}

void RunList(string directory)
{
    if (!Directory.Exists(directory))
    {
        Console.WriteLine($"Directory not found: {directory}");
        return;
    }

    var files = Directory.GetFiles(directory, "*.json.gz", SearchOption.AllDirectories)
        .OrderByDescending(f => File.GetLastWriteTime(f));

    Console.WriteLine($"Recordings in: {Path.GetFullPath(directory)}");
    Console.WriteLine();

    foreach (var file in files)
    {
        try
        {
            var recording = LoadRecording(file);
            if (recording == null) continue;

            var sizeKb = new FileInfo(file).Length / 1024;
            var relPath = Path.GetRelativePath(directory, file);
            Console.WriteLine($"  {relPath,-50} {recording.AreaName,-20} {recording.TickCount,5} ticks  {recording.DurationSeconds,5:F0}s  {sizeKb,5} KB");
        }
        catch
        {
            Console.WriteLine($"  {Path.GetFileName(file),-50} (failed to read)");
        }
    }
}

GameplayRecording? LoadRecording(string path)
{
    try
    {
        return HumanGameplayRecorder.LoadRecording(path);
    }
    catch
    {
        // Recording may be truncated (save interrupted). Try streaming loader.
        Console.WriteLine("Standard load failed, trying streaming recovery...");
        return LoadRecordingStreaming(path);
    }
}

/// <summary>
/// Streaming loader that reads ticks one-by-one via Utf8JsonReader.
/// Recovers truncated recordings by keeping all complete ticks before the break.
/// </summary>
GameplayRecording? LoadRecordingStreaming(string path)
{
    try
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var bytes = ms.ToArray();

        var recording = new GameplayRecording();

        // Parse the top-level object manually — read metadata fields, then stream ticks
        var reader = new Utf8JsonReader(bytes.AsSpan(), new JsonReaderOptions { AllowTrailingCommas = true });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                var prop = reader.GetString();
                switch (prop)
                {
                    case "Version": reader.Read(); recording.Version = reader.GetString() ?? "1.0"; break;
                    case "AreaName": reader.Read(); recording.AreaName = reader.GetString() ?? ""; break;
                    case "AreaHash": reader.Read(); recording.AreaHash = reader.GetInt64(); break;
                    case "DurationSeconds": reader.Read(); recording.DurationSeconds = reader.GetSingle(); break;
                    case "TickCount": reader.Read(); recording.TickCount = reader.GetInt32(); break;
                    case "RecordedAt": reader.Read(); recording.RecordedAt = reader.GetDateTime(); break;
                    case "Ticks":
                        reader.Read(); // StartArray
                        if (reader.TokenType != JsonTokenType.StartArray) break;
                        // Read ticks one by one, using deserialize from the reader position
                        while (true)
                        {
                            try
                            {
                                if (!reader.Read()) break;
                                if (reader.TokenType == JsonTokenType.EndArray) break;
                                var tick = JsonSerializer.Deserialize<RecordingTick>(ref reader);
                                if (tick != null) recording.Ticks.Add(tick);
                            }
                            catch
                            {
                                // Truncated mid-tick — stop here, keep what we have
                                break;
                            }
                        }
                        break;
                    default:
                        // Skip terrain or unknown fields
                        try { reader.Read(); reader.TrySkip(); } catch { }
                        break;
                }
            }
        }
        catch { /* truncated at top level — we still have partial ticks */ }

        recording.TickCount = recording.Ticks.Count;
        if (recording.Ticks.Count > 0)
            recording.DurationSeconds = recording.Ticks.Count * 0.016f; // approximate

        Console.WriteLine($"Recovered {recording.Ticks.Count} complete ticks from truncated recording");
        return recording.Ticks.Count > 0 ? recording : null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Streaming recovery failed: {ex.Message}");
        return null;
    }
}
