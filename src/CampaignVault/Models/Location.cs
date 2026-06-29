using System.Text.Json.Serialization;

namespace CampaignVault.Models;

public class Location : IHasSemanticVector
{
    public string Id { get; set; } = default!;
    
    public float[]? SemanticVector { get; set; }
    
    public string Name { get; set; } = default!;
    
    public string Description { get; set; } = default!;
    
    public LocationType Type { get; set; } = LocationType.Building;
    
    public string? ParentLocationId { get; set; }
    
    public List<LocationExit> Exits { get; set; } = [];
    
    public List<string> PointsOfInterest { get; set; } = [];
    
    /// <summary>
    /// Richer details for PointsOfInterest that have been examined or otherwise materialized.
    /// Keys match (case-insensitive) entries from PointsOfInterest. Values are the persistent,
    /// recallable description/content discovered through interaction/examination.
    /// This turns lightweight PoI strings into anchored world knowledge (analogous to
    /// promoting an ambient NPC via character_create).
    /// </summary>
    [JsonPropertyName("pointOfInterestDetails")]
    public Dictionary<string, string> PointOfInterestDetails { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    
    public string? AmbientCrowd { get; set; }
    
    public int? LastVisitedDay { get; set; }
    
    public Dictionary<string, object> Metadata { get; set; } = [];
    
    public string? CurrentState { get; set; }
    public List<string> VisualTags { get; set; } = [];
    public List<string> DistinctiveFeatures { get; set; } = [];
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional faction ID that controls or "owns" this location.
    /// Set via faction_create or faction_state changes. Null = unclaimed/independent.
    /// Used by GetScene to surface faction presence and by EncounterResolver for encounter bias.
    /// </summary>
    public string? ControllingFactionId { get; set; }

    /// <summary>

    /// Associates the entity with a specific campaign for multi-campaign isolation.
    /// Set automatically from current campaign context on create/upsert (via repo + handlers).
    /// (No legacy BC requirement per review feedback; always set for new data. Locations may be shareable across camps in some designs.)
    /// </summary>
    public string? CampaignName { get; set; }

    /// <summary>
    /// Narrative danger modifier set by the LLM (-50 to +50).
    /// Used by EncounterResolver to dynamically scale threat chances based on narrative events.
    /// </summary>
    public int DangerModifier { get; set; } = 0;
}

public enum LocationType
{
    Region,
    Settlement,
    District,
    Building,
    Room,
    Wilderness
}

public record LocationExit(
    string TargetLocationId,
    string Description,
    string? LockCondition = null,
    int? TravelCostHours = 0,
    string? Terrain = null,
    string? EncounterHint = null
)
{
    public LocationExit() : this(default!, default!) { }
}
