using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class QuestCreateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is QuestCreate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var qc = (QuestCreate)change;

        if (string.IsNullOrWhiteSpace(qc.QuestId) || string.IsNullOrWhiteSpace(qc.Title))
        {
            return ChangeHandlerResult.Failure("QuestId and Title are required.");
        }

        if (context.Quests.ContainsKey(qc.QuestId))
        {
            return ChangeHandlerResult.Failure($"Quest {qc.QuestId} already exists.");
        }

        var time = await context.GetCurrentTimeAsync();

        var quest = new Quest
        {
            Id = qc.QuestId,
            Title = qc.Title,
            GiverId = qc.GiverId,
            Category = qc.Category,
            Urgency = qc.Urgency,
            RelatedLocationIds = qc.RelatedLocationIds ?? [],
            RelatedFactionIds = qc.RelatedFactionIds ?? [],
            DmNotes = qc.DmNotes,
            DeadlineDay = qc.DeadlineDay,
            CampaignName = context.CampaignName,
            LastUpdatedDay = time.TotalDaysElapsed,
            LastUpdated = DateTime.UtcNow,
            Objectives = qc.Objectives?.Select(o => new QuestObjective(o.Description, QuestState.Open, o.RewardHint, DeadlineDay: o.DeadlineDay)).ToList() ??
                         []
        };

        context.RegisterNewQuest(quest);
        await context.Session.StoreAsync(quest, ct);
        context.RecordMessage($"Created new quest: {qc.Title}");

        return ChangeHandlerResult.Ok;
    }
}

public class QuestProgressHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is QuestProgress;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var qp = (QuestProgress)change;

        if (!context.Quests.TryGetValue(qp.QuestId, out var quest))
        {
            var suggested = await context.SuggestQuestMatchAsync(qp.QuestId);
            return ChangeHandlerResult.Failure($"Quest {qp.QuestId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        var indexToUpdate = -1;

        if (!qp.ObjectiveIndex.HasValue && string.IsNullOrWhiteSpace(qp.ObjectiveName))
        {
            return ChangeHandlerResult.Failure($"Must specify either ObjectiveIndex or ObjectiveName to update progress for quest {qp.QuestId}.");
        }

        if (qp.ObjectiveIndex.HasValue)
        {
            indexToUpdate = qp.ObjectiveIndex.Value;
        }
        else if (!string.IsNullOrWhiteSpace(qp.ObjectiveName))
        {
            indexToUpdate = quest.Objectives.FindIndex(o => o.Description.Contains(qp.ObjectiveName, StringComparison.OrdinalIgnoreCase));
        }

        if (indexToUpdate < 0 || indexToUpdate >= quest.Objectives.Count)
        {
            return ChangeHandlerResult.Failure($"Objective not found in quest {qp.QuestId}.");
        }

        var oldOverallState = quest.OverallState;
        var objective = quest.Objectives[indexToUpdate];
        
        // Update objective state
        var time = await context.GetCurrentTimeAsync();
        var dayCompleted = qp.NewState switch
        {
            QuestState.Complete => time.TotalDaysElapsed,
            QuestState.Open or QuestState.InProgress => null,
            _ => objective.DayCompleted
        };
        
        // Anchor staleness tracking on first progress from Open (including direct Open → Complete).
        var dayStarted = objective.State == QuestState.Open && qp.NewState is QuestState.InProgress or QuestState.Complete
            ? time.TotalDaysElapsed
            : objective.DayStarted;

        quest.Objectives[indexToUpdate] = objective with 
        { 
            State = qp.NewState, 
            DayCompleted = dayCompleted,
            DayStarted = dayStarted
        };

        // Determine new overall state
        if (quest.Objectives.All(o => o.State == QuestState.Complete))
        {
            quest.OverallState = QuestState.Complete;
        }
        else if (quest.Objectives.Any(o => o.State == QuestState.Failed))
        {
            quest.OverallState = QuestState.Failed;
        }
        else if (quest.Objectives.Any(o => o.State is QuestState.InProgress or QuestState.Complete))
        {
            quest.OverallState = QuestState.InProgress;
        }
        else
        {
            quest.OverallState = QuestState.Open;
        }

        quest.LastUpdatedDay = time.TotalDaysElapsed;
        quest.LastUpdated = DateTime.UtcNow;

        if (oldOverallState != quest.OverallState && quest.OverallState is QuestState.Complete or QuestState.Failed)
        {
            await context.Dispatcher.DispatchMutationAsync(context, new EventOccurred
            {
                Category = EventCategory.Discovery,
                Summary = $"Quest '{quest.Title}' is now {quest.OverallState}.",
                Involved = qp.InvolvedIds ?? []
            }, ct);
        }

        context.RecordMessage($"Quest progress on '{quest.Title}': Objective {indexToUpdate} is now {qp.NewState}. {qp.NarrativeNote}");

        return ChangeHandlerResult.Ok;
    }
}
