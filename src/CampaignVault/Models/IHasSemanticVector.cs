namespace CampaignVault.Models;

/// <summary>
/// Marker interface for entities that have semantic embeddings stored in RavenDB.
/// These fields are stripped from MCP responses to conserve token context
/// but kept in the database for search operations.
/// </summary>
public interface IHasSemanticVector
{
    float[]? SemanticVector { get; set; }
    string? EmbeddingTextHash { get; set; }
    string BuildEmbeddingText();

    /// <summary>
    /// Property names that should be stripped from MCP responses.
    /// </summary>
    static readonly string[] StrippedFields =
    [
        nameof(SemanticVector),
        nameof(EmbeddingTextHash)
    ];
}
