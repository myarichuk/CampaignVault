using AllMiniLmL6V2Sharp;
using AllMiniLmL6V2Sharp.Tokenizer;

namespace CampaignVault.Services;

public class LocalEmbeddingService : ILocalEmbeddingService, IDisposable
{
    private readonly AllMiniLmL6V2Embedder _embedder;

    public LocalEmbeddingService()
    {
        var modelPath = EmbeddingModelPaths.ModelOnnxPath;
        var vocabPath = EmbeddingModelPaths.VocabPath;

        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
        {
            throw new FileNotFoundException(
                $"Embedding model assets not found under '{EmbeddingModelPaths.ModelDirectory}'.");
        }

        var tokenizer = new BertTokenizer(vocabPath);
        _embedder = new AllMiniLmL6V2Embedder(modelPath, tokenizer);
    }

    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(new float[EmbeddingModelPaths.VectorDimensions]);

        return Task.Run(() => _embedder.GenerateEmbedding(text).ToArray(), ct);
    }

    public void Dispose() => _embedder.Dispose();
}