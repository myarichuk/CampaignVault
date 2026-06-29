using CampaignVault.Models;
using Raven.Client.Documents.Linq;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class PlotThreadStalenessContributor : IPressureContributor
{
    public const string StaleGroupingKey = "PlotThread:NoEngagement";
    public const string ClimaxGroupingKey = "PlotThread:Climax";
    public const string DeadlineGroupingKey = "PlotThread:Deadline";

    public PressureScope Scope => PressureScope.World;
    public int Order => 42;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();

        var threads = await ctx.Session.Query<PlotThread, PlotThread_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(3)))
            .Where(t => t.CampaignName == ctx.CampaignName
                && t.State != PlotThreadState.Dormant
                && t.State != PlotThreadState.Resolved
                && t.State != PlotThreadState.Abandoned)
            .ToListAsync(ct);

        if (threads.Count == 0)
            return pressures;

        var currentDay = (int)ctx.Time.TotalDaysElapsed;

        foreach (var thread in threads)
        {
            // Climax pressure — always surface
            if (thread.State == PlotThreadState.Climax)
            {
                pressures.Add(new WorldPressureItem(
                    PressureSeverity.EngineWarning,
                    thread.Id,
                    $"Plot thread '{thread.Title}' is at CLIMAX (tension {thread.TensionLevel}/100). Resolve it now or commit consequences: " +
                    $"[ {{\"$type\": \"plot_thread_progress\", \"plotThreadId\": \"{thread.Id}\", \"newState\": \"Resolved\", \"narrativeNote\": \"...\" }} ]",
                    ClimaxGroupingKey));
                continue;
            }

            // Deadline pressure
            if (thread.DeadlineDay.HasValue)
            {
                var daysLeft = thread.DeadlineDay.Value - currentDay;
                if (daysLeft is > 0 and <= 3)
                {
                    pressures.Add(new WorldPressureItem(
                        PressureSeverity.NarrativePrompt,
                        thread.Id,
                        $"Plot thread '{thread.Title}' deadline in {daysLeft} days. No clues discovered recently.",
                        DeadlineGroupingKey));
                }
            }

            // Staleness pressure — no clue discovered in 5+ days
            var lastEngagement = thread.Clues
                .Where(c => c.IsDiscovered && c.DiscoveredOnDay.HasValue)
                .Select(c => c.DiscoveredOnDay!.Value)
                .DefaultIfEmpty(thread.LastUpdatedDay)
                .Max();

            var daysSilent = currentDay - lastEngagement;
            if (daysSilent >= 5 && thread.State == PlotThreadState.Escalating)
            {
                var undiscoveredClue = thread.Clues.FirstOrDefault(c => !c.IsDiscovered);
                var clueHint = undiscoveredClue != null
                    ? $" Surface clue '{undiscoveredClue.Id}' via a scene or NPC."
                    : " Consider adding new clues via plot_thread_progress.";

                pressures.Add(new WorldPressureItem(
                    PressureSeverity.NarrativePrompt,
                    thread.Id,
                    $"Plot thread '{thread.Title}' (Escalating, tension {thread.TensionLevel}) has had no player engagement in {daysSilent} days.{clueHint}",
                    StaleGroupingKey));
            }
        }

        return pressures;
    }
}
