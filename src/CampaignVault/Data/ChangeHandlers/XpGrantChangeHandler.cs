using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class XpGrantChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is XpGrantChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var xpGrant = (XpGrantChange)change;

        if (!context.Characters.TryGetValue(xpGrant.CharacterId, out var character))
        {
            character = await context.Session.LoadAsync<Character>(xpGrant.CharacterId, ct);
            if (character == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(xpGrant.CharacterId);
                var msg = $"Character {xpGrant.CharacterId} not found.";
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

        var previousXp = character.ExperiencePoints;
        character.ExperiencePoints = Math.Max(0, character.ExperiencePoints + xpGrant.Amount);

        var direction = xpGrant.Amount >= 0 ? "gained" : "lost";
        var reason = string.IsNullOrWhiteSpace(xpGrant.Reason) ? "" : $" ({xpGrant.Reason})";
        context.RecordMessage(
            $"{character.Name} {direction} {Math.Abs(xpGrant.Amount)} XP ({previousXp} → {character.ExperiencePoints}){reason}. Source: {xpGrant.Source}.");

        return ChangeHandlerResult.Ok;
    }
}