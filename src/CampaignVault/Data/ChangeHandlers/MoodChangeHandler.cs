using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class MoodChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is MoodChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var mood = (MoodChange)change;

        if (!context.Characters.TryGetValue(mood.CharacterId, out var character))
        {
            character = await context.Session.LoadAsync<Character>(mood.CharacterId, ct);
            if (character == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(mood.CharacterId);
                var msg = $"Character {mood.CharacterId} not found.";
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

        if (character.Psychology == null)
        {
            context.RecordMessage($"WARNING: Character {mood.CharacterId} has no PsychologyProfile during MoodChange.");
            context.RecordFailure();
            return ChangeHandlerResult.Failure();
        }

        character.Psychology.CurrentMood = mood.NewMood;
        context.RecordMessage($"Mood set to '{mood.NewMood}' for {mood.CharacterId}");

        return ChangeHandlerResult.Ok;
    }
}