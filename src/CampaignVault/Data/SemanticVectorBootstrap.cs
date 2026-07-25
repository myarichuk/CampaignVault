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
        Console.WriteLine("[SemanticVectorBootstrap] Starting semantic vector enrichment check...");
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

            Console.WriteLine("[SemanticVectorBootstrap] Semantic vector enrichment complete ✓");
            _logger.LogInformation("Semantic vector enrichment complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Semantic vector bootstrap failed");
            Console.WriteLine($"[SemanticVectorBootstrap] ERROR: {ex.Message}");
            throw;
        }
    }

    private async Task EnrichMissingVectorsAsync<T>(CancellationToken cancellationToken)
        where T : class, IHasSemanticVector
    {
        const int batchSize = 50;
        var typeName = typeof(T).Name;

        using var session = _store.OpenAsyncSession();
        var missing = await session.Query<T>()
            .Where(x => x.SemanticVector == null)
            .ToListAsync(cancellationToken);

        if (missing.Count == 0)
        {
            _logger.LogDebug("{EntityType}: all vectors present", typeName);
            return;
        }

        _logger.LogInformation("{EntityType}: enriching {Count} missing vectors", typeName, missing.Count);
        Console.WriteLine($"  {typeName}: enriching {missing.Count} entities...");

        for (int i = 0; i < missing.Count; i += batchSize)
        {
            var batch = missing.Skip(i).Take(batchSize).ToList();
            foreach (var entity in batch)
            {
                await SemanticEnrichmentHelper.EnrichAsync(entity, _embeddingService, _logger, cancellationToken);
            }

            // Batch save
            foreach (var entity in batch)
            {
                await session.StoreAsync(entity, cancellationToken);
            }

            await session.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("{EntityType}: saved batch {BatchNum}/{TotalBatches}", typeName,
                (i / batchSize) + 1, (missing.Count + batchSize - 1) / batchSize);
        }

        _logger.LogInformation("{EntityType}: enrichment complete ({Count} entities)", typeName, missing.Count);
    }
}
