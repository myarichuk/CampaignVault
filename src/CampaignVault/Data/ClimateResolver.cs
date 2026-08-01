using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

/// <summary>
/// Resolves a location's effective ClimateZone by walking ParentLocationId ancestors
/// (depth-capped like ContainerResolver's nesting walk). Unset locations inherit from the
/// nearest ancestor that has one set; Temperate if none in the chain.
/// </summary>
public static class ClimateResolver
{
    public const int MaxAncestryDepth = 8;

    public static Task<ClimateZone> ResolveEffectiveZoneAsync(
        IAsyncDocumentSession session,
        Location location,
        CancellationToken ct = default) =>
        ResolveEffectiveZoneAsync(session, location.Id, location.ParentLocationId, location.ClimateZone, ct);

    public static Task<ClimateZone> ResolveEffectiveZoneAsync(
        IAsyncDocumentSession session,
        LocationDetailView location,
        CancellationToken ct = default) =>
        ResolveEffectiveZoneAsync(session, location.Id, location.ParentLocationId, location.ClimateZone, ct);

    private static async Task<ClimateZone> ResolveEffectiveZoneAsync(
        IAsyncDocumentSession session,
        string locationId,
        string? parentLocationId,
        ClimateZone? ownClimateZone,
        CancellationToken ct)
    {
        if (ownClimateZone.HasValue)
        {
            return ownClimateZone.Value;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { locationId };
        var currentParentId = parentLocationId;
        var depth = 0;

        while (!string.IsNullOrEmpty(currentParentId) && depth < MaxAncestryDepth)
        {
            if (!visited.Add(currentParentId))
            {
                break; // cycle guard
            }

            var parent = await session.LoadAsync<Location>(currentParentId, ct);
            if (parent == null)
            {
                break;
            }

            if (parent.ClimateZone.HasValue)
            {
                return parent.ClimateZone.Value;
            }

            currentParentId = parent.ParentLocationId;
            depth++;
        }

        return Models.ClimateZone.Temperate;
    }
}
