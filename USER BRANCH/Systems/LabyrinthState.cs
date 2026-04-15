using ExileCore;
using ExileCore.Shared.Enums;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Tracks labyrinth run state: entity positions (font, chests, Izaro, exits),
    /// zone/encounter counters, and run statistics.
    /// Entity IDs are cached and re-resolved each tick — never hold Entity references across ticks.
    /// All positions stored in grid coordinates.
    /// </summary>
    public class LabyrinthState
    {
        // ── Entity tracking ──

        public long? FontId { get; private set; }
        public long? ReturnPortalId { get; private set; }
        public long? StashId { get; private set; }
        public long? IzaroId { get; private set; }
        public long? IzaroDoorId { get; private set; }
        public long? ArenaTransitionId { get; private set; }

        public Vector2? FontPosition { get; private set; }
        public Vector2? ReturnPortalPosition { get; private set; }
        public Vector2? StashPosition { get; private set; }
        public Vector2? IzaroPosition { get; private set; }
        public Vector2? IzaroDoorPosition { get; private set; }
        public Vector2? ArenaTransitionPosition { get; private set; }

        // Reward chests
        private readonly List<long> _chestIds = new();
        private readonly List<Vector2> _chestPositions = new();
        public IReadOnlyList<Vector2> ChestPositions => _chestPositions;
        public int ChestCount => _chestIds.Count;

        // Exit transitions found in current zone
        private readonly List<(long Id, Vector2 Position)> _exitTransitions = new();
        public IReadOnlyList<(long Id, Vector2 Position)> ExitTransitions => _exitTransitions;

        // ── Run state ──

        public int IzaroEncounterCount { get; set; }
        public int ZoneCount { get; set; }
        public int DeathCount { get; set; }
        public int RunsCompleted { get; private set; }
        public DateTime RunStartedAt { get; private set; } = DateTime.Now;

        // Session stats
        public double TotalProfit { get; set; }
        public int GemsTransformed { get; set; }

        // The gem we've selected to transform this run
        public string? SelectedGemName { get; set; }
        public double SelectedGemValue { get; set; }
        public int SelectedGemLevel { get; set; } = 1;
        public int SelectedGemQuality { get; set; }

        // Position sanity
        private const float PositionSanityThreshold = 50f;

        /// <summary>
        /// Full reset for starting a new run.
        /// </summary>
        public void Reset()
        {
            FontId = null;
            ReturnPortalId = null;
            StashId = null;
            IzaroId = null;
            IzaroDoorId = null;
            ArenaTransitionId = null;

            FontPosition = null;
            ReturnPortalPosition = null;
            StashPosition = null;
            IzaroPosition = null;
            IzaroDoorPosition = null;
            ArenaTransitionPosition = null;

            _chestIds.Clear();
            _chestPositions.Clear();
            _exitTransitions.Clear();

            IzaroEncounterCount = 0;
            ZoneCount = 0;
            DeathCount = 0;
            SelectedGemName = null;
            SelectedGemValue = 0;
            SelectedGemLevel = 1;
            SelectedGemQuality = 0;
        }

        /// <summary>
        /// Clear entity caches on area change but preserve run-level state.
        /// </summary>
        public void OnAreaChanged()
        {
            FontId = null;
            ReturnPortalId = null;
            StashId = null;
            IzaroId = null;
            IzaroDoorId = null;
            ArenaTransitionId = null;

            FontPosition = null;
            ReturnPortalPosition = null;
            StashPosition = null;
            IzaroPosition = null;
            IzaroDoorPosition = null;
            ArenaTransitionPosition = null;

            _chestIds.Clear();
            _chestPositions.Clear();
            _exitTransitions.Clear();
        }

        /// <summary>
        /// Record a completed run.
        /// </summary>
        public void RecordRunComplete()
        {
            RunsCompleted++;
            RunStartedAt = DateTime.Now;
        }

        /// <summary>
        /// Scan entities each tick to update cached positions.
        /// </summary>
        public void Tick(GameController gc)
        {
            if (gc?.EntityListWrapper == null) return;

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity?.Path == null) continue;
                var path = entity.Path;
                var gridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);

                // Divine Font
                if (path.Contains("LabyrinthBlessingBench", StringComparison.Ordinal))
                {
                    if (FontId == null || FontId == entity.Id)
                    {
                        FontId = entity.Id;
                        if (IsSanePosition(FontPosition, gridPos))
                            FontPosition = gridPos;
                    }
                }
                // Return portal
                else if (path.Contains("LabyrinthReturnPortal", StringComparison.Ordinal))
                {
                    if (ReturnPortalId == null || ReturnPortalId == entity.Id)
                    {
                        ReturnPortalId = entity.Id;
                        if (IsSanePosition(ReturnPortalPosition, gridPos))
                            ReturnPortalPosition = gridPos;
                    }
                }
                // Izaro door (staging room)
                else if (path.Contains("LabyrinthIzaroDoor", StringComparison.Ordinal))
                {
                    IzaroDoorId = entity.Id;
                    IzaroDoorPosition = gridPos;
                }
                // Izaro arena transition
                else if (path.Contains("LabyrinthIzaroArenaTransition", StringComparison.Ordinal))
                {
                    ArenaTransitionId = entity.Id;
                    ArenaTransitionPosition = gridPos;
                }
                // Izaro boss
                else if (path.Contains("Monsters/Labyrinth/LabyrinthBoss", StringComparison.Ordinal)
                         && entity.IsAlive && entity.IsTargetable)
                {
                    IzaroId = entity.Id;
                    IzaroPosition = gridPos;
                }
                // Reward chests
                else if (path.Contains("Chests/Labyrinth/Izaro/", StringComparison.Ordinal))
                {
                    if (!_chestIds.Contains(entity.Id) && entity.IsTargetable)
                    {
                        _chestIds.Add(entity.Id);
                        _chestPositions.Add(gridPos);
                    }
                }
                // Stash
                else if (path.Contains("MiscellaneousObjects/Stash", StringComparison.Ordinal))
                {
                    if (StashId == null || StashId == entity.Id)
                    {
                        StashId = entity.Id;
                        StashPosition = gridPos;
                    }
                }
                // Area transitions (zone exits)
                else if (entity.Type == EntityType.AreaTransition && entity.IsTargetable)
                {
                    if (!_exitTransitions.Any(e => e.Id == entity.Id))
                        _exitTransitions.Add((entity.Id, gridPos));
                }
            }

            // Check if Izaro has left (entity no longer valid/alive)
            if (IzaroId.HasValue)
            {
                bool found = false;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Id == IzaroId.Value && entity.IsAlive && entity.IsTargetable)
                    {
                        found = true;
                        IzaroPosition = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                        break;
                    }
                }
                if (!found)
                {
                    IzaroId = null;
                    IzaroPosition = null;
                }
            }
        }

        /// <summary>
        /// Check if Izaro is currently present and alive.
        /// </summary>
        public bool IsIzaroPresent => IzaroId.HasValue;

        /// <summary>
        /// Check if we're in a zone with the Divine Font.
        /// </summary>
        public bool HasFont => FontId.HasValue;

        /// <summary>
        /// Check if we're in a staging room (has Izaro door).
        /// </summary>
        public bool HasIzaroDoor => IzaroDoorId.HasValue;

        /// <summary>
        /// Check if we're in a zone with a return portal.
        /// </summary>
        public bool HasReturnPortal => ReturnPortalId.HasValue;

        private static bool IsSanePosition(Vector2? previous, Vector2 current)
        {
            if (!previous.HasValue) return true;
            return Vector2.Distance(previous.Value, current) < PositionSanityThreshold;
        }
    }
}
