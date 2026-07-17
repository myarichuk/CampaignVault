using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Pure (ClimateZone, TimeOfDay) → °C lookup table. Desert gets an exaggerated diurnal swing
/// (hot days, cold nights); Subterranean is nearly flat (caves don't feel day/night). No wall-clock
/// hour granularity needed — TimeOfDay's 7-value cycle is enough for a narrative temperature curve.
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

    // Normalized diurnal curve: peaks at Noon (+1), troughs at Night (-1).
    private static readonly Dictionary<TimeOfDay, float> DiurnalMultiplier = new()
    {
        [TimeOfDay.Dawn] = -0.3f,
        [TimeOfDay.Morning] = 0.3f,
        [TimeOfDay.Noon] = 1.0f,
        [TimeOfDay.Afternoon] = 0.7f,
        [TimeOfDay.Evening] = 0.1f,
        [TimeOfDay.Dusk] = -0.3f,
        [TimeOfDay.Night] = -1.0f,
    };

    public static float GetTemperatureCelsius(ClimateZone zone, TimeOfDay timeOfDay)
    {
        var (baseline, amplitude) = ZoneProfiles.GetValueOrDefault(zone, ZoneProfiles[ClimateZone.Temperate]);
        var multiplier = DiurnalMultiplier.GetValueOrDefault(timeOfDay, 0f);
        return baseline + amplitude * multiplier;
    }
}
