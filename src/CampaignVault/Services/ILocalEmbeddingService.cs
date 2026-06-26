namespace CampaignVault.Services;

public interface ILocalEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}
