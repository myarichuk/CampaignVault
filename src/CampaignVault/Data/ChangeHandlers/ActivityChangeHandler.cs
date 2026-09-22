using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class ActivityChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ActivityChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var act = (ActivityChange)change;
        
        if (!ctx.Characters.TryGetValue(act.CharacterId, out var character))
        {
            character = await ctx.Session.LoadAsync<Character>(act.CharacterId, ct);
            if (character == null)
            {
                var hints = await ctx.SuggestCharacterMatchAsync(act.CharacterId);
                var msg = $"Character {act.CharacterId} not found during ActivityChange.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                ctx.RecordMessage("WARNING: " + msg);
                ctx.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }

            ctx.RegisterNewCharacter(character);
        }

        if (act.NewActivity != null)
        {
            character.CurrentActivity = act.NewActivity;
        }

        if (act.UpdateLocation)
        {
            // Supports explicit clears (NewLocationId=null + UpdateLocation=true) from TransientEvictionRule etc.
            // For LLM-authored partial updates that only change activity, omit newLocationId (or set UpdateLocation false).
            // UpdateLocation is the sole authority for whether this change touches location — a non-null
            // NewLocationId with UpdateLocation:false must not relocate the character (see RestChangeHandler,
            // which intentionally never moves the character on rest).
            if (!string.IsNullOrEmpty(act.NewLocationId) && !ctx.Locations.TryGetValue(act.NewLocationId, out _))
            {
                var suggested = await ctx.SuggestLocationMatchAsync(act.NewLocationId);
                var msg = $"Location {act.NewLocationId} not found during ActivityChange.";
                if (suggested != null)
                {
                    msg += $" Did you mean: {suggested}?";
                }

                ctx.RecordMessage("WARNING: " + msg);
                ctx.RecordFailure();
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