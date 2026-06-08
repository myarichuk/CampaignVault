using CampaignVault.Data.Initiative;
using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Flags NPCs whose high needs conflict with current activity (sim → read-side bridge).
/// Runs after <see cref="NeedsAccumulationRule"/> (order 35).
/// </summary>
public class NeedConflictRule : ISimulationRule
{
    public string Name => "Need / Activity Conflict";
    public int Order => 36;

    public virtual Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var config = context.Config ?? new CampaignConfig();
        var deltas = new List<WorldChange>();
        var narratives = new List<string>();

        foreach (var npc in context.ScheduledNpcs)
        {
            if (npc.Needs is null)
            {
                continue;
            }

            var wasActive = npc.Needs.ActivityConflictActive;
            var (hasConflict, need, _) = NeedActivityConflictHelper.EvaluateConflict(npc, config);

            if (hasConflict && !string.IsNullOrWhiteSpace(need))
            {
                npc.Needs.ActivityConflictActive = true;
                npc.Needs.ActivityConflictNeed = need;

                if (!wasActive)
                {
                    narratives.Add($"{npc.Name} is struggling — high {need} while {npc.CurrentActivity}.");
                    if (need == "tiredness" && npc.Psychology != null
                        && npc.Psychology.CurrentMood != "Exhausted")
                    {
                        deltas.Add(new MoodChange { CharacterId = npc.Id, NewMood = "Exhausted" });
                    }
                }
            }
            else
            {
                npc.Needs.ActivityConflictActive = false;
                npc.Needs.ActivityConflictNeed = null;
            }
        }

        return Task.FromResult(new RuleResult(narratives, deltas));
    }
}