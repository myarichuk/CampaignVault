using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Plugins;
using CampaignVault.Rulesets.Modes;

namespace CraftingMode;

/// <summary>Neutral in-tree reference plugin: multi-step crafting interaction mode.</summary>
public sealed class CraftingInteractionMode : IInteractionMode
{
    public string ModeId => "crafting";
    public string DisplayName => "Crafting";
    public IReadOnlyList<string> CompatibleSystems => [];
    public IModeStateMachine StateMachine { get; } = new CraftingStateMachine();
}

file sealed class CraftingStateMachine : IModeStateMachine
{
    public ModeEncounter CreateEncounter(string locationId, IReadOnlyList<string> participantIds) =>
        new()
        {
            LocationId = locationId,
            ModeId = "crafting",
            IsActive = true,
            Round = 1,
            Participants = participantIds.Select(id => new ModeParticipantState
            {
                CharacterId = id,
                ActionBudget = new Dictionary<string, int> { ["action"] = 1 },
                State = new Dictionary<string, object> { ["stage"] = "prepare" }
            }).ToList(),
            ActiveTurnId = participantIds.FirstOrDefault()
        };

    public IReadOnlyDictionary<string, int> GetTurnActionBudget(Character participant) =>
        new Dictionary<string, int> { ["action"] = 1 };

    public bool TryConsumeActionSlot(ModeParticipantState state, WorldChange action, out string? errorReason)
    {
        errorReason = null;
        if (!state.ActionBudget.TryGetValue("action", out var remaining) || remaining <= 0)
        {
            errorReason = "No crafting action remaining this turn.";
            return false;
        }

        state.ActionBudget["action"] = remaining - 1;
        return true;
    }

    public bool AdvanceTurn(ModeEncounter encounter)
    {
        if (!encounter.IsActive || encounter.Participants.Count == 0)
            return false;

        var idx = encounter.Participants.FindIndex(p => p.CharacterId == encounter.ActiveTurnId);
        idx = idx < 0 ? 0 : (idx + 1) % encounter.Participants.Count;
        if (idx == 0)
            encounter.Round++;

        foreach (var p in encounter.Participants)
            p.ActionBudget["action"] = 1;

        encounter.ActiveTurnId = encounter.Participants[idx].CharacterId;
        return true;
    }

    public bool IsComplete(ModeEncounter encounter, out string? outcomeNarrative)
    {
        outcomeNarrative = null;
        var done = encounter.Participants.Any(p =>
            p.State.TryGetValue("stage", out var stage) &&
            string.Equals(stage?.ToString(), "complete", StringComparison.OrdinalIgnoreCase));
        if (done)
            outcomeNarrative = "Crafting project finished.";
        return done;
    }
}

[PluginWorldChange("crafting_step")]
public sealed class CraftingStepChange : WorldChange
{
    public string CharacterId { get; set; } = null!;
    public string? Stage { get; set; }
    public string? Notes { get; set; }
}

public sealed class CraftingStepHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is CraftingStepChange;

    public Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var step = (CraftingStepChange)change;
        if (string.IsNullOrWhiteSpace(step.CharacterId))
            return Task.FromResult(ChangeHandlerResult.Failure("characterId is required."));

        var mode = context.ActiveMode;
        if (mode is null || !mode.IsActive ||
            !string.Equals(mode.ModeId, "crafting", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ChangeHandlerResult.Failure(
                "crafting_step requires an active crafting mode encounter. Enter via mode_transition first."));
        }

        var participant = mode.Participants.FirstOrDefault(p =>
            string.Equals(p.CharacterId, step.CharacterId, StringComparison.OrdinalIgnoreCase));
        if (participant is null)
            return Task.FromResult(ChangeHandlerResult.Failure($"Character '{step.CharacterId}' is not in the crafting encounter."));

        var stage = string.IsNullOrWhiteSpace(step.Stage) ? "work" : step.Stage.Trim();
        participant.State["stage"] = stage;
        if (!string.IsNullOrWhiteSpace(step.Notes))
            participant.State["notes"] = step.Notes!;

        context.RecordMessage($"Crafting step for {step.CharacterId}: stage={stage}.");
        return Task.FromResult(ChangeHandlerResult.Ok);
    }
}
