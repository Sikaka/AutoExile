using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Persistent, map-wide spatial grid tracking every monster observed during a run.
    /// Callback-driven — no full entity list iteration. Updated via EntityAdded/EntityRemoved
    /// hooks plus periodic reconciliation of nearby chunks for death detection.
    ///
    /// Chunk-based: map divided into ChunkSize x ChunkSize grid cells.
    /// Each chunk tracks how many monsters were seen, how many are confirmed dead,
    /// and maintains entity IDs for reconciliation.
    ///
    /// Used by WaveTick to bias exploration toward uncleared areas and make
    /// density-based combat engagement decisions with map-wide awareness.
    /// </summary>
    public class ThreatMap
    {
        public const int ChunkSize = 40; // grid units per chunk

        // ── Chunk grid ──
        private ThreatChunk[]? _chunks;
        private int _cols, _rows;
        private int _originX, _originY;

        // ── Entity tracking: entityId → (chunkIndex, rarity weight) ──
        private readonly Dictionary<long, TrackedMonster> _tracked = new(512);

        // ── Reconciliation timing ──
        private DateTime _lastReconcile = DateTime.MinValue;
        private const double ReconcileIntervalMs = 250;
        private const float ReconcileRadius = 200f; // grid — matches network bubble

        // ── Public state ──
        public int TotalAlive { get; private set; }
        public int TotalTracked { get; private set; }
        public int TotalDead { get; private set; }
        public int ChunkCount => _chunks?.Length ?? 0;
        public bool IsInitialized => _chunks != null;

        // ══════════════════════════════════════════════════════════════
        // Initialization
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Initialize the chunk grid from pathfinding data dimensions.
        /// Call once per zone (map entry or sub-zone transition).
        /// </summary>
        public void Initialize(int[][] pfGrid)
        {
            Clear();

            if (pfGrid == null || pfGrid.Length == 0) return;

            int gridRows = pfGrid.Length;
            int gridCols = pfGrid[0].Length;

            _originX = 0;
            _originY = 0;
            _cols = (gridCols + ChunkSize - 1) / ChunkSize;
            _rows = (gridRows + ChunkSize - 1) / ChunkSize;

            _chunks = new ThreatChunk[_rows * _cols];
            for (int i = 0; i < _chunks.Length; i++)
                _chunks[i] = new ThreatChunk();
        }

        /// <summary>Clear all state. Call on area change.</summary>
        public void Clear()
        {
            _chunks = null;
            _tracked.Clear();
            TotalAlive = 0;
            TotalTracked = 0;
            TotalDead = 0;
            _lastReconcile = DateTime.MinValue;
        }

        // ══════════════════════════════════════════════════════════════
        // Entity callbacks
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Called when a new entity enters the game's entity list.
        /// Filters for hostile alive monsters and tracks them in the appropriate chunk.
        /// </summary>
        public void OnEntityAdded(Entity entity)
        {
            if (!IsInitialized) return;
            if (entity?.Type != EntityType.Monster) return;
            if (!entity.IsHostile || !entity.IsAlive) return;
            if (_tracked.ContainsKey(entity.Id)) return; // already tracking

            var pos = entity.GridPosNum;
            int ci = ChunkIndex(pos);
            if (ci < 0) return;

            var weight = RarityWeight(entity.Rarity);
            _tracked[entity.Id] = new TrackedMonster
            {
                EntityId = entity.Id,
                GridPos = pos,
                Rarity = entity.Rarity,
                Weight = weight,
                ChunkIndex = ci,
                Status = MonsterStatus.Alive,
            };

            var chunk = _chunks![ci];
            chunk.AliveCount++;
            chunk.AliveWeight += weight;
            chunk.TotalSeen++;
            chunk.TotalWeight += weight;
            chunk.EntityIds.Add(entity.Id);

            TotalAlive++;
            TotalTracked++;
        }

        /// <summary>
        /// Called when an entity leaves the game's entity list.
        /// If the monster is dead (IsAlive=false), mark it dead in its chunk.
        /// If alive, it just left the network bubble — mark as LeftRange
        /// (it's probably still alive out there, we just can't see it).
        /// </summary>
        public void OnEntityRemoved(Entity entity)
        {
            if (entity?.Type != EntityType.Monster) return;
            if (!_tracked.TryGetValue(entity.Id, out var tracked)) return;
            if (tracked.Status != MonsterStatus.Alive) return;

            if (!entity.IsAlive)
            {
                // Confirmed dead
                MarkDead(entity.Id, tracked);
            }
            else
            {
                // Left network bubble — mark as left-range.
                // Keep in _tracked so if EntityAdded fires again later
                // (player returns to area), we can update.
                tracked.Status = MonsterStatus.LeftRange;
                _tracked[entity.Id] = tracked;

                if (tracked.ChunkIndex >= 0 && _chunks != null)
                {
                    // Don't decrement AliveCount for LeftRange — these monsters
                    // are probably still alive. The chunk keeps its density estimate.
                    // If we revisit and they're dead, reconciliation will catch it.
                }
            }
        }

        /// <summary>
        /// Called when an entity re-enters the game's entity list (player returned to area).
        /// Updates tracking: if we had it as LeftRange and it's now dead, mark dead.
        /// If it's alive, confirm it's still alive.
        /// </summary>
        public void OnEntityReAdded(Entity entity)
        {
            if (!IsInitialized) return;
            if (entity?.Type != EntityType.Monster) return;
            if (!entity.IsHostile) return;

            if (!_tracked.TryGetValue(entity.Id, out var tracked))
            {
                // Never seen before — treat as new
                OnEntityAdded(entity);
                return;
            }

            if (tracked.Status == MonsterStatus.Dead) return; // already counted

            if (!entity.IsAlive)
            {
                // Was LeftRange or Alive, now dead
                MarkDead(entity.Id, tracked);
            }
            else
            {
                // Still alive — update status back to Alive if was LeftRange
                if (tracked.Status == MonsterStatus.LeftRange)
                {
                    tracked.Status = MonsterStatus.Alive;
                    _tracked[entity.Id] = tracked;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Reconciliation
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Periodic check of nearby chunks — detects monster deaths that didn't
        /// trigger EntityRemoved (dead entities persist in OnlyValidEntities).
        /// Call every tick; internal timer gates to ReconcileIntervalMs.
        /// </summary>
        public void Reconcile(Vector2 playerPos, EntityCache entities)
        {
            if (!IsInitialized || _chunks == null) return;
            if ((DateTime.Now - _lastReconcile).TotalMilliseconds < ReconcileIntervalMs) return;
            _lastReconcile = DateTime.Now;

            // Check chunks within reconciliation radius
            int pcx = ((int)playerPos.X - _originX) / ChunkSize;
            int pcy = ((int)playerPos.Y - _originY) / ChunkSize;
            int chunkRadius = (int)(ReconcileRadius / ChunkSize) + 1;

            for (int dy = -chunkRadius; dy <= chunkRadius; dy++)
            {
                int cy = pcy + dy;
                if (cy < 0 || cy >= _rows) continue;
                for (int dx = -chunkRadius; dx <= chunkRadius; dx++)
                {
                    int cx = pcx + dx;
                    if (cx < 0 || cx >= _cols) continue;

                    var chunk = _chunks[cy * _cols + cx];
                    if (chunk.AliveCount == 0) continue;

                    // Check each tracked entity in this chunk
                    for (int i = chunk.EntityIds.Count - 1; i >= 0; i--)
                    {
                        var entityId = chunk.EntityIds[i];
                        if (!_tracked.TryGetValue(entityId, out var tracked)) continue;
                        if (tracked.Status != MonsterStatus.Alive) continue;

                        // Check live entity state
                        var entity = entities.Get(entityId);
                        if (entity == null || !entity.IsAlive)
                        {
                            MarkDead(entityId, tracked);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Rebuild ThreatMap from an existing entity list.
        /// Call after EntityCache.Rebuild (e.g., zone transition).
        /// </summary>
        public void RebuildFromEntities(IReadOnlyList<Entity> monsters)
        {
            foreach (var entity in monsters)
            {
                if (entity.IsHostile && entity.IsAlive)
                    OnEntityAdded(entity);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Queries
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Find the chunk center with the highest estimated alive monster weight.
        /// Skips chunks within minDistance of the player (don't micro-navigate).
        /// Returns null if no chunks have alive monsters.
        /// </summary>
        public Vector2? GetDensestAliveChunk(Vector2 playerPos, float minDistance = 15f)
        {
            if (_chunks == null) return null;

            float bestWeight = 0;
            Vector2? bestPos = null;
            float minDistSq = minDistance * minDistance;

            for (int cy = 0; cy < _rows; cy++)
            {
                for (int cx = 0; cx < _cols; cx++)
                {
                    var chunk = _chunks[cy * _cols + cx];
                    if (chunk.AliveWeight <= 0) continue;

                    var center = ChunkCenter(cx, cy);
                    if (Vector2.DistanceSquared(playerPos, center) < minDistSq) continue;

                    if (chunk.AliveWeight > bestWeight)
                    {
                        bestWeight = chunk.AliveWeight;
                        bestPos = center;
                    }
                }
            }

            return bestPos;
        }

        /// <summary>
        /// Find the nearest chunk with alive monsters. Returns chunk center or null.
        /// </summary>
        public Vector2? GetNearestAliveChunk(Vector2 playerPos, float minDistance = 15f)
        {
            if (_chunks == null) return null;

            float bestDistSq = float.MaxValue;
            Vector2? bestPos = null;
            float minDistSq = minDistance * minDistance;

            for (int cy = 0; cy < _rows; cy++)
            {
                for (int cx = 0; cx < _cols; cx++)
                {
                    var chunk = _chunks[cy * _cols + cx];
                    if (chunk.AliveCount <= 0) continue;

                    var center = ChunkCenter(cx, cy);
                    var distSq = Vector2.DistanceSquared(playerPos, center);
                    if (distSq < minDistSq) continue;

                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestPos = center;
                    }
                }
            }

            return bestPos;
        }

        /// <summary>
        /// Get the alive monster estimate for the chunk containing a position.
        /// Returns 0 if not initialized or position is out of bounds.
        /// </summary>
        public int GetChunkAliveCount(Vector2 pos)
        {
            int ci = ChunkIndex(pos);
            if (ci < 0 || _chunks == null) return 0;
            return _chunks[ci].AliveCount;
        }

        /// <summary>
        /// Get the alive weight (rarity-weighted) for the chunk containing a position.
        /// </summary>
        public float GetChunkAliveWeight(Vector2 pos)
        {
            int ci = ChunkIndex(pos);
            if (ci < 0 || _chunks == null) return 0;
            return _chunks[ci].AliveWeight;
        }

        /// <summary>
        /// Sum alive weight across all chunks within radius of a point.
        /// </summary>
        public float GetThreatInRadius(Vector2 center, float radius)
        {
            if (_chunks == null) return 0;

            float total = 0;
            float radiusSq = radius * radius;

            int minCx = Math.Max(0, ((int)(center.X - radius) - _originX) / ChunkSize);
            int maxCx = Math.Min(_cols - 1, ((int)(center.X + radius) - _originX) / ChunkSize);
            int minCy = Math.Max(0, ((int)(center.Y - radius) - _originY) / ChunkSize);
            int maxCy = Math.Min(_rows - 1, ((int)(center.Y + radius) - _originY) / ChunkSize);

            for (int cy = minCy; cy <= maxCy; cy++)
            {
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    var chunk = _chunks[cy * _cols + cx];
                    if (chunk.AliveWeight <= 0) continue;

                    var cc = ChunkCenter(cx, cy);
                    if (Vector2.DistanceSquared(center, cc) <= radiusSq)
                        total += chunk.AliveWeight;
                }
            }

            return total;
        }

        // ══════════════════════════════════════════════════════════════
        // Snapshot / Restore
        // ══════════════════════════════════════════════════════════════

        internal ThreatMapSnapshot CreateSnapshot()
        {
            var snap = new ThreatMapSnapshot
            {
                Cols = _cols,
                Rows = _rows,
                OriginX = _originX,
                OriginY = _originY,
                TotalAlive = TotalAlive,
                TotalTracked = TotalTracked,
                TotalDead = TotalDead,
            };

            if (_chunks != null)
            {
                snap.Chunks = new ThreatChunkSnapshot[_chunks.Length];
                for (int i = 0; i < _chunks.Length; i++)
                {
                    var c = _chunks[i];
                    snap.Chunks[i] = new ThreatChunkSnapshot
                    {
                        AliveCount = c.AliveCount,
                        DeadCount = c.DeadCount,
                        TotalSeen = c.TotalSeen,
                        AliveWeight = c.AliveWeight,
                        TotalWeight = c.TotalWeight,
                        EntityIds = new List<long>(c.EntityIds),
                    };
                }
            }

            snap.Tracked = new Dictionary<long, TrackedMonster>(_tracked);
            return snap;
        }

        internal void RestoreSnapshot(ThreatMapSnapshot snap)
        {
            Clear();

            _cols = snap.Cols;
            _rows = snap.Rows;
            _originX = snap.OriginX;
            _originY = snap.OriginY;
            TotalAlive = snap.TotalAlive;
            TotalTracked = snap.TotalTracked;
            TotalDead = snap.TotalDead;

            if (snap.Chunks != null)
            {
                _chunks = new ThreatChunk[snap.Chunks.Length];
                for (int i = 0; i < snap.Chunks.Length; i++)
                {
                    var s = snap.Chunks[i];
                    _chunks[i] = new ThreatChunk
                    {
                        AliveCount = s.AliveCount,
                        DeadCount = s.DeadCount,
                        TotalSeen = s.TotalSeen,
                        AliveWeight = s.AliveWeight,
                        TotalWeight = s.TotalWeight,
                        EntityIds = new List<long>(s.EntityIds),
                    };
                }
            }

            foreach (var kvp in snap.Tracked)
                _tracked[kvp.Key] = kvp.Value;
        }

        // ══════════════════════════════════════════════════════════════
        // Internals
        // ══════════════════════════════════════════════════════════════

        private void MarkDead(long entityId, TrackedMonster tracked)
        {
            tracked.Status = MonsterStatus.Dead;
            _tracked[entityId] = tracked;

            if (tracked.ChunkIndex >= 0 && _chunks != null)
            {
                var chunk = _chunks[tracked.ChunkIndex];
                chunk.AliveCount = Math.Max(0, chunk.AliveCount - 1);
                chunk.AliveWeight = MathF.Max(0, chunk.AliveWeight - tracked.Weight);
                chunk.DeadCount++;
            }

            TotalAlive = Math.Max(0, TotalAlive - 1);
            TotalDead++;
        }

        private int ChunkIndex(Vector2 pos)
        {
            if (_chunks == null) return -1;
            int cx = ((int)pos.X - _originX) / ChunkSize;
            int cy = ((int)pos.Y - _originY) / ChunkSize;
            if (cx < 0 || cx >= _cols || cy < 0 || cy >= _rows) return -1;
            return cy * _cols + cx;
        }

        private Vector2 ChunkCenter(int cx, int cy) =>
            new(_originX + cx * ChunkSize + ChunkSize / 2f,
                _originY + cy * ChunkSize + ChunkSize / 2f);

        private static float RarityWeight(MonsterRarity rarity) => rarity switch
        {
            MonsterRarity.Magic => 2f,
            MonsterRarity.Rare => 5f,
            MonsterRarity.Unique => 8f,
            _ => 1f,
        };
    }

    // ══════════════════════════════════════════════════════════════
    // Supporting types
    // ══════════════════════════════════════════════════════════════

    internal struct TrackedMonster
    {
        public long EntityId;
        public Vector2 GridPos;
        public MonsterRarity Rarity;
        public float Weight;
        public int ChunkIndex;
        public MonsterStatus Status;
    }

    internal enum MonsterStatus : byte
    {
        Alive,
        Dead,
        LeftRange,
    }

    internal class ThreatChunk
    {
        public int AliveCount;
        public int DeadCount;
        public int TotalSeen;
        public float AliveWeight;
        public float TotalWeight;
        public List<long> EntityIds = new(8);
    }

    // ── Snapshot types ──

    internal class ThreatMapSnapshot
    {
        public int Cols, Rows, OriginX, OriginY;
        public int TotalAlive, TotalTracked, TotalDead;
        public ThreatChunkSnapshot[]? Chunks;
        public Dictionary<long, TrackedMonster> Tracked = new();
    }

    internal class ThreatChunkSnapshot
    {
        public int AliveCount, DeadCount, TotalSeen;
        public float AliveWeight, TotalWeight;
        public List<long> EntityIds = new();
    }
}
