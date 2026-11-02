using System.Text.RegularExpressions;
using CampaignVault.Models;

namespace CampaignVault.Data.Witness;

/// <summary>What one observer's eyes allow, and why not when they don't.</summary>
public sealed record SightLine(bool Sees, string Band, string? Note = null);

/// <summary>The light, the beat's own glow and who it comes from: what a visible beat needs to be seen (IMPRESSIONS_PLAN.md I1).</summary>
public sealed record SightContext(LightLevel Light, bool Luminous, Character? Origin);

/// <summary>
/// Can this observer see that source (IMPRESSIONS_PLAN.md I1, D7, D8)? A location is a bucket, so distance comes from the recorded
/// <see cref="SpatialPosition"/> bands (either side's record), then the light decides how far eyes reach:
/// House rule on the SRD's vision rules (D7, D8): the SRD makes dim light only a Perception penalty and darkness effective
/// blindness, which a location-sized bucket can't apply faithfully, so reach stands in for both:
/// <list type="bullet">
/// <item>Bright: everything at the location.</item>
/// <item>Dim (lightly obscured): out to Far. Darkvision sees dim light as bright within its range (SRD).</item>
/// <item>Dark (heavily obscured): Touch and Close only (someone at arm's length, a shape by firelight), or out to the
/// darkvision range, which treats darkness as dim light (SRD).</item>
/// <item>A luminous beat (fire, radiant light, a Daylight) is seen at any distance in any light.</item>
/// </list>
/// Hearing isn't touched: a loud beat carries across the whole location (and next door, <see cref="WitnessSelector.InEarshotAsync"/>).
/// </summary>
public static class SightResolver
{
    /// <summary>D7: house mapping from band to feet, only to compare against a darkvision range. Unknown or missing is Near.</summary>
    public static int Feet(string? band) => band?.Trim().ToLowerInvariant() switch
    {
        "touch" => 5,
        "close" => 10,
        "near" => 30,
        "far" => 120,
        "distant" => 500,
        _ => 30,
    };

    /// <summary>A bare "Darkvision" (PF2e's trait) has no range limit.</summary>
    public const int UnlimitedDarkvision = 10_000;

    private static readonly Regex DarkvisionPattern = new(@"\bdarkvision\b(?:\D{0,6}(\d+))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// D8: <see cref="Location.Lighting"/> when set; otherwise indoors (Building, Room) is dim, and outdoors follows the hour:
    /// bright 07–17, dim at dawn and dusk (06, 18–19), dark at night, except a settlement or district, which its lamps and
    /// windows keep dim. With no location or no clock there's nothing to go on, so nothing is hidden (bright).
    /// </summary>
    public static LightLevel LightAt(Location? location, CampaignTime? time)
    {
        if (location?.Lighting is { } set)
        {
            return set;
        }

        if (location is null)
        {
            return LightLevel.Bright;
        }

        if (location.Type is LocationType.Building or LocationType.Room)
        {
            return LightLevel.Dim;
        }

        if (time is null)
        {
            return LightLevel.Bright;
        }

        var hour = time.Hour;
        if (hour is >= 7 and <= 17)
        {
            return LightLevel.Bright;
        }

        var night = hour < 6 || hour > 19;
        return night && location.Type is not (LocationType.Settlement or LocationType.District) ? LightLevel.Dark : LightLevel.Dim;
    }

    /// <summary>
    /// F4: when the light was the engine's guess (no <see cref="Location.Lighting"/>) and it hid something, what was assumed and how
    /// to correct it. Null when the light was set, or bright (nothing hidden).
    /// </summary>
    public static string? AssumedLightNote(Location? location, CampaignTime? time, LightLevel light)
    {
        if (location is null || location.Lighting != null || light == LightLevel.Bright)
        {
            return null;
        }

        var basis = location.Type is LocationType.Building or LocationType.Room
            ? $"{location.Type}, indoors"
            : $"{location.Type}, {time?.Hour ?? 0:00}:00";
        return $"Light at {location.Name} taken as {light.ToString().ToLowerInvariant()} ({basis}), so not everyone could see it; "
               + "if that's wrong, set location_update lighting (bright, dim, dark; it persists, 'auto' clears it).";
    }

    /// <summary>Darkvision range in feet: <c>Attributes["darkvision"]</c>, else the race trait ("Darkvision 60 ft."), else none.</summary>
    public static int DarkvisionFeet(Character observer)
    {
        if (observer.SystemStats?.Attributes?.TryGetValue("darkvision", out var feet) == true && feet > 0)
        {
            return (int)Math.Round(feet);
        }

        foreach (var feature in observer.DistinctiveFeatures ?? [])
        {
            var match = DarkvisionPattern.Match(feature ?? "");
            if (match.Success)
            {
                return match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out var range) ? range : UnlimitedDarkvision;
            }
        }

        return 0;
    }

    /// <summary>The band between two characters as recorded by either side, or Near when neither recorded one (today's default).</summary>
    public static string BandBetween(Character observer, Character? source)
    {
        if (source is null || string.Equals(source.Id, observer.Id, StringComparison.OrdinalIgnoreCase))
        {
            return SpatialDistanceBand.Near;
        }

        if (IsDeparting(observer) || IsDeparting(source))
        {
            return SpatialDistanceBand.Distant;
        }

        return Recorded(observer, source.Id) ?? Recorded(source, observer.Id) ?? SpatialDistanceBand.Near;
    }

    /// <summary>D9: someone leaving is already Distant from everyone they're leaving behind.</summary>
    public static bool IsDeparting(Character c) => c.Departing != null;

    public static SightLine Sees(Character observer, SightContext sight)
    {
        var band = BandBetween(observer, sight.Origin);
        if (sight.Luminous || sight.Light == LightLevel.Bright)
        {
            return new SightLine(true, band);
        }

        var feet = Feet(band);
        var reach = Feet(sight.Light == LightLevel.Dim ? SpatialDistanceBand.Far : SpatialDistanceBand.Close);
        if (feet <= reach || DarkvisionFeet(observer) >= feet)
        {
            return new SightLine(true, band);
        }

        var where = band.ToLowerInvariant();
        return new SightLine(false, band, sight.Light == LightLevel.Dark ? $"{where}, too dark to see it" : $"{where}, too far to make it out in the dim light");
    }

    private static string? Recorded(Character from, string toId) =>
        from.SystemStats?.SpatialPositions?
            .FirstOrDefault(p => string.Equals(p.TargetId, toId, StringComparison.OrdinalIgnoreCase))?.DistanceBand;
}
