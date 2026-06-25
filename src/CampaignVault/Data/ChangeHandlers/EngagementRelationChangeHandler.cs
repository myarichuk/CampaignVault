using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class EngagementRelationChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is EngagementRelationChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var src = (EngagementRelationChange)change;

        if (!context.Characters.TryGetValue(src.CharacterId, out var actor))
        {
            actor = context.Session != null ? await context.Session.LoadAsync<Character>(src.CharacterId, ct) : null;
            if (actor == null) return ChangeHandlerResult.Failure($"Character {src.CharacterId} not found.");
            context.RegisterNewCharacter(actor);
        }

        if (!context.Characters.TryGetValue(src.TargetId, out var target))
        {
            target = context.Session != null ? await context.Session.LoadAsync<Character>(src.TargetId, ct) : null;
            if (target == null) return ChangeHandlerResult.Failure($"Target {src.TargetId} not found.");
            context.RegisterNewCharacter(target);
        }

        actor.SystemStats ??= new SystemExtension();
        target.SystemStats ??= new SystemExtension();
        actor.SystemStats.EngagementRelations ??= new List<EngagementRelation>();
        target.SystemStats.EngagementRelations ??= new List<EngagementRelation>();

        if (EngagementRelationHelpers.IsClearRequest(src.Verb))
        {
            actor.SystemStats.EngagementRelations.RemoveAll(r => r.TargetId == src.TargetId);
            if (src.Bidirectional)
            {
                target.SystemStats.EngagementRelations.RemoveAll(r => r.TargetId == src.CharacterId);
            }
            context.RecordMessage($"EngagementRelation removed between {src.CharacterId} and {src.TargetId}.");
        }
        else
        {
            var verb = EngagementRelationHelpers.ResolveVerb(src.Verb)!;
            var category = src.Category ?? EngagementRelationCatalog.InferCategory(verb);
            var relation = new EngagementRelation
            {
                TargetId = src.TargetId,
                Category = category,
                Verb = verb,
                RestrictionLevel = src.RestrictionLevel
            };

            actor.SystemStats.EngagementRelations.RemoveAll(r => r.TargetId == src.TargetId);
            actor.SystemStats.EngagementRelations.Add(relation);

            if (src.Bidirectional)
            {
                var inverseVerb = EngagementRelationCatalog.GetInverseVerb(category, verb);
                var inverseCategory = EngagementRelationCatalog.InferCategory(inverseVerb);
                target.SystemStats.EngagementRelations.RemoveAll(r => r.TargetId == src.CharacterId);
                target.SystemStats.EngagementRelations.Add(new EngagementRelation
                {
                    TargetId = src.CharacterId,
                    Category = inverseCategory,
                    Verb = inverseVerb,
                    RestrictionLevel = src.RestrictionLevel
                });
            }

            context.RecordMessage($"EngagementRelation established: {src.CharacterId} is {verb} ({category}) with {src.TargetId}.");
        }

        return ChangeHandlerResult.Ok;
    }
}