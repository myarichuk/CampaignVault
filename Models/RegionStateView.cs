namespace CampaignVault.Models;

public class RegionStateView
{
    public string RegionName { get; set; } = default!;
    
    public CampaignTime CurrentTime { get; set; } = default!;
    
    public string? Description { get; set; }
    
    public IEnumerable<RumorSummary> ActiveRumors { get; set; } = [];
    
    public IEnumerable<LocationSummary> KnownLocations { get; set; } = [];
    
    public IEnumerable<NpcActivitySummary> NotableNPCs { get; set; } = [];
    
    public Dictionary<string, object>? Metadata { get; set; }
}

public record RumorSummary(string Subject, string CurrentText, RumorState State);

public record LocationSummary(string Id, string Name, LocationType Type);

public record NpcActivitySummary(string Name, string CurrentActivity);
