namespace CampaignVault.Models;

/// <summary>
/// Campaign-scoped homebrew creature stat-block template. Authored by LLM DMs via MCP tools
/// for reuse across encounters. Distinct from Character (which represents live PC/NPC/monster instances).
/// Implements semantic indexing for potential future fuzzy search.
/// </summary>
public class CustomCreature : ICampaignScopedEntity, IArchivable
{
    public string Id { get; set; } = null!;

    [System.Text.Json.Serialization.JsonIgnore]
    public float[]? SemanticVector { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EmbeddingTextHash { get; set; }

    public string BuildEmbeddingText() => $"{Name}\n{Description}";

    public string Name { get; set; } = null!;

    public string? System { get; set; }

    public string? Description { get; set; }

    public int? Level { get; set; }

    public string? ChallengeRating { get; set; }

    public int? Hp { get; set; }

    public int? Defense { get; set; }

    public List<string> Skills { get; set; } = [];

    public List<string> Abilities { get; set; } = [];

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Associates the creature with a specific campaign for multi-campaign isolation.
    /// </summary>
    public string? CampaignName { get; set; }

    /// <summary>
    /// When true, hidden from default search/scene results (soft delete). Does not remove history.
    /// </summary>
    public bool IsArchived { get; set; }
}
