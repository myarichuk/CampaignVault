using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Shared logic for materializing/updating a named Point of Interest on a Location.
/// Used by both LocationUpdateHandler (explicit location_update) and ActivityChangeHandler
/// (inline poiName/poiDetails on an activity move), so the two entry points stay in sync.
/// </summary>
internal static class LocationPoiMaterializer
{
    public static void Apply(Location loc, string poiName, string? details)
    {
        if (!loc.PointsOfInterest.Contains(poiName))
        {
            loc.PointsOfInterest.Add(poiName);
        }

        if (string.IsNullOrWhiteSpace(details)) return;

        loc.PointOfInterestDetails ??= new(StringComparer.OrdinalIgnoreCase);
        var existingKey = loc.PointOfInterestDetails.Keys
            .FirstOrDefault(k => string.Equals(k, poiName, StringComparison.OrdinalIgnoreCase));
        var key = existingKey ?? poiName;
        loc.PointOfInterestDetails[key] = details!;
    }
}
