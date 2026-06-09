using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class QuestDeadlinePressureContributor : IPressureContributor
{
    public const string ApproachingDeadlineGroupingKey = "Quest:ApproachingDeadline";
    public const string MissedDeadlineGroupingKey = "Quest:MissedDeadline";

    public PressureScope Scope => PressureScope.Both;
    public int Order => 40;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.QuestDeadlines == null)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        var currentDay = (int)ctx.Time.TotalDaysElapsed;
        foreach (var (id, title, deadline) in ctx.QuestDeadlines.Where(x => x.DeadlineDay.HasValue))
        {
            var daysLeft = deadline!.Value - currentDay;
            if (daysLeft > 0 && daysLeft <= 3)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, id,
                    $"Quest '{title}' deadline in {daysLeft} days (Day {deadline}). Progress or fail it: [ {{\"$type\": \"quest_progress\", \"questId\": \"{id}\", \"objectiveIndex\": 0, \"newState\": \"Complete\", \"narrativeNote\": \"...\" }} ] (or Failed).",
                    ApproachingDeadlineGroupingKey));
            }
            else if (daysLeft <= 0)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.Simulation, id, $"Quest '{title}' deadline passed. Engine may have auto-failed objectives.", MissedDeadlineGroupingKey));
            }
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}