using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class RelationshipChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is RelationshipChange;

    public Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var rel = (RelationshipChange)change;

        if (!context.Characters.TryGetValue(rel.SourceId, out var source) || source is null)
        {
            context.RecordMessage($"WARNING: Character {rel.SourceId} not found during RelationshipChange.");
            context.RecordFailure();
            return Task.FromResult(ChangeHandlerResult.Failure());
        }

        source.Mind ??= new NpcMind();
        source.Mind.Relationships ??= new Dictionary<string, int>();

        var currentVal = source.Mind.Relationships.GetValueOrDefault(rel.TargetId, 0);
        source.Mind.Relationships[rel.TargetId] = Math.Clamp(currentVal + rel.Delta, -100, 100);

        context.RecordMessage($"Relationship from {rel.SourceId} to {rel.TargetId} shifted by {rel.Delta} ({rel.Reason})");

        return Task.FromResult(ChangeHandlerResult.Ok);
    }
}