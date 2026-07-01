namespace CampaignVault.Models
{
    public interface IHasSemanticVector
    {
        float[]? SemanticVector { get; set; }
        string? EmbeddingTextHash { get; set; }
        string BuildEmbeddingText();
    }
}
