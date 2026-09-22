using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class XpGrantChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is XpGrantChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var xpGrant = (XpGrantChange)change;

        if (!ctx.Characters.TryGetValue(xpGrant.CharacterId, out var character))
        {
            character = await ctx.Session.LoadAsync<Character>(xpGrant.CharacterId, ct);
            if (character == null)
            {
                var hints = await ctx.SuggestCharacterMatchAsync(xpGrant.CharacterId);
                var msg = $"Character {xpGrant.CharacterId} not found.";
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

        var previousXp = character.ExperiencePoints;
        character.ExperiencePoints = Math.Max(0, character.ExperiencePoints + xpGrant.Amount);

        // Don't echo xpGrant.Reason/Source back — the caller just supplied them in this same request.
        // The clamped resulting total is the only part it couldn't already compute itself.
        var direction = xpGrant.Amount >= 0 ? "gained" : "lost";
        ctx.RecordMessage(
            $"{character.Name} {direction} {Math.Abs(xpGrant.Amount)} XP ({previousXp} → {character.ExperiencePoints}).");

        return ChangeHandlerResult.Ok;
    }
}