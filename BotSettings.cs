using ExileCore.PoEMemory;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using AutoExile.Mechanics;
using AutoExile.Systems;
using System.Windows.Forms;

namespace AutoExile
{
    public class BotSettings : ISettings
    {
        public ToggleNode Enable { get; set; } = new ToggleNode(false);
        public ToggleNode Running { get; set; } = new ToggleNode(false);
        public HotkeyNode ToggleRunning { get; set; } = new HotkeyNode(Keys.Insert);

        [Menu("Test Map Explore", "Hotkey to start/restart map exploration test (navigate to beacon, fight, explore 70%).")]
        public HotkeyNode TestMapExplore { get; set; } = new HotkeyNode(Keys.F5);

        [Menu("Dump Game State", "Hotkey to dump terrain, exploration, and pathfinding data to image + JSON files.")]
        public HotkeyNode DumpGameState { get; set; } = new HotkeyNode(Keys.F6);

        [Menu("Active Mode", "Bot mode to run. Persists across reloads.")]
        public ListNode ActiveMode { get; set; } = new ListNode() { Value = "Idle" };

        [Menu("Action Cooldown (ms)", "Minimum time between mouse actions. Prevents server kicks from input spam.")]
        public RangeNode<int> ActionCooldownMs { get; set; } = new RangeNode<int>(75, 50, 300);

        [Menu("Auto Level Gems", "Automatically level up skill gems when the level-up panel appears.")]
        public ToggleNode AutoLevelGems { get; set; } = new ToggleNode(true);

        [Menu("Auto Apply Incubators", "Apply incubators from stash to equipment during stash phase.")]
        public ToggleNode AutoApplyIncubators { get; set; } = new ToggleNode(true);

        // --- Build (player setup: movement, skills, combat, flasks) ---

        public BuildSettings Build { get; set; } = new BuildSettings();

        // --- Loot ---

        public LootSettings Loot { get; set; } = new LootSettings();

        // --- Follower (mode-specific) ---

        public FollowerSettings Follower { get; set; } = new FollowerSettings();

        // --- Blight (mode-specific) ---

        public BlightSettings Blight { get; set; } = new BlightSettings();

        // --- Simulacrum (mode-specific) ---

        public SimulacrumSettings Simulacrum { get; set; } = new SimulacrumSettings();

        // --- In-map Mechanics ---

        public MechanicsSettings Mechanics { get; set; } = new MechanicsSettings();

        // =====================================================================
        // Submenu classes
        // =====================================================================

        [Submenu(CollapsedByDefault = false)]
        public class BuildSettings
        {
            // ── Movement ──

            [Menu("Blink Range", "Max grid distance for gap-jumping with movement skills. Gaps wider than this won't be attempted.")]
            public RangeNode<int> BlinkRange { get; set; } = new RangeNode<int>(25, 5, 50);

            // ── Skill Slots ──
            // Configure each skill on your bar: what key it's bound to, what role it plays,
            // and its priority (higher = checked first during combat).
            // One slot should be PrimaryMovement (your Move Only key).
            // Movement skills (dash/blink) use the MovementSkill role.

            public SkillSlotConfig Skill1 { get; set; } = new SkillSlotConfig(Keys.T, SkillRole.PrimaryMovement);
            public SkillSlotConfig Skill2 { get; set; } = new SkillSlotConfig(Keys.Q);
            public SkillSlotConfig Skill3 { get; set; } = new SkillSlotConfig(Keys.W);
            public SkillSlotConfig Skill4 { get; set; } = new SkillSlotConfig(Keys.E);
            public SkillSlotConfig Skill5 { get; set; } = new SkillSlotConfig(Keys.R);
            public SkillSlotConfig Skill6 { get; set; } = new SkillSlotConfig(Keys.None);

            /// <summary>All configured skill slots.</summary>
            public IEnumerable<SkillSlotConfig> AllSkillSlots => new[] { Skill1, Skill2, Skill3, Skill4, Skill5, Skill6 };

            /// <summary>Find the first skill slot with PrimaryMovement role, or null.</summary>
            public SkillSlotConfig? GetPrimaryMovement()
            {
                foreach (var slot in AllSkillSlots)
                {
                    if (slot.Key.Value != Keys.None && slot.Role.Value == SkillRole.PrimaryMovement.ToString())
                        return slot;
                }
                return null;
            }

            /// <summary>Find all movement skills (dash/blink), ordered by priority.</summary>
            public List<SkillSlotConfig> GetMovementSkills()
            {
                var result = new List<SkillSlotConfig>();
                foreach (var slot in AllSkillSlots)
                {
                    if (slot.Key.Value != Keys.None && slot.Role.Value == SkillRole.MovementSkill.ToString())
                        result.Add(slot);
                }
                result.Sort((a, b) => b.Priority.Value.CompareTo(a.Priority.Value));
                return result;
            }

            // ── Combat Behavior ──

            [Menu("Default Positioning", "How to position relative to monsters. Modes can override.")]
            public ListNode DefaultPositioning { get; set; } = new ListNode();

            [Menu("Corpse Search Radius", "Max grid distance to find corpses for offering skills.")]
            public RangeNode<float> CorpseSearchRadius { get; set; } = new RangeNode<float>(60f, 10f, 120f);

            // ── Positioning Ranges ──

            [Menu("Melee Range", "Move within this grid distance for aggressive positioning.")]
            public RangeNode<float> MeleeRange { get; set; } = new RangeNode<float>(15f, 5f, 40f);

            [Menu("Orbit Range", "Maintain this grid distance for summoner/orbit positioning.")]
            public RangeNode<float> OrbitRange { get; set; } = new RangeNode<float>(40f, 15f, 80f);

            [Menu("Ranged Range", "Stay at least this grid distance for ranged positioning.")]
            public RangeNode<float> RangedRange { get; set; } = new RangeNode<float>(60f, 20f, 120f);

            // ── Guard / Defensive Thresholds ──

            [Menu("Guard HP Threshold", "Use guard skill when HP drops below this (0-1).")]
            public RangeNode<float> GuardHpThreshold { get; set; } = new RangeNode<float>(0.7f, 0.1f, 1.0f);

            [Menu("Guard ES Threshold", "Use guard skill when ES drops below this (0-1). Only if char has ES.")]
            public RangeNode<float> GuardEsThreshold { get; set; } = new RangeNode<float>(0.5f, 0.1f, 1.0f);

            // ── Vaal ──

            [Menu("Vaal Min Monsters", "Min nearby monsters to use vaal skills.")]
            public RangeNode<int> VaalMinMonsters { get; set; } = new RangeNode<int>(5, 1, 30);

            // ── Summon ──

            [Menu("Summon Expected Count", "Expected deployed count for summon skills. Recast if fewer.")]
            public RangeNode<int> SummonExpectedCount { get; set; } = new RangeNode<int>(1, 1, 20);

            // ── Flasks ──

            [Menu("Flasks Enabled", "Enable automatic flask usage.")]
            public ToggleNode FlasksEnabled { get; set; } = new ToggleNode(true);

            [Menu("Life Flask Slot (0=none)", "Flask slot for life flask (1-5, 0 to disable).")]
            public RangeNode<int> LifeFlaskSlot { get; set; } = new RangeNode<int>(1, 0, 5);

            [Menu("Life Flask HP Threshold", "Use life flask when HP below this (0-1).")]
            public RangeNode<float> LifeFlaskHpThreshold { get; set; } = new RangeNode<float>(0.5f, 0.1f, 0.9f);

            [Menu("Mana Flask Slot (0=none)", "Flask slot for mana flask (1-5, 0 to disable).")]
            public RangeNode<int> ManaFlaskSlot { get; set; } = new RangeNode<int>(0, 0, 5);

            [Menu("Mana Flask Threshold", "Use mana flask when mana below this (0-1).")]
            public RangeNode<float> ManaFlaskManaThreshold { get; set; } = new RangeNode<float>(0.3f, 0.1f, 0.9f);

            [Menu("Utility Flask Interval (ms)", "How often to press utility flasks during combat.")]
            public RangeNode<int> UtilityFlaskIntervalMs { get; set; } = new RangeNode<int>(5000, 1000, 30000);

            public BuildSettings()
            {
                DefaultPositioning.SetListValues(Enum.GetNames<CombatPositioning>().ToList());
                DefaultPositioning.Value = CombatPositioning.Aggressive.ToString();
            }
        }

        [Submenu(CollapsedByDefault = true)]
        public class SkillSlotConfig
        {
            public SkillSlotConfig() : this(Keys.None) { }

            public SkillSlotConfig(Keys defaultKey, SkillRole defaultRole = SkillRole.Disabled)
            {
                Key = new HotkeyNode(defaultKey);
                Role.SetListValues(Enum.GetNames<SkillRole>().ToList());
                Role.Value = defaultRole.ToString();
                TargetFilter.SetListValues(Enum.GetNames<SkillTargetFilter>().ToList());
                TargetFilter.Value = SkillTargetFilter.Any.ToString();
            }

            [Menu("Key", "Keyboard key bound to this skill slot in-game.")]
            public HotkeyNode Key { get; set; } = new HotkeyNode(Keys.None);

            [Menu("Role", "Where to aim: Enemy (at target), Corpse (at corpse), Self (no cursor). PrimaryMovement/MovementSkill for navigation.")]
            public ListNode Role { get; set; } = new ListNode();

            [Menu("Priority", "Execution priority (higher = checked first). 10=highest.")]
            public RangeNode<int> Priority { get; set; } = new RangeNode<int>(5, 0, 10);

            [Menu("Can Cross Terrain", "MovementSkill only: can this skill blink/jump across gaps?")]
            public ToggleNode CanCrossTerrain { get; set; } = new ToggleNode(false);

            // ── Skill Conditions ──
            // All "when to fire" logic is configured here. The Role only controls cursor targeting.

            [Menu("Target Filter", "Restrict skill to certain monster rarities.")]
            public ListNode TargetFilter { get; set; } = new ListNode();

            [Menu("Min Nearby Enemies", "Only use when at least this many enemies are within engage radius. 0=disabled.")]
            public RangeNode<int> MinNearbyEnemies { get; set; } = new RangeNode<int>(0, 0, 30);

            [Menu("Max Target Range", "Only use when target is within this grid distance. 0=disabled (use engage radius).")]
            public RangeNode<float> MaxTargetRange { get; set; } = new RangeNode<float>(0f, 0f, 200f);

            [Menu("Only When Buff Missing", "Skip if buff/debuff is already active — checks player buffs (Self) or target debuffs (Enemy).")]
            public ToggleNode OnlyWhenBuffMissing { get; set; } = new ToggleNode(false);

            [Menu("Only On Low Life", "Only use when HP is below guard threshold.")]
            public ToggleNode OnlyOnLowLife { get; set; } = new ToggleNode(false);

            [Menu("Summon Recast", "Recast when deployed count below Summon Expected Count. For summon/minion skills.")]
            public ToggleNode SummonRecast { get; set; } = new ToggleNode(false);

            [Menu("Min Cast Interval (ms)", "Minimum time between casts of this skill in milliseconds. 0=no limit. Prevents debuffs from overriding primary attacks.")]
            public RangeNode<int> MinCastIntervalMs { get; set; } = new RangeNode<int>(0, 0, 10000);

            [Menu("Buff/Debuff Name", "Name to match in buff list (substring, case-insensitive). Used with OnlyWhenBuffMissing to check player buffs or target debuffs. Use Scan to discover.")]
            public TextNode BuffDebuffName { get; set; } = new TextNode("");
        }

        [Submenu(CollapsedByDefault = true)]
        public class FollowerSettings
        {
            [Menu("Leader Name", "Character name to follow.")]
            public TextNode LeaderName { get; set; } = new TextNode("");

            [Menu("Follow Distance", "Start following when leader is farther than this (grid units).")]
            public RangeNode<float> FollowDistance { get; set; } = new RangeNode<float>(28f, 5f, 100f);

            [Menu("Stop Distance", "Stop moving when within this distance of leader (grid units).")]
            public RangeNode<float> StopDistance { get; set; } = new RangeNode<float>(14f, 3f, 50f);

            [Menu("Follow Through Transitions", "Follow leader through area transitions.")]
            public ToggleNode FollowThroughTransitions { get; set; } = new ToggleNode(false);
        }

        [Submenu(CollapsedByDefault = true)]
        public class LootSettings
        {
            [Menu("Loot Radius", "Click items within this grid distance without pathing. Items beyond this require navigation first.")]
            public RangeNode<float> LootRadius { get; set; } = new RangeNode<float>(35f, 10f, 80f);

            [Menu("Skip Low-Value Uniques", "Skip unique items below the minimum chaos value.")]
            public ToggleNode SkipLowValueUniques { get; set; } = new ToggleNode(false);

            [Menu("Min Unique Chaos Value", "Minimum chaos value for a unique to be picked up.")]
            public RangeNode<float> MinUniqueChaosValue { get; set; } = new RangeNode<float>(10f, 1f, 100f);

            [Menu("Min Chaos Per Slot (0=off)", "Minimum chaos value per inventory slot. 0 to disable.")]
            public RangeNode<float> MinChaosPerSlot { get; set; } = new RangeNode<float>(0f, 0f, 10f);

            [Menu("Ignore Quest Items", "Skip quest items (heist contracts, etc.) during loot pickup.")]
            public ToggleNode IgnoreQuestItems { get; set; } = new ToggleNode(true);
        }

        [Submenu(CollapsedByDefault = true)]
        public class SimulacrumSettings
        {
            [Menu("Max Deaths", "Abandon run after this many deaths. Start new simulacrum.")]
            public RangeNode<int> MaxDeaths { get; set; } = new RangeNode<int>(3, 1, 10);

            [Menu("Min Wave Delay (s)", "Minimum seconds to wait between wave end and next wave start.")]
            public RangeNode<float> MinWaveDelaySeconds { get; set; } = new RangeNode<float>(5f, 1f, 30f);

            [Menu("Wave Timeout (min)", "Max minutes per wave before abandoning the run.")]
            public RangeNode<float> WaveTimeoutMinutes { get; set; } = new RangeNode<float>(3f, 1f, 10f);

            [Menu("Stash Item Threshold", "Stash items between waves when inventory has this many items.")]
            public RangeNode<int> StashItemThreshold { get; set; } = new RangeNode<int>(5, 1, 30);

            [Menu("Stash Cooldown (ms)", "Delay between each Ctrl+click when stashing items.")]
            public RangeNode<int> StashItemCooldownMs { get; set; } = new RangeNode<int>(450, 200, 1000);
        }

        [Submenu(CollapsedByDefault = true)]
        public class BlightSettings
        {
            [Menu("Tower Build Radius", "Max grid distance from pump to consider foundations for building.")]
            public RangeNode<float> TowerBuildRadius { get; set; } = new RangeNode<float>(90f, 40f, 200f);

            [Menu("Tower Build Cooldown (ms)", "Min time between starting new tower builds (not clicks).")]
            public RangeNode<int> TowerBuildCooldownMs { get; set; } = new RangeNode<int>(3000, 500, 10000);

            [Menu("Tower Click Cooldown (ms)", "Min time between individual click actions (label, menu button).")]
            public RangeNode<int> TowerClickCooldownMs { get; set; } = new RangeNode<int>(200, 50, 1000);

            [Menu("Tower Approach Distance", "Navigate this close to towers before clicking (grid units).")]
            public RangeNode<float> TowerApproachDistance { get; set; } = new RangeNode<float>(40f, 10f, 60f);

            [Menu("Sweep Delay After Timer (s)", "Wait this long after timer ends before starting sweep (mobs still spawning).")]
            public RangeNode<float> SweepDelayAfterTimerSeconds { get; set; } = new RangeNode<float>(30f, 5f, 60f);

            [Menu("Sweep Timeout (s)", "Global max time in sweep phase before giving up.")]
            public RangeNode<float> SweepTimeoutSeconds { get; set; } = new RangeNode<float>(120f, 30f, 300f);

            [Menu("Sweep Patrol Lane Timeout (s)", "Max time per lane during patrol.")]
            public RangeNode<float> SweepPatrolLaneTimeoutSeconds { get; set; } = new RangeNode<float>(30f, 10f, 60f);

            [Menu("Sweep Pump Leash Radius", "Soft max grid distance from pump during patrol.")]
            public RangeNode<float> SweepPumpLeashRadius { get; set; } = new RangeNode<float>(60f, 28f, 138f);

            [Menu("Sweep Pump Danger Radius", "Rush back if monster this close to pump (grid units).")]
            public RangeNode<float> SweepPumpDangerRadius { get; set; } = new RangeNode<float>(28f, 9f, 55f);

            [Menu("Stash Item Cooldown (ms)", "Delay between each Ctrl+click when stashing items.")]
            public RangeNode<int> StashItemCooldownMs { get; set; } = new RangeNode<int>(450, 200, 1000);

            public TowerTypeSettings Chilling { get; set; } = new TowerTypeSettings(5, canStack: false, tier3Branch: "None");
            public TowerTypeSettings Fireball { get; set; } = new TowerTypeSettings(4, canStack: true, tier3Branch: "Left");
            public TowerTypeSettings Empowering { get; set; } = new TowerTypeSettings(3, requiresNearbyTower: true, tier3Branch: "Left");
            public TowerTypeSettings Seismic { get; set; } = new TowerTypeSettings(2);
            public TowerTypeSettings Minion { get; set; } = new TowerTypeSettings(0);
            public TowerTypeSettings ShockNova { get; set; } = new TowerTypeSettings(0);

            public TowerTypeSettings GetTowerConfig(string type) => type?.ToLowerInvariant() switch
            {
                "chilling" => Chilling,
                "seismic" => Seismic,
                "empowering" => Empowering,
                "fireball" => Fireball,
                "minion" => Minion,
                "shocknova" => ShockNova,
                _ => new TowerTypeSettings(0),
            };

            public List<(string Name, TowerTypeSettings Config)> GetPriorityOrder()
            {
                var all = new List<(string Name, TowerTypeSettings Config)>
                {
                    ("Chilling", Chilling), ("Seismic", Seismic), ("Empowering", Empowering),
                    ("Fireball", Fireball), ("Minion", Minion), ("ShockNova", ShockNova),
                };
                all.RemoveAll(t => t.Config.Priority.Value <= 0);
                all.Sort((a, b) => b.Config.Priority.Value.CompareTo(a.Config.Priority.Value));
                return all;
            }
        }

        [Submenu(CollapsedByDefault = true)]
        public class TowerTypeSettings
        {
            public TowerTypeSettings() { InitBranch(); }

            public TowerTypeSettings(int defaultPriority, bool canStack = false, bool requiresNearbyTower = false, string tier3Branch = "Left")
            {
                Priority = new RangeNode<int>(defaultPriority, 0, 5);
                CanStack = new ToggleNode(canStack);
                RequiresNearbyTower = new ToggleNode(requiresNearbyTower);
                InitBranch();
                Tier3Branch.Value = tier3Branch;
            }

            private void InitBranch()
            {
                Tier3Branch.SetListValues(new List<string> { "None", "Left", "Right" });
                if (string.IsNullOrEmpty(Tier3Branch.Value))
                    Tier3Branch.Value = "Left";
            }

            [Menu("Priority", "Build priority (0=never build, 5=highest).")]
            public RangeNode<int> Priority { get; set; } = new RangeNode<int>(3, 0, 5);

            [Menu("Can Stack", "Allow multiple of this tower type within effect radius.")]
            public ToggleNode CanStack { get; set; } = new ToggleNode(false);

            [Menu("Requires Nearby Tower", "Only build if other towers exist within effect radius to benefit from.")]
            public ToggleNode RequiresNearbyTower { get; set; } = new ToggleNode(false);

            [Menu("Tier 3 Branch", "Final upgrade path (None=stop at tier 3, Left, Right).")]
            public ListNode Tier3Branch { get; set; } = new ListNode() { Value = "Left" };
        }

        [Submenu(CollapsedByDefault = true)]
        public class MechanicsSettings
        {
            public UltimatumMechanicSettings Ultimatum { get; set; } = new UltimatumMechanicSettings();
        }

        [Submenu(CollapsedByDefault = false)]
        public class UltimatumMechanicSettings
        {
            public UltimatumMechanicSettings()
            {
                Mode.SetListValues(Enum.GetNames<MechanicMode>().ToList());
                Mode.Value = MechanicMode.Optional.ToString();
            }

            [Menu("Mode", "Skip=ignore, Optional=do if found, Required=must complete for map done.")]
            public ListNode Mode { get; set; } = new ListNode();

            // ── Encounter types ──

            [Menu("Do Survive", "Complete Survive encounters.")]
            public ToggleNode DoSurvive { get; set; } = new ToggleNode(true);

            [Menu("Do Kill Enemies", "Complete Kill Enemies encounters.")]
            public ToggleNode DoKillEnemies { get; set; } = new ToggleNode(true);

            [Menu("Do Defend Altar", "Complete Defend the Altar encounters.")]
            public ToggleNode DoDefendAltar { get; set; } = new ToggleNode(true);

            [Menu("Do Stand in Circles", "Complete Stand in the Circles encounters (requires positional logic).")]
            public ToggleNode DoStandInCircles { get; set; } = new ToggleNode(false);

            // ── Risk management ──

            [Menu("Max Waves", "Maximum wave to push to before taking rewards.")]
            public RangeNode<int> MaxWaves { get; set; } = new RangeNode<int>(10, 1, 10);

            [Menu("Danger Threshold", "Take rewards when cumulative modifier danger exceeds this. Higher = more risk tolerance. Each mod is 1-5 danger, so 30 ≈ 10 medium mods.")]
            public RangeNode<int> DangerThreshold { get; set; } = new RangeNode<int>(30, 5, 100);

            [Menu("Min Secure Value (chaos)", "Take rewards early when accumulated reward value (via NinjaPrice) exceeds this chaos threshold. 0 = disabled (only danger/wave limits apply).")]
            public RangeNode<int> MinSecureValue { get; set; } = new RangeNode<int>(0, 0, 500);

            // ── Positioning ──

            [Menu("Orbit Radius", "Stay within this grid distance of the altar during combat. Halved when limited area mod is active.")]
            public RangeNode<float> OrbitRadius { get; set; } = new RangeNode<float>(50f, 10f, 80f);

            // ── Modifier danger overrides ──
            public UltimatumModRanking ModRanking { get; set; } = new();

            /// <summary>
            /// Get the effective danger rating for a modifier (user override > default > 3).
            /// </summary>
            public int GetModDanger(string modId)
            {
                return UltimatumModDanger.GetDanger(modId, ModRanking.DangerOverrides);
            }
        }

        [Submenu(RenderMethod = nameof(Render))]
        public class UltimatumModRanking
        {
            // Key = modifier Id, Value = danger tier (1-5 or 99=SKIP)
            public Dictionary<string, int> DangerOverrides { get; set; } = new();
            private string _filter = "";

            private static readonly string[] TierLabels = { "Free", "Easy", "Medium", "Hard", "Very Hard", "SKIP" };
            private static readonly int[] TierValues = { 0, 1, 3, 5, 10, UltimatumModDanger.BlockedValue };

            private static int ValueToComboIndex(int value) => value switch
            {
                0 => 0,
                1 => 1,
                2 or 3 => 2,
                4 or 5 => 3,
                >= 6 and < UltimatumModDanger.BlockedValue => 4,
                >= UltimatumModDanger.BlockedValue => 5,
                _ => 2,
            };

            public void Render()
            {
                ImGui.TextWrapped("Rate each modifier: Free (trivial) → Very Hard (deadly) → SKIP (never accept)");
                ImGui.Separator();
                ImGui.InputTextWithHint("##ModFilter", "Filter mods...", ref _filter, 100);
                ImGui.Separator();

                // Try to load full mod list from game files, fall back to known defaults
                var modEntries = new List<(string Id, string DisplayName)>();
                try
                {
                    var fileMods = RemoteMemoryObject.pTheGame.Files.UltimatumModifiers.EntriesList;
                    if (fileMods != null)
                    {
                        foreach (var m in fileMods)
                        {
                            var display = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name;
                            modEntries.Add((m.Id, display));
                        }
                    }
                }
                catch { }

                // Fallback: use known defaults + any overrides
                if (modEntries.Count == 0)
                {
                    var allIds = new HashSet<string>(UltimatumModDanger.Defaults.Keys);
                    foreach (var k in DangerOverrides.Keys) allIds.Add(k);
                    foreach (var id in allIds.OrderBy(x => x))
                        modEntries.Add((id, id));
                }

                foreach (var (modId, displayName) in modEntries)
                {
                    if (!string.IsNullOrEmpty(_filter) &&
                        !displayName.Contains(_filter, StringComparison.OrdinalIgnoreCase) &&
                        !modId.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var currentValue = UltimatumModDanger.GetDanger(modId, DangerOverrides);
                    var comboIndex = ValueToComboIndex(currentValue);
                    var isOverridden = DangerOverrides.ContainsKey(modId);
                    var label = isOverridden ? $"{displayName} *" : displayName;

                    ImGui.PushItemWidth(120);
                    if (ImGui.Combo($"{label}###{modId}", ref comboIndex, TierLabels, TierLabels.Length))
                    {
                        var newValue = TierValues[comboIndex];
                        if (UltimatumModDanger.Defaults.TryGetValue(modId, out var def) && newValue == def)
                            DangerOverrides.Remove(modId);
                        else
                            DangerOverrides[modId] = newValue;
                    }
                    ImGui.PopItemWidth();
                }
            }
        }
    }
}
