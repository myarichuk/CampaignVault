using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Evaluates and fires world events based on their trigger types.
/// - TimeBased: fires on interval, status stays Pending (recurring).
/// - Scheduled: fires once at TargetDay, transitions to Triggered.
/// - Conditional: fires when condition met (Phase 2), transitions to Triggered.
/// </summary>
public class WorldEventRule : ISimulationRule
{
    public string Name => "World Event";
    public int Order => 55;

    private readonly Func<double> _nextDouble;
    private readonly Func<int, int> _nextInt;

    public WorldEventRule() : this(() => Random.Shared.NextDouble(), max => Random.Shared.Next(max)) { }

    public WorldEventRule(Func<double> nextDouble, Func<int, int> nextInt)
    {
        _nextDouble = nextDouble;
        _nextInt = nextInt;
    }

    public virtual Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        if (context.ActiveWorldEvents == null || context.ActiveWorldEvents.Count == 0)
        {
            return Task.FromResult(new RuleResult(narratives, deltas));
        }

        var currentDays = (int)context.Time.TotalDaysElapsed;
        var previousDays = currentDays - (int)context.DaysPassed;

        foreach (var evt in context.ActiveWorldEvents)
        {
            if (evt.Status is not WorldEventStatus.Pending)
                continue;

            // Check prevention condition first (only for one-shot events)
            if (evt.TriggerType is WorldEventTriggerType.Scheduled or WorldEventTriggerType.Conditional)
            {
                if (evt.PreventionCondition != null && EvaluateCondition(evt.PreventionCondition, context))
                {
                    deltas.Add(new WorldEventStatusChange
                    {
                        WorldEventId = evt.Id,
                        NewStatus = WorldEventStatus.Prevented,
                        NarrativeNote = $"Event prevented (prevention condition satisfied)."
                    });
                    narratives.Add($"World event '{evt.Title}' has been prevented.");
                    continue;
                }
            }

            // Evaluate trigger
            if (evt.TriggerType == WorldEventTriggerType.TimeBased)
            {
                if (evt.IntervalDays.HasValue && evt.IntervalDays.Value > 0)
                {
                    var cyclesElapsed = (currentDays / evt.IntervalDays.Value) - (previousDays / evt.IntervalDays.Value);
                    if (cyclesElapsed > 0 && evt.LastTriggeredDay != currentDays)
                    {
                        // Fire effects for TimeBased event
                        EmitEventEffects(evt, context, deltas);

                        deltas.Add(new WorldEventStatusChange
                        {
                            WorldEventId = evt.Id,
                            NewStatus = null,
                            LastTriggeredDay = currentDays,
                            NarrativeNote = $"Recurring event fired (interval: {evt.IntervalDays} days)."
                        });

                        narratives.Add($"World event '{evt.Title}' has occurred (recurring).");
                    }
                }
            }
            else if (evt.TriggerType == WorldEventTriggerType.Scheduled)
            {
                if (evt.TargetDay.HasValue && currentDays >= evt.TargetDay.Value)
                {
                    // Fire effects and transition to Triggered
                    EmitEventEffects(evt, context, deltas);

                    deltas.Add(new WorldEventStatusChange
                    {
                        WorldEventId = evt.Id,
                        NewStatus = WorldEventStatus.Triggered,
                        NarrativeNote = $"Scheduled event fired on day {currentDays}."
                    });

                    narratives.Add($"World event '{evt.Title}' has triggered.");
                }
            }
            else if (evt.TriggerType == WorldEventTriggerType.Conditional)
            {
                // Phase 2: evaluate conditional
                if (evt.Condition != null && EvaluateCondition(evt.Condition, context))
                {
                    // Fire effects and transition to Triggered
                    EmitEventEffects(evt, context, deltas);

                    deltas.Add(new WorldEventStatusChange
                    {
                        WorldEventId = evt.Id,
                        NewStatus = WorldEventStatus.Triggered,
                        NarrativeNote = $"Conditional event fired (condition satisfied)."
                    });

                    narratives.Add($"World event '{evt.Title}' has triggered (condition met).");
                }
            }
        }

        return Task.FromResult(new RuleResult(narratives, deltas));
    }

    private void EmitEventEffects(WorldEvent evt, SimulationContext context, List<WorldChange> deltas)
    {
        if (evt.Effects == null || evt.Effects.Count == 0)
            return;

        foreach (var effect in evt.Effects)
        {
            switch (effect.Kind)
            {
                case WorldEventEffectKind.RumorCreate:
                    if (!string.IsNullOrWhiteSpace(effect.RumorSubject) && !string.IsNullOrWhiteSpace(effect.Text))
                    {
                        deltas.Add(new RumorCreate
                        {
                            RumorId = $"rumors/event_{evt.Id.Split('/').LastOrDefault()}_{context.Time.TotalDaysElapsed}_{Guid.NewGuid().ToString("N")[..4]}",
                            Subject = effect.RumorSubject,
                            Text = effect.Text
                        });
                    }
                    break;

                case WorldEventEffectKind.FactionStateChange:
                    if (!string.IsNullOrWhiteSpace(effect.TargetFactionId) && effect.NewStance.HasValue)
                    {
                        deltas.Add(new FactionStateChange
                        {
                            FactionId = effect.TargetFactionId,
                            NewStance = effect.NewStance.Value,
                            Narrative = $"Faction state changed via world event '{evt.Title}'."
                        });

                        if (effect.InfluenceDelta.HasValue)
                        {
                            // Will be handled by FactionEcosystemRule or similar
                        }
                    }
                    break;

                case WorldEventEffectKind.EventOccurred:
                    deltas.Add(new EventOccurred
                    {
                        Category = EventCategory.Simulation,
                        Summary = effect.Text ?? evt.Title,
                        Involved = []
                    });
                    break;

                case WorldEventEffectKind.KnowledgeUpdate:
                    if (!string.IsNullOrWhiteSpace(effect.Text))
                    {
                        // KnowledgeUpdate requires a characterId to update, which isn't available in effect data.
                        // For now, emit an EventOccurred instead. Future: extend effect to include targetCharacterId.
                        deltas.Add(new EventOccurred
                        {
                            Category = EventCategory.Simulation,
                            Summary = $"Knowledge update: {effect.Text}",
                            Involved = []
                        });
                    }
                    break;
            }
        }
    }

    private bool EvaluateCondition(WorldEventCondition condition, SimulationContext context)
    {
        if (condition == null)
            return false;

        // Handle conjunction (AllOf)
        if (condition.AllOf != null && condition.AllOf.Count > 0)
        {
            return condition.AllOf.All(c => EvaluateCondition(c, context));
        }

        // Evaluate individual condition
        return condition.Kind switch
        {
            WorldEventConditionKind.FactionInfluenceAtLeast =>
                EvaluateFactionInfluenceCondition(condition, context, atLeast: true),
            WorldEventConditionKind.FactionInfluenceAtMost =>
                EvaluateFactionInfluenceCondition(condition, context, atLeast: false),
            WorldEventConditionKind.FactionStanceToward =>
                EvaluateFactionStanceCondition(condition, context),
            WorldEventConditionKind.PlotThreadStateIs =>
                EvaluatePlotThreadStateCondition(condition, context),
            WorldEventConditionKind.PlotThreadTensionAtLeast =>
                EvaluatePlotThreadTensionCondition(condition, context),
            WorldEventConditionKind.QuestStateIs =>
                EvaluateQuestStateCondition(condition, context),
            WorldEventConditionKind.DaysSinceDayElapsed =>
                EvaluateDaysSinceCondition(condition, context),
            _ => false
        };
    }

    private bool EvaluateFactionInfluenceCondition(WorldEventCondition condition, SimulationContext context, bool atLeast)
    {
        if (!condition.NumericThreshold.HasValue || string.IsNullOrEmpty(condition.TargetEntityId))
            return false;

        var faction = context.ActiveFactions?.FirstOrDefault(f => f.Id == condition.TargetEntityId);
        if (faction == null)
            return false;

        return atLeast
            ? faction.InfluenceLevel >= condition.NumericThreshold.Value
            : faction.InfluenceLevel <= condition.NumericThreshold.Value;
    }

    private bool EvaluateFactionStanceCondition(WorldEventCondition condition, SimulationContext context)
    {
        if (string.IsNullOrEmpty(condition.TargetEntityId) || string.IsNullOrEmpty(condition.EnumValue))
            return false;

        if (!Enum.TryParse<FactionStance>(condition.EnumValue, ignoreCase: true, out var targetStance))
            return false;

        var parts = condition.TargetEntityId.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        var factionAId = parts[0];
        var factionBId = parts[1];

        var factionA = context.ActiveFactions?.FirstOrDefault(f => f.Id == factionAId);
        if (factionA == null)
            return false;

        factionA.StanceToward.TryGetValue(factionBId, out var stance);
        return stance == targetStance;
    }

    private bool EvaluatePlotThreadStateCondition(WorldEventCondition condition, SimulationContext context)
    {
        if (string.IsNullOrEmpty(condition.TargetEntityId) || string.IsNullOrEmpty(condition.EnumValue))
            return false;

        if (!Enum.TryParse<PlotThreadState>(condition.EnumValue, ignoreCase: true, out var targetState))
            return false;

        var thread = context.ActivePlotThreads?.FirstOrDefault(t => t.Id == condition.TargetEntityId);
        return thread?.State == targetState;
    }

    private bool EvaluatePlotThreadTensionCondition(WorldEventCondition condition, SimulationContext context)
    {
        if (!condition.NumericThreshold.HasValue || string.IsNullOrEmpty(condition.TargetEntityId))
            return false;

        var thread = context.ActivePlotThreads?.FirstOrDefault(t => t.Id == condition.TargetEntityId);
        return thread?.TensionLevel >= condition.NumericThreshold.Value;
    }

    private bool EvaluateQuestStateCondition(WorldEventCondition condition, SimulationContext context)
    {
        if (string.IsNullOrEmpty(condition.TargetEntityId) || string.IsNullOrEmpty(condition.EnumValue))
            return false;

        if (!Enum.TryParse<QuestState>(condition.EnumValue, ignoreCase: true, out var targetState))
            return false;

        var quest = context.ActiveQuests?.FirstOrDefault(q => q.Id == condition.TargetEntityId);
        return quest?.OverallState == targetState;
    }

    private bool EvaluateDaysSinceCondition(WorldEventCondition condition, SimulationContext context)
    {
        if (!condition.NumericThreshold.HasValue || string.IsNullOrEmpty(condition.TargetEntityId))
            return false;

        if (!int.TryParse(condition.TargetEntityId, out var targetDay))
            return false;

        var daysSince = context.Time.TotalDaysElapsed - targetDay;
        return daysSince >= condition.NumericThreshold.Value;
    }
}
