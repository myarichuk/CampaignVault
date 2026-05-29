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

    public Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        var amount = (float)(10.0 * context.DaysPassed);

        foreach (var npc in context.ScheduledNpcs)
        {
            if (npc.Mind is null) continue;

            // Accumulate core needs (positive deltas = increasing deprivation)
            deltas.Add(new NeedChange { CharacterId = npc.Id, Need = "hunger", Delta = amount });
            deltas.Add(new NeedChange { CharacterId = npc.Id, Need = "thirst", Delta = amount * 1.2f });
            deltas.Add(new NeedChange { CharacterId = npc.Id, Need = "tiredness", Delta = amount * 0.8f });
            deltas.Add(new NeedChange { CharacterId = npc.Id, Need = "arousal", Delta = amount * 0.15f }); // low rate, was previously dead

            // Re-evaluate mood after accumulation (the actual mood value will be applied by the MoodChange we emit below)
            // We compute what the mood *should* become based on the post-delta state.
            // Note: Because deltas are applied later, we approximate using current + projected.
            var projectedHunger = Math.Clamp(npc.Mind.Needs.GetValueOrDefault("hunger") + amount, 0f, 100f);
            var projectedTiredness = Math.Clamp(npc.Mind.Needs.GetValueOrDefault("tiredness") + (amount * 0.8f), 0f, 100f);

            string newMood = npc.Mind.CurrentMood ?? "Content";
            if (projectedTiredness > 80) newMood = "Exhausted";
            else if (projectedHunger > 70) newMood = "Ravenous";
            else if (projectedHunger > 40 || projectedTiredness > 40) newMood = "Grumpy";
            else newMood = "Content";

            if (newMood != npc.Mind.CurrentMood)
            {
                deltas.Add(new MoodChange { CharacterId = npc.Id, NewMood = newMood });
            }

            // Small morale drift on sustained bad states (reasonable expansion for "living" feel)
            if (projectedTiredness > 75 || projectedHunger > 65)
            {
                var moraleDrift = -0.8f * (float)context.DaysPassed; // slow negative drift
                deltas.Add(new AttributeChange
                {
                    CharacterId = npc.Id,
                    Attribute = "morale",
                    Value = Math.Clamp(npc.Mind.Morale + moraleDrift, 0f, 100f)
                });
            }
        }

        return Task.FromResult(new RuleResult(narratives, deltas));
    }
}
