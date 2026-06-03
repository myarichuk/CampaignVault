using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class ActivityChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ActivityChange;

    public Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var act = (ActivityChange)change;

        if (!context.Characters.TryGetValue(act.CharacterId, out var character))
        {
            context.RecordMessage($"WARNING: Character {act.CharacterId} not found during ActivityChange.");
            context.RecordFailure();
            return Task.FromResult(ChangeHandlerResult.Failure());
        }

        if (act.NewActivity != null)
            character.CurrentActivity = act.NewActivity;

        if (act.NewLocationId != null || act.UpdateLocation)
        {
            // Supports explicit clears (NewLocationId=null + UpdateLocation=true) from TransientEvictionRule etc.
            // For LLM-authored partial updates that only change activity, omit newLocationId (or set UpdateLocation false).
            character.CurrentLocationId = act.NewLocationId;
        }

        context.RecordMessage($"Activity updated for {act.CharacterId}: {act.NewActivity ?? "(unchanged)"} @ {act.NewLocationId ?? "(unchanged)"}");

        return Task.FromResult(ChangeHandlerResult.Ok);
    }
}