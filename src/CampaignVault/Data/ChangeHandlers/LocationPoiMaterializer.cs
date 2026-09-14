using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Shared logic for materializing/updating a named Point of Interest on a Location.
/// Only entry point is LocationUpdateHandler (explicit location_update) — ActivityChange carries
/// no PoI fields, so a character move never materializes location state on its own.
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
