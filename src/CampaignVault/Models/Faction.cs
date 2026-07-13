namespace CampaignVault.Models;

public class Faction : ICampaignScopedEntity, IArchivable
{
    public string Id { get; set; } = default!;
    public float[]? SemanticVector { get; set; }
    public string? EmbeddingTextHash { get; set; }

    public string BuildEmbeddingText() => $"{Name}\n{Description}";

    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public FactionType FactionType { get; set; }
    public string? ControllingTerritory { get; set; }
    public List<string> TerritoryLocationIds { get; set; } = [];
    public List<string> KnownLeaderIds { get; set; } = [];
    public int InfluenceLevel { get; set; } = 50;
    public List<string> EnemyFactionIds { get; set; } = [];
    public Dictionary<string, FactionStance> StanceToward { get; set; } = [];
    public Dictionary<string, float> EconomicDemand { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Metadata { get; set; } = [];
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Associates the entity with a specific campaign for multi-campaign isolation.
    /// </summary>
    public string? CampaignName { get; set; }

    /// <summary>
    /// When true, hidden from default search/scene results (soft delete). Does not remove history.
    /// </summary>
    public bool IsArchived { get; set; }
}

public enum FactionType
{ 
    Guild, 
    Kingdom, 
    Cult, 
    MerchantHouse, 
    MilitaryOrder, 
    Criminal, 
    Religious 
}

public enum FactionStance 
{ 
    Neutral, 
    Allied, 
    TradePartner, 
    Hostile, 
    AtWar, 
    Subjugated,
    Opportunistic
}
