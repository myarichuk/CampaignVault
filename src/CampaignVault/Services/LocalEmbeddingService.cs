using Microsoft.ML.OnnxRuntime;

namespace CampaignVault.Services;

public class LocalEmbeddingService : ILocalEmbeddingService
{
    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        // Full implementation will be loaded when the actual model is downloaded.
        // For now, return a dummy vector to satisfy the interface.
        return Task.FromResult(new float[384]); 
    }
}
