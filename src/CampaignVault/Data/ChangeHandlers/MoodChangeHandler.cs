using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class MoodChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is MoodChange;

    public Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var mood = (MoodChange)change;

        if (!context.Characters.TryGetValue(mood.CharacterId, out var character) || character?.Psychology is null)
        {
            context.RecordMessage($"WARNING: Character {mood.CharacterId} not found or has no PsychologyProfile during MoodChange.");
            context.RecordFailure();
            return Task.FromResult(ChangeHandlerResult.Failure());
        }

        character.Psychology.CurrentMood = mood.NewMood;
        context.RecordMessage($"Mood set to '{mood.NewMood}' for {mood.CharacterId}");

        return Task.FromResult(ChangeHandlerResult.Ok);
    }
}