using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using Pathfinding = AutoExile.Systems.Pathfinding;

namespace AutoExile.Mechanics
{
    public enum EssencePhase
    {
        Idle,
        NavigateToMonolith,
        Corrupt,
        ClickToRelease,
        WaitForRelease,
        Fighting,
        Looting,
        Complete,
        Abandoned,
        Failed,
    }

    /// <summary>
    /// Essence encounter mechanic handler.
    ///
    /// Lifecycle:
    ///   Detect monolith → Navigate → (optional) Corrupt via Vaal Orb button →
    ///   Click monolith to release → Fight → Loot → Complete
    ///
    /// Entity structure:
    ///   - Monolith: Metadata/MiscellaneousObjects/Monolith, EntityType.Monolith, IsTargetable, has Monolith component
    ///   - MiniMonolith: individual crystals (ignored)
    ///   - Trapped monster: Monster at same position, Stats: MonsterInsideMonolith=1, CannotBeDamaged=1
    ///
    /// Ground label structure:
    ///   Child[0]: Monster name
    ///   Child[1]: Info panel — children[2..N-2] are essence names
    ///   Child[2]: Corruption (Vaal Orb) button → [0] clickable container → [0] button icon
    /// </summary>
    public class EssenceMechanic : IMapMechanic
    {
        public string Name => "Essence";
        public string Status { get; private set; } = "";
        public Vector2? AnchorGridPos { get; private set; }
        public bool IsEncounterActive => _phase == EssencePhase.Fighting;
        public bool IsComplete => _phase is EssencePhase.Complete
                                       or EssencePhase.Abandoned
                                       or EssencePhase.Failed;
        public bool IsRepeatable => true;

        // ── Phase machine ──
        private EssencePhase _phase = EssencePhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;

        // ── Entity references ──
        private Entity? _monolith;
        private uint _monolithId;
        private Vector2 _monolithGridPos;

        // ── Encounter state ──
        private readonly List<string> _essenceNames = new();
        private int _highestTier;
        private bool _hasCorruptionTarget;
        private bool _corruptionAttempted;

        // ── Click state ──
        private DateTime _lastClickTime = DateTime.MinValue;
        private int _clickAttempts;
        private const float ClickCooldownMs = 500;
        private const float PhaseTimeoutSeconds = 30;

        // ── Combat tracking ──
        private DateTime _lastCombatTime;

        // ── Loot tracking ──
        private DateTime _lootStartTime;
        private DateTime _lastLootScan = DateTime.MinValue;
        private uint _pendingLootId;
        private string? _pendingLootName;
        private double _pendingLootValue;

        // ── Overlay state ──
        public EssencePhase Phase => _phase;
        public IReadOnlyList<string> EssenceNames => _essenceNames;
        public int HighestTier => _highestTier;

        // ── Essence tier data ──
        private static readonly string[] TierPrefixes =
        {
            "Whispering", "Muttering", "Weeping", "Wailing",
            "Screaming", "Shrieking", "Deafening"
        };

        private static readonly HashSet<string> CorruptionTargetEssences = new(StringComparer.OrdinalIgnoreCase)
        {
            "Misery", "Envy", "Dread", "Scorn"
        };

        // ══════════════════════════════════════════════════════════════
        // IMapMechanic
        // ══════════════════════════════════════════════════════════════

        public bool Detect(BotContext ctx)
        {
            if (_phase != EssencePhase.Idle) return true;

            var gc = ctx.Game;
            var settings = ctx.Settings.Mechanics.Essence;
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);

            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Path == null) continue;
                // Match Monolith but not MiniMonolith
                if (!entity.Path.Contains("MiscellaneousObjects/Monolith")) continue;
                if (entity.Path.Contains("MiniMonolith")) continue;
                if (!entity.IsTargetable) continue;

                // Check if monolith is already opened
                try
                {
                    var monolithComp = entity.GetComponent<Monolith>();
                    if (monolithComp?.IsOpened == true) continue;
                }
                catch { continue; }

                var dist = Vector2.Distance(playerGrid, entity.GridPosNum);
                if (dist > Pathfinding.NetworkBubbleRadius) continue;

                // Read essence names from ground label
                _essenceNames.Clear();
                _highestTier = 0;
                _hasCorruptionTarget = false;
                ReadEssencesFromLabel(gc, entity);

                // Evaluate tier filter
                if (_essenceNames.Count > 0)
                {
                    var minTier = ParseMinTier(settings.MinEssenceTier.Value);
                    if (minTier > 0 && _highestTier < minTier)
                    {
                        // Below minimum tier — skip silently
                        continue;
                    }
                }

                _monolith = entity;
                _monolithId = entity.Id;
                _monolithGridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);
                AnchorGridPos = _monolithGridPos;

                ctx.Log($"[Essence] Detected monolith at ({_monolithGridPos.X:F0}, {_monolithGridPos.Y:F0}) — " +
                        $"{_essenceNames.Count} essences, tier {_highestTier}, corruption target: {_hasCorruptionTarget}");
                return true;
            }

            return false;
        }

        public MechanicResult Tick(BotContext ctx)
        {
            RefreshMonolith(ctx);

            // Phase timeout
            if (_phase != EssencePhase.Idle && _phase != EssencePhase.Complete &&
                _phase != EssencePhase.Abandoned && _phase != EssencePhase.Failed)
            {
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > PhaseTimeoutSeconds)
                {
                    ctx.Log($"[Essence] Phase {_phase} timed out after {PhaseTimeoutSeconds}s");
                    Status = $"Timed out in {_phase}";
                    _phase = EssencePhase.Failed;
                    return MechanicResult.Failed;
                }
            }

            return _phase switch
            {
                EssencePhase.Idle => TickIdle(ctx),
                EssencePhase.NavigateToMonolith => TickNavigateToMonolith(ctx),
                EssencePhase.Corrupt => TickCorrupt(ctx),
                EssencePhase.ClickToRelease => TickClickToRelease(ctx),
                EssencePhase.WaitForRelease => TickWaitForRelease(ctx),
                EssencePhase.Fighting => TickFighting(ctx),
                EssencePhase.Looting => TickLooting(ctx),
                EssencePhase.Complete or EssencePhase.Abandoned or EssencePhase.Failed
                    => _phase == EssencePhase.Complete ? MechanicResult.Complete
                     : _phase == EssencePhase.Abandoned ? MechanicResult.Abandoned
                     : MechanicResult.Failed,
                _ => MechanicResult.Idle,
            };
        }

        public void Reset()
        {
            _phase = EssencePhase.Idle;
            _monolith = null;
            _monolithId = 0;
            AnchorGridPos = null;
            Status = "";
            _essenceNames.Clear();
            _highestTier = 0;
            _hasCorruptionTarget = false;
            _corruptionAttempted = false;
            _clickAttempts = 0;
            _pendingLootId = 0;
            _pendingLootName = null;
        }

        // ══════════════════════════════════════════════════════════════
        // Phase handlers
        // ══════════════════════════════════════════════════════════════

        private MechanicResult TickIdle(BotContext ctx)
        {
            if (_monolith == null) return MechanicResult.Idle;

            var settings = ctx.Settings.Mechanics.Essence;

            // Decide whether to corrupt
            bool willCorrupt = settings.CorruptEssences.Value &&
                               _hasCorruptionTarget &&
                               !_corruptionAttempted;

            SetPhase(EssencePhase.NavigateToMonolith,
                     willCorrupt ? "Navigating to monolith (will corrupt)" : "Navigating to monolith");
            return MechanicResult.InProgress;
        }

        private MechanicResult TickNavigateToMonolith(BotContext ctx)
        {
            var gc = ctx.Game;
            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, _monolithGridPos);

            if (dist < 25)
            {
                var settings = ctx.Settings.Mechanics.Essence;
                bool willCorrupt = settings.CorruptEssences.Value &&
                                   _hasCorruptionTarget &&
                                   !_corruptionAttempted;

                if (willCorrupt)
                    SetPhase(EssencePhase.Corrupt, "In range, corrupting essences");
                else
                    SetPhase(EssencePhase.ClickToRelease, "In range, clicking to release");

                return MechanicResult.InProgress;
            }

            ctx.Navigation.NavigateTo(gc, _monolithGridPos);
            Status = $"[Nav] Walking to monolith ({dist:F0}g away)";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickCorrupt(BotContext ctx)
        {
            var gc = ctx.Game;

            if (_corruptionAttempted)
            {
                // Re-read essences after corruption
                _essenceNames.Clear();
                _highestTier = 0;
                _hasCorruptionTarget = false;
                if (_monolith != null)
                    ReadEssencesFromLabel(gc, _monolith);

                ctx.Log($"[Essence] Post-corruption essences: {string.Join(", ", _essenceNames)}");
                SetPhase(EssencePhase.ClickToRelease, "Corruption applied, clicking to release");
                return MechanicResult.InProgress;
            }

            if (!CanClick()) return MechanicResult.InProgress;

            // Find the Vaal Orb button on the monolith label
            var corruptButton = FindCorruptButton(gc);
            if (corruptButton == null)
            {
                ctx.Log("[Essence] Cannot find corruption button on label, skipping corruption");
                _corruptionAttempted = true;
                SetPhase(EssencePhase.ClickToRelease, "No corruption button, clicking to release");
                return MechanicResult.InProgress;
            }

            ClickElement(gc, corruptButton);
            _corruptionAttempted = true;
            _lastClickTime = DateTime.Now;
            _phaseStartTime = DateTime.Now; // Reset timeout to allow label re-read
            ctx.Log("[Essence] Clicked Vaal Orb corruption button");
            Status = "Clicked corruption, waiting for result...";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickClickToRelease(BotContext ctx)
        {
            var gc = ctx.Game;

            if (_monolith == null || !_monolith.IsTargetable)
            {
                SetPhase(EssencePhase.WaitForRelease, "Monolith no longer targetable");
                return MechanicResult.InProgress;
            }

            // Check if already opened
            try
            {
                var monolithComp = _monolith.GetComponent<Monolith>();
                if (monolithComp?.IsOpened == true)
                {
                    SetPhase(EssencePhase.WaitForRelease, "Monolith opened");
                    return MechanicResult.InProgress;
                }
            }
            catch { }

            if (!CanClick()) return MechanicResult.InProgress;

            if (_clickAttempts >= 10)
            {
                ctx.Log("[Essence] Too many click attempts on monolith");
                _phase = EssencePhase.Failed;
                Status = "Failed: too many click attempts";
                return MechanicResult.Failed;
            }

            // Click the monolith entity
            if (BotInput.ClickEntity(gc, _monolith))
            {
                _clickAttempts++;
                _lastClickTime = DateTime.Now;
                Status = $"Clicking monolith (attempt {_clickAttempts})";
                ctx.Log($"[Essence] Clicking monolith to release (attempt {_clickAttempts})");
            }

            return MechanicResult.InProgress;
        }

        private MechanicResult TickWaitForRelease(BotContext ctx)
        {
            var gc = ctx.Game;

            // Check if monster became damageable (monolith opened)
            bool monolithOpened = false;
            if (_monolith != null)
            {
                try
                {
                    var monolithComp = _monolith.GetComponent<Monolith>();
                    monolithOpened = monolithComp?.IsOpened == true;
                }
                catch { }

                // Also detect via targetable becoming false
                if (!_monolith.IsTargetable)
                    monolithOpened = true;
            }

            if (monolithOpened)
            {
                _lastCombatTime = DateTime.Now;
                SetPhase(EssencePhase.Fighting, "Monster released, fighting!");
                ctx.Log("[Essence] Monolith opened, monster released");
                return MechanicResult.InProgress;
            }

            // Also check if combat started (monsters nearby that are damageable)
            if (ctx.Combat.InCombat)
            {
                _lastCombatTime = DateTime.Now;
                SetPhase(EssencePhase.Fighting, "Combat detected, fighting!");
                ctx.Log("[Essence] Combat started (monster became damageable)");
                return MechanicResult.InProgress;
            }

            Status = "Waiting for monster release...";
            return MechanicResult.InProgress;
        }

        private MechanicResult TickFighting(BotContext ctx)
        {
            ctx.Combat.Tick(ctx);

            if (ctx.Combat.InCombat)
            {
                _lastCombatTime = DateTime.Now;
                Status = $"[Combat] Fighting essence monster ({ctx.Combat.NearbyMonsterCount} nearby)";
                return MechanicResult.InProgress;
            }

            // Wait 2 seconds after combat ends to confirm kill
            var timeSinceCombat = (DateTime.Now - _lastCombatTime).TotalSeconds;
            if (timeSinceCombat < 2.0)
            {
                Status = $"[Combat] Waiting for clear ({timeSinceCombat:F1}s)";
                return MechanicResult.InProgress;
            }

            // Combat over — transition to looting
            _lootStartTime = DateTime.Now;
            _lastLootScan = DateTime.MinValue;
            SetPhase(EssencePhase.Looting, "Monster killed, looting");
            ctx.Log("[Essence] Essence monster killed, starting loot sweep");
            return MechanicResult.InProgress;
        }

        private MechanicResult TickLooting(BotContext ctx)
        {
            var gc = ctx.Game;
            var settings = ctx.Settings.Mechanics.Essence;
            var elapsed = (DateTime.Now - _lootStartTime).TotalSeconds;

            // Run combat in case stragglers appear
            ctx.Combat.Tick(ctx);

            // Scan for loot periodically
            if ((DateTime.Now - _lastLootScan).TotalMilliseconds >= 500)
            {
                ctx.Loot.Scan(gc);
                _lastLootScan = DateTime.Now;
            }

            // Tick interaction system for pickup results
            if (ctx.Interaction.IsBusy)
            {
                var result = ctx.Interaction.Tick(gc);
                if (result == InteractionResult.Succeeded && _pendingLootName != null)
                {
                    ctx.LootTracker.RecordItem(_pendingLootName, _pendingLootValue, _pendingLootId);
                    if (_pendingLootId > 0)
                        ctx.Loot.MarkFailed(_pendingLootId, "picked up");
                    ctx.Log($"[Essence] Picked up: {_pendingLootName} ({_pendingLootValue:F0}c)");
                    _pendingLootName = null;
                }
                else if (result == InteractionResult.Failed && _pendingLootId > 0)
                {
                    ctx.Loot.MarkFailed(_pendingLootId);
                    _pendingLootName = null;
                }
                Status = $"Looting ({elapsed:F1}s)";
                return MechanicResult.InProgress;
            }

            // Pick up nearby items
            if (ctx.Loot.HasLootNearby)
            {
                var (wasInRadius, candidate) = ctx.Loot.PickupNext(ctx.Interaction, ctx.Navigation);
                if (candidate != null && ctx.Interaction.IsBusy)
                {
                    _pendingLootId = candidate.Entity.Id;
                    _pendingLootName = candidate.ItemName;
                    _pendingLootValue = candidate.ChaosValue;
                    Status = $"Picking up: {candidate.ItemName}";
                    // Reset loot timer when we find items
                    _lootStartTime = DateTime.Now;
                    return MechanicResult.InProgress;
                }
            }

            // Done looting after timeout with no items to pick up
            if (elapsed >= settings.LootSweepSeconds.Value)
            {
                SetPhase(EssencePhase.Complete, "Loot sweep done");
                ctx.Log("[Essence] Loot sweep complete, encounter done");
                return MechanicResult.Complete;
            }

            Status = $"Looting ({elapsed:F1}s / {settings.LootSweepSeconds.Value:F0}s)";
            return MechanicResult.InProgress;
        }

        // ══════════════════════════════════════════════════════════════
        // Entity management
        // ══════════════════════════════════════════════════════════════

        private void RefreshMonolith(BotContext ctx)
        {
            if (_monolithId == 0) return;

            var gc = ctx.Game;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Id == _monolithId)
                {
                    _monolith = entity;
                    return;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Label parsing
        // ══════════════════════════════════════════════════════════════

        private void ReadEssencesFromLabel(GameController gc, Entity monolith)
        {
            var label = FindMonolithLabel(gc, monolith);
            if (label == null) return;

            // Child[1] is the info panel
            if (label.ChildCount < 2) return;
            var infoPanel = label.GetChildAtIndex(1);
            if (infoPanel == null || infoPanel.ChildCount < 3) return;

            // Essence names are at indices 2..N-2 (between monster mods/separator and final separator/text)
            for (int i = 2; i < infoPanel.ChildCount - 1; i++)
            {
                var child = infoPanel.GetChildAtIndex(i);
                if (child == null) continue;
                var text = child.Text;
                if (string.IsNullOrEmpty(text)) continue;

                // Skip separators and footer text
                if (text.Contains("imprisoned")) continue;

                _essenceNames.Add(text);

                // Parse tier
                var tier = ParseEssenceTier(text);
                if (tier > _highestTier)
                    _highestTier = tier;

                // Check for corruption target
                foreach (var target in CorruptionTargetEssences)
                {
                    if (text.Contains(target, StringComparison.OrdinalIgnoreCase))
                    {
                        _hasCorruptionTarget = true;
                        break;
                    }
                }
            }
        }

        private Element? FindMonolithLabel(GameController gc, Entity monolith)
        {
            foreach (var label in gc.IngameState.IngameUi.ItemsOnGroundLabelsVisible)
            {
                if (label.ItemOnGround != null &&
                    label.ItemOnGround.Id == monolith.Id &&
                    label.Label?.IsVisible == true)
                {
                    return label.Label;
                }
            }
            return null;
        }

        private Element? FindCorruptButton(GameController gc)
        {
            if (_monolith == null) return null;

            var label = FindMonolithLabel(gc, _monolith);
            if (label == null || label.ChildCount < 3) return null;

            // Child[2] is the corruption button area
            var corruptArea = label.GetChildAtIndex(2);
            if (corruptArea == null || !corruptArea.IsVisible) return null;

            // Sub[0] is the clickable container
            if (corruptArea.ChildCount < 1) return null;
            var clickable = corruptArea.GetChildAtIndex(0);
            return clickable;
        }

        // ══════════════════════════════════════════════════════════════
        // Tier parsing
        // ══════════════════════════════════════════════════════════════

        private static int ParseEssenceTier(string essenceName)
        {
            for (int i = 0; i < TierPrefixes.Length; i++)
            {
                if (essenceName.StartsWith(TierPrefixes[i], StringComparison.OrdinalIgnoreCase))
                    return i + 1; // 1-based: Whispering=1 .. Deafening=7
            }
            // Unknown tier (could be a corrupted-only essence like Insanity/Horror/Delirium/Hysteria)
            // Treat as highest tier
            return 7;
        }

        private static int ParseMinTier(string tierName)
        {
            if (string.IsNullOrEmpty(tierName) || tierName == "Any")
                return 0; // No minimum
            for (int i = 0; i < TierPrefixes.Length; i++)
            {
                if (TierPrefixes[i].Equals(tierName, StringComparison.OrdinalIgnoreCase))
                    return i + 1;
            }
            return 0;
        }

        // ══════════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════════

        private void SetPhase(EssencePhase phase, string status)
        {
            _phase = phase;
            _phaseStartTime = DateTime.Now;
            Status = status;
        }

        private bool CanClick()
        {
            if (!BotInput.CanAct) return false;
            return (DateTime.Now - _lastClickTime).TotalMilliseconds >= ClickCooldownMs;
        }

        private void ClickElement(GameController gc, Element element)
        {
            var rect = element.GetClientRectCache;
            var clickPos = new Vector2(rect.Center.X, rect.Center.Y);
            var windowRect = gc.Window.GetWindowRectangleTimeCache;
            var absPos = clickPos + new Vector2(windowRect.X, windowRect.Y);
            BotInput.Click(absPos);
        }
    }
}
