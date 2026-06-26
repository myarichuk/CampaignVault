namespace CampaignVault.Models;

public class Lore
{
    public string Id { get; set; } = default!;
    
    public float[]? SemanticVector { get; set; }
    
    public string Title { get; set; } = default!;
    
    public string Content { get; set; } = default!;
    
    public List<string> Tags { get; set; } = [];
    
    public List<string> Keywords { get; set; } = [];
    
    public string? Category { get; set; }
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Associates the entity with a specific campaign for multi-campaign isolation.
    /// Set automatically from current campaign context on create/upsert (via repo + handlers).
    /// (No legacy BC requirement per review feedback; always set for new data. Lore may be shareable across camps in some designs.)
    /// </summary>
    public string? CampaignName { get; set; }
}
