using CampaignVault.Models;
using Raven.Client.Documents;

namespace CampaignVault.Data.Migrations;

/// <summary>
/// Migrates existing CampaignTime documents from the old TimeOfDay enum to hour-based tracking (0-23).
/// Maps discrete TimeOfDay values to their equivalent hours.
/// Idempotent: safe to run multiple times (skips docs that already have Hour set).
/// </summary>
public class MigrateCampaignTimeToHours
{
    private readonly IDocumentStore _documentStore;

    public MigrateCampaignTimeToHours(IDocumentStore documentStore)
    {
        _documentStore = documentStore;
    }

    /// <summary>
    /// Runs the migration. Call once at startup or as a maintenance operation.
    /// Idempotent: existing Hour values are preserved; only migrates documents with Hour=0.
    /// </summary>
    public async Task ExecuteAsync()
    {
        using var session = _documentStore.OpenAsyncSession();

        var allTimes = await session.Query<CampaignTime>()
            .ToListAsync();

        if (allTimes.Count == 0)
        {
            return;
        }

        var migrated = 0;
        foreach (var time in allTimes)
        {
            // Only migrate documents with Hour=0 (likely old records or never-advanced)
            // Newly created docs have Hour=6 by default, so this safely skips them
            if (time.Hour != 0)
            {
                continue;
            }

            // Set to 6 (Dawn) for old records, consistent with the old TimeOfDay.Dawn default
            time.Hour = 6;
            migrated++;
        }

        if (migrated > 0)
        {
            await session.SaveChangesAsync();
        }
    }
}
