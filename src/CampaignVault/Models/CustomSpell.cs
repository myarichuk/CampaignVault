namespace CampaignVault.Models;

/// <summary>
/// Campaign-scoped homebrew spell definition. Authored by LLM DMs via MCP tools;
/// overrides SRD spells of the same name when queried via get_spells.
/// </summary>
public class CustomSpell : ICampaignScopedEntity, IArchivable
{
    public string Id { get; set; } = null!;

    [System.Text.Json.Serialization.JsonIgnore]
    public float[]? SemanticVector { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EmbeddingTextHash { get; set; }

    public string BuildEmbeddingText() => $"{Name}\n{Description}";

    public string Name { get; set; } = null!;

    public string System { get; set; }

    public string? Description { get; set; }

    /// <summary>0 = cantrip.</summary>
    public int? Level { get; set; }

    public List<string> Classes { get; set; } = [];

    public bool? Concentration { get; set; }

    public string? CastingTime { get; set; }

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Associates the spell with a specific campaign for multi-campaign isolation.
    /// </summary>
    public string? CampaignName { get; set; }

    /// <summary>
    /// When true, hidden from default search/scene results (soft delete). Does not remove history.
    /// </summary>
    public bool IsArchived { get; set; }
}
