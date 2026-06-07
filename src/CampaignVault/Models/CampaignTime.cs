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

    public void AdvanceHours(int hours)
    {
        if (hours <= 0)
        {
            return;
        }

        // Simple approximation: 3-4 hours per TimeOfDay segment. Let's say 4 hours per segment to wrap around 6 segments roughly.
        // Dawn(0), Morning(1), Noon(2), Afternoon(3), Evening(4), Dusk(5), Night(6).
        // 7 segments * 4 hours = 28 hours (close enough for narrative time).
        var segmentsToAdvance = (int)Math.Ceiling(hours / 3.0);
        
        var currentSegment = (int)TimeOfDay;
        var newSegment = currentSegment + segmentsToAdvance;
        
        var daysPassed = newSegment / 7;
        TimeOfDay = (TimeOfDay)(newSegment % 7);
        
        TotalDaysElapsed += daysPassed;
        Day += daysPassed;
    }
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
