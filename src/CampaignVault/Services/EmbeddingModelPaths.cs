namespace CampaignVault.Services;

public static class EmbeddingModelPaths
{
    public const int VectorDimensions = 384;
    public static string ModelDirectory =>
        Path.Combine(AppContext.BaseDirectory, "models", "embedding");

    public static string ModelOnnxPath => Path.Combine(ModelDirectory, "model.onnx");

    public static string VocabPath => Path.Combine(ModelDirectory, "vocab.txt");
}