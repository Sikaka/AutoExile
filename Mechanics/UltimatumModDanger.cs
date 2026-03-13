namespace AutoExile.Mechanics
{
    /// <summary>
    /// Default danger ratings for Ultimatum modifiers.
    /// Scale: 0=Free, 1=Easy, 3=Medium, 5=Hard, 10=Very Hard, 999=SKIP.
    /// Users can override per-mod in settings. Unknown mods default to Medium (3).
    ///
    /// Categorized based on typical softcore mapping builds.
    /// Mods that require specific avoidance patterns (Stalking Ruin, Blood Altar)
    /// are rated higher since bot may not dodge optimally.
    /// </summary>
    public static class UltimatumModDanger
    {
        public const int BlockedValue = 999;
        public const int DefaultDanger = 3; // Medium

        /// <summary>
        /// Default danger ratings, keyed by modifier Id from game files.
        /// These are starting defaults — users should tune for their build.
        /// </summary>
        public static readonly Dictionary<string, int> Defaults = new()
        {
            // ── Free (0) — barely noticeable ──
            ["MonsterBuffAcceleratingSpeed"] = 0,         // Escalating Monster Speed
            ["MonsterBuffAdditionalProjectiles"] = 0,     // Additional Projectiles
            ["Radius1"] = 0,                              // Limited Arena (halves encounter area)

            // ── Easy (1) — manageable for most builds ──
            ["MonsterBuffChaosDamage"] = 1,               // Chaos Damage
            ["MonsterBuffIncreasedDamage"] = 1,           // Increased Damage
            ["MonsterBuffResistances"] = 1,               // Monster Resistances
            ["MonsterBuffLife"] = 1,                      // Increased Life
            ["PlayerDebuffReducedDamage"] = 1,            // Reduced Damage
            ["MonsterHitsAreCriticalStrikes"] = 1,        // Deadly Monsters
            ["FlamespitterDaemon1"] = 1,                  // Raging Dead I
            ["ChaosCloudDaemon1"] = 1,                    // Choking Miasma I

            // ── Medium (3) — noticeable danger ──
            ["FlamespitterDaemon2"] = 3,                  // Raging Dead II
            ["ChaosCloudDaemon2"] = 3,                    // Choking Miasma II
            ["AltarDaemon1"] = 3,                         // Blood Altar I
            ["PlayerDebuffReducedRecovery"] = 3,          // Reduced Recovery

            // ── Hard (5) — high threat, build-dependent ──
            ["AltarDaemon2"] = 5,                         // Blood Altar II
            ["PlayerDebuffNoLeech"] = 5,                  // No Leech
            ["PlayerDebuffNoRegen"] = 5,                  // No Regeneration
            ["PlayerDebuffLimitedFlasks"] = 5,            // Limited Flasks

            // ── Very Hard (10) — very dangerous for bots ──
            ["PlayerDebuffNullification"] = 10,           // Nullification (removes charges/buffs)


            // ── SKIP (999) — do not choose under any circumstance ──
            ["RevenantDaemon1"] = 999,                      // Stalking Ruin I
            ["RevenantDaemon2"] = 999,                      // Stalking Ruin II
        };

        /// <summary>
        /// Get danger rating for a modifier. Checks user overrides first, then defaults.
        /// </summary>
        public static int GetDanger(string modId, Dictionary<string, int>? userOverrides)
        {
            if (userOverrides != null && userOverrides.TryGetValue(modId, out var userDanger))
                return userDanger;
            if (Defaults.TryGetValue(modId, out var defaultDanger))
                return defaultDanger;
            return DefaultDanger;
        }
    }
}
