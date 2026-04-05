using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Numerics;

namespace AutoExile.Systems
{
    /// <summary>
    /// Push-based entity tracking using ExileCore's EntityAdded/EntityRemoved callbacks.
    /// Maintains indexed collections by type for O(1) lookup by ID and fast iteration by type.
    /// Eliminates the need for systems to scan OnlyValidEntities every tick.
    ///
    /// Systems read from here instead of iterating the full entity list.
    /// BotCore feeds EntityAdded/EntityRemoved events, plus a full rebuild on area change
    /// (callbacks don't re-fire on map re-entry after death).
    /// </summary>
    public class EntityCache
    {
        // ── Primary index: all tracked entities by ID ──
        private readonly Dictionary<long, Entity> _byId = new(512);

        // ── Type-based collections for fast iteration ──
        private readonly List<Entity> _monsters = new(256);
        private readonly List<Entity> _chests = new(64);
        private readonly List<Entity> _worldItems = new(128);
        private readonly List<Entity> _ingameIcons = new(64);
        private readonly List<Entity> _areaTransitions = new(8);
        private readonly List<Entity> _shrines = new(8);
        private readonly List<Entity> _portals = new(8);

        // ── Public read-only access ──
        public IReadOnlyDictionary<long, Entity> ById => _byId;
        public IReadOnlyList<Entity> Monsters => _monsters;
        public IReadOnlyList<Entity> Chests => _chests;
        public IReadOnlyList<Entity> WorldItems => _worldItems;
        public IReadOnlyList<Entity> IngameIcons => _ingameIcons;
        public IReadOnlyList<Entity> AreaTransitions => _areaTransitions;
        public IReadOnlyList<Entity> Shrines => _shrines;
        public IReadOnlyList<Entity> Portals => _portals;
        public int TotalCount => _byId.Count;

        /// <summary>O(1) lookup by entity ID. Returns null if not tracked.</summary>
        public Entity? Get(long entityId) => _byId.GetValueOrDefault(entityId);

        /// <summary>
        /// Called from BotCore.EntityAdded(). Adds entity to type-indexed collections.
        /// </summary>
        public void OnEntityAdded(Entity entity)
        {
            if (entity?.Id == 0) return;
            _byId[entity.Id] = entity;
            GetListForType(entity.Type)?.Add(entity);
        }

        /// <summary>
        /// Called from BotCore.EntityRemoved(). Removes entity from all collections.
        /// </summary>
        public void OnEntityRemoved(Entity entity)
        {
            if (entity?.Id == 0) return;
            _byId.Remove(entity.Id);
            GetListForType(entity.Type)?.RemoveAll(e => e.Id == entity.Id);
        }

        /// <summary>
        /// Full rebuild from entity list. Call on area change / map re-entry
        /// since EntityAdded callbacks don't re-fire for existing entities.
        /// </summary>
        public void Rebuild(IEnumerable<Entity> allEntities)
        {
            Clear();
            foreach (var entity in allEntities)
            {
                if (entity?.Id == 0) continue;
                _byId[entity.Id] = entity;
                GetListForType(entity.Type)?.Add(entity);
            }
        }

        /// <summary>Clear all collections. Call on area change before rebuild.</summary>
        public void Clear()
        {
            _byId.Clear();
            _monsters.Clear();
            _chests.Clear();
            _worldItems.Clear();
            _ingameIcons.Clear();
            _areaTransitions.Clear();
            _shrines.Clear();
            _portals.Clear();
        }

        /// <summary>
        /// Prune dead/invalid entities from type lists. Call periodically (~every 1s)
        /// to clean up entities that became invalid without a Remove event.
        /// Does NOT remove from _byId — entities may become valid again.
        /// </summary>
        public void Prune()
        {
            PruneList(_monsters, e => e.IsValid && e.IsAlive && e.Type == EntityType.Monster);
            // Chests/icons: keep until explicitly removed (they go IsTargetable=false, not invalid)
            PruneList(_worldItems, e => e.IsValid);
            PruneList(_areaTransitions, e => e.IsValid);
            PruneList(_shrines, e => e.IsValid);
            PruneList(_portals, e => e.IsValid);
        }

        private static void PruneList(List<Entity> list, Func<Entity, bool> keep)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!keep(list[i]))
                    list.RemoveAt(i);
            }
        }

        private List<Entity>? GetListForType(EntityType type) => type switch
        {
            EntityType.Monster => _monsters,
            EntityType.Chest => _chests,
            EntityType.WorldItem => _worldItems,
            EntityType.IngameIcon => _ingameIcons,
            EntityType.AreaTransition => _areaTransitions,
            EntityType.Shrine => _shrines,
            EntityType.TownPortal => _portals,
            _ => null, // don't track other types
        };
    }
}
