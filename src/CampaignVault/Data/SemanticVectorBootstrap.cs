using CampaignVault.Models;
using CampaignVault.Services;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;

namespace CampaignVault.Data;

/// <summary>
/// Startup recovery task: enriches semantic vectors for any entities missing them.
/// Runs synchronously after RavenDB initialization to ensure search operations have complete data.
/// </summary>
internal class SemanticVectorBootstrap
{
    private readonly IDocumentStore _store;
    private readonly ILocalEmbeddingService _embeddingService;
    private readonly ILogger _logger;

    public SemanticVectorBootstrap(IDocumentStore store, ILocalEmbeddingService embeddingService, ILogger logger)
    {
        _store = store;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.Error.WriteLine("[SemanticVectorBootstrap] Starting semantic vector enrichment check...");
        _logger.LogInformation("Starting semantic vector enrichment check for all entity types");

        try
        {
            // Enrich each entity type that implements IHasSemanticVector
            await EnrichMissingVectorsAsync<Event>(cancellationToken);
            await EnrichMissingVectorsAsync<SessionLog>(cancellationToken);
            await EnrichMissingVectorsAsync<Character>(cancellationToken);
            await EnrichMissingVectorsAsync<Lore>(cancellationToken);
            await EnrichMissingVectorsAsync<Location>(cancellationToken);
            await EnrichMissingVectorsAsync<Faction>(cancellationToken);
            await EnrichMissingVectorsAsync<Rumor>(cancellationToken);
            await EnrichMissingVectorsAsync<Quest>(cancellationToken);
            await EnrichMissingVectorsAsync<Item>(cancellationToken);
            await EnrichMissingVectorsAsync<CustomSpell>(cancellationToken);
            await EnrichMissingVectorsAsync<CustomCreature>(cancellationToken);
            await EnrichMissingVectorsAsync<CustomFeat>(cancellationToken);
            await EnrichMissingVectorsAsync<PlotThread>(cancellationToken);

            Console.Error.WriteLine("[SemanticVectorBootstrap] Semantic vector enrichment complete ✓");
            _logger.LogInformation("Semantic vector enrichment complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Semantic vector bootstrap failed");
            Console.Error.WriteLine($"[SemanticVectorBootstrap] ERROR: {ex.Message}");
            throw;
        }
    }

    private async Task EnrichMissingVectorsAsync<T>(CancellationToken cancellationToken)
        where T : class, IHasSemanticVector
    {
        const int batchSize = 50;
        var typeName = typeof(T).Name;

        // Paged, one session per batch: a large campaign can have far more than one page's worth
        // of entities missing vectors, and re-querying "still missing" each round (rather than
        // Skip-ing over an ever-mutating predicate) means already-enriched entities naturally fall
        // out of subsequent pages.
        var totalEnriched = 0;
        while (true)
        {
            using var session = _store.OpenAsyncSession();
            var batch = await session.Query<T>()
                .Where(x => x.SemanticVector == null)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var entity in batch)
            {
                await SemanticEnrichmentHelper.EnrichAsync(entity, _embeddingService, _logger, cancellationToken);
                await session.StoreAsync(entity, cancellationToken);
            }

            await session.SaveChangesAsync(cancellationToken);
            totalEnriched += batch.Count;
            _logger.LogDebug("{EntityType}: saved batch of {BatchCount} (running total {Total})", typeName,
                batch.Count, totalEnriched);

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        if (totalEnriched == 0)
        {
            _logger.LogDebug("{EntityType}: all vectors present", typeName);
            return;
        }

        _logger.LogInformation("{EntityType}: enrichment complete ({Count} entities)", typeName, totalEnriched);
        Console.Error.WriteLine($"  {typeName}: enriched {totalEnriched} entities.");
    }
}
