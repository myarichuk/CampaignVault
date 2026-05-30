using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Evaluates NPC Schedules during time advancement to determine current location and activity.
/// 
/// This is the core rule that brings the previously dead Schedule/Routine/StateModifier model to life.
/// 
/// Behavior:
/// - Matches routines against current TimeOfDay (simple string contains or "Any"/empty).
/// - Respects Probability (picks highest for determinism in first version; can become weighted random later).
/// - Applies active non-expired StateModifiers (fear, weather, quest overrides, etc.).
/// - Emits ActivityChange deltas (plus narrative) when an NPC's location or activity actually changes.
///   All deltas flow through the unified StageChangesAsync path (clamping, summary logging, etc.).
/// 
/// Future (agency/initiative):
/// - NPCs with high Willpower or certain Wants can generate autonomous EventOccurred or RelationshipChange
///   even without player input (e.g. "Aldric decides to confront the party on his own").
/// </summary>
public sealed class ScheduleEvaluationRule : ISimulationRule
{
    public string Name => "Schedule Evaluation & NPC Activity";
    public int Order => 30;

    public Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        var timeOfDay = context.Time.TimeOfDay.ToString();

        foreach (var npc in context.ScheduledNpcs)
        {
            if (npc.Schedule == null) continue;

            string? baseLocation = npc.Schedule.DefaultLocationId;
            string? baseActivity = "Idle / at default location";

            // 1. Find best matching routine for current time (base schedule, before modifiers)
            var candidates = npc.Schedule.Routines
                .Where(r => string.IsNullOrWhiteSpace(r.Condition) ||
                            r.Condition.Equals("Any", StringComparison.OrdinalIgnoreCase) ||
                            timeOfDay.Contains(r.Condition, StringComparison.OrdinalIgnoreCase) ||
                            r.Condition.Contains(timeOfDay, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Probability)
                .ToList();

            if (candidates.Count > 0)
            {
                var chosen = candidates.First();
                baseLocation = chosen.LocationId;
                baseActivity = chosen.Activity;
            }

            // 2. Apply active StateModifiers (highest priority overrides)
            var activeModifiers = (npc.Schedule.ActiveModifiers ?? new List<StateModifier>())
                .Where(m => m.ExpiryDay == null || m.ExpiryDay > context.Time.TotalDaysElapsed)
                .ToList();

            string? effectiveLocation = baseLocation;
            string? effectiveActivity = baseActivity;

            foreach (var mod in activeModifiers)
            {
                bool thisModOverrides = false;

                if (!string.IsNullOrWhiteSpace(mod.OverrideLocationId))
                {
                    effectiveLocation = mod.OverrideLocationId;
                    thisModOverrides = true;
                }

                if (!string.IsNullOrWhiteSpace(mod.OverrideActivity))
                {
                    effectiveActivity = mod.OverrideActivity;
                    thisModOverrides = true;
                }

                // Only emit the narrative when the modifier is actually causing a visible override this tick.
                // Prevents spamming the same "affected by X" message every simulation tick.
                if (thisModOverrides)
                {
                    narratives.Add($"{npc.Name} is affected by: {mod.Description}");
                }
            }

            // 3. If location or activity actually changed, emit ActivityChange delta + narrative.
            // Deltas are collected across all rules and applied in batch after the simulation step
            // via StageChangesAsync (gives us consistent clamping, logging, optimistic concurrency, etc.).
            bool locationChanged = !string.Equals(npc.CurrentLocationId, effectiveLocation, StringComparison.Ordinal);
            bool activityChanged = !string.Equals(npc.CurrentActivity, effectiveActivity, StringComparison.Ordinal);

            if (locationChanged || activityChanged)
            {
                deltas.Add(new ActivityChange
                {
                    CharacterId = npc.Id,
                    NewActivity = effectiveActivity,
                    NewLocationId = effectiveLocation,
                    Reason = activeModifiers.Count > 0 ? "Schedule evaluation + state modifier override" : "Schedule evaluation"
                });

                narratives.Add($"{npc.Name} is now {effectiveActivity} (at {effectiveLocation}).");
            }

            // 4. Tiny agency/initiative hook (per user feedback)
            // If an NPC has high Willpower and is in a "negative" mood, they may take independent action.
            if (npc.Mind != null &&
                npc.Mind.Willpower > 80 &&
                (npc.Mind.CurrentMood == "Grumpy" || npc.Mind.CurrentMood == "Ravenous" || npc.Mind.CurrentMood == "Exhausted"))
            {
                // Emit an unresolved event that the DM can choose to resolve later.
                // This is the beginning of true NPC agency.
                narratives.Add($"[Agency] {npc.Name} is growing restless and may act on their own soon.");
            }
        }

        return Task.FromResult(new RuleResult(narratives, deltas));
    }
}
