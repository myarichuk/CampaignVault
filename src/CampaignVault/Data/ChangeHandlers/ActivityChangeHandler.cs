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

        if (act.Departing == true)
        {
            return await DepartAsync(act, character, ctx, ct);
        }

        if (act.NewActivity != null)
        {
            character.CurrentActivity = act.NewActivity;
        }

        if (act.Departing == false && character.Departing != null)
        {
            character.Departing = null;
            ctx.RecordMessage($"{character.Name} turns back and is here again.");
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
            character.Departing = null;
            if (!string.IsNullOrEmpty(act.NewLocationId))
            {
                character.DepartedAtDay = null;
                character.DepartedFromLocationId = null;
            }
        }

        return ChangeHandlerResult.Ok;
    }

    /// <summary>
    /// IMPRESSIONS_PLAN.md D9: they set off. They stay where they are, but Distant from everyone there, until
    /// <c>WorldChangeDispatcher</c> completes the move.
    /// </summary>
    private static async Task<ChangeHandlerResult> DepartAsync(ActivityChange act, Character character, ChangeContext ctx, CancellationToken ct)
    {
        if (act.UpdateLocation)
        {
            const string both = "departing and updateLocation can't be combined: departing:true says they're on their way (still in "
                                + "sight), updateLocation moves them now. Pick one.";
            ctx.RecordMessage("WARNING: " + both);
            ctx.RecordFailure();
            return ChangeHandlerResult.Failure(both);
        }

        if (string.IsNullOrEmpty(character.CurrentLocationId))
        {
            var nowhere = $"{character.Name} isn't anywhere to depart from. Use updateLocation:true with newLocationId to place them.";
            ctx.RecordMessage("WARNING: " + nowhere);
            ctx.RecordFailure();
            return ChangeHandlerResult.Failure(nowhere);
        }

        Location? destination = null;
        if (!string.IsNullOrEmpty(act.NewLocationId)
            && !ctx.Locations.TryGetValue(act.NewLocationId, out destination)
            && (destination = await ctx.Session.LoadAsync<Location>(act.NewLocationId, ct)) == null)
        {
            var suggested = await ctx.SuggestLocationMatchAsync(act.NewLocationId);
            var msg = $"Location {act.NewLocationId} not found for the departure." + (suggested != null ? $" Did you mean: {suggested}?" : "");
            ctx.RecordMessage("WARNING: " + msg);
            ctx.RecordFailure();
            return ChangeHandlerResult.Failure(msg);
        }

        if (act.NewActivity != null)
        {
            character.CurrentActivity = act.NewActivity;
        }

        var time = await ctx.GetCurrentTimeAsync();
        character.Departing = new Departing
        {
            ToLocationId = destination?.Id, FromLocationId = character.CurrentLocationId, SinceHour = CampaignClock.Now(time),
        };
        ctx.RecordMessage($"{character.Name} is leaving{(destination != null ? $" for {destination.Name}" : "")}: already distant from "
                          + "everyone here (heard, but seen only in good light), gone after two more beats.");
        return ChangeHandlerResult.Ok;
    }
}
