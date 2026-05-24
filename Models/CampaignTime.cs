namespace CampaignVault.Models;

public class CampaignTime
{
    public string Id { get; set; } = "state/time";
    
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
