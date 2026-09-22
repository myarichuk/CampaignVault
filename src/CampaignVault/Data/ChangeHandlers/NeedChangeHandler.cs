using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class NeedChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is NeedChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var nc = (NeedChange)change;

        if (!ctx.Characters.TryGetValue(nc.CharacterId, out var character))
        {
            character = await ctx.Session.LoadAsync<Character>(nc.CharacterId, ct);
            if (character == null)
            {
                var hints = await ctx.SuggestCharacterMatchAsync(nc.CharacterId);
                var msg = $"Character {nc.CharacterId} not found.";
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

        if (character.Needs == null)
        {
            ctx.RecordMessage($"WARNING: Character {nc.CharacterId} has no NeedsProfile during NeedChange.");
            ctx.RecordFailure();
            return ChangeHandlerResult.Failure();
        }

        var current = character.Needs.ActiveNeeds.GetValueOrDefault(nc.Need, 0f);
        var newValue = Math.Clamp(current + nc.Delta, 0f, 100f);

        // Replace the entire ActiveNeeds dictionary to ensure RavenDB tracks the change.
        // Modifying a nested dictionary in-place doesn't trigger change detection in RavenDB.
        var updatedNeeds = new Dictionary<string, float>(character.Needs.ActiveNeeds)
        {
            [nc.Need] = newValue
        };
        character.Needs.ActiveNeeds = updatedNeeds;

        // No success message: the caller specified the need/delta itself (nothing computed to echo
        // back), and background accumulation ticks were already silent before this.

        return ChangeHandlerResult.Ok;
    }
}