using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Thin orchestrator for SceneSetupChange — synthesizes and dispatches an EngagementRelationChange
/// and/or SpatialPositionChange, reusing their existing validation, bidirectional mirroring, no-op
/// detection, and history logging unchanged. Mirrors the pattern RulesetActionHandler uses to
/// auto-apply derived mutations via DispatchMutationAsync.
/// </summary>
public sealed class SceneSetupChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is SceneSetupChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var src = (SceneSetupChange)change;

        if (src.Engagement is null && src.Spatial is null)
        {
            return ChangeHandlerResult.Failure("SceneSetupChange requires at least one of Engagement or Spatial.");
        }

        if (src.Engagement is not null)
        {
            await context.Dispatcher.DispatchMutationAsync(context, new EngagementRelationChange
            {
                CharacterId = src.CharacterId,
                TargetId = src.TargetId,
                Category = src.Engagement.Category,
                Verb = src.Engagement.Verb,
                RestrictionLevel = src.Engagement.RestrictionLevel,
                Bidirectional = src.Engagement.Bidirectional
            }, ct);
        }

        if (src.Spatial is not null)
        {
            await context.Dispatcher.DispatchMutationAsync(context, new SpatialPositionChange
            {
                CharacterId = src.CharacterId,
                TargetId = src.TargetId,
                DistanceBand = src.Spatial.DistanceBand,
                Bearing = src.Spatial.Bearing,
                Zone = src.Spatial.Zone
            }, ct);
        }

        context.RecordMessage($"SceneSetup applied for {src.CharacterId} relative to {src.TargetId}.");
        return ChangeHandlerResult.Ok;
    }
}
