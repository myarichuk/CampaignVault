using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Models;

namespace CampaignVault.Data;

public sealed class QuestStalenessRule : ISimulationRule
{
    public string Name => "Quest Staleness and Deadlines";
    
    // Order = 45 as per plan
    public int Order => 45;

    public Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        if (context.ActiveQuests == null || !context.ActiveQuests.Any())
        {
            return Task.FromResult(new RuleResult(narratives, deltas));
        }

        foreach (var quest in context.ActiveQuests)
        {
            if (quest.OverallState != QuestState.Open && quest.OverallState != QuestState.InProgress)
                continue;

            int currentDay = context.Time.TotalDaysElapsed;

            // 1. Check Deadlines
            bool missedDeadline = false;
            var objectiveIndex = -1;
            int? missedDay = null;

            if (quest.DeadlineDay.HasValue && currentDay > quest.DeadlineDay.Value)
            {
                objectiveIndex = quest.Objectives.FindIndex(o => o.State == QuestState.Open || o.State == QuestState.InProgress);
                missedDeadline = objectiveIndex >= 0;
                missedDay = quest.DeadlineDay.Value;
            }
            else
            {
                // Check objective-specific deadlines
                objectiveIndex = quest.Objectives.FindIndex(o => (o.State == QuestState.Open || o.State == QuestState.InProgress) && o.DeadlineDay.HasValue && currentDay > o.DeadlineDay.Value);
                if (objectiveIndex >= 0)
                {
                    missedDeadline = true;
                    missedDay = quest.Objectives[objectiveIndex].DeadlineDay!.Value;
                }
            }

            if (missedDeadline)
            {
                // Fail the quest objective (which cascades to failing the quest)
                    deltas.Add(new QuestProgress
                    {
                        QuestId = quest.Id,
                        ObjectiveIndex = objectiveIndex,
                        NewState = QuestState.Failed,
                        NarrativeNote = $"Failed automatically by the engine because the deadline (Day {missedDay}) was missed."
                    });

                    // Emit an event for history
                    deltas.Add(new EventOccurred
                    {
                        Category = EventCategory.Simulation,
                        Summary = $"The deadline for '{quest.Title}' has passed. The opportunity was lost.",
                        Involved = (quest.RelatedFactionIds ?? Enumerable.Empty<string>()).Concat(quest.RelatedLocationIds ?? Enumerable.Empty<string>()).Concat(quest.GiverId != null ? new[] { quest.GiverId } : Array.Empty<string>()).Distinct().ToList()
                    });

                    // Emit a rumor
                    deltas.Add(new RumorCreate
                    {
                        RumorId = $"rumors/quest_fail_{quest.Id.Split('/').LastOrDefault()}_{currentDay}",
                        Subject = quest.Title,
                        Text = $"Rumor has it that the opportunity for '{quest.Title}' was squandered and it is now too late.",
                        RelatedLocationIds = quest.RelatedLocationIds
                    });

                    narratives.Add($"Quest '{quest.Title}' failed because its deadline of Day {missedDay} has passed.");
                
                // Skip the 10-day nag if we just failed it
                continue;
            }

            // 2. Nag for Staleness (No Deadline or Deadline not reached)
            // Find the oldest open objective
            var oldestDayStarted = quest.Objectives
                .Where(o => o.State == QuestState.Open || o.State == QuestState.InProgress)
                .Select(o => o.DayStarted ?? quest.LastUpdatedDay)
                .DefaultIfEmpty(quest.LastUpdatedDay)
                .Min();

            if (currentDay - oldestDayStarted > 10)
            {
                narratives.Add($"Quest '{quest.Title}' has been pending for over 10 days. Consider progressing or failing it.");
            }
        }

        return Task.FromResult(new RuleResult(narratives, deltas));
    }
}
