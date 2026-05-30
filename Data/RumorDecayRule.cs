using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Rumor lifecycle rule.
/// Currently only auto-fades rumors after 14 days of silence (preserves original behavior).
/// Future expansions (per V4 vision): escalation, spreading based on NPC density, resolution pressure, etc.
/// </summary>
public sealed class RumorDecayRule : ISimulationRule
{
    public string Name => "Rumor Decay";
    public int Order => 20;

    public Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        foreach (var rumor in context.ActiveRumors)
        {
            if (rumor.State == RumorState.Resolved || rumor.State == RumorState.Forgotten)
                continue;

            var daysSinceUpdate = context.Time.TotalDaysElapsed - rumor.LastStateChangeDay;

            if (daysSinceUpdate > 14)
            {
                if (rumor.State == RumorState.Nascent || rumor.State == RumorState.Spreading)
                {
                    deltas.Add(new RumorEvolves
                    {
                        RumorId = rumor.Id,
                        NewState = RumorState.Peak
                    });
                    narratives.Add($"The rumor '{rumor.Subject}' has reached peak circulation.");
                }
                else
                {
                    deltas.Add(new RumorEvolves
                    {
                        RumorId = rumor.Id,
                        NewState = RumorState.Fading
                    });
                    narratives.Add($"The rumor '{rumor.Subject}' is starting to fade from public memory.");
                }
            }
        }

        return Task.FromResult(new RuleResult(narratives, deltas));
    }
}
