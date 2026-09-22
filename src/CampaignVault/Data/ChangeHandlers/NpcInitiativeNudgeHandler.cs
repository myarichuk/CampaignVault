using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Validates the target NPC exists and clamps Intensity. The actual pending-nudge bookkeeping
/// (TurnCursor.PendingInitiativeNudgesByEntityId, consumption by the initiative scheduler) happens in
/// MutationTools after commit, from ctx.AppliedChanges — this handler has no access to TurnCursor and
/// isn't the right layer for cross-turn state (see MutationTools.ApplyInitiativeNudges).
/// </summary>
public sealed class NpcInitiativeNudgeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is NpcInitiativeNudge;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var nudge = (NpcInitiativeNudge)change;

        if (!ctx.Characters.TryGetValue(nudge.CharacterId, out _))
        {
            var character = await ctx.Session.LoadAsync<Character>(nudge.CharacterId, ct);
            if (character == null)
            {
                var hints = await ctx.SuggestCharacterMatchAsync(nudge.CharacterId);
                var msg = $"Character {nudge.CharacterId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                ctx.RecordMessage($"WARNING: {msg}");
                ctx.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            ctx.RegisterNewCharacter(character);
        }

        nudge.Intensity = Math.Clamp(nudge.Intensity, 0f, 1f);
        ctx.RecordMessage($"Initiative nudge recorded for {nudge.CharacterId} (intensity {nudge.Intensity:0.00}).");

        return ChangeHandlerResult.Ok;
    }
}
