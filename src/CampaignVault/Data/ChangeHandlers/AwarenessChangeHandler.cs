using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// awareness_change (WITNESS_CHANNEL_PLAN.md A1): sets a character alert, distracted or asleep, and/or ends their hiding. Alert is
/// stored as null so a character who never had a state reads the same as one who woke up.
/// </summary>
public sealed class AwarenessChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is AwarenessChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var ac = (AwarenessChange)change;

        if (!ctx.Characters.TryGetValue(ac.CharacterId, out var character)
            && ctx.Session != null && await ctx.Session.LoadAsync<Character>(ac.CharacterId, ct) is { } loaded)
        {
            ctx.RegisterNewCharacter(loaded);
            character = loaded;
        }

        if (character == null)
        {
            var suggested = await ctx.SuggestCharacterMatchAsync(ac.CharacterId);
            return ChangeHandlerResult.Failure(
                $"Character {ac.CharacterId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        AwarenessState? state = null;
        if (!string.IsNullOrWhiteSpace(ac.State))
        {
            // Names only: Enum.TryParse would also take "1" or "2".
            if (!Enum.GetNames<AwarenessState>().Any(n => string.Equals(n, ac.State.Trim(), StringComparison.OrdinalIgnoreCase))
                || !Enum.TryParse<AwarenessState>(ac.State.Trim(), ignoreCase: true, out var parsed))
            {
                return ChangeHandlerResult.Failure($"state must be alert, distracted or asleep (got '{ac.State}').");
            }

            state = parsed;
        }
        else if (!ac.Revealed)
        {
            return ChangeHandlerResult.Failure("awareness_change needs a state (alert, distracted, asleep) or revealed:true.");
        }

        var notes = new List<string>();
        if (state != null)
        {
            var time = await ctx.GetCurrentTimeAsync();
            character.Awareness = state == AwarenessState.Alert
                ? null
                : new Awareness { State = state.Value, Reason = ac.Reason, SinceHour = CampaignClock.Now(time) };
            notes.Add($"{character.Name} is now {state.Value.ToString().ToLowerInvariant()}{(ac.Reason != null ? $" ({ac.Reason})" : "")}.");
        }

        if (ac.Revealed && character.Concealment != null)
        {
            character.Concealment = null;
            notes.Add($"{character.Name} is no longer hidden.");
        }

        return new ChangeHandlerResult(true, notes.Count > 0 ? string.Join(" ", notes) : null);
    }
}
