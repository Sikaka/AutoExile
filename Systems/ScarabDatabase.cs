namespace AutoExile.Systems
{
    /// <summary>
    /// Maps scarab display names to entity path suffixes for stash lookup.
    /// Scarab paths follow: Metadata/Items/Scarabs/{PathSuffix}
    /// Display names are the baseName from the item (what the user sees).
    /// </summary>
    public static class ScarabDatabase
    {
        /// <summary>Key = baseName (display), Value = path suffix after "Metadata/Items/Scarabs/".</summary>
        public static readonly Dictionary<string, string> NameToPath = new(StringComparer.OrdinalIgnoreCase)
        {
            // Divination
            ["Divination Scarab of The Cloister"] = "ScarabDivinationCardsNew1",
            ["Divination Scarab of Plenty"] = "ScarabDivinationCardsNew2",
            ["Divination Scarab of Pilfering"] = "ScarabDivinationCardsNew3",

            // Ritual
            ["Ritual Scarab of Selectiveness"] = "ScarabRitual1",
            ["Ritual Scarab of Wisps"] = "ScarabRitual2",
            ["Ritual Scarab of Corpses"] = "ScarabRitual4",

            // Ultimatum
            ["Ultimatum Scarab"] = "ScarabUltimatum1",
            ["Ultimatum Scarab of Bribing"] = "ScarabUltimatum2",
            ["Ultimatum Scarab of Dueling"] = "ScarabUltimatum3",
            ["Ultimatum Scarab of Inscription"] = "ScarabUltimatum5",

            // Harvest
            ["Harvest Scarab"] = "ScarabHarvest1",
            ["Harvest Scarab of Doubling"] = "ScarabHarvest2",

            // Essence
            ["Essence Scarab"] = "ScarabEssence1",
            ["Essence Scarab of Ascent"] = "ScarabEssence2",
            ["Essence Scarab of Stability"] = "ScarabEssence3",

            // Bestiary
            ["Bestiary Scarab"] = "ScarabBeastsNew1",
            ["Bestiary Scarab of the Herd"] = "ScarabBeastsNew2",
            ["Bestiary Scarab of Duplicating"] = "ScarabBeastsNew3",

            // Expedition
            ["Expedition Scarab"] = "ScarabExpedition1",
            ["Expedition Scarab of Runefinding"] = "ScarabExpedition2",
            ["Expedition Scarab of Verisium Powder"] = "ScarabExpedition3",
            ["Expedition Scarab of Infusion"] = "ScarabExpedition4",
            ["Expedition Scarab of Archaeology"] = "ScarabExpedition5",

            // Ambush (Strongbox)
            ["Ambush Scarab"] = "ScarabStrongboxNew1",
            ["Ambush Scarab of Hidden Compartments"] = "ScarabStrongboxNew2",
            ["Ambush Scarab of Potency"] = "ScarabStrongboxNew3",
            ["Ambush Scarab of Discernment"] = "ScarabStrongboxNew5",

            // Delirium
            ["Delirium Scarab"] = "ScarabDelirium1",
            ["Delirium Scarab of Neuroses"] = "ScarabDelirium4",
            ["Delirium Scarab of Delusions"] = "ScarabDelirium5",

            // Breach
            ["Breach Scarab of the Hive"] = "ScarabBreachNew1",
            ["Breach Scarab of Instability"] = "ScarabBreachNew3",
            ["Breach Scarab of the Marshal"] = "ScarabBreachNew4",

            // Legion
            ["Legion Scarab"] = "ScarabLegionNew1",
            ["Legion Scarab of Officers"] = "ScarabLegionNew2",
            ["Legion Scarab of Treasures"] = "ScarabLegionNew3",
            ["Legion Scarab of Eternal Conflict"] = "ScarabLegionNew5",

            // Abyss
            ["Abyss Scarab"] = "ScarabAbyssNew1",
            ["Abyss Scarab of Multitudes"] = "ScarabAbyssNew2",
            ["Abyss Scarab of Descending"] = "ScarabAbyssNew3",
            ["Abyss Scarab of Edifice"] = "ScarabAbyssNew4",

            // Domination (Shrines)
            ["Domination Scarab"] = "ScarabDomination1",
            ["Domination Scarab of Apparitions"] = "ScarabDomination2",
            ["Domination Scarab of Evolution"] = "ScarabDomination3",

            // Beyond
            ["Beyond Scarab"] = "ScarabBeyond1",
            ["Beyond Scarab of Haemophilia"] = "ScarabBeyond3",
            ["Beyond Scarab of Resurgence"] = "ScarabBeyond4",
            ["Beyond Scarab of the Invasion"] = "ScarabBeyond5",

            // Cartography
            ["Cartography Scarab of Escalation"] = "ScarabMapsNew1",
            ["Cartography Scarab of Corruption"] = "ScarabMapsNew4",
            ["Cartography Scarab of the Multitude"] = "ScarabMapsNew5",

            // Misc
            ["Scarab of Monstrous Lineage"] = "ScarabMisc1",
            ["Scarab of Adversaries"] = "ScarabMisc2",
            ["Scarab of Divinity"] = "ScarabMisc3",
            ["Scarab of Stability"] = "ScarabMisc5",
            ["Scarab of Wisps"] = "ScarabMisc8",
            ["Scarab of the Sinistral"] = "ScarabMisc9",
            ["Scarab of the Dextral"] = "ScarabMisc10",

            // Titanic (Unique)
            ["Titanic Scarab"] = "ScarabUniquesNew1",
            ["Titanic Scarab of Treasures"] = "ScarabUniquesNew2",
            ["Titanic Scarab of Legend"] = "ScarabUniquesNew3",

            // Anarchy (Rogue Exiles)
            ["Anarchy Scarab"] = "ScarabAnarchy1",
            ["Anarchy Scarab of Gigantification"] = "ScarabAnarchy2",
            ["Anarchy Scarab of Partnership"] = "ScarabAnarchy3",
            ["Anarchy Scarab of the Exceptional"] = "ScarabAnarchy4",

            // Torment
            ["Torment Scarab"] = "ScarabTormentNew1",
            ["Torment Scarab of Peculiarity"] = "ScarabTormentNew2",
            ["Torment Scarab of Possession"] = "ScarabTormentNew4",

            // Influence
            ["Influencing Scarab of the Shaper"] = "ScarabInfluenceNew1",
            ["Influencing Scarab of the Elder"] = "ScarabInfluenceNew2",
            ["Influencing Scarab of Hordes"] = "ScarabInfluenceNew3",
            ["Influencing Scarab of Interference"] = "ScarabInfluenceNew4",

            // Blight
            ["Blight Scarab"] = "ScarabBlightNew1",
            ["Blight Scarab of the Blightheart"] = "ScarabBlightNew3",
            ["Blight Scarab of Blooming"] = "ScarabBlightNew4",
            ["Blight Scarab of Invigoration"] = "ScarabBlightNew5",

            // Betrayal
            ["Betrayal Scarab"] = "ScarabBetrayal1",
            ["Betrayal Scarab of the Allflame"] = "ScarabBetrayal2",
            ["Betrayal Scarab of Reinforcements"] = "ScarabBetrayal3",
            ["Betrayal Scarab of Unbreaking"] = "ScarabBetrayal4",

            // Sulphite (Delve)
            ["Sulphite Scarab"] = "ScarabSulphiteNew1",
            ["Sulphite Scarab of Fumes"] = "ScarabSulphiteNew3",

            // Incursion
            ["Incursion Scarab"] = "ScarabIncursion1",
            ["Incursion Scarab of Invasion"] = "ScarabIncursion2",
            ["Incursion Scarab of Champions"] = "ScarabIncursion3",
            ["Incursion Scarab of Timelines"] = "ScarabIncursion4",

            // Kalguuran (Settlers)
            ["Kalguuran Scarab"] = "ScarabSettlers1",
            ["Kalguuran Scarab of Guarded Riches"] = "ScarabSettlers2",

            // Horned (Uber)
            ["Horned Scarab of Nemeses"] = "ScarabUber2",
            ["Horned Scarab of Glittering"] = "ScarabUber6",
        };

        /// <summary>Get the full entity path for a scarab by display name.</summary>
        public static string? GetPath(string displayName)
        {
            if (NameToPath.TryGetValue(displayName, out var suffix))
                return $"Metadata/Items/Scarabs/{suffix}";
            return null;
        }

        /// <summary>Check if a stash item matches a scarab by display name.</summary>
        public static bool Matches(string itemPath, string displayName)
        {
            if (!NameToPath.TryGetValue(displayName, out var suffix)) return false;
            return itemPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
