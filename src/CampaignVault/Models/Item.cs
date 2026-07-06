namespace CampaignVault.Models;

public class Item : ICampaignScopedEntity
{
    public string Id { get; set; } = default!;
    
    public float[]? SemanticVector { get; set; }
    public string? EmbeddingTextHash { get; set; }

    public string BuildEmbeddingText() => $"{Name}\n{Description}";

    public string Name { get; set; } = default!;
    
    public string Description { get; set; } = default!;
    
    public string HolderId { get; set; } = default!; // Character.Id, Location.Id, or Item.Id

    public int Quantity { get; set; } = 1;

    public string? CurrentState { get; set; }

    public List<string> DistinctiveFeatures { get; set; } = [];

    public ItemCategory CoreCategory { get; set; }

    public List<string> Tags { get; set; } = [];

    public Dictionary<string, object> Properties { get; set; } = [];
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Associates the entity with a specific campaign for multi-campaign isolation.
    /// Set automatically from current campaign context on create/upsert (via repo + handlers).
    /// (No legacy BC requirement per review feedback; always set for new data. Items may be shareable across camps in some designs.)
    /// </summary>
    public string? CampaignName { get; set; }
}

public enum ItemCategory
{
    Weapon,
    Armor,
    Clothing,
    Container,
    Consumable,
    Tool,
    Material,
    Valuable,
    Document,
    Key,
    Other
}
