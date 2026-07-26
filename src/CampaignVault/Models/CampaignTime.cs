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

    /// <summary>
    /// Hour of day (0-23). Precision tracking stops at hours; minutes/seconds not tracked.
    /// </summary>
    public int Hour { get; set; } = 6; // 6 = Dawn

    public int TotalDaysElapsed { get; set; } = 0;

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public void AdvanceHours(int hours)
    {
        if (hours <= 0)
        {
            return;
        }

        var newHour = Hour + hours;
        var daysPassed = newHour / 24;
        Hour = newHour % 24;

        TotalDaysElapsed += daysPassed;
        Day += daysPassed;
    }

    /// <summary>
    /// Maps the current hour (0-23) to a narrative time-of-day category.
    /// Used for display and systems that need coarse time categories.
    /// </summary>
    public string GetTimeOfDayName() =>
        Hour switch
        {
            >= 0 and < 6 => "Night",
            >= 6 and < 9 => "Dawn",
            >= 9 and < 12 => "Morning",
            >= 12 and < 15 => "Noon",
            >= 15 and < 18 => "Afternoon",
            >= 18 and < 21 => "Evening",
            >= 21 and < 24 => "Dusk",
            _ => "Night"
        };
}
