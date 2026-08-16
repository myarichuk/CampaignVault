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
            actor = await context.Session.LoadAsync<Character>(src.CharacterId, ct);
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
            var removedRelation = actor.SystemStats.EngagementRelations.FirstOrDefault(r => r.TargetId == src.TargetId);
            actor.SystemStats.EngagementRelations.RemoveAll(r => r.TargetId == src.TargetId);
            if (src.Bidirectional)
            {
                target.SystemStats.EngagementRelations.RemoveAll(r => r.TargetId == src.CharacterId);
            }
            context.RecordMessage($"EngagementRelation removed between {src.CharacterId} and {src.TargetId}.");

            // Only Physical/Medical relations (restraint, grappling, tending wounds) gate action legality
            // enough to warrant a history entry — Social/Attention/Proximity churn constantly (e.g. batched
            // per-pair commits for a multi-person conversation) and would flood the event log otherwise.
            if (removedRelation != null && IsHistoryWorthy(removedRelation.Category))
            {
                await LogEngagementEventAsync(context, actor, target,
                    $"{actor.Name}'s engagement with {target.Name} ended.");
            }
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

            var existing = actor.SystemStats.EngagementRelations.FirstOrDefault(r => r.TargetId == src.TargetId);
            var isNoOp = existing != null && existing.Verb == verb && existing.Category == category
                && existing.RestrictionLevel == src.RestrictionLevel;

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

            if (!isNoOp && IsHistoryWorthy(category))
            {
                await LogEngagementEventAsync(context, actor, target,
                    $"{actor.Name} is now {verb} ({category}) with {target.Name}.");
            }
        }

        return ChangeHandlerResult.Ok;
    }

    // Only Physical/Medical relations (restraint, grappling, dragging, tending wounds) gate action
    // legality enough to warrant a standalone history entry. Social/Attention/Proximity relations are
    // high-frequency conversational/spatial bookkeeping (e.g. one commit per pair in a group
    // conversation) already covered by the explicit Conversation `event` the DM commits separately —
    // auto-logging those too would flood recall_history/NpcRecentEvents with near-duplicate noise.
    private static bool IsHistoryWorthy(EngagementCategory category) =>
        category is EngagementCategory.Physical or EngagementCategory.Medical;

    // Engagement relations (restraint, grappling, escort) often gate what actions are legal, so their
    // history is Important, not Trivial. Auto-logged so recall_history/NpcRecentEvents can surface it
    // without a second, separate `event` commit for the same narrative beat.
    private static async Task LogEngagementEventAsync(ChangeContext context, Character actor, Character target, string summary)
    {
        await context.LogEventAsync(new Event
        {
            Id = "events/" + Guid.NewGuid(),
            Summary = summary,
            Category = EventCategory.Interaction,
            Importance = MemoryImportance.Important,
            Involved = [actor.Id, target.Id],
            LocationId = actor.CurrentLocationId,
            DayLogged = (await context.GetCurrentTimeAsync()).TotalDaysElapsed,
            CampaignName = context.CampaignName,
        });
    }
}