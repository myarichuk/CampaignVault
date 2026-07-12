using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Rumor lifecycle rule.
///
/// Advances rumors through the full lifecycle, one threshold-crossing step at a time:
///   Nascent → Spreading (after 7 days of silence)
///   Spreading → Peak    (after another 7 days, 14 cumulative)
///   Peak → Fading       (after 14 days of silence at Peak or higher)
///   Fading → Forgotten  (after 14 days of silence at Fading)
///
/// A single AdvanceWorld call can cross multiple thresholds (e.g. a 30-day time skip pushes a
/// Nascent rumor through Spreading and on to Peak in one tick) — the loop below traverses every
/// intermediate state instead of jumping directly from Nascent to Peak (which would skip the
/// "spreading" beat) or stalling at one step per call regardless of how much time passed.
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
        var narratives = new List<RuleNarrative>();
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
            var currentState = rumor.State;

            // Traverse every threshold crossed by this time skip, not just the first.
            while (true)
            {
                RumorState? nextState = currentState switch
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
                    break;
                }

                currentState = nextState.Value;

                // Escalation transitions (real state changes) persist; decay transitions are routine
                var persist = nextState.Value is RumorState.Spreading or RumorState.Peak;
                var narrative = nextState.Value switch
                {
                    RumorState.Spreading => $"The rumor '{rumor.Subject}' is beginning to spread.",
                    RumorState.Peak      => $"The rumor '{rumor.Subject}' has reached peak circulation.",
                    RumorState.Fading    => $"The rumor '{rumor.Subject}' is starting to fade from public memory.",
                    RumorState.Forgotten => $"The rumor '{rumor.Subject}' has been forgotten.",
                    _                    => $"The rumor '{rumor.Subject}' has transitioned to {nextState.Value}."
                };
                narratives.Add(new RuleNarrative(narrative, Persist: persist));

                // Consume the days spent on this transition before checking the next threshold.
                daysSinceUpdate -= nextState.Value is RumorState.Spreading or RumorState.Peak
                    ? EscalationDays
                    : DecayDays;
            }

            // Update the final state if it changed
            if (currentState != rumor.State)
            {
                deltas.Add(new RumorEvolves { RumorId = rumor.Id, NewState = currentState });
            }
        }

        return Task.FromResult(new RuleResult(narratives, deltas));
    }
}
