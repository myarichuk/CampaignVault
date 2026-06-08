using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class SceneQuestStalenessPressureContributor : IPressureContributor
{
    public PressureScope Scope => PressureScope.Scene;
    public int Order => 30;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene?.ActiveQuests == null)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        var staleDays = ctx.Config.QuestStalenessDays;
        foreach (var q in ctx.Scene.ActiveQuests)
        {
            var staleAnchor = q.OldestOpenObjectiveDay > 0 ? q.OldestOpenObjectiveDay : q.LastUpdatedDay;
            if (ctx.Time.TotalDaysElapsed - staleAnchor > staleDays && !q.DeadlineDay.HasValue)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, q.QuestId,
                    $"Quest '{q.Title}' has seen no progress in over {staleDays} days. Consider advancing or failing it: [ {{ \"$type\": \"quest_progress\", \"questId\": \"{q.QuestId}\", \"objectiveIndex\": 0, \"newState\": \"InProgress\", \"narrativeNote\": \"Party investigated...\" }} ]",
                    "Quest:Stale"));
            }
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}