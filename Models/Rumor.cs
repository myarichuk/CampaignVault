namespace CampaignVault.Models;

public class Rumor
{
    public string Id { get; set; } = default!;
    
    public string RegionLocationId { get; set; } = default!;
    
    public string Subject { get; set; } = default!;
    
    public string CurrentText { get; set; } = default!;
    
    public RumorState State { get; set; } = RumorState.Nascent;
    
    public string TruthValue { get; set; } = "True"; // Narrative metadata
    
    public int DayCreated { get; set; }
    
    public int LastStateChangeDay { get; set; }
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public enum RumorState
{
    Nascent,
    Spreading,
    Peak,
    Fading,
    Resolved,
    Forgotten
}
