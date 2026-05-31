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
public sealed class NeedsAccumulationRule : ISimulationRule
{
    public string Name => "Needs & Mood Accumulation";
    public int Order => 10;

    public Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        // Use consistent float math (addresses review point about casts)
        float days = (float)context.DaysPassed;
        var amount = 10f * days;

        foreach (var npc in context.ScheduledNpcs)
        {
            if (npc.Mind is null) continue;

            // Accumulate core needs, but cap the delta so we don't emit meaningless " +120 when already at 100"
            // (CommitChangesAsync will still clamp, but this keeps summaries and rule output cleaner)
            void AddCappedNeed(string need, float baseAmount)
            {
                var current = npc.Mind.Needs.GetValueOrDefault(need, 0f);
                var effective = Math.Min(baseAmount, 100f - current);
                if (effective > 0.0001f)
                {
                    deltas.Add(new NeedChange { CharacterId = npc.Id, Need = need, Delta = effective });
                }
            }

            AddCappedNeed("hunger", amount);
            AddCappedNeed("thirst", amount * 1.2f);
            AddCappedNeed("tiredness", amount * 0.8f);
            AddCappedNeed("social_drive", amount * 0.15f); // low rate, was previously dead

            // Re-evaluate mood after accumulation (the actual mood value will be applied by the MoodChange we emit below)
            // We compute what the mood *should* become based on the post-delta state.
            // Note: Because deltas are applied later, we approximate using current + projected.
            var projectedHunger = Math.Clamp(npc.Mind.Needs.GetValueOrDefault("hunger") + amount, 0f, 100f);
            var projectedTiredness = Math.Clamp(npc.Mind.Needs.GetValueOrDefault("tiredness") + (amount * 0.8f), 0f, 100f);

            string newMood = npc.Mind.CurrentMood ?? "Content";
            if (projectedTiredness > NpcMoodThresholds.ExhaustedTiredness) newMood = "Exhausted";
            else if (projectedHunger > NpcMoodThresholds.RavenousHunger) newMood = "Ravenous";
            else if (projectedHunger > NpcMoodThresholds.GrumpyHunger || projectedTiredness > NpcMoodThresholds.GrumpyTiredness) newMood = "Grumpy";
            else newMood = "Content";

            if (newMood != npc.Mind.CurrentMood)
            {
                deltas.Add(new MoodChange { CharacterId = npc.Id, NewMood = newMood });
            }

            // Small morale drift on sustained bad states (reasonable expansion for "living" feel)
            if (projectedTiredness > NpcMoodThresholds.MoraleDriftTiredness || projectedHunger > NpcMoodThresholds.MoraleDriftHunger)
            {
                var moraleDrift = -0.8f * days; // slow negative drift (consistent float)
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
