using AllMiniLmL6V2Sharp;
using AllMiniLmL6V2Sharp.Tokenizer;

namespace CampaignVault.Services;

public class LocalEmbeddingService : ILocalEmbeddingService, IDisposable
{
    private readonly AllMiniLmL6V2Embedder _embedder;

    /// <summary>Serializes native calls: the embedder's thread-safety is undocumented.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

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

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new float[EmbeddingModelPaths.VectorDimensions];

        // A burst of concurrent turns must not run native inference concurrently (corruption
        // risk) or queue unbounded CPU work (DoS): one embedding at a time, cancellable.
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await Task.Run(() => _embedder.GenerateEmbedding(text).ToArray(), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _embedder.Dispose();
        _gate.Dispose();
    }
}