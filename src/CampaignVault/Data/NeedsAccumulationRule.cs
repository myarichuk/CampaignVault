using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Core needs & mood simulation rule.
/// 
/// Expanded reasonably from the original thin logic:
/// - Hunger, Thirst (faster), Tiredness at base rates.
/// - Low-rate arousal accumulation (was initialized but never touched before).
/// - Mood derivation now also considers sustained high tiredness or hunger affecting Morale slightly.
/// - All changes emitted as proper WorldChange deltas (NeedChange + MoodChange) for unified application.
/// </summary>
public class NeedsAccumulationRule : ISimulationRule
{
    public string Name => "Needs & Mood Accumulation";
    public int Order => 35;

    public virtual Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        // POLICY NOTE: Entities (like Character) are not currently namespaced per-campaign.
        // Therefore, this simulation rule operates globally across all campaigns.

        // Use consistent float math (addresses review point about casts)
        var days = (float)context.DaysPassed;
        var tiredMult = context.Config?.TirednessAccumulationMultiplier ?? 0.8f;
        var moraleDriftPerDay = context.Config?.MoraleDriftPerDay ?? -0.8f;
        var amount = (context.Config?.NeedAccumulationRate ?? 10f) * days;
        var perDayDeltas = NeedAccumulationMath.ComputeDeltas(context.Config, context.DaysPassed);

        foreach (var npc in context.ScheduledNpcs)
        {
            if (npc.Needs is null || npc.Psychology is null)
            {
                continue;
            }

            // Dead characters don't get hungry. There's no persisted deceased/archived flag on
            // Character (MaxHp can be 0 for entities that never had HP tracked at all, e.g. some
            // non-combatant NPCs, so only skip once MaxHp confirms HP is actually tracked).
            if (npc.MaxHp > 0 && npc.CurrentHp <= 0)
            {
                continue;
            }

            // Accumulate core needs, but cap the delta so we don't emit meaningless " +120 when already at 100"
            // (CommitChangesAsync will still clamp, but this keeps summaries and rule output cleaner)
            void AddCappedNeed(string need, float baseAmount)
            {
                var current = npc.Needs.ActiveNeeds.GetValueOrDefault(need, 0f);
                var effective = Math.Min(baseAmount, 100f - current);
                if (effective > 0.0001f)
                {
                    deltas.Add(new NeedChange { CharacterId = npc.Id, Need = need, Delta = effective });
                }
            }

            // "tiredness" is narrative fatigue (drives pressure/mood/narration), ruleset-agnostic —
            // distinct from mechanical D&D exhaustion (Attributes["exhaustion_level"], 1-6 scale).
            foreach (var (need, delta) in perDayDeltas)
            {
                AddCappedNeed(need, delta);
            }

            // Re-evaluate mood after accumulation (the actual mood value will be applied by the MoodChange we emit below)
            // We compute what the mood *should* become based on the post-delta state.
            // Note: Because deltas are applied later, we approximate using current + projected.
            var projectedHunger = Math.Clamp(npc.Needs.ActiveNeeds.GetValueOrDefault("hunger") + amount, 0f, 100f);
            var projectedTiredness = Math.Clamp(npc.Needs.ActiveNeeds.GetValueOrDefault("tiredness") + (amount * tiredMult), 0f, 100f);

            var newMood = npc.Psychology.CurrentMood ?? "Content";
            if (projectedTiredness > NpcMoodThresholds.ExhaustedTiredness)
            {
                newMood = "Exhausted";
            }
            else if (projectedHunger > NpcMoodThresholds.RavenousHunger)
            {
                newMood = "Ravenous";
            }
            else if (projectedHunger > NpcMoodThresholds.GrumpyHunger || projectedTiredness > NpcMoodThresholds.GrumpyTiredness)
            {
                newMood = "Grumpy";
            }
            else
            {
                newMood = "Content";
            }

            if (newMood != npc.Psychology.CurrentMood)
            {
                deltas.Add(new MoodChange { CharacterId = npc.Id, NewMood = newMood });
            }

            // Small morale drift on sustained bad states (reasonable expansion for "living" feel)
            if (projectedTiredness > NpcMoodThresholds.MoraleDriftTiredness || projectedHunger > NpcMoodThresholds.MoraleDriftHunger)
            {
                var moraleDrift = moraleDriftPerDay * days;
                deltas.Add(new AttributeChange
                {
                    CharacterId = npc.Id,
                    Attribute = "morale",
                    Value = moraleDrift,
                    IsDelta = true
                });
            }
        }

        return Task.FromResult(new RuleResult(narratives, deltas));
    }
}
