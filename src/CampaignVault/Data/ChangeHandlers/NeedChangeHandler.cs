using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class NeedChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is NeedChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var nc = (NeedChange)change;

        if (!context.Characters.TryGetValue(nc.CharacterId, out var character))
        {
            character = context.Session != null ? await context.Session.LoadAsync<Character>(nc.CharacterId, ct) : null;
            if (character == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(nc.CharacterId);
                var msg = $"Character {nc.CharacterId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                context.RecordMessage($"WARNING: {msg}");
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewCharacter(character);
        }

        if (character.Needs == null)
        {
            context.RecordMessage($"WARNING: Character {nc.CharacterId} has no NeedsProfile during NeedChange.");
            context.RecordFailure();
            return ChangeHandlerResult.Failure();
        }

        var current = character.Needs.ActiveNeeds.GetValueOrDefault(nc.Need, 0f);
        character.Needs.ActiveNeeds[nc.Need] = Math.Clamp(current + nc.Delta, 0f, 100f);

        context.RecordMessage($"Need '{nc.Need}' adjusted for {nc.CharacterId} by {nc.Delta}");

        return ChangeHandlerResult.Ok;
    }
}