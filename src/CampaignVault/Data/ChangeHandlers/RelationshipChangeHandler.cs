using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class RelationshipChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is RelationshipChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var rel = (RelationshipChange)change;

        if (!context.Characters.TryGetValue(rel.CharacterId, out var source))
        {
            source = context.Session != null ? await context.Session.LoadAsync<Character>(rel.CharacterId, ct) : null;
            if (source == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(rel.CharacterId);
                var msg = $"Character {rel.CharacterId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                context.RecordMessage($"WARNING: {msg}");
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewCharacter(source);
        }

        source.Social ??= new SocialProfile();
        source.Social.Relationships ??= new Dictionary<string, int>();

        var currentVal = source.Social.Relationships.GetValueOrDefault(rel.TargetId, 0);
        source.Social.Relationships[rel.TargetId] = Math.Clamp(currentVal + rel.Delta, -100, 100);

        context.RecordMessage($"Relationship from {rel.CharacterId} to {rel.TargetId} shifted by {rel.Delta} ({rel.Reason})");

        return ChangeHandlerResult.Ok;
    }
}