using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Rumor lifecycle rule.
///
/// Advances rumors one step at a time through the full lifecycle:
///   Nascent → Spreading (after 7 days of silence)
///   Spreading → Peak    (after another 7 days, 14 cumulative)
///   Peak → Fading       (after 14 days of silence at Peak or higher)
///   Fading → Forgotten  (after 14 days of silence at Fading)
///
/// Advancing one step per tick ensures the bell-curve lifecycle is actually traversed
/// instead of jumping directly from Nascent to Peak (the previous bug).
///
/// Future expansions: escalation based on NPC density, party involvement pressure, etc.
/// </summary>
public class RumorDecayRule : ISimulationRule
{
    public string Name => "Rumor Decay";
    public int Order => 20;

    // Thresholds in days of silence before the next state transition.
    private const int EscalationDays = 7;   // Nascent → Spreading, Spreading → Peak
    private const int DecayDays = 14;        // Peak → Fading, Fading → Forgotten

    public virtual Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        // Scoping hardened: ActiveRumors from AdvanceWorld are now pre-filtered by CampaignName (strict for rumors).
        // Rules no longer need to be global.

        foreach (var rumor in context.ActiveRumors)
        {
            if (rumor.State == RumorState.Resolved || rumor.State == RumorState.Forgotten)
            {
                continue;
            }

            var daysSinceUpdate = context.Time.TotalDaysElapsed - rumor.LastStateChangeDay;

            RumorState? nextState = rumor.State switch
            {
                // Escalation: growing rumors advance one step after EscalationDays
                RumorState.Nascent when daysSinceUpdate > EscalationDays
                    => RumorState.Spreading,
                RumorState.Spreading when daysSinceUpdate > EscalationDays
                    => RumorState.Peak,

                // Decay: stale rumors fade one step after DecayDays
                RumorState.Peak when daysSinceUpdate > DecayDays
                    => RumorState.Fading,
                RumorState.Fading when daysSinceUpdate > DecayDays
                    => RumorState.Forgotten,

                _ => null // no transition yet
            };

            if (nextState is null)
            {
                continue;
            }

            deltas.Add(new RumorEvolves { RumorId = rumor.Id, NewState = nextState.Value });

            var narrative = nextState.Value switch
            {
                RumorState.Spreading => $"The rumor '{rumor.Subject}' is beginning to spread.",
                RumorState.Peak      => $"The rumor '{rumor.Subject}' has reached peak circulation.",
                RumorState.Fading    => $"The rumor '{rumor.Subject}' is starting to fade from public memory.",
                RumorState.Forgotten => $"The rumor '{rumor.Subject}' has been forgotten.",
                _                    => $"The rumor '{rumor.Subject}' has transitioned to {nextState.Value}."
            };
            narratives.Add(narrative);
        }

        return Task.FromResult(new RuleResult(narratives, deltas));
    }
}
