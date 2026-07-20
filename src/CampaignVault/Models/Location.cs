using System.Text.Json.Serialization;

namespace CampaignVault.Models;

public class Location : ICampaignScopedEntity, IArchivable
{
    public string Id { get; set; } = default!;

    public float[]? SemanticVector { get; set; }
    public string? EmbeddingTextHash { get; set; }

    public string BuildEmbeddingText() => $"{Name}\n{Description}";

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
    /// promoting an ambient NPC via world_build).
    /// </summary>
    [JsonPropertyName("pointOfInterestDetails")]
    public Dictionary<string, string> PointOfInterestDetails { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    
    public string? AmbientCrowd { get; set; }
    
    public int? LastVisitedDay { get; set; }

    /// <summary>
    /// Transient NPCs evicted from this location, most recent first (capped by handler).
    /// Surfaced in get_scene via the full Location object so the LLM can reference who recently left.
    /// </summary>
    [JsonPropertyName("recentlyDeparted")]
    public List<DepartedNpcRecord> RecentlyDeparted { get; set; } = [];
    
    public Dictionary<string, object> Metadata { get; set; } = [];
    
    public string? CurrentState { get; set; }
    public List<string> VisualTags { get; set; } = [];
    public List<string> DistinctiveFeatures { get; set; } = [];

    /// <summary>
    /// Maps a tag/feature/state text (as it appears in VisualTags/DistinctiveFeatures/CurrentState) to
    /// the event ID(s) that established it — objective ground truth, distinct from any NPC's subjective
    /// PsychologyProfile.Memories. Engine-populated only; not an LLM-settable commit field.
    /// </summary>
    public Dictionary<string, List<string>> TagProvenance { get; set; } = [];

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional faction ID that controls or "owns" this location.
    /// Set via world_build or faction_state changes. Null = unclaimed/independent.
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

    /// <summary>
    /// When true, hidden from default search/scene results (soft delete). Does not remove history.
    /// </summary>
    public bool IsArchived { get; set; }

    /// <summary>
    /// Climate zone for weather/temperature simulation. Null = inherit from the nearest
    /// ParentLocationId ancestor that has one set (via ClimateResolver); defaults to Temperate
    /// if none in the chain.
    /// </summary>
    public ClimateZone? ClimateZone { get; set; }
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

/// <summary>Broad climate zone used to derive ambient temperature (see ClimateCycle).</summary>
public enum ClimateZone
{
    Arctic,
    Tundra,
    Temperate,
    Desert,
    Tropical,
    Alpine,
    Subterranean
}

public record LocationExit(
    string TargetLocationId,
    string Description,
    string? LockCondition = null,
    int? TravelCostHours = 0,
    string? Terrain = null,
    string? EncounterHint = null,
    bool OneWay = false
)
{
    public LocationExit() : this(default!, default!) { }
}
