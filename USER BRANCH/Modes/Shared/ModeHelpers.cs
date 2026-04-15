using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using AutoExile.Systems;
using System.Numerics;

namespace AutoExile.Modes.Shared
{
    /// <summary>
    /// Static utilities shared across farming modes.
    /// </summary>
    public static class ModeHelpers
    {
        /// <summary>
        /// Find the best targetable TownPortal entity.
        /// Prefers the portal with lowest grid Y (south on screen / behind map device in isometric view).
        /// This avoids portals that visually block the map device.
        /// </summary>
        public static Entity? FindNearestPortal(GameController gc)
        {
            Entity? best = null;
            float bestY = float.MaxValue;
            foreach (var entity in gc.EntityListWrapper.OnlyValidEntities)
            {
                if (entity.Type != EntityType.TownPortal || !entity.IsTargetable) continue;
                if (entity.GridPosNum.Y < bestY)
                {
                    bestY = entity.GridPosNum.Y;
                    best = entity;
                }
            }
            return best;
        }

        /// <summary>
        /// WorldToScreen → window offset → BotInput.Click. Updates lastActionTime on success.
        /// </summary>
        public static bool ClickEntity(GameController gc, Entity entity, ref DateTime lastActionTime)
        {
            if (!BotInput.CanAct) return false;
            if (!BotInput.ClickEntity(gc, entity)) return false;
            lastActionTime = DateTime.Now;
            return true;
        }

        /// <summary>
        /// BotInput gate + cooldown check.
        /// </summary>
        public static bool CanAct(DateTime lastActionTime, float cooldownMs)
        {
            return BotInput.CanAct &&
                   (DateTime.Now - lastActionTime).TotalMilliseconds >= cooldownMs;
        }

        /// <summary>
        /// Parse DefaultPositioning setting and enable combat with that profile.
        /// </summary>
        public static void EnableDefaultCombat(BotContext ctx)
        {
            var positioning = Enum.TryParse<CombatPositioning>(ctx.Settings.Build.DefaultPositioning.Value, out var pos)
                ? pos : CombatPositioning.Aggressive;
            ctx.Combat.SetProfile(new CombatProfile
            {
                Enabled = true,
                Positioning = positioning,
            });
        }

        /// <summary>
        /// Enable combat, optionally forcing Ranged (kiting) positioning regardless of the global default.
        /// </summary>
        public static void EnableCombatWithKiting(BotContext ctx, bool kiteEnabled)
        {
            if (!kiteEnabled) { EnableDefaultCombat(ctx); return; }
            ctx.Combat.SetProfile(new CombatProfile
            {
                Enabled = true,
                Positioning = CombatPositioning.Ranged,
            });
        }

        /// <summary>
        /// Wrapper for StashSystem.HasInventoryItems.
        /// </summary>
        public static bool HasInventoryItems(GameController gc) => StashSystem.HasInventoryItems(gc);

        /// <summary>
        /// Cancel MapDevice + Stash + Interaction systems. Fixes BlightMode bug where
        /// these weren't cancelled on area change.
        /// </summary>
        public static void CancelAllSystems(BotContext ctx)
        {
            var gc = ctx.Game;
            ctx.MapDevice.Cancel(gc, ctx.Navigation);
            if (ctx.Stash.IsBusy)
                ctx.Stash.Cancel(gc, ctx.Navigation);
            ctx.Interaction.Cancel(gc);
        }
    }
}
