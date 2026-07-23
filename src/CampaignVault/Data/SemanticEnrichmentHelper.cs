using CampaignVault.Models;
using CampaignVault.Services;
using Microsoft.Extensions.Logging;

namespace CampaignVault.Data;

/// <summary>
/// Shared semantic-embedding logic: refreshing an entity's cached vector (hash-guarded to skip
/// unchanged text) and comparing vectors for similarity. Extracted from
/// <see cref="CampaignRepository"/>'s original EnrichSemanticVectorAsync so it can also be called
/// from the incremental commit path (e.g. ItemUpdateHandler), which has no built-in re-embed hook.
/// </summary>
internal static class SemanticEnrichmentHelper
{
    public static async Task EnrichAsync(IHasSemanticVector entity, ILocalEmbeddingService embeddingService, ILogger logger, CancellationToken ct = default)
    {
        var textToEmbed = entity.BuildEmbeddingText();
        if (string.IsNullOrWhiteSpace(textToEmbed))
        {
            entity.SemanticVector = null;
            entity.EmbeddingTextHash = null;
            return;
        }

        var hash = ComputeEmbeddingHash(textToEmbed);
        if (hash == entity.EmbeddingTextHash)
            return;

        try
        {
            entity.SemanticVector = await embeddingService.GenerateEmbeddingAsync(textToEmbed, ct);
            entity.EmbeddingTextHash = hash;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Embedding generation FAILED for {EntityType} (text: {TextPreview}); semantic search unavailable for this entity until it's re-embedded.",
                entity.GetType().Name, textToEmbed.Length > 80 ? textToEmbed[..80] + "..." : textToEmbed);
            entity.SemanticVector = null;
            entity.EmbeddingTextHash = null;
        }
    }

    public static string ComputeEmbeddingHash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Cosine similarity in [-1, 1]. Returns 0 if either vector is empty or lengths mismatch.</summary>
    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            return 0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0)
            return 0;

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
