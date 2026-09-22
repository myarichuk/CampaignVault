using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class MoodChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is MoodChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var mood = (MoodChange)change;

        if (!ctx.Characters.TryGetValue(mood.CharacterId, out var character))
        {
            character = await ctx.Session.LoadAsync<Character>(mood.CharacterId, ct);
            if (character == null)
            {
                var hints = await ctx.SuggestCharacterMatchAsync(mood.CharacterId);
                var msg = $"Character {mood.CharacterId} not found.";
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

        if (character.Psychology == null)
        {
            ctx.RecordMessage($"WARNING: Character {mood.CharacterId} has no PsychologyProfile during MoodChange.");
            ctx.RecordFailure();
            return ChangeHandlerResult.Failure();
        }

        character.Psychology.CurrentMood = mood.NewMood;

        // Only report an engine-computed threshold crossing (needs simulation) — new info the caller
        // couldn't have known. An explicit, LLM-authored mood change is just an echo of what it set.
        if (mood.IsEngineAuthored)
        {
            ctx.RecordMessage($"{mood.CharacterId}'s mood shifted to '{mood.NewMood}' (needs-driven).");
        }

        return ChangeHandlerResult.Ok;
    }
}