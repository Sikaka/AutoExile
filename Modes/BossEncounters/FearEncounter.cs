using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using System.Numerics;

namespace AutoExile.Modes.BossEncounters
{
    /// <summary>
    /// Incarnation of Fear (Anger Boss UBER) encounter.
    ///
    /// Flow:
    ///   1. Enter "Moment of Trauma"
    ///   2. Two NPCs (ChildZana + Valdo) have dialog — boss exists but is far away and idle
    ///   3. Walk south toward boss at ~(206, 306) — combat handles the fight
    ///   4. Boss entity disappears from snapshot when killed
    ///   5. Loot drops near boss position → Complete (BossMode handles loot + exit via portal scroll)
    ///
    /// Single zone, no spawner click, no phases, no descension altar.
    ///
    /// Fragment: Metadata/Items/MapFragments/CurrencyUberBossKeyAnger (Traumatic Fragment)
    /// Boss: Metadata/Monsters/AtlasMemory/AngerBossUBER@85
    /// NPCs: AngerBoss/NPC/ChildZana, AngerBoss/NPC/Valdo (dialog, non-interactive)
    /// Arena markers: AngerBoss/Objects/AngerBossSolarOrbsMarkers
    /// </summary>
    public class FearEncounter : IBossEncounter
    {
        public string Name => "Incarnation of Fear";
        public string Status { get; private set; } = "";

        private const string FragmentPath = "CurrencyUberBossKeyAnger";
        private const string BossPath = "AngerBossUBER@";
        private const string AreaTransitionPath = "AreaTransition";

        // Behind boss — walk past the boss to the far side so traps/skills don't hit terrain
        // Boss sits at ~(206, 306), player enters from north ~(204, 148)
        private static readonly Vector2 BehindBossPos = new(206, 330);

        public Func<Element, bool> MapFilter => el =>
        {
            var entity = el.Entity;
            return entity?.Path?.Contains(FragmentPath) == true;
        };

        public string? InventoryFragmentPath => FragmentPath;

        // Suppress combat during approach — traps hit terrain if cast from entrance side
        public bool SuppressCombat => _phase == FearPhase.Approaching;

        // ── State ──
        private FearPhase _phase = FearPhase.Idle;
        private DateTime _phaseStartTime;
        private Entity? _bossEntity;
        private bool _bossWasAlive;

        private enum FearPhase
        {
            Idle,
            Approaching,    // Walking toward boss arena
            Fighting,       // Boss alive, combat handles it
            WaitingForLoot, // Boss dead, signal complete
        }

        public void OnEnterZone(BotContext ctx)
        {
            var gc = ctx.Game;

            var pfGrid = gc.IngameState?.Data?.RawPathfindingData;
            var tgtGrid = gc.IngameState?.Data?.RawTerrainTargetingData;
            if (pfGrid != null && gc.Player != null)
            {
                var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
                ctx.Exploration.Initialize(pfGrid, tgtGrid, playerGrid,
                    ctx.Settings.Build.BlinkRange.Value);
            }

            _phase = FearPhase.Approaching;
            _phaseStartTime = DateTime.Now;
            _bossEntity = null;
            _bossWasAlive = false;
            Status = "Entered Moment of Trauma";
            ctx.Log("[Fear] Zone entered");
        }

        public BossEncounterResult Tick(BotContext ctx)
        {
            var gc = ctx.Game;
            if (gc?.Player == null) return BossEncounterResult.InProgress;

            var playerGrid = new Vector2(gc.Player.GridPosNum.X, gc.Player.GridPosNum.Y);
            ctx.Exploration.Update(playerGrid);

            // Scan for boss
            _bossEntity = FindBoss(gc);
            if (_bossEntity != null && _bossEntity.IsAlive)
                _bossWasAlive = true;

            // Detect kill — boss entity disappears from snapshot after death
            if (_bossWasAlive && _phase == FearPhase.Fighting && (_bossEntity == null || !_bossEntity.IsAlive))
            {
                _phase = FearPhase.WaitingForLoot;
                _phaseStartTime = DateTime.Now;
                Status = "Boss killed — looting";
                ctx.Log("[Fear] Kill detected: boss entity gone");
                return BossEncounterResult.Complete;
            }

            switch (_phase)
            {
                case FearPhase.Approaching:
                    return TickApproaching(ctx, gc, playerGrid);
                case FearPhase.Fighting:
                    return TickFighting(ctx, gc, playerGrid);
                default:
                    return BossEncounterResult.InProgress;
            }
        }

        private BossEncounterResult TickApproaching(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 60)
            {
                Status = "Timeout: couldn't reach boss";
                return BossEncounterResult.Failed;
            }

            // After death re-entry: waiting room has an area transition to the boss room.
            // Click it via InteractionSystem if far from the fight area.
            var distToBehind = Vector2.Distance(playerGrid, BehindBossPos);
            if (distToBehind > 100)
            {
                Entity? transition = null;
                foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
                {
                    if (entity.Type != EntityType.AreaTransition) continue;
                    if (!entity.IsTargetable) continue;
                    if (entity.Path?.Contains(AreaTransitionPath) != true) continue;
                    transition = entity;
                    break;
                }

                if (transition != null)
                {
                    if (!ctx.Interaction.IsBusy)
                        ctx.Interaction.InteractWithEntity(transition, ctx.Navigation, requireProximity: true);
                    Status = "Clicking area transition to boss room";
                    return BossEncounterResult.InProgress;
                }
            }

            // Walk behind the boss (past it to the far side) so traps/skills don't hit terrain
            if (distToBehind > 15)
            {
                if (!ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, BehindBossPos);
                Status = $"Positioning behind boss ({distToBehind:F0}g)";
                return BossEncounterResult.InProgress;
            }

            // In position — start fighting
            _phase = FearPhase.Fighting;
            _phaseStartTime = DateTime.Now;
            ctx.Log("[Fear] In position behind boss — fighting");
            return BossEncounterResult.InProgress;
        }

        private BossEncounterResult TickFighting(BotContext ctx, GameController gc, Vector2 playerGrid)
        {
            if ((DateTime.Now - _phaseStartTime).TotalSeconds > 300)
            {
                Status = "Fight timeout (5min)";
                return BossEncounterResult.Failed;
            }

            if (_bossEntity != null && _bossEntity.IsAlive)
            {
                var dist = Vector2.Distance(playerGrid, _bossEntity.GridPosNum);

                // Stay near the boss
                if (dist > 20 && !ctx.Navigation.IsNavigating)
                    ctx.Navigation.NavigateTo(gc, _bossEntity.GridPosNum);

                Status = $"Fighting Incarnation of Fear — dist={dist:F0}";
            }
            else
            {
                Status = "Boss not visible — waiting";
            }

            return BossEncounterResult.InProgress;
        }

        private Entity? FindBoss(GameController gc)
        {
            try
            {
                foreach (var entity in gc.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
                {
                    if (!entity.IsHostile) continue;
                    if (entity.Rarity != MonsterRarity.Unique) continue;
                    if (entity.Path?.Contains(BossPath) != true) continue;
                    return entity;
                }
            }
            catch (IndexOutOfRangeException)
            {
                // ExileCore entity dictionary race — retry next tick
            }
            return null;
        }

        public void Render(BotContext ctx)
        {
            var gc = ctx.Game;
            var g = ctx.Graphics;
            if (gc?.Player == null || g == null) return;

            var cam = gc.IngameState.Camera;

            // Boss marker
            if (_bossEntity != null)
            {
                var world = _bossEntity.BoundsCenterPosNum;
                var screen = cam.WorldToScreen(world);
                if (screen.X > -200 && screen.X < 2400)
                {
                    var color = _bossEntity.IsAlive ? SharpDX.Color.Red : SharpDX.Color.LimeGreen;
                    g.DrawText(_bossEntity.IsAlive ? "FEAR BOSS" : "FEAR BOSS (dead)",
                        screen + new Vector2(-30, -30), color);
                }
            }

            // HUD
            float hudX = 20, hudY = 250, lineH = 18;
            var phaseColor = _phase switch
            {
                FearPhase.Fighting => SharpDX.Color.Red,
                FearPhase.WaitingForLoot => SharpDX.Color.LimeGreen,
                _ => SharpDX.Color.White,
            };
            g.DrawText($"Fear: {_phase}", new Vector2(hudX, hudY), phaseColor);
            hudY += lineH;
            g.DrawText(Status, new Vector2(hudX, hudY), SharpDX.Color.Gray);
        }

        public void Reset()
        {
            _phase = FearPhase.Idle;
            _bossEntity = null;
            _bossWasAlive = false;
            Status = "";
        }
    }
}
