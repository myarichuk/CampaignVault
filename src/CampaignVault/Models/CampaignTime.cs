namespace CampaignVault.Models;

public class CampaignTime
{
    /// <summary>
    /// Document ID. Should come from CampaignDocumentKeys.StateTime(campaignName).
    /// Old singleton "state/time" is being replaced by per-campaign namespacing.
    /// </summary>
    public string Id { get; set; } = null!;

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

    /// <summary>
    /// Advances the clock by the given number of hours, rolling Day/Month/Year over on the fixed
    /// 360-day (12×30) fantasy calendar so a multi-day hour skip (e.g. a long rest spanning a
    /// month boundary) doesn't leave Day sitting above 30.
    /// Fractional hours are rounded to the nearest whole hour.
    /// </summary>
    public void AdvanceHours(double hours)
    {
        if (hours <= 0)
        {
            return;
        }

        var roundedHours = (int)Math.Round(hours);
        var newHour = Hour + roundedHours;
        var daysPassed = newHour / 24;
        Hour = newHour % 24;

        TotalDaysElapsed += daysPassed;
        RollCalendar(daysPassed);
    }

    /// <summary>
    /// Advances the clock by whole days, rolling Day/Month/Year over on the fixed 360-day (12×30)
    /// fantasy calendar. Rolls forward from this instance's own current Year/Month/Day rather than
    /// recomputing from TotalDaysElapsed against a hardcoded epoch, so it stays correct for
    /// campaigns whose LoreSettings started at a year other than the 1492 default.
    /// </summary>
    public void AdvanceDays(int days)
    {
        if (days <= 0)
        {
            return;
        }

        TotalDaysElapsed += days;
        RollCalendar(days);
    }

    private void RollCalendar(int daysPassed)
    {
        if (daysPassed <= 0)
        {
            return;
        }

        var zeroBasedDay = Day - 1 + daysPassed;
        var monthsPassed = zeroBasedDay / 30;
        Day = (zeroBasedDay % 30) + 1;

        if (monthsPassed > 0)
        {
            var zeroBasedMonth = Month - 1 + monthsPassed;
            Year += zeroBasedMonth / 12;
            Month = (zeroBasedMonth % 12) + 1;
        }
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

    /// <summary>
    /// Human-readable in-world date, e.g. "Day 12, Month 3, Year 1492 (Current Era) — Morning".
    /// Rides along wherever CampaignTime itself is serialized (WorldStateView.Time, AdvanceWorld results,
    /// etc.) so the DM-LLM has an actual sentence to narrate/reference instead of having to assemble
    /// one from the raw Year/Month/Day/Epoch fields itself.
    /// </summary>
    public string FormattedDate => $"Day {Day}, Month {Month}, Year {Year} ({Epoch}) — {GetTimeOfDayName()}";
}

/// <summary>
/// Wire-facing projection of <see cref="CampaignTime"/> for WorldStateView/WorldStateDeltaView —
/// drops Id (singleton-doc-key bookkeeping) and LastUpdated (write-time bookkeeping, not narrative
/// content). Carries FormattedDate so the wire shape keeps the ready-to-narrate sentence.
/// </summary>
public record CampaignTimeView(
    string Epoch,
    int Year,
    int Month,
    int Day,
    int Hour,
    int TotalDaysElapsed,
    string FormattedDate)
{
    public static CampaignTimeView From(CampaignTime t) => new(
        t.Epoch, t.Year, t.Month, t.Day, t.Hour, t.TotalDaysElapsed, t.FormattedDate);
}
