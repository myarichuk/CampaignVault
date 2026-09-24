using System.Text.Json.Serialization;

namespace CampaignVault.Models;

public class Location : ICampaignScopedEntity, IArchivable
{
    public string Id { get; set; } = null!;

    [JsonIgnore]
    public float[]? SemanticVector { get; set; }
    [JsonIgnore]
    public string? EmbeddingTextHash { get; set; }

    public string BuildEmbeddingText() => $"{Name}\n{Description}";

    public string Name { get; set; } = null!;
    
    public string Description { get; set; } = null!;
    
    public LocationType Type { get; set; } = LocationType.Building;
    
    public string? ParentLocationId { get; set; }
    
    public List<LocationExit> Exits { get; set; } = [];

    /// <summary>Traps and dangers in the location itself (a collapsing floor), fired on entering.</summary>
    public List<Hazard> Hazards { get; set; } = [];
    
    /// <summary>LEGACY (points of interest are retired). Kept only so MigratePointsOfInterestToFixtures can read and clear
    /// old documents; nothing else reads or writes it. Remove once every deployment has run the migration.</summary>
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

    /// <summary>
    /// PoI names (case-insensitive) that a location_update has ever marked as occupied via
    /// materializePointOfInterest + poiOccupantCharacterId — i.e. a character was actually placed
    /// there, not just described. That's a behavioral signal that the PoI is functioning as a real
    /// place, distinct from PointOfInterestDetails (which only says a PoI has *some* recorded
    /// description). Drives PointOfInterestPressureContributor's "promote to a proper child
    /// Location" nudge. Internal bookkeeping only — not projected onto LocationDetailView.
    /// </summary>
    public List<string> PoisUsedByActivity { get; set; } = [];

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

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
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
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
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
    double? TravelCostHours = 0,
    string? Terrain = null,
    string? EncounterHint = null,
    bool OneWay = false,
    /// <summary>A secret door or passage: kept out of scene payloads until found (a check or passive
    /// Perception at this location meeting DiscoverDc, or the party using it).</summary>
    bool Hidden = false,
    int? DiscoverDc = null,
    /// <summary>DM-only: who hid it and why, how it is found. Never shown as scene text.</summary>
    string? Intent = null,
    /// <summary>A trap on this exit, fired when someone travels through it.</summary>
    Hazard? Hazard = null
)
{
    public LocationExit() : this(null!, null!) { }
}

/// <summary>
/// A trap or danger on an exit, an item or a location. Minimal by design: the engine tracks whether it
/// was spotted or disarmed and fires it on its trigger; the DM resolves the effect with a ruleset_action
/// (usually a SavingThrow at <see cref="SaveDc"/>), since the save and damage belong to the rules module.
/// </summary>
public record Hazard
{
    /// <summary>Short name, unique on its host (e.g. "needle trap", "loose flagstone").</summary>
    public string Name { get; init; } = null!;

    /// <summary>"enter" (exits and locations: passing through / arriving) or "take" (items: taking or opening it).</summary>
    public string Trigger { get; init; } = "enter";

    /// <summary>Perception/Investigation DC to spot it. Null: it is only spotted when the DM says so.</summary>
    public int? DetectDc { get; init; }

    /// <summary>DC for a check whose parameters name it as "disarm" to make it safe.</summary>
    public int? DisarmDc { get; init; }

    /// <summary>What happens when it fires, e.g. "2d10 piercing, DC 13 Dex save for half".</summary>
    public string Effect { get; init; } = null!;

    /// <summary>Save DC the DM should roll against when it fires (the ruleset_action SavingThrow's dc).</summary>
    public int? SaveDc { get; init; }

    /// <summary>Save ability or skill, e.g. "Dexterity".</summary>
    public string? SaveAbility { get; init; }

    public bool Detected { get; init; }
    public bool Disarmed { get; init; }

    /// <summary>A fired one-shot trap stays spent.</summary>
    public bool Spent { get; init; }

    /// <summary>Re-arms after firing (a pressure plate) instead of staying spent (a gas cloud).</summary>
    public bool Rearms { get; init; }

    /// <summary>DM-only: who set it, why, what it guards.</summary>
    public string? Intent { get; init; }

    public bool IsLive => !Disarmed && !Spent;
}
