using CampaignVault.Models;

namespace CampaignVault.Data;

public class ScheduleNeedSatisfactionRule : ISimulationRule
{
    public string Name => "Schedule-Driven Need Satisfaction";
    public int Order => 36;

    private static readonly Dictionary<string, List<string>> ActivityKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hunger"] = new() { "eat", "meal", "dine", "food", "feast", "breakfast", "lunch", "dinner" },
        ["thirst"] = new() { "drink", "water", "beverage", "ale", "wine" },
        ["tiredness"] = new() { "sleep", "rest", "nap", "bed", "camp" },
        ["social_drive"] = new() { "tavern", "socialize", "gather", "party", "celebrate", "mingle" },
    };

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
                var activity = routine.Activity?.ToLowerInvariant() ?? "";
                foreach (var (needName, keywords) in ActivityKeywords)
                {
                    if (keywords.Any(kw => activity.Contains(kw, StringComparison.OrdinalIgnoreCase)))
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
