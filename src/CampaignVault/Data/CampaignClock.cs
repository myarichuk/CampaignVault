using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>Absolute campaign time in hours, minutes included (the clock face moves by whole hours; HourCarry holds the rest).</summary>
public static class CampaignClock
{
    public static double Now(CampaignTime time) => time.TotalDaysElapsed * 24.0 + time.Hour + time.HourCarry;
}
