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

        // Only report an engine-computed threshold crossing (needs simulation) — new info the caller
        // couldn't have known. An explicit, LLM-authored mood change is just an echo of what it set.
        if (mood.IsEngineAuthored)
        {
            context.RecordMessage($"{mood.CharacterId}'s mood shifted to '{mood.NewMood}' (needs-driven).");
        }

        return ChangeHandlerResult.Ok;
    }
}