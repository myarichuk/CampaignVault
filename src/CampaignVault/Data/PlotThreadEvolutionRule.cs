using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Auto-escalates tension on active plot threads and transitions state when thresholds are crossed.
/// Active → Escalating at tension ≥ 60, Escalating → Climax at tension ≥ 80.
/// Dormant, Resolved, and Abandoned threads are ignored.
/// </summary>
public class PlotThreadEvolutionRule : ISimulationRule
{
    public string Name => "Plot Thread Evolution";
    public int Order => 50; // after QuestStalenessRule (45), before TransientEviction (100)

    public virtual Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        if (context.ActivePlotThreads == null || context.ActivePlotThreads.Count == 0)
            return Task.FromResult(new RuleResult(narratives, deltas));

        foreach (var thread in context.ActivePlotThreads)
        {
            if (thread.State is PlotThreadState.Dormant or PlotThreadState.Resolved or PlotThreadState.Abandoned)
                continue;

            var tensionGain = thread.State == PlotThreadState.Escalating ? 10 : 5;
            var newTension = Math.Clamp(thread.TensionLevel + (int)(tensionGain * context.DaysPassed), 0, 100);

            PlotThreadState? newState = null;
            if (thread.State == PlotThreadState.Active && newTension >= 60)
            {
                newState = PlotThreadState.Escalating;
                narratives.Add($"Plot thread '{thread.Title}' has escalated — tension reached {newTension}. Consequences are becoming imminent.");
            }
            else if (thread.State == PlotThreadState.Escalating && newTension >= 80)
            {
                newState = PlotThreadState.Climax;
                narratives.Add($"Plot thread '{thread.Title}' has reached CLIMAX — tension {newTension}/100. Resolution or disaster must occur soon.");
            }

            if (newTension != thread.TensionLevel || newState.HasValue)
            {
                deltas.Add(new PlotThreadProgress
                {
                    PlotThreadId = thread.Id,
                    TensionDelta = newTension - thread.TensionLevel,
                    NewState = newState,
                    NarrativeNote = newState.HasValue
                        ? $"Engine auto-escalated to {newState} (tension {newTension})."
                        : null
                });
            }

            if (thread.State == PlotThreadState.Climax)
            {
                narratives.Add($"URGENT: Plot thread '{thread.Title}' is at Climax (tension {thread.TensionLevel}/100). Resolve or commit consequences now.");
            }
        }

        return Task.FromResult(new RuleResult(narratives, deltas));
    }
}
