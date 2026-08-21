using CampaignVault.Models;
using Raven.Client.Documents;

namespace CampaignVault.Data.Migrations;

/// <summary>
/// Migrates RulesetSystem from a closed enum to an open string id.
/// Converts persisted system IDs from member-name form (e.g. "Dnd5e") to slug form (e.g. "dnd5e").
///
/// Affected documents and fields:
/// - Campaign.System
/// - CampaignConfig.ActiveSystem
/// - CustomSpell.System
/// - CustomFeat.System
/// - CustomCreature.System
///
/// Idempotent: safe to run multiple times (skips documents already in slug form).
/// </summary>
public class MigrateRulesetSystemToString
{
    private readonly IDocumentStore _documentStore;

    public MigrateRulesetSystemToString(IDocumentStore documentStore)
    {
        _documentStore = documentStore;
    }

    /// <summary>
    /// Runs the migration. Converts all persisted system IDs from member-name to slug form.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        using var session = _documentStore.OpenAsyncSession();

        // Mapping from member-name form to slug form
        var systemMapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Dnd5e", "dnd5e" },
            { "Pathfinder2e", "pf2e" },
            { "Narrative", "narrative" }
        };

        var migratedCount = 0;

        // Migrate Campaign documents
        var campaigns = await session.Query<Campaign>().ToListAsync();
        foreach (var campaign in campaigns)
        {
            if (systemMapping.TryGetValue(campaign.System, out var newSystemId))
            {
                campaign.System = newSystemId;
                migratedCount++;
            }
        }

        // Migrate CampaignConfig documents
        var configs = await session.Query<CampaignConfig>().ToListAsync();
        foreach (var config in configs)
        {
            if (systemMapping.TryGetValue(config.ActiveSystem, out var newSystemId))
            {
                config.ActiveSystem = newSystemId;
                migratedCount++;
            }
        }

        // Migrate CustomSpell documents
        var spells = await session.Query<CustomSpell>().ToListAsync();
        foreach (var spell in spells)
        {
            if (spell.System != null && systemMapping.TryGetValue(spell.System, out var newSystemId))
            {
                spell.System = newSystemId;
                migratedCount++;
            }
        }

        // Migrate CustomFeat documents
        var feats = await session.Query<CustomFeat>().ToListAsync();
        foreach (var feat in feats)
        {
            if (feat.System != null && systemMapping.TryGetValue(feat.System, out var newSystemId))
            {
                feat.System = newSystemId;
                migratedCount++;
            }
        }

        // Migrate CustomCreature documents
        var creatures = await session.Query<CustomCreature>().ToListAsync();
        foreach (var creature in creatures)
        {
            if (creature.System != null && systemMapping.TryGetValue(creature.System, out var newSystemId))
            {
                creature.System = newSystemId;
                migratedCount++;
            }
        }

        if (migratedCount > 0)
        {
            await session.SaveChangesAsync();
        }
    }
}
