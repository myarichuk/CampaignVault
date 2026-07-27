using CampaignVault.Data.Migrations;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents.Indexes;
using Raven.Embedded;

namespace CampaignVault.Data;

public static class RavenStartup
{
    public static IDocumentStore Initialize(string dbPath)
    {
        EmbeddedServer.Instance.StartServer(new ServerOptions
        {
            DataDirectory = dbPath,
            ServerUrl = "http://127.0.0.1:0" // Use a random port
        });

        // AdvanceWorld/pressure evaluation are composed of many independent, small per-rule and
        // per-contributor queries (deliberately isolated/pluggable rather than batched into one big
        // query) — the default 30-request session guard is tuned for typical CRUD request handlers,
        // not this fan-out. Raised per RavenDB's own guidance once call-count reduction isn't
        // reasonable without giving up the plugin-style rule/contributor architecture.
        var databaseOptions = new DatabaseOptions("CampaignVault")
        {
            Conventions = new Raven.Client.Documents.Conventions.DocumentConventions { MaxNumberOfRequestsPerSession = 200 },
        };
        var documentStore = EmbeddedServer.Instance.GetDocumentStore(databaseOptions);

#if DEBUG
        // Convenience for local dev only — opens the embedded server's Studio UI in the default
        // browser so schema/data can be inspected without hunting down the random bound port.
        EmbeddedServer.Instance.OpenStudioInBrowser();
#endif

        // Create indexes from assembly
        IndexCreation.CreateIndexes(typeof(RavenStartup).Assembly, documentStore);

        // Universal sanitizing listener on the Raven persistence boundary.
        documentStore.OnBeforeStore += (_, args) =>
        {
            if (args.Entity is not null)
            {
                JsonSanitizer.Sanitize(args.Entity);
            }
        };

        return documentStore;
    }

    /// <summary>
    /// Run self-healing migrations and data repairs after the document store is initialized.
    /// Call this once during application startup, after Initialize().
    /// Idempotent: safe to run multiple times.
    /// </summary>
    public static async Task RunDataMigrationsAsync(
        IDocumentStore documentStore,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var logger = loggerFactory.CreateLogger(nameof(RavenStartup));
        logger.LogInformation("════════════════════════════════════════════════════════════════");
        logger.LogInformation("Starting data migrations and self-healing routines...");
        logger.LogInformation("════════════════════════════════════════════════════════════════");

        try
        {
            // Strip stale anonymous-type $type discriminators from Event.Details at the raw-JSON
            // level, before any typed query against the Event collection (below) can trip over them.
            var anonymousTypeRepair = new RepairAnonymousTypeDiscriminators(documentStore);
            await anonymousTypeRepair.ExecuteAsync(ct);
            logger.LogInformation("✓ Event anonymous-type discriminator repair: completed");

            // Migrate CampaignTime from old TimeOfDay enum to hour-based tracking
            var timeHourMigration = new MigrateCampaignTimeToHours(documentStore);
            await timeHourMigration.ExecuteAsync();
            logger.LogInformation("✓ CampaignTime hours migration: completed");

            // Repair corrupted Event documents
            var repairLogger = loggerFactory.CreateLogger<EventDataRepair>();
            var repair = new EventDataRepair(repairLogger);
            var (repaired, details) = await repair.RepairAsync(documentStore, ct);

            if (repaired > 0)
            {
                logger.LogWarning("████████████████████████████████████████████████████████████████");
                logger.LogWarning($"⚠️  DATA CORRUPTION DETECTED AND REPAIRED: {repaired} Event(s) had mixed entity types in Involved field");
                logger.LogWarning("████████████████████████████████████████████████████████████████");
                foreach (var detail in details)
                {
                    logger.LogWarning($"  • {detail}");
                }
                logger.LogWarning("");
                logger.LogWarning("This indicates a bug in event creation logic. All corrupted events have been");
                logger.LogWarning("self-healed (non-character IDs moved to proper fields), but the root cause should");
                logger.LogWarning("be investigated and fixed to prevent future corruption.");
                logger.LogWarning("████████████████████████████████████████████████████████████████");
            }
            else
            {
                logger.LogInformation("✓ Event data validation: no corruption detected");
            }

            logger.LogInformation("════════════════════════════════════════════════════════════════");
            logger.LogInformation("Data migrations complete");
            logger.LogInformation("════════════════════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FATAL: Data migration failed. The database may be in an inconsistent state.");
            throw;
        }
    }
}
