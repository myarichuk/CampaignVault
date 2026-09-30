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
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var ac = (ArchiveEntityChange)change;

        if (string.IsNullOrWhiteSpace(ac.EntityId))
        {
            return ChangeHandlerResult.Failure("entityId is required.");
        }

        if (ac.EntityType == null)
        {
            return ChangeHandlerResult.Failure(
                "entityType is required (one of: Location, Item, Faction, Quest, Creature, Spell, Feat, Rumor, PlotThread). " +
                "Characters (PCs, NPCs, the dead) cannot be archived. If the character died commit {\"$type\": \"death\", \"characterId\": \"chars/...\", \"cause\": \"...\"}; " +
                "to make a living NPC leave the scene use an activity change with newLocationId: null, updateLocation: true; a one-scene transient with keepAlive:false auto-GCs.");
        }

        IArchivable? entity = ac.EntityType switch
        {
            ArchivableEntityType.Location => await ctx.Session.LoadAsync<Location>(ac.EntityId, ct),
            ArchivableEntityType.Item => await ctx.Session.LoadAsync<Item>(ac.EntityId, ct),
            ArchivableEntityType.Faction => await ctx.Session.LoadAsync<Faction>(ac.EntityId, ct),
            ArchivableEntityType.Quest => await ctx.Session.LoadAsync<Quest>(ac.EntityId, ct),
            ArchivableEntityType.Creature => await ctx.Session.LoadAsync<CustomCreature>(ac.EntityId, ct),
            ArchivableEntityType.Spell => await ctx.Session.LoadAsync<CustomSpell>(ac.EntityId, ct),
            ArchivableEntityType.Feat => await ctx.Session.LoadAsync<CustomFeat>(ac.EntityId, ct),
            ArchivableEntityType.Rumor => await ctx.Session.LoadAsync<Rumor>(ac.EntityId, ct),
            ArchivableEntityType.PlotThread => await ctx.Session.LoadAsync<PlotThread>(ac.EntityId, ct),
            _ => null
        };

        if (entity == null)
        {
            return ChangeHandlerResult.Failure($"{ac.EntityType} '{ac.EntityId}' not found.");
        }

        entity.IsArchived = ac.Archived;
        ctx.RecordMessage(ac.Archived
            ? $"{ac.EntityType} '{ac.EntityId}' archived (hidden from default search/scene/list results; the document itself is not deleted and can be restored)."
            : $"{ac.EntityType} '{ac.EntityId}' restored (visible again in default results).");

        return ChangeHandlerResult.Ok;
    }
}
