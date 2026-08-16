using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles archive_entity — the play-LLM-reachable soft-delete/restore for entities created via
/// world_build. See ArchiveEntityChange and C1 in the tool-usage audit for background.
/// </summary>
public sealed class ArchiveEntityChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ArchiveEntityChange;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var ac = (ArchiveEntityChange)change;

        if (string.IsNullOrWhiteSpace(ac.EntityId))
        {
            return ChangeHandlerResult.Failure("entityId is required.");
        }

        if (ac.EntityType == ArchivableEntityType.Character)
        {
            return ChangeHandlerResult.Failure(
                "Characters cannot be archived — the Character model has no archive support yet. " +
                "To remove a mistakenly-created NPC from play, set keepAlive:false and clear its schedule " +
                "(via character_update) so the transient-eviction GC can clean it up, or leave it in place " +
                "and simply stop referencing it.");
        }

        IArchivable? entity = ac.EntityType switch
        {
            ArchivableEntityType.Location => await context.Session.LoadAsync<Location>(ac.EntityId, ct),
            ArchivableEntityType.Item => await context.Session.LoadAsync<Item>(ac.EntityId, ct),
            ArchivableEntityType.Faction => await context.Session.LoadAsync<Faction>(ac.EntityId, ct),
            ArchivableEntityType.Quest => await context.Session.LoadAsync<Quest>(ac.EntityId, ct),
            ArchivableEntityType.Creature => await context.Session.LoadAsync<CustomCreature>(ac.EntityId, ct),
            ArchivableEntityType.Spell => await context.Session.LoadAsync<CustomSpell>(ac.EntityId, ct),
            ArchivableEntityType.Feat => await context.Session.LoadAsync<CustomFeat>(ac.EntityId, ct),
            ArchivableEntityType.Rumor => await context.Session.LoadAsync<Rumor>(ac.EntityId, ct),
            ArchivableEntityType.PlotThread => await context.Session.LoadAsync<PlotThread>(ac.EntityId, ct),
            _ => null
        };

        if (entity == null)
        {
            return ChangeHandlerResult.Failure($"{ac.EntityType} '{ac.EntityId}' not found.");
        }

        entity.IsArchived = ac.Archived;
        context.RecordMessage(ac.Archived
            ? $"{ac.EntityType} '{ac.EntityId}' archived (hidden from default search/scene/list results; the document itself is not deleted and can be restored)."
            : $"{ac.EntityType} '{ac.EntityId}' restored (visible again in default results).");

        return ChangeHandlerResult.Ok;
    }
}
