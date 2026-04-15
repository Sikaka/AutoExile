using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using AutoExile.Systems;
using System.Numerics;

namespace AutoExile.WebServer
{
    /// <summary>
    /// Encodes terrain grid data and collects entity positions for the web map view.
    /// All coordinates are in grid units.
    /// </summary>
    public static class MapRenderer
    {
        /// <summary>
        /// Encode terrain grid to a compact byte array (1 byte per cell).
        /// Values: 0=wall, 1-5=walkable, 6=jumpable gap, +8 if explored (bit 3).
        /// Cropped to walkable bounds.
        /// </summary>
        public static MapTerrainData? BuildTerrainData(int[][]? pfGrid, int[][]? tgtGrid,
            ExplorationMap? exploration)
        {
            if (pfGrid == null || pfGrid.Length == 0) return null;

            int rows = pfGrid.Length;
            int cols = pfGrid[0].Length;

            // Find walkable bounds (crop to relevant area)
            int minR = rows, maxR = 0, minC = cols, maxC = 0;
            for (int r = 0; r < rows; r++)
            {
                var row = pfGrid[r];
                if (row == null) continue;
                for (int c = 0; c < row.Length; c++)
                {
                    if (row[c] > 0)
                    {
                        if (r < minR) minR = r;
                        if (r > maxR) maxR = r;
                        if (c < minC) minC = c;
                        if (c > maxC) maxC = c;
                    }
                }
            }

            if (minR > maxR) return null; // No walkable cells

            // Add small padding
            minR = Math.Max(0, minR - 5);
            maxR = Math.Min(rows - 1, maxR + 5);
            minC = Math.Max(0, minC - 5);
            maxC = Math.Min(cols - 1, maxC + 5);

            int w = maxC - minC + 1;
            int h = maxR - minR + 1;
            var data = new byte[w * h];

            // Get exploration seen cells
            HashSet<Vector2i>? seenCells = null;
            if (exploration?.IsInitialized == true && exploration.ActiveBlob != null)
                seenCells = exploration.ActiveBlob.SeenCells;

            for (int r = minR; r <= maxR; r++)
            {
                var pfRow = pfGrid[r];
                var tgtRow = tgtGrid != null && r < tgtGrid.Length ? tgtGrid[r] : null;
                if (pfRow == null) continue;

                for (int c = minC; c <= maxC; c++)
                {
                    if (c >= pfRow.Length) continue;

                    int idx = (r - minR) * w + (c - minC);
                    int pfVal = pfRow[c];
                    int tgtVal = tgtRow != null && c < tgtRow.Length ? tgtRow[c] : 0;

                    byte cellValue;
                    if (pfVal > 0)
                        cellValue = (byte)Math.Min(pfVal, 5); // walkable 1-5
                    else if (tgtVal > 0)
                        cellValue = 6; // jumpable gap
                    else
                        cellValue = 0; // wall

                    // Set explored bit (bit 3)
                    if (seenCells != null && seenCells.Contains(new Vector2i(c, r)))
                        cellValue |= 0x08;

                    data[idx] = cellValue;
                }
            }

            return new MapTerrainData
            {
                Data = data,
                Width = w,
                Height = h,
                OriginX = minC,
                OriginY = minR,
            };
        }

        /// <summary>
        /// Collect visible entities for the map overlay.
        /// Returns compact entity list within network bubble.
        /// </summary>
        public static List<MapEntity> CollectEntities(GameController gc, Vector2 playerGrid)
        {
            var entities = new List<MapEntity>();
            if (gc?.EntityListWrapper == null) return entities;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                var gridPos = entity.GridPosNum;
                var dist = Vector2.Distance(gridPos, playerGrid);
                if (dist > Pathfinding.NetworkBubbleRadius) continue;

                var type = ClassifyEntity(entity);
                if (type == null) continue;

                // Filter: skip dead monsters, non-targetable chests
                if (type == "m" && !entity.IsAlive) continue;
                if (type == "c" && !entity.IsTargetable) continue;

                var me = new MapEntity
                {
                    X = gridPos.X,
                    Y = gridPos.Y,
                    T = type,
                    A = entity.IsAlive,
                };

                // Add rarity for monsters
                if (type == "m")
                {
                    me.R = entity.Rarity switch
                    {
                        ExileCore.Shared.Enums.MonsterRarity.Magic => "m",
                        ExileCore.Shared.Enums.MonsterRarity.Rare => "r",
                        ExileCore.Shared.Enums.MonsterRarity.Unique => "u",
                        _ => null,
                    };
                }

                entities.Add(me);
            }

            return entities;
        }

        /// <summary>Collect navigation path waypoints as compact arrays.</summary>
        public static List<float[]>? CollectNavPath(NavigationSystem nav)
        {
            if (!nav.IsNavigating || nav.CurrentNavPath.Count == 0) return null;

            var path = new List<float[]>();
            for (int i = nav.CurrentWaypointIndex; i < nav.CurrentNavPath.Count; i++)
            {
                var wp = nav.CurrentNavPath[i];
                path.Add(new[] { wp.Position.X, wp.Position.Y, wp.Action == WaypointAction.Blink ? 1f : 0f });
            }
            return path.Count > 0 ? path : null;
        }

        private static string? ClassifyEntity(Entity entity)
        {
            var type = entity.Type;
            var path = entity.Path ?? "";

            return type switch
            {
                ExileCore.Shared.Enums.EntityType.Monster when entity.IsHostile => "m",
                ExileCore.Shared.Enums.EntityType.Player => "p",
                ExileCore.Shared.Enums.EntityType.Chest => "c",
                ExileCore.Shared.Enums.EntityType.AreaTransition => "a",
                ExileCore.Shared.Enums.EntityType.TownPortal or ExileCore.Shared.Enums.EntityType.Portal => "o",
                ExileCore.Shared.Enums.EntityType.Stash => "s",
                _ when path.Contains("MiscellaneousObjects/Stash") => "s",
                _ when path.Contains("Afflictionator") => "n", // monolith
                _ => null,
            };
        }
    }

    // ================================================================
    // Map data types (compact for WebSocket serialization)
    // ================================================================

    public class MapTerrainData
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int Width { get; set; }
        public int Height { get; set; }
        public int OriginX { get; set; }
        public int OriginY { get; set; }
    }

    /// <summary>Compact entity for map overlay. Short property names for wire size.</summary>
    public class MapEntity
    {
        /// <summary>Grid X</summary>
        public float X { get; set; }
        /// <summary>Grid Y</summary>
        public float Y { get; set; }
        /// <summary>Type: m=monster, p=player, c=chest, a=transition, o=portal, s=stash, n=monolith</summary>
        public string T { get; set; } = "";
        /// <summary>Rarity: null=normal, m=magic, r=rare, u=unique</summary>
        public string? R { get; set; }
        /// <summary>Alive</summary>
        public bool A { get; set; }
    }
}
