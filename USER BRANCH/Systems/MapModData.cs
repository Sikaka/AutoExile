namespace AutoExile.Systems
{
    /// <summary>
    /// Known PoE1 map modifier raw-name substrings with human-readable display names.
    /// Raw names sourced from MapNotify's map_mods_data.json.
    /// The filter uses substring matching on mod.RawName, so exact type names work as keys.
    /// </summary>
    public static class MapModData
    {
        public record ModInfo(string DisplayName, string Category);

        public static readonly IReadOnlyDictionary<string, ModInfo> KnownMods =
            new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Player Curses ────────────────────────────────────────────────────────────
            ["MapPlayerCurseTemporalChains"]      = new("Temporal Chains",            "Player Curses"),
            ["MapPlayerCurseEnfeeblement"]        = new("Enfeeble",                   "Player Curses"),
            ["MapPlayerCurseElementalWeakness"]   = new("Elemental Weakness",         "Player Curses"),
            ["MapPlayerCurseVulnerability"]       = new("Vulnerability",              "Player Curses"),
            ["MapPlayerCurseFrostbite"]           = new("Frostbite",                  "Player Curses"),
            ["MapPlayerCurseFlammability"]        = new("Flammability",               "Player Curses"),
            ["MapPlayerCurseConductivity"]        = new("Conductivity",               "Player Curses"),
            ["MapPlayerCurseProjectileWeakness"]  = new("Despair",                    "Player Curses"),
            ["MapPlayerCursePunishment"]          = new("Punishment",                 "Player Curses"),
            ["MapPlayerCurseWarlordsMark"]        = new("Warlord's Mark",             "Player Curses"),

            // ── Player Penalties ─────────────────────────────────────────────────────────
            ["MapPlayerNoLifeESRegen"]            = new("No Life/ES Regen",           "Player Penalties"),
            ["MapPlayerNoRegen"]                  = new("No Life or Mana Regen",      "Player Penalties"),
            ["MapPlayerReducedRegen"]             = new("Reduced Recovery Rate",      "Player Penalties"),
            ["MapPlayerMaxResists"]               = new("Reduced Max Resistances",    "Player Penalties"),
            ["MapPlayerBloodMagic"]               = new("Blood Magic",                "Player Penalties"),
            ["MapPlayerCorruptingBlood"]          = new("Corrupting Blood",           "Player Penalties"),
            ["MapPlayerContinuousDamage"]         = new("Chaos Damage per Second",    "Player Penalties"),
            ["MapPlayerProjectileDamage"]         = new("No Projectile Damage",       "Player Penalties"),
            ["MapPlayersBlockAndArmour"]          = new("Less Block and Armour",      "Player Penalties"),
            ["MapPlayersAccuracyRating"]          = new("Less Accuracy Rating",       "Player Penalties"),
            ["MapPlayersAoERadius"]               = new("Less AoE",                   "Player Penalties"),
            ["MapPlayersGainReducedFlaskCharges"] = new("Reduced Flask Charges",      "Player Penalties"),
            ["MapPlayersLessCooldownRecovery"]    = new("Less Cooldown Recovery",     "Player Penalties"),
            ["MapPlayersReducedAuraEffect"]       = new("Reduced Aura Effect",        "Player Penalties"),
            ["MapPlayersBuffsExpireFaster"]       = new("Buffs Expire Faster",        "Player Penalties"),
            ["MapPlayerElementalEquilibrium"]     = new("Cannot Inflict Exposure",    "Player Penalties"),

            // ── Monster Buffs ────────────────────────────────────────────────────────────
            ["MapMonsterPhysicalReflection"]      = new("Physical Reflect",           "Monster Buffs"),
            ["MapMonsterElementalReflection"]     = new("Elemental Reflect",          "Monster Buffs"),
            ["MapHexproof"]                       = new("Hexproof",                   "Monster Buffs"),
            ["MapMonsterCriticalStrikesAndDamage"]= new("High Crit Chance + Multiplier","Monster Buffs"),
            ["MapMonsterCriticalStrikeMultiplier"]= new("High Crit Multiplier",       "Monster Buffs"),
            ["MapMonsterCriticalStrikeChance"]    = new("High Crit Chance",           "Monster Buffs"),
            ["MapMonsterDamage"]                  = new("Increased Monster Damage",   "Monster Buffs"),
            ["MapMonsterFast"]                    = new("Increased Monster Speed",    "Monster Buffs"),
            ["MapMonsterLife"]                    = new("More Monster Life",          "Monster Buffs"),
            ["MapMonsterAllsResistances"]         = new("Monster All Resistances",    "Monster Buffs"),
            ["MapMonsterFireResistance"]          = new("Monster Fire Resistance",    "Monster Buffs"),
            ["MapMonsterColdResistance"]          = new("Monster Cold Resistance",    "Monster Buffs"),
            ["MapMonsterLightningResistance"]     = new("Monster Lightning Resistance","Monster Buffs"),
            ["MapMonsterPhysicalResistance"]      = new("Monster Physical Resistance","Monster Buffs"),
            ["MapMonsterChain"]                   = new("Monsters Chain",             "Monster Buffs"),
            ["MapMonsterMultipleProjectiles"]     = new("Extra Projectiles",          "Monster Buffs"),
            ["MapMonsterUnwavering"]              = new("Cannot be Stunned",          "Monster Buffs"),
            ["MapMonsterCannotBeStunned"]         = new("Cannot be Stunned (v2)",     "Monster Buffs"),
            ["MapMonstersReflectCurses"]          = new("Reflect Curses",             "Monster Buffs"),
            ["MapMonsterManaLeechResistance"]     = new("Cannot Leech Mana",          "Monster Buffs"),
            ["MapMonstersStealChargesOnHit"]      = new("Steal Charges on Hit",       "Monster Buffs"),
            ["MapMonstersRemoveCharges"]          = new("Remove Charges",             "Monster Buffs"),
            ["MapMonstersChanceToSuppressSpells"] = new("+20% Spell Suppress",        "Monster Buffs"),
            ["MapMonstersAllDamageAlwaysIgnites"] = new("All Damage Ignites",         "Monster Buffs"),
            ["MapMonstersAvoidAilments"]          = new("Avoid Ailments",             "Monster Buffs"),
            ["MapMonstersAvoidPoisonBleedBlind"]  = new("Avoid Poison/Bleed/Blind",   "Monster Buffs"),
            ["MapMonstersAilmentAvoidance"]       = new("Ailment Avoidance",          "Monster Buffs"),
            ["MapMonsterFreezeAndChillAvoidance"] = new("Avoid Freeze & Chill",       "Monster Buffs"),
            ["MapMonsterIgniteAvoidance"]         = new("Avoid Ignite",               "Monster Buffs"),
            ["MapMonsterShockAvoidance"]          = new("Avoid Shock",                "Monster Buffs"),
            ["MapMonsterAilmentAvoidance"]        = new("Ailment Avoidance (v2)",     "Monster Buffs"),
            ["MapMonsterArea"]                    = new("Increased AoE",              "Monster Buffs"),
            ["MapMonsterFracturing"]              = new("Fracture",                   "Monster Buffs"),
            ["MapMonstersMaximumLifeAddedEnergyShield"] = new("Life as Extra ES",     "Monster Buffs"),
            ["MapMonstersResistPenetration"]      = new("Damage Penetrates Resistances","Monster Buffs"),
            ["MapMonstersHinderOnHit"]            = new("Hinder on Hit",              "Monster Buffs"),
            ["MapMonstersMaimOnHit"]              = new("Maim on Hit",                "Monster Buffs"),
            ["MapMonstersImpaleOnHit"]            = new("Impale on Hit",              "Monster Buffs"),
            ["MapMonstersBlindOnHit"]             = new("Blind on Hit",               "Monster Buffs"),
            ["MapMonsterAttacksApplyRandomCurses"]= new("Apply Random Hexes",         "Monster Buffs"),
            ["MapMonstersCantBeSlowedOrTaunted"]  = new("Cannot be Slowed/Taunted",   "Monster Buffs"),

            // ── Content / League Mods ────────────────────────────────────────────────────
            ["MapBeyondLeague"]                   = new("Beyond",                     "Content"),
            ["MapBloodlinesModOnMagics"]          = new("Bloodlines",                 "Content"),
            ["MapNemesisModOnRares"]              = new("Nemesis",                    "Content"),
            ["MapMultipleExiles"]                 = new("Rogue Exiles",               "Content"),
            ["MapTwoBosses"]                      = new("Two Bosses",                 "Content"),
            ["MapTotems"]                         = new("Totems",                     "Content"),
            ["MapPoisoning"]                      = new("Monsters Poison on Hit",     "Content"),
            ["MapBossPossessed"]                  = new("Boss Possessed",             "Content"),

            // ── Ground Effects ───────────────────────────────────────────────────────────
            ["MapBurningGround"]                  = new("Burning Ground",             "Ground Effects"),
            ["MapChilledGround"]                  = new("Chilled Ground",             "Ground Effects"),
            ["MapDesecratedGround"]               = new("Desecrated Ground",          "Ground Effects"),
            ["MapShockedGround"]                  = new("Shocked Ground",             "Ground Effects"),
            ["MapConsecratedGround"]              = new("Consecrated Ground",         "Ground Effects"),
        };
    }
}
