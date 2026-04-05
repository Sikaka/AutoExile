using AutoExile.Recording;

namespace AutoExile.Replay
{
    /// <summary>
    /// Classifies each tick of a recording into a high-level action type.
    /// Uses heuristic rules based on game state (not inputs) so the same
    /// classifier works for both human recordings and bot replay output.
    ///
    /// The key insight: we classify by SITUATION, not by what the player did.
    /// "12 mobs nearby" → FIGHTING, regardless of whether the player is clicking
    /// monsters or running away. This lets us compare human vs bot decisions
    /// at the logic level even though maps are different each run.
    /// </summary>
    public static class ActionClassifier
    {
        /// <summary>
        /// Classify all ticks in a recording into a timeline of actions.
        /// Adjacent ticks with the same action type are merged into ranges.
        /// </summary>
        public static List<ClassifiedAction> Classify(GameplayRecording recording)
        {
            var actions = new List<ClassifiedAction>();
            if (recording.Ticks.Count == 0) return actions;

            ActionType? currentType = null;
            int rangeStart = 0;
            string currentDetail = "";

            for (int i = 0; i < recording.Ticks.Count; i++)
            {
                var tick = recording.Ticks[i];
                var (type, detail) = ClassifyTick(tick);

                if (type != currentType)
                {
                    // Close previous range
                    if (currentType.HasValue)
                    {
                        actions.Add(new ClassifiedAction
                        {
                            StartTick = rangeStart,
                            EndTick = i - 1,
                            Type = currentType.Value,
                            Detail = currentDetail,
                        });
                    }

                    currentType = type;
                    currentDetail = detail;
                    rangeStart = i;
                }
                else if (!string.IsNullOrEmpty(detail) && detail != currentDetail)
                {
                    // Same type but different detail — update detail
                    currentDetail = detail;
                }
            }

            // Close final range
            if (currentType.HasValue)
            {
                actions.Add(new ClassifiedAction
                {
                    StartTick = rangeStart,
                    EndTick = recording.Ticks.Count - 1,
                    Type = currentType.Value,
                    Detail = currentDetail,
                });
            }

            return actions;
        }

        /// <summary>
        /// Classify a single tick based on game state.
        /// Returns (ActionType, detail string).
        /// </summary>
        public static (ActionType Type, string Detail) ClassifyTick(RecordingTick tick)
        {
            // Dead
            if (!tick.Player.IsAlive)
                return (ActionType.Dead, "");

            // Hideout/town
            if (tick.AreaName != null && (tick.AreaName.Contains("Hideout") || tick.AreaName == "Oriath"))
                return (ActionType.InHideout, "");

            // Ritual shop open
            if (tick.UI.RitualWindowOpen)
                return (ActionType.RitualShop, $"tribute={tick.UI.RitualTribute}");

            // Check for mechanic proximity
            var mechanicAction = ClassifyMechanic(tick);
            if (mechanicAction.HasValue)
                return mechanicAction.Value;

            // Looting — ground labels being clicked (input has click near a label)
            if (IsLooting(tick))
                return (ActionType.Looting, "");

            // Fighting — monsters nearby and engaged
            if (tick.InCombat && tick.NearbyMonsterCount >= 3)
                return (ActionType.Fighting, $"{tick.NearbyMonsterCount} mobs");

            // Light combat while moving (1-2 mobs, skills firing passively)
            if (tick.InCombat)
                return (ActionType.Fighting, $"{tick.NearbyMonsterCount} mobs (light)");

            // Exploring — default when moving, no combat
            return (ActionType.Exploring, $"coverage={tick.ExplorationCoverage:P0}");
        }

        /// <summary>Check if the tick involves a mechanic based on nearby entities.</summary>
        private static (ActionType, string)? ClassifyMechanic(RecordingTick tick)
        {
            foreach (var entity in tick.Entities)
            {
                if (entity.Path.Contains("Ritual/RitualRuneInteractable"))
                {
                    var state = entity.States?.GetValueOrDefault("current_state") ?? 0;
                    if (state == 2) // Active ritual
                        return (ActionType.MechanicRitual, $"altar at ({entity.GridX:F0},{entity.GridY:F0}) ACTIVE");
                    if (entity.Distance < 30 && state == 1)
                        return (ActionType.MechanicRitual, $"near altar ({entity.GridX:F0},{entity.GridY:F0})");
                }

                if (entity.Path.Contains("Faridun/FaridunInitiator") && entity.Distance < 60)
                    return (ActionType.MechanicWishes, $"initiator at ({entity.GridX:F0},{entity.GridY:F0})");

                if (entity.Path.Contains("Kubera/Varashta") && entity.Distance < 30)
                    return (ActionType.MechanicWishes, "NPC interaction");

                if (entity.Path.Contains("SekhemaPortal") && entity.IsTargetable && entity.Distance < 30)
                    return (ActionType.ExitingSubZone, "near SekhemaPortal");
            }

            return null;
        }

        /// <summary>Check if inputs suggest looting (click near a ground label).</summary>
        private static bool IsLooting(RecordingTick tick)
        {
            if (tick.GroundLabels.Count == 0) return false;
            if (tick.Inputs.Count == 0) return false;

            foreach (var input in tick.Inputs)
            {
                if (input.Type != InputEventType.LeftClick || !input.X.HasValue) continue;

                // Check if click is near any ground label
                foreach (var label in tick.GroundLabels)
                {
                    if (!label.IsVisible) continue;
                    if (input.X >= label.RectX && input.X <= label.RectX + label.RectW &&
                        input.Y >= label.RectY && input.Y <= label.RectY + label.RectH)
                        return true;
                }
            }

            return false;
        }

        /// <summary>Generate a summary of classified actions.</summary>
        public static string Summarize(List<ClassifiedAction> actions, int totalTicks)
        {
            var counts = new Dictionary<ActionType, int>();
            foreach (var a in actions)
            {
                counts[a.Type] = counts.GetValueOrDefault(a.Type) + a.Duration;
            }

            var lines = new List<string> { $"Total: {totalTicks} ticks, {actions.Count} action segments" };
            foreach (var (type, ticks) in counts.OrderByDescending(kv => kv.Value))
            {
                var pct = totalTicks > 0 ? (float)ticks / totalTicks : 0;
                lines.Add($"  {type,-20} {ticks,5} ticks ({pct:P0})");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }
}
