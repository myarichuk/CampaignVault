namespace CampaignVault.Models;

/// <summary>
/// Campaign-scoped homebrew feat/perk definition. Authored by LLM DMs via MCP tools;
/// overrides SRD feats of the same name when queried via get_system_handbook.
/// </summary>
public class CustomFeat : ICampaignScopedEntity, IArchivable
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

    public string? Prerequisite { get; set; }

    public string? MechanicalSummary { get; set; }

    /// <summary>
    /// Passive spell-component requirements this feat waives (see FeatDefinition.CastingWaivers
    /// for the recognized values, e.g. "SomaticHandsFull").
    /// </summary>
    public List<string> CastingWaivers { get; set; } = [];

    /// <summary>Classes that can take this feat. Empty for ancestry/general/skill feats.</summary>
    public List<string> Classes { get; set; } = [];

    /// <summary>Minimum character level required to take this feat.</summary>
    public int? Level { get; set; }

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Associates the feat with a specific campaign for multi-campaign isolation.
    /// </summary>
    public string? CampaignName { get; set; }

    /// <summary>
    /// When true, hidden from default search/scene results (soft delete). Does not remove history.
    /// </summary>
    public bool IsArchived { get; set; }
}
