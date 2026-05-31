namespace CampaignVault.Models;

public class CampaignTime
{
    /// <summary>
    /// Document ID. Should come from CampaignDocumentKeys.StateTime(campaignName).
    /// Old singleton "state/time" is being replaced by per-campaign namespacing.
    /// </summary>
    public string Id { get; set; } = default!;
    
    public string Epoch { get; set; } = "Current Era";
    
    public int Year { get; set; } = 1492;
    
    public int Month { get; set; } = 1;
    
    public int Day { get; set; } = 1;
    
    public TimeOfDay TimeOfDay { get; set; } = TimeOfDay.Dawn;
    
    public int TotalDaysElapsed { get; set; } = 0;
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public enum TimeOfDay
{
    Dawn,
    Morning,
    Noon,
    Afternoon,
    Evening,
    Dusk,
    Night
}
