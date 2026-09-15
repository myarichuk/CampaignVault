using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class ActivityChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ActivityChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var act = (ActivityChange)change;
        
        if (!context.Characters.TryGetValue(act.CharacterId, out var character))
        {
            character = await context.Session.LoadAsync<Character>(act.CharacterId, ct);
            if (character == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(act.CharacterId);
                var msg = $"Character {act.CharacterId} not found during ActivityChange.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                context.RecordMessage("WARNING: " + msg);
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }

            context.RegisterNewCharacter(character);
        }

        if (act.NewActivity != null)
        {
            character.CurrentActivity = act.NewActivity;
        }

        if (act.NewLocationId != null || act.UpdateLocation)
        {
            // Supports explicit clears (NewLocationId=null + UpdateLocation=true) from TransientEvictionRule etc.
            // For LLM-authored partial updates that only change activity, omit newLocationId (or set UpdateLocation false).
            if (!string.IsNullOrEmpty(act.NewLocationId) && !context.Locations.TryGetValue(act.NewLocationId, out _))
            {
                var suggested = await context.SuggestLocationMatchAsync(act.NewLocationId);
                var msg = $"Location {act.NewLocationId} not found during ActivityChange.";
                if (suggested != null)
                {
                    msg += $" Did you mean: {suggested}?";
                }

                context.RecordMessage("WARNING: " + msg);
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }

            character.CurrentLocationId = act.NewLocationId;
            if (!string.IsNullOrEmpty(act.NewLocationId))
            {
                character.DepartedAtDay = null;
                character.DepartedFromLocationId = null;
            }
        }

        return ChangeHandlerResult.Ok;
    }
}