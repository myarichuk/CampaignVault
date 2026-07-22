using CampaignVault.Models;

namespace CampaignVault.Data;

public class ScheduleNeedSatisfactionRule : ISimulationRule
{
    public string Name => "Schedule-Driven Need Satisfaction";
    public int Order => 36;

    public async Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<RuleNarrative>();
        var deltas = new List<WorldChange>();
        var baseline = context.Config?.NeedSatisfactionBaseline ?? 20;

        foreach (var npc in context.ScheduledNpcs)
        {
            if (npc.Schedule?.Routines == null || npc.Schedule.Routines.Count == 0)
                continue;

            var matchedNeeds = new HashSet<string>();

            foreach (var routine in npc.Schedule.Routines)
            {
                var activity = routine.Activity;
                foreach (var needName in NeedSatisfyingActivityKeywords.Map.Keys)
                {
                    if (NeedSatisfyingActivityKeywords.IsSatisfying(needName, activity))
                    {
                        matchedNeeds.Add(needName);
                    }
                }
            }

            if (matchedNeeds.Count > 0 && npc.Needs != null)
            {
                foreach (var needName in matchedNeeds)
                {
                    var current = npc.Needs.ActiveNeeds.GetValueOrDefault(needName, 0f);
                    var delta = -(current - baseline);

                    if (delta < -0.0001f)
                    {
                        deltas.Add(new NeedChange
                        {
                            CharacterId = npc.Id,
                            Need = needName,
                            Delta = delta,
                        });
                    }
                }
            }
        }

        return new RuleResult(narratives, deltas);
    }
}
