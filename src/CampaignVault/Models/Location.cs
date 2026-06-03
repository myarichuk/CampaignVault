namespace CampaignVault.Models;

public class Location
{
    public string Id { get; set; } = default!;
    
    public string Name { get; set; } = default!;
    
    public string Description { get; set; } = default!;
    
    public LocationType Type { get; set; } = LocationType.Building;
    
    public string? ParentLocationId { get; set; }
    
    public List<LocationExit> Exits { get; set; } = [];
    
    public List<string> PointsOfInterest { get; set; } = [];
    
    public string? AmbientCrowd { get; set; }
    
    public int? LastVisitedDay { get; set; }
    
    public Dictionary<string, object> Metadata { get; set; } = [];
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Associates the entity with a specific campaign for multi-campaign isolation.
    /// Set automatically from current campaign context on create/upsert (via repo + handlers).
    /// (No legacy BC requirement per review feedback; always set for new data. Locations may be shareable across camps in some designs.)
    /// </summary>
    public string? CampaignName { get; set; }
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

public record LocationExit(string TargetLocationId, string Description, string? LockCondition = null);
