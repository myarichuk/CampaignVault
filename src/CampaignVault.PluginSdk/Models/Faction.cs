namespace CampaignVault.Models;

public class Faction : ICampaignScopedEntity, IArchivable
{
    public string Id { get; set; } = null!;
    [System.Text.Json.Serialization.JsonIgnore]
    public float[]? SemanticVector { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EmbeddingTextHash { get; set; }

    public string BuildEmbeddingText() => $"{Name}\n{Description}";

    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public FactionType FactionType { get; set; }
    public string? ControllingTerritory { get; set; }
    public List<string> TerritoryLocationIds { get; set; } = [];
    public List<string> KnownLeaderIds { get; set; } = [];
    public int InfluenceLevel { get; set; } = 50;
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

    /// <summary>Plot threads associated with this faction (referenced via thread-level or clue-level involvedEntityIds).</summary>
    public List<PlotThreadMinimal> AssociatedPlotThreads { get; set; } = [];
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
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

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
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
