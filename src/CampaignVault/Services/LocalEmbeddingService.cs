using Microsoft.ML.OnnxRuntime;

namespace CampaignVault.Services;

public class LocalEmbeddingService : ILocalEmbeddingService
{
    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        // full implementation will be loaded when the actual model is downloaded
        // for now, return a dummy vector to satisfy the interface -> but don't "poison" so empty
        return Task.FromResult(Array.Empty<float>()); 
    }
}
