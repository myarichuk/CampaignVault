using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Pure (ClimateZone, Hour) → °C lookup. Desert gets an exaggerated diurnal swing
/// (hot days, cold nights); Subterranean is nearly flat (caves don't feel day/night).
/// Uses smooth diurnal curve mapped from hour (0-23).
/// </summary>
public static class ClimateCycle
{
    private static readonly Dictionary<ClimateZone, (float Baseline, float Amplitude)> ZoneProfiles = new()
    {
        [ClimateZone.Arctic] = (-20f, 6f),
        [ClimateZone.Tundra] = (-5f, 6f),
        [ClimateZone.Temperate] = (15f, 6f),
        [ClimateZone.Desert] = (25f, 16f),
        [ClimateZone.Tropical] = (28f, 5f),
        [ClimateZone.Alpine] = (-5f, 8f),
        [ClimateZone.Subterranean] = (12f, 1f),
    };

    /// <summary>
    /// Maps hour of day (0-23) to a diurnal temperature multiplier.
    /// Peaks at noon (hour 12) = +1, troughs at midnight (hour 0) = -1.
    /// Smooth cosine-like curve.
    /// </summary>
    private static float GetDiurnalMultiplier(int hour)
    {
        // Normalize hour to 0-1 range, offset so 12 (noon) = 0 in the sine curve
        var normalizedHour = (hour - 6) / 12f; // 6am = -1, 6pm = +1
        return (float)Math.Sin(Math.PI * normalizedHour); // Smooth curve -1 to +1
    }

    public static float GetTemperatureCelsius(ClimateZone zone, int hour)
    {
        var (baseline, amplitude) = ZoneProfiles.GetValueOrDefault(zone, ZoneProfiles[ClimateZone.Temperate]);
        var multiplier = GetDiurnalMultiplier(hour);
        return baseline + amplitude * multiplier;
    }
}
