using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using SharpDX;
using Pathfinding = AutoExile.Systems.Pathfinding;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using RectangleF = SharpDX.RectangleF;

namespace AutoExile.Mechanics
{
    /// <summary>
    /// Ultimatum encounter mechanic handler.
    ///
    /// Two UI phases:
    ///   1. Ground label panel (pre-start): shows reward, encounter type, 3 modifier choices, BEGIN button.
    ///      Accessed via ItemsOnGroundLabelsVisible → label[0][0] root element.
    ///   2. UltimatumPanel (between waves): shows modifier choices + "accept trial" confirm button.
    ///      Accessed via IngameState.IngameUi.UltimatumPanel.
    ///
    /// Lifecycle:
    ///   Detect altar → Navigate → Read ground label → Select mod → Click BEGIN →
    ///   Fight wave → UltimatumPanel: choose mod (or take reward) → repeat → Loot rewards
    /// </summary>
    public class UltimatumMechanic : IMapMechanic
    {
        public string Name => "Ultimatum";
        public string Status { get; private set; } = "";
        public Vector2? AnchorGridPos { get; private set; }
        public bool IsEncounterActive => _phase == UltimatumPhase.Fighting;
        public bool IsComplete => _phase is UltimatumPhase.Complete
                                       or UltimatumPhase.Abandoned
                                       or UltimatumPhase.Failed;

        // ── Phase machine ──
        private UltimatumPhase _phase = UltimatumPhase.Idle;
        private DateTime _phaseStartTime = DateTime.Now;

        // ── Entity references ──
        private Entity? _altarEntity;

        // ── Encounter state ──
        private string _encounterType = "";
        private int _currentRound;
        private int _totalRounds;
        private int _cumulativeDanger;
        private readonly List<string> _acceptedMods = new();
        private readonly List<string> _acceptedModIds = new();

        // ── Reward value tracking ──
        private double _accumulatedRewardValue;
        private Func<Entity, double>? _getNinjaValue;
        private bool _encounterConfirmedStarted; // True once we've seen encounter_started=1

        // ── Loot wait ──
        private DateTime _rewardTakenTime;
        private const float LootWaitSeconds = 3f;

        // ── Previous combat profile (to restore after encounter) ──
        private CombatProfile? _savedCombatProfile;

        // ── Pre-start click state ──
        private bool _preStartModSelected;
        private int _preStartSelectedIndex = -1;
        private bool _preStartEntityClicked;  // True after clicking the altar entity to open UI
        private DateTime _lastPreStartClickTime = DateTime.MinValue;
        private bool _preStartBeginClicked;   // True after clicking BEGIN, waiting for encounter_started=1
        private DateTime _preStartBeginClickTime = DateTime.MinValue;
        private bool _takeRewardClicked;
        private DateTime _takeRewardClickTime = DateTime.MinValue;

        public enum UltimatumPhase
        {
            Idle,
            Detected,
            Navigating,
            PreStart,       // Ground label visible: read type, select mod, click BEGIN
            Fighting,       // Wave active, combat handles it
            ChoosingMod,    // UltimatumPanel visible between waves: select mod or take reward
            TakeReward,     // Clicking "take reward" button, verifying panel closes
            WaitingForLoot,
            Complete,
            Abandoned,
            Failed,
        }

        // ── Public state for overlay ──
        public UltimatumPhase Phase => _phase;
        public string EncounterType => _encounterType;
        public int CurrentRound => _currentRound;
        public int TotalRounds => _totalRounds;
        public int CumulativeDanger => _cumulativeDanger;
        public IReadOnlyList<string> AcceptedMods => _acceptedMods;
        public double AccumulatedRewardValue => _accumulatedRewardValue;

        /// <summary>
        /// Effective orbit radius — halved if "limited area" modifier is active.
        /// Checks both mod IDs and display names since the exact game ID is unknown.
        /// </summary>
        private float GetEffectiveOrbitRadius(float baseRadius)
        {
            foreach (var id in _acceptedModIds)
            {
                // Known ID: "Radius1" = Limited Arena (confirmed via live game data)
                if (id.StartsWith("Radius", StringComparison.OrdinalIgnoreCase))
                    return baseRadius * 0.5f;
            }
            // Fallback: check display names
            foreach (var name in _acceptedMods)
            {
                if (name.Contains("Limited Arena", StringComparison.OrdinalIgnoreCase))
                    return baseRadius * 0.5f;
            }
            return baseRadius;
        }

        // ══════════════════════════════════════════════════════════════
        // Detection
        // ══════════════════════════════════════════════════════════════

        public bool Detect(BotContext ctx)
        {
            if (_phase != UltimatumPhase.Idle) return _altarEntity != null;

            var gc = ctx.Game;
            foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon])
            {
                if (entity.Path != "Metadata/Terrain/Leagues/Ultimatum/Objects/UltimatumChallengeInteractable")
                    continue;

                if (!entity.TryGetComponent<StateMachine>(out var sm))
                    continue;

                var finished = sm.States.FirstOrDefault(s => s.Name == "encounter_finished");
                if (finished != null && finished.Value > 0) continue; // 1=completed, 2=failed

                _altarEntity = entity;
                AnchorGridPos = new Vector2(entity.GridPosNum.X, entity.GridPosNum.Y);

                // Check if encounter is truly in progress (UltimatumPanel visible = reliable signal)
                // Don't trust encounter_started alone — it may be stale or pre-set
                var panel = gc.IngameState.IngameUi.UltimatumPanel;
                if (panel != null && panel.IsVisible)
                {
                    _encounterConfirmedStarted = true;
                    _phase = UltimatumPhase.ChoosingMod;
                    Status = $"Joined in-progress encounter (choosing)";
                    _phaseStartTime = DateTime.Now;
                    SaveAndOverrideCombatProfile(ctx, ctx.Settings.Mechanics.Ultimatum);
                    ctx.Log($"[Ultimatum] Detected in-progress encounter at ({AnchorGridPos.Value.X:F0}, {AnchorGridPos.Value.Y:F0}), phase={_phase}");
                    return true;
                }

                _phase = UltimatumPhase.Detected;
                Status = $"Detected altar at ({AnchorGridPos.Value.X:F0}, {AnchorGridPos.Value.Y:F0})";
                return true;
            }

            return false;
        }

        // ══════════════════════════════════════════════════════════════
        // Tick
        // ══════════════════════════════════════════════════════════════

        public MechanicResult Tick(BotContext ctx)
        {
            var gc = ctx.Game;
            if (gc?.Player == null || !gc.InGame) return MechanicResult.InProgress;

            // Check for player death
            if (!gc.Player.IsAlive && _phase is UltimatumPhase.Fighting or UltimatumPhase.ChoosingMod or UltimatumPhase.TakeReward)
            {
                _phase = UltimatumPhase.Failed;
                Status = "Died during encounter";
                RestoreCombatProfile(ctx);
                return MechanicResult.Failed;
            }

            // Check encounter_finished on entity
            if (_altarEntity != null && _phase is UltimatumPhase.Fighting or UltimatumPhase.ChoosingMod)
            {
                if (_altarEntity.TryGetComponent<StateMachine>(out var sm))
                {
                    var finished = sm.States.FirstOrDefault(s => s.Name == "encounter_finished");
                    if (finished != null && finished.Value > 0)
                    {
                        _phase = UltimatumPhase.Failed;
                        Status = $"Encounter ended (finished={finished.Value})";
                        RestoreCombatProfile(ctx);
                        return MechanicResult.Failed;
                    }
                }
            }

            switch (_phase)
            {
                case UltimatumPhase.Detected:
                case UltimatumPhase.Navigating:
                    return TickNavigating(ctx, gc);

                case UltimatumPhase.PreStart:
                    return TickPreStart(ctx, gc);

                case UltimatumPhase.Fighting:
                    return TickFighting(ctx, gc);

                case UltimatumPhase.ChoosingMod:
                    return TickChoosingMod(ctx, gc);

                case UltimatumPhase.TakeReward:
                    return TickTakeReward(ctx, gc);

                case UltimatumPhase.WaitingForLoot:
                    return TickWaitingForLoot(ctx, gc);

                case UltimatumPhase.Complete:
                    return MechanicResult.Complete;
                case UltimatumPhase.Abandoned:
                    return MechanicResult.Abandoned;
                case UltimatumPhase.Failed:
                    return MechanicResult.Failed;

                default:
                    return MechanicResult.Idle;
            }
        }

        // ── Navigate to altar ──

        private MechanicResult TickNavigating(BotContext ctx, GameController gc)
        {
            if (AnchorGridPos == null) return MechanicResult.Failed;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            var dist = Vector2.Distance(playerGrid, AnchorGridPos.Value);
            var settings = ctx.Settings.Mechanics.Ultimatum;

            // Close enough to interact
            if (dist <= ctx.Interaction.InteractRadius)
            {
                // Check if encounter is already in progress (mid-encounter rejoin after death)
                // Only trust encounter_started=1 if we already confirmed it ourselves
                // (prevents false positive from stale state machine data)
                if (_encounterConfirmedStarted && _altarEntity != null &&
                    _altarEntity.TryGetComponent<StateMachine>(out var sm))
                {
                    var started = sm.States.FirstOrDefault(s => s.Name == "encounter_started");
                    if (started?.Value == 1)
                    {
                        SaveAndOverrideCombatProfile(ctx, settings);
                        var panel = gc.IngameState.IngameUi.UltimatumPanel;
                        if (panel != null && panel.IsVisible)
                        {
                            _phase = UltimatumPhase.ChoosingMod;
                            Status = "Rejoined encounter (choosing)";
                        }
                        else
                        {
                            _phase = UltimatumPhase.Fighting;
                            Status = "Rejoined encounter (fighting)";
                        }
                        _phaseStartTime = DateTime.Now;
                        return MechanicResult.InProgress;
                    }
                }

                _phase = UltimatumPhase.PreStart;
                _phaseStartTime = DateTime.Now;
                _preStartModSelected = false;
                _preStartSelectedIndex = -1;
                _preStartEntityClicked = false;
                Status = "At altar, clicking to open UI";
                ctx.Log($"[Ultimatum] Arrived at altar, entering PreStart");
                return MechanicResult.InProgress;
            }

            // Start or continue navigation
            if (!ctx.Navigation.IsNavigating)
            {
                if (!ctx.Navigation.NavigateTo(gc, AnchorGridPos.Value))
                {
                    _phase = UltimatumPhase.Failed;
                    Status = "Cannot path to altar";
                    return MechanicResult.Failed;
                }
            }

            _phase = UltimatumPhase.Navigating;
            Status = $"Navigating to altar (dist={dist:F0})";
            return MechanicResult.InProgress;
        }

        // ── Pre-start: click entity to open UI, read type, select mod, click BEGIN ──

        private MechanicResult TickPreStart(BotContext ctx, GameController gc)
        {
            var settings = ctx.Settings.Mechanics.Ultimatum;

            // Step 0: Click the altar entity to open the interaction UI
            if (!_preStartEntityClicked)
            {
                if (!BotInput.CanAct) return MechanicResult.InProgress;

                if (_altarEntity != null && _altarEntity.IsTargetable)
                {
                    var cam = gc.IngameState.Camera;
                    var screenPos = cam.WorldToScreen(_altarEntity.BoundsCenterPosNum);
                    var windowRect = gc.Window.GetWindowRectangleTimeCache;
                    if (screenPos.X > 0 && screenPos.X < windowRect.Width &&
                        screenPos.Y > 0 && screenPos.Y < windowRect.Height)
                    {
                        var absPos = new Vector2(windowRect.X + screenPos.X, windowRect.Y + screenPos.Y);
                        BotInput.Click(absPos);
                        _preStartEntityClicked = true;
                        _lastPreStartClickTime = DateTime.Now;
                        ctx.Log("[Ultimatum] Clicked altar entity to open UI");
                        Status = "Clicked altar, waiting for panel...";
                        return MechanicResult.InProgress;
                    }
                }

                // Can't click entity — timeout
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 10)
                {
                    _phase = UltimatumPhase.Failed;
                    Status = "Timeout: cannot click altar entity";
                    return MechanicResult.Failed;
                }
                Status = "Waiting to click altar...";
                return MechanicResult.InProgress;
            }

            // Wait a moment after clicking for UI to appear
            if ((DateTime.Now - _lastPreStartClickTime).TotalMilliseconds < 500)
            {
                Status = "Waiting for UI to open...";
                return MechanicResult.InProgress;
            }

            // Find the ground label for the altar entity
            var groundLabel = FindAltarGroundLabel(gc);
            if (groundLabel == null)
            {
                // Retry clicking if label doesn't appear within 3s
                if ((DateTime.Now - _lastPreStartClickTime).TotalSeconds > 3)
                {
                    _preStartEntityClicked = false; // Try clicking again
                    ctx.Log("[Ultimatum] Ground label not found, retrying click");
                    Status = "Retrying altar click...";
                    return MechanicResult.InProgress;
                }

                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 15)
                {
                    _phase = UltimatumPhase.Failed;
                    Status = "Timeout: ground label not found after clicking";
                    return MechanicResult.Failed;
                }
                Status = "Waiting for ground label...";
                return MechanicResult.InProgress;
            }

            // Root element: label[0][0]
            var root = groundLabel.GetChildAtIndex(0)?.GetChildAtIndex(0);
            if (root == null || root.ChildCount < 5)
            {
                Status = $"Ground label structure unexpected (children={root?.ChildCount ?? 0})";
                ctx.Log($"[Ultimatum] Ground label root has {root?.ChildCount ?? 0} children, expected 5+");
                return MechanicResult.InProgress;
            }

            // Read encounter type from root[1][1].Text
            if (string.IsNullOrEmpty(_encounterType))
            {
                var typeElement = root.GetChildAtIndex(1)?.GetChildAtIndex(1);
                if (typeElement?.Text != null)
                {
                    _encounterType = typeElement.Text.Trim();
                    ctx.Log($"[Ultimatum] Encounter type: {_encounterType}");
                }
            }

            // Check if we should do this encounter type
            if (!string.IsNullOrEmpty(_encounterType) && !ShouldDoEncounterType(_encounterType, settings))
            {
                _phase = UltimatumPhase.Abandoned;
                Status = $"Skipping: {_encounterType} type is disabled";
                return MechanicResult.Abandoned;
            }

            // Get choices panel from root[2]
            var choicesElement = root.GetChildAtIndex(2);
            var choicePanel = choicesElement?.AsObject<UltimatumChoicePanel>();
            if (choicePanel == null)
            {
                Status = "Waiting for choice panel...";
                return MechanicResult.InProgress;
            }

            var modifiers = choicePanel.Modifiers;
            var choiceElements = choicePanel.ChoiceElements;
            if (modifiers == null || modifiers.Count == 0 || choiceElements == null || choiceElements.Count == 0)
            {
                Status = "Waiting for modifier data...";
                return MechanicResult.InProgress;
            }

            if (!BotInput.CanAct) return MechanicResult.InProgress;

            // Step 1: Select the best modifier
            if (!_preStartModSelected)
            {
                var bestChoice = PickBestModifier(modifiers, settings);
                if (bestChoice < 0)
                {
                    _phase = UltimatumPhase.Abandoned;
                    Status = "All initial modifiers are blocked";
                    return MechanicResult.Abandoned;
                }

                // Click the choice element
                var elem = choiceElements[bestChoice];
                ClickElement(gc, elem);
                _preStartSelectedIndex = bestChoice;
                _preStartModSelected = true;
                Status = $"Selected: {modifiers[bestChoice].Name} (danger={settings.GetModDanger(modifiers[bestChoice].Id)})";
                ctx.Log($"[Ultimatum] Selected mod: {modifiers[bestChoice].Name} (id={modifiers[bestChoice].Id})");
                return MechanicResult.InProgress;
            }

            // Step 2: Click BEGIN button and verify encounter actually started
            if (!_preStartBeginClicked)
            {
                var beginBtn = root.GetChildAtIndex(4)?.GetChildAtIndex(0)?.GetChildAtIndex(0);
                if (beginBtn == null || !beginBtn.IsVisible)
                {
                    Status = "BEGIN button not found";
                    if ((DateTime.Now - _phaseStartTime).TotalSeconds > 20)
                    {
                        _phase = UltimatumPhase.Failed;
                        Status = "Timeout: BEGIN button not found";
                        return MechanicResult.Failed;
                    }
                    return MechanicResult.InProgress;
                }

                ClickElement(gc, beginBtn);
                _preStartBeginClicked = true;
                _preStartBeginClickTime = DateTime.Now;
                ctx.Log("[Ultimatum] Clicked BEGIN, waiting for encounter_started confirmation");
                Status = "Clicked BEGIN, verifying...";
                return MechanicResult.InProgress;
            }

            // Step 3: Verify encounter actually started (encounter_started == 1 or ground label gone)
            bool encounterStarted = false;
            if (_altarEntity?.TryGetComponent<StateMachine>(out var preSm) == true)
            {
                var started = preSm.States.FirstOrDefault(s => s.Name == "encounter_started");
                if (started?.Value == 1)
                    encounterStarted = true;
            }

            // Ground label disappearing is also a reliable signal
            if (!encounterStarted && FindAltarGroundLabel(gc) == null &&
                (DateTime.Now - _preStartBeginClickTime).TotalMilliseconds > 500)
            {
                encounterStarted = true;
            }

            if (encounterStarted)
            {
                // Record the accepted mod
                if (_preStartSelectedIndex >= 0 && _preStartSelectedIndex < modifiers.Count)
                {
                    var mod = modifiers[_preStartSelectedIndex];
                    int danger = settings.GetModDanger(mod.Id);
                    _cumulativeDanger += danger;
                    _acceptedMods.Add(mod.Name);
                    _acceptedModIds.Add(mod.Id);
                }

                SaveAndOverrideCombatProfile(ctx, settings);

                _phase = UltimatumPhase.Fighting;
                _phaseStartTime = DateTime.Now;
                _currentRound = 1;
                _encounterConfirmedStarted = true;
                Status = $"BEGIN confirmed — fighting round 1, type={_encounterType}";
                ctx.Log($"[Ultimatum] Encounter confirmed started: type={_encounterType}, danger={_cumulativeDanger}");
                return MechanicResult.InProgress;
            }

            // Retry BEGIN click if not confirmed after 3s
            if ((DateTime.Now - _preStartBeginClickTime).TotalSeconds > 3)
            {
                _preStartBeginClicked = false;
                ctx.Log("[Ultimatum] BEGIN click not confirmed after 3s, retrying");
                Status = "BEGIN click missed, retrying...";
                return MechanicResult.InProgress;
            }

            // Overall timeout
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 25)
            {
                _phase = UltimatumPhase.Failed;
                Status = "Timeout: encounter never started after BEGIN clicks";
                return MechanicResult.Failed;
            }

            Status = "Waiting for encounter to start...";
            return MechanicResult.InProgress;
        }

        // ── Fighting: combat handles skills, we stay near altar ──

        private MechanicResult TickFighting(BotContext ctx, GameController gc)
        {
            var settings = ctx.Settings.Mechanics.Ultimatum;

            // Suppress combat positioning when leash will pull us back,
            // otherwise combat movement fights navigation for cursor control
            bool leashing = false;
            if (AnchorGridPos.HasValue)
            {
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var dist = Vector2.Distance(playerGrid, AnchorGridPos.Value);
                var effectiveRadius = GetEffectiveOrbitRadius(settings.OrbitRadius.Value);
                leashing = dist > effectiveRadius;
            }
            ctx.Combat.SuppressPositioning = leashing;
            ctx.Combat.Tick(ctx);
            ctx.Combat.SuppressPositioning = false;

            // Check if wave ended (UltimatumPanel appeared for between-wave choices)
            var panel = gc.IngameState.IngameUi.UltimatumPanel;
            if (panel != null && panel.IsVisible)
            {
                _phase = UltimatumPhase.ChoosingMod;
                _phaseStartTime = DateTime.Now;
                Status = "Wave complete, choosing next modifier";
                return MechanicResult.InProgress;
            }

            // Track encounter_started state — only treat 0 as "ended" after we've seen 1
            if (_altarEntity?.TryGetComponent<StateMachine>(out var sm) == true)
            {
                var started = sm.States.FirstOrDefault(s => s.Name == "encounter_started");
                if (started?.Value == 1)
                {
                    _encounterConfirmedStarted = true;
                }
                else if (started?.Value == 0 && _encounterConfirmedStarted)
                {
                    _phase = UltimatumPhase.Complete;
                    Status = "Encounter ended during fight";
                    RestoreCombatProfile(ctx);
                    return MechanicResult.Complete;
                }
            }

            // Leash to altar
            if (AnchorGridPos.HasValue)
            {
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                var dist = Vector2.Distance(playerGrid, AnchorGridPos.Value);
                var effectiveRadius = GetEffectiveOrbitRadius(settings.OrbitRadius.Value);
                if (dist > effectiveRadius)
                {
                    ctx.Navigation.NavigateTo(gc, AnchorGridPos.Value);
                    Status = $"Fighting round {_currentRound} — leashing back (dist={dist:F0}, radius={effectiveRadius:F0})";
                }
                else
                {
                    Status = $"Fighting round {_currentRound}/{_totalRounds} (dist={dist:F0}, danger={_cumulativeDanger})";
                }
            }

            // Timeout safety
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 120)
            {
                _phase = UltimatumPhase.Failed;
                Status = "Wave timeout (120s)";
                RestoreCombatProfile(ctx);
                return MechanicResult.Failed;
            }

            return MechanicResult.InProgress;
        }

        // ── Choosing modifier between waves: UltimatumPanel ──

        private MechanicResult TickChoosingMod(BotContext ctx, GameController gc)
        {
            var panel = gc.IngameState.IngameUi.UltimatumPanel;
            var settings = ctx.Settings.Mechanics.Ultimatum;

            if (panel == null || !panel.IsVisible)
            {
                // Panel hidden — might be transitioning to fight
                if ((DateTime.Now - _phaseStartTime).TotalSeconds > 15)
                {
                    _phase = UltimatumPhase.Failed;
                    Status = "Panel disappeared unexpectedly";
                    RestoreCombatProfile(ctx);
                    return MechanicResult.Failed;
                }
                Status = "Waiting for panel...";
                return MechanicResult.InProgress;
            }

            // Parse round from panel text
            ParseRoundText(panel);

            // Price the last completed wave's reward and accumulate
            PriceCurrentReward(gc, panel);

            // Check if we've hit max waves
            if (_currentRound > settings.MaxWaves.Value)
            {
                return TakeReward(ctx, gc, panel, "Reached max wave limit");
            }

            // Check if accumulated reward value is worth securing
            if (settings.MinSecureValue.Value > 0 && _accumulatedRewardValue >= settings.MinSecureValue.Value)
            {
                return TakeReward(ctx, gc, panel,
                    $"Securing {_accumulatedRewardValue:F0}c rewards (threshold={settings.MinSecureValue.Value})");
            }

            // Use ChoicesPanel (typed UltimatumChoicePanel) for modifier selection
            var choicePanel = panel.ChoicesPanel;
            if (choicePanel == null)
            {
                Status = "Waiting for choices panel...";
                return MechanicResult.InProgress;
            }

            var modifiers = choicePanel.Modifiers;
            var choiceElements = choicePanel.ChoiceElements;

            if (modifiers == null || modifiers.Count == 0 || choiceElements == null || choiceElements.Count == 0)
            {
                Status = "Waiting for modifier data...";
                return MechanicResult.InProgress;
            }

            // Check cumulative danger threshold
            var bestChoice = PickBestModifier(modifiers, settings);
            if (bestChoice < 0)
            {
                return TakeReward(ctx, gc, panel, "All modifiers blocked");
            }

            var bestMod = modifiers[bestChoice];
            int modDanger = settings.GetModDanger(bestMod.Id);
            if (_cumulativeDanger + modDanger > settings.DangerThreshold.Value)
            {
                return TakeReward(ctx, gc, panel, $"Danger would exceed threshold ({_cumulativeDanger}+{modDanger} > {settings.DangerThreshold.Value})");
            }

            if (!BotInput.CanAct) return MechanicResult.InProgress;

            // Click the choice if not already selected
            if (choicePanel.SelectedChoice != bestChoice)
            {
                ClickElement(gc, choiceElements[bestChoice]);
                Status = $"Selecting: {bestMod.Name} (danger={modDanger})";
                return MechanicResult.InProgress;
            }

            // Click confirm button ("accept trial") — record mod only once per round
            var confirmBtn = panel.ConfirmButton;
            if (confirmBtn != null && confirmBtn.IsVisible)
            {
                ClickElement(gc, confirmBtn);

                // Guard: only record the mod if we haven't already for this round
                if (_acceptedMods.Count < _currentRound)
                {
                    _cumulativeDanger += modDanger;
                    _acceptedMods.Add(bestMod.Name);
                    _acceptedModIds.Add(bestMod.Id);
                    ctx.Log($"[Ultimatum] Accepted mod: {bestMod.Name} (danger={modDanger}, total={_cumulativeDanger})");

                }

                Status = $"Accepted: {bestMod.Name} (total danger={_cumulativeDanger})";
                _phase = UltimatumPhase.Fighting;
                _phaseStartTime = DateTime.Now;
                _currentRound++;
            }

            return MechanicResult.InProgress;
        }

        // ── Take reward ──

        private string _takeRewardReason = "";

        private MechanicResult TakeReward(BotContext ctx, GameController gc, Element panel, string reason)
        {
            ctx.Log($"[Ultimatum] Taking reward: {reason}");
            _takeRewardReason = reason;

            _phase = UltimatumPhase.TakeReward;
            _phaseStartTime = DateTime.Now;
            _takeRewardClicked = false;
            Status = $"Taking reward: {reason}";
            RestoreCombatProfile(ctx);

            return MechanicResult.InProgress;
        }

        // ── Take reward: click button with retry until panel closes ──

        private MechanicResult TickTakeReward(BotContext ctx, GameController gc)
        {
            var panel = gc.IngameState.IngameUi.UltimatumPanel;

            // Panel closed = reward taken successfully
            if (panel == null || !panel.IsVisible)
            {
                _rewardTakenTime = DateTime.Now;
                _phase = UltimatumPhase.WaitingForLoot;
                _phaseStartTime = DateTime.Now;
                Status = $"Reward taken — waiting for drops";
                ctx.Log("[Ultimatum] Panel closed, reward confirmed taken");
                return MechanicResult.InProgress;
            }

            // Overall timeout
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 15)
            {
                _phase = UltimatumPhase.Failed;
                Status = "Timeout: take reward button never worked";
                return MechanicResult.Failed;
            }

            if (!BotInput.CanAct) return MechanicResult.InProgress;

            // Retry click every 2s if panel still visible
            if (!_takeRewardClicked || (DateTime.Now - _takeRewardClickTime).TotalSeconds > 2)
            {
                var takeBtn = panel.GetChildAtIndex(1)?.GetChildAtIndex(4)?.GetChildAtIndex(0)?.GetChildAtIndex(0);
                if (takeBtn != null && takeBtn.IsVisible)
                {
                    ClickElement(gc, takeBtn);
                    _takeRewardClicked = true;
                    _takeRewardClickTime = DateTime.Now;
                    Status = $"Clicking take reward... ({_takeRewardReason})";
                    ctx.Log("[Ultimatum] Clicked take reward button");
                }
                else
                {
                    Status = "Take reward button not found, waiting...";
                }
            }
            else
            {
                Status = $"Waiting for panel to close...";
            }

            return MechanicResult.InProgress;
        }

        // ── Waiting for loot to drop ──

        private MechanicResult TickWaitingForLoot(BotContext ctx, GameController gc)
        {
            var elapsed = (DateTime.Now - _rewardTakenTime).TotalSeconds;
            if (elapsed < LootWaitSeconds)
            {
                Status = $"Waiting for reward drops ({elapsed:F1}s / {LootWaitSeconds}s)";
                return MechanicResult.InProgress;
            }

            _phase = UltimatumPhase.Complete;
            Status = $"Complete — {_currentRound} waves, danger={_cumulativeDanger}";
            return MechanicResult.Complete;
        }

        // ══════════════════════════════════════════════════════════════
        // Reward value tracking
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Price the reward shown on the UltimatumPanel after completing a wave.
        /// Uses LastRewardInventory (what we just earned) via NinjaPrice bridge.
        /// Called each tick in ChoosingMod — only prices once per round transition.
        /// </summary>
        private int _lastPricedRound;

        private void PriceCurrentReward(GameController gc, Element panel)
        {
            // Only price once per round
            if (_currentRound <= _lastPricedRound) return;

            // Initialize NinjaPrice bridge if needed
            if (_getNinjaValue == null)
            {
                try
                {
                    _getNinjaValue = gc.PluginBridge.GetMethod<Func<Entity, double>>("NinjaPrice.GetValue");
                }
                catch { }
            }

            try
            {
                // LastRewardInventory = what was earned in the wave we just completed
                var ultimatumPanel = gc.IngameState.IngameUi.UltimatumPanel;
                if (ultimatumPanel == null) return;

                var lastRewardInv = ultimatumPanel.LastRewardInventory;
                if (lastRewardInv?.ServerInventory?.InventorySlotItems == null) return;

                foreach (var slotItem in lastRewardInv.ServerInventory.InventorySlotItems)
                {
                    var item = slotItem.Item;
                    if (item == null) continue;

                    double value = 0;
                    if (_getNinjaValue != null)
                    {
                        try { value = _getNinjaValue(item); } catch { }
                    }

                    // If NinjaPrice can't value it, estimate stack value at 1c per item
                    if (value <= 0)
                    {
                        var stack = item.GetComponent<ExileCore.PoEMemory.Components.Stack>();
                        value = stack?.Size ?? 1;
                    }

                    _accumulatedRewardValue += value;
                }

                _lastPricedRound = _currentRound;
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════
        // Modifier selection
        // ══════════════════════════════════════════════════════════════

        private int PickBestModifier(
            IList<ExileCore.PoEMemory.FilesInMemory.Ultimatum.UltimatumModifier> modifiers,
            BotSettings.UltimatumMechanicSettings settings)
        {
            int bestIndex = -1;
            int bestDanger = int.MaxValue;

            for (int i = 0; i < modifiers.Count; i++)
            {
                int danger = settings.GetModDanger(modifiers[i].Id);
                if (danger >= UltimatumModDanger.BlockedValue) continue;
                if (danger < bestDanger)
                {
                    bestDanger = danger;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        // ══════════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Find the ground label element for the altar entity.
        /// </summary>
        private Element? FindAltarGroundLabel(GameController gc)
        {
            if (_altarEntity == null) return null;

            foreach (var label in gc.IngameState.IngameUi.ItemsOnGroundLabelsVisible)
            {
                if (label.ItemOnGround != null &&
                    label.ItemOnGround.Path == _altarEntity.Path &&
                    label.Label?.IsVisible == true)
                {
                    return label.Label;
                }
            }
            return null;
        }

        /// <summary>
        /// Click a UI element using its client rect center + window offset.
        /// </summary>
        private void ClickElement(GameController gc, Element element)
        {
            var rect = element.GetClientRectCache;
            var clickPos = new Vector2(rect.Center.X, rect.Center.Y);
            var windowRect = gc.Window.GetWindowRectangleTimeCache;
            var absPos = clickPos + new Vector2(windowRect.X, windowRect.Y);
            BotInput.Click(absPos);
        }

        private void ParseRoundText(Element? panel)
        {
            // Text format: "Round <ultimatumnumber>{2/10}"
            var roundElement = panel?.GetChildAtIndex(0)?.GetChildAtIndex(3);
            if (roundElement?.Text == null) return;

            var text = roundElement.Text;
            var braceStart = text.IndexOf('{');
            var braceEnd = text.IndexOf('}');
            if (braceStart >= 0 && braceEnd > braceStart)
            {
                var inner = text.Substring(braceStart + 1, braceEnd - braceStart - 1);
                var parts = inner.Split('/');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var current) &&
                    int.TryParse(parts[1], out var total))
                {
                    _currentRound = current;
                    _totalRounds = total;
                }
            }
        }

        private bool ShouldDoEncounterType(string type, BotSettings.UltimatumMechanicSettings settings)
        {
            // Normalize: "Protect the Altar" maps to DoDefendAltar
            return type switch
            {
                "Survive" => settings.DoSurvive.Value,
                "Kill Enemies" => settings.DoKillEnemies.Value,
                "Defend the Altar" or "Protect the Altar" => settings.DoDefendAltar.Value,
                "Stand in the Circles" => settings.DoStandInCircles.Value,
                _ => true,
            };
        }

        private void SaveAndOverrideCombatProfile(BotContext ctx, BotSettings.UltimatumMechanicSettings settings)
        {
            if (_savedCombatProfile != null) return; // Already saved

            _savedCombatProfile = new CombatProfile
            {
                Enabled = ctx.Combat.Profile.Enabled,
                Positioning = ctx.Combat.Profile.Positioning,
            };

            var effectiveRadius = GetEffectiveOrbitRadius(settings.OrbitRadius.Value);
            ctx.Combat.SetProfile(new CombatProfile
            {
                Enabled = true,
                Positioning = CombatPositioning.Ranged,
                LeashAnchor = AnchorGridPos,
                LeashRadius = effectiveRadius,
            });
        }

        private void RestoreCombatProfile(BotContext ctx)
        {
            if (_savedCombatProfile != null)
            {
                ctx.Combat.SetProfile(_savedCombatProfile);
                _savedCombatProfile = null;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Render
        // ══════════════════════════════════════════════════════════════

        public void Render(BotContext ctx)
        {
            if (_phase == UltimatumPhase.Idle) return;

            var g = ctx.Graphics;
            var gc = ctx.Game;
            if (g == null || gc?.Player == null || !gc.InGame) return;

            var cam = gc.IngameState.Camera;
            var playerZ = gc.Player.PosNum.Z;
            var settings = ctx.Settings.Mechanics.Ultimatum;

            // ═══ HUD Panel ═══
            var hudX = 20f;
            var hudY = 550f;
            var lineH = 18f;

            var titleColor = _phase switch
            {
                UltimatumPhase.Fighting => SharpDX.Color.Red,
                UltimatumPhase.ChoosingMod => SharpDX.Color.Yellow,
                UltimatumPhase.PreStart => SharpDX.Color.Cyan,
                UltimatumPhase.Complete => SharpDX.Color.LimeGreen,
                UltimatumPhase.Abandoned => SharpDX.Color.Gray,
                UltimatumPhase.Failed => SharpDX.Color.Red,
                _ => SharpDX.Color.Cyan,
            };

            g.DrawText("=== ULTIMATUM ===", new Vector2(hudX, hudY), titleColor);
            hudY += lineH;

            g.DrawText($"Phase: {_phase}", new Vector2(hudX, hudY), titleColor);
            hudY += lineH;

            if (!string.IsNullOrEmpty(_encounterType))
            {
                g.DrawText($"Type: {_encounterType}", new Vector2(hudX, hudY), SharpDX.Color.White);
                hudY += lineH;
            }

            if (_totalRounds > 0)
            {
                g.DrawText($"Round: {_currentRound}/{_totalRounds}", new Vector2(hudX, hudY), SharpDX.Color.White);
                hudY += lineH;
            }

            // Danger bar
            if (_cumulativeDanger > 0 || _phase is UltimatumPhase.Fighting or UltimatumPhase.ChoosingMod)
            {
                var threshold = settings.DangerThreshold.Value;
                var barWidth = 200f;
                var barHeight = 14f;

                g.DrawBox(new RectangleF(hudX, hudY, barWidth, barHeight),
                    new SharpDX.Color(40, 40, 40, 200));
                var fillRatio = Math.Min((float)_cumulativeDanger / threshold, 1f);
                var fillColor = fillRatio < 0.5f ? new SharpDX.Color(0, 200, 0, 200)
                    : fillRatio < 0.8f ? new SharpDX.Color(200, 200, 0, 200)
                    : new SharpDX.Color(200, 0, 0, 200);
                g.DrawBox(new RectangleF(hudX, hudY, barWidth * fillRatio, barHeight), fillColor);
                g.DrawText($"Danger: {_cumulativeDanger}/{threshold}",
                    new Vector2(hudX + barWidth + 8, hudY - 1), SharpDX.Color.White);
                hudY += barHeight + 4;
            }

            // Reward value
            if (_accumulatedRewardValue > 0)
            {
                var secureThreshold = settings.MinSecureValue.Value;
                var rewardColor = secureThreshold > 0 && _accumulatedRewardValue >= secureThreshold
                    ? SharpDX.Color.LimeGreen
                    : SharpDX.Color.Gold;
                var rewardText = secureThreshold > 0
                    ? $"Rewards: {_accumulatedRewardValue:F0}c / {secureThreshold}c"
                    : $"Rewards: {_accumulatedRewardValue:F0}c";
                g.DrawText(rewardText, new Vector2(hudX, hudY), rewardColor);
                hudY += lineH;
            }

            g.DrawText(Status, new Vector2(hudX, hudY), SharpDX.Color.Gray);
            hudY += lineH;

            // Accepted mods
            if (_acceptedMods.Count > 0)
            {
                g.DrawText("Mods:", new Vector2(hudX, hudY), SharpDX.Color.White);
                hudY += lineH;
                foreach (var mod in _acceptedMods)
                {
                    g.DrawText($"  - {mod}", new Vector2(hudX, hudY), SharpDX.Color.LightGray);
                    hudY += lineH - 2;
                }
            }

            // ═══ World Overlays ═══
            if (AnchorGridPos.HasValue)
            {
                var altarWorld = new Vector3(
                    AnchorGridPos.Value.X * Pathfinding.GridToWorld,
                    AnchorGridPos.Value.Y * Pathfinding.GridToWorld, playerZ);

                var effectiveRadius = GetEffectiveOrbitRadius(settings.OrbitRadius.Value);
                var orbitWorldRadius = effectiveRadius * Pathfinding.GridToWorld;
                g.DrawCircleInWorld(altarWorld, orbitWorldRadius,
                    new SharpDX.Color(255, 165, 0, 80), 2f);

                var altarScreen = cam.WorldToScreen(altarWorld);
                if (altarScreen.X > -200 && altarScreen.X < 2400)
                {
                    g.DrawCircleInWorld(altarWorld, 30f, SharpDX.Color.Orange, 3f);
                    g.DrawText("ULTIMATUM", altarScreen + new Vector2(-30, -25), SharpDX.Color.Orange);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // Reset
        // ══════════════════════════════════════════════════════════════

        public void Reset()
        {
            _phase = UltimatumPhase.Idle;
            _altarEntity = null;
            AnchorGridPos = null;
            _encounterType = "";
            _currentRound = 0;
            _totalRounds = 0;
            _cumulativeDanger = 0;
            _acceptedMods.Clear();
            _acceptedModIds.Clear();
            _encounterConfirmedStarted = false;
            _savedCombatProfile = null;
            _preStartModSelected = false;
            _preStartSelectedIndex = -1;
            _preStartEntityClicked = false;
            _lastPreStartClickTime = DateTime.MinValue;
            _preStartBeginClicked = false;
            _preStartBeginClickTime = DateTime.MinValue;
            _takeRewardClicked = false;
            _takeRewardClickTime = DateTime.MinValue;
            _takeRewardReason = "";
            _accumulatedRewardValue = 0;
            _lastPricedRound = 0;
            Status = "";
        }
    }
}
