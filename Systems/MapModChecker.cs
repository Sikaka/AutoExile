using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;

namespace AutoExile.Systems
{
    /// <summary>
    /// Checks map mods for dangerous affixes that a build can't handle.
    /// Reads ExplicitMods from the map item's Mods component.
    /// Mod names follow pattern: Map{Effect}{Tier}MapWorlds, groups are Map{Effect}.
    ///
    /// Usage:
    ///   var checker = new MapModChecker(dangerousMods);
    ///   var (safe, badMods) = checker.CheckMap(mapEntity);
    ///   if (!safe) reroll();
    /// </summary>
    public class MapModChecker
    {
        /// <summary>
        /// Known dangerous map mod groups. Key = mod group prefix (matched against ExplicitMod.Group).
        /// Value = human-readable description for logging/UI.
        ///
        /// Mod groups from research (ExplicitMod.Group values):
        ///   MapHexproof, MapMonsterCannotBeStunned, MapPoisoning,
        ///   MapMonsterAccuracyPlayersUnlockyDodge, MapMonstersStealChargesOnHit,
        ///   MapBloodlinesModOnMagics, MapMonsterDamage, MapMonstersEnduranceChargeOnHit,
        ///   MapMonstersPowerChargeOnHit, MapMonstersMaximumLifeAddedEnergyShield,
        ///   MapMonsterAreaOfEffect
        /// </summary>
        public static readonly Dictionary<string, string> AllKnownDangerousMods = new()
        {
            // Reflect — instant death for many builds
            ["MapElementalReflect"] = "Elemental Reflect",
            ["MapPhysicalReflect"] = "Physical Reflect",

            // Recovery — dangerous for regen/leech builds
            ["MapNoRegen"] = "No Regeneration",
            ["MapReducedRecovery"] = "Reduced Recovery",
            ["MapPlayerMaxResists"] = "Reduced Max Resistances",
            ["MapCannotLeech"] = "Cannot Leech",

            // Avoidance — dangerous for evasion/dodge builds
            ["MapMonsterAccuracyPlayersUnlockyDodge"] = "Unlucky Dodge / Reduced Evasion",

            // Curse immunity — dangerous for curse builds
            ["MapHexproof"] = "Hexproof (Curse Immune)",

            // Stun — dangerous for stun builds
            ["MapMonsterCannotBeStunned"] = "Cannot Be Stunned",

            // Status ailment — dangerous for ailment builds
            ["MapMonsterStatusAilmentThreshold"] = "Reduced Ailment Effect",
            ["MapPoisoning"] = "Poisoning (players take poison damage)",

            // Monster power — general danger
            ["MapBloodlinesModOnMagics"] = "Bloodlines (dangerous magic packs)",
            ["MapMonstersStealChargesOnHit"] = "Monsters Steal Charges",
            ["MapMonstersCritChance"] = "Monsters Crit Chance",
        };

        private readonly HashSet<string> _dangerousGroups;

        /// <summary>
        /// Create a checker with specific dangerous mod groups.
        /// Pass the Group values from ExplicitMod.Group that this build cannot handle.
        /// </summary>
        public MapModChecker(IEnumerable<string> dangerousGroups)
        {
            _dangerousGroups = new HashSet<string>(dangerousGroups, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Create a checker from a comma-separated settings string.
        /// </summary>
        public MapModChecker(string dangerousGroupsCsv)
        {
            _dangerousGroups = new HashSet<string>(
                (dangerousGroupsCsv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Check if a map entity has any dangerous mods.
        /// Returns (isSafe, list of dangerous mod names found).
        /// Returns (true, empty) if map is unidentified or has no mods.
        /// </summary>
        public (bool IsSafe, List<string> DangerousMods) CheckMap(Entity mapEntity)
        {
            var dangerous = new List<string>();

            var mods = mapEntity?.GetComponent<Mods>();
            if (mods == null) return (true, dangerous);

            // Unidentified maps can't be checked — treat as safe (need to ID first)
            if (!mods.Identified) return (true, dangerous);

            var explicits = mods.ExplicitMods;
            if (explicits == null) return (true, dangerous);

            foreach (var mod in explicits)
            {
                if (_dangerousGroups.Contains(mod.Group))
                {
                    dangerous.Add(mod.Group);
                }
            }

            return (dangerous.Count == 0, dangerous);
        }

        /// <summary>
        /// Quick check — is this map safe to run?
        /// </summary>
        public bool IsSafe(Entity mapEntity)
        {
            return CheckMap(mapEntity).IsSafe;
        }

        /// <summary>
        /// Read the item quantity value from map stats.
        /// Returns 0 if not available.
        /// </summary>
        public static int GetItemQuantity(Entity mapEntity)
        {
            var mods = mapEntity?.GetComponent<Mods>();
            if (mods == null || !mods.Identified) return 0;

            // Item quantity is typically a stat, not a mod
            var stats = mapEntity.GetComponent<Stats>();
            if (stats?.StatDictionary == null) return 0;

            // The stat key for map IIQ — may need POEMCP verification
            // Common stat keys: "map_item_drop_quantity_+%"
            foreach (var kv in stats.StatDictionary)
            {
                var key = kv.Key.ToString();
                if (key.Contains("quantity", StringComparison.OrdinalIgnoreCase))
                    return (int)kv.Value;
            }

            return 0;
        }
    }
}
