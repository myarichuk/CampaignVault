using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class SpatialRelationChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is SpatialRelationChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var src = (SpatialRelationChange)change;

        if (!context.Characters.TryGetValue(src.ActorId, out var actor))
        {
            actor = context.Session != null ? await context.Session.LoadAsync<Character>(src.ActorId, ct) : null;
            if (actor == null) return ChangeHandlerResult.Failure($"Actor {src.ActorId} not found.");
            context.RegisterNewCharacter(actor);
        }

        if (!context.Characters.TryGetValue(src.TargetId, out var target))
        {
            target = context.Session != null ? await context.Session.LoadAsync<Character>(src.TargetId, ct) : null;
            if (target == null) return ChangeHandlerResult.Failure($"Target {src.TargetId} not found.");
            context.RegisterNewCharacter(target);
        }

        actor.SystemStats.SpatialRelations ??= new List<SpatialRelation>();
        target.SystemStats.SpatialRelations ??= new List<SpatialRelation>();

        if (string.IsNullOrEmpty(src.RelationType))
        {
            actor.SystemStats.SpatialRelations.RemoveAll(r => r.TargetId == src.TargetId);
            if (src.Bidirectional)
            {
                target.SystemStats.SpatialRelations.RemoveAll(r => r.TargetId == src.ActorId);
            }
            context.RecordMessage($"SpatialRelation removed between {src.ActorId} and {src.TargetId}.");
        }
        else
        {
            actor.SystemStats.SpatialRelations.RemoveAll(r => r.TargetId == src.TargetId);
            actor.SystemStats.SpatialRelations.Add(new SpatialRelation { TargetId = src.TargetId, RelationType = src.RelationType });

            if (src.Bidirectional)
            {
                target.SystemStats.SpatialRelations.RemoveAll(r => r.TargetId == src.ActorId);
                var inverse = GetInverseType(src.RelationType);
                target.SystemStats.SpatialRelations.Add(new SpatialRelation { TargetId = src.ActorId, RelationType = inverse });
            }
            context.RecordMessage($"SpatialRelation established: {src.ActorId} is {src.RelationType} with {src.TargetId}.");
        }

        return ChangeHandlerResult.Ok;
    }

    private string GetInverseType(string relationType)
    {
        return relationType switch
        {
            "Grappling" => "GrappledBy",
            "GrappledBy" => "Grappling",
            "Watching" => "WatchedBy",
            "WatchedBy" => "Watching",
            _ => relationType // Symmetric fallbacks (LeaningIn, CloseProximity)
        };
    }
}
