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

    public static async Task<ClimateZone> ResolveEffectiveZoneAsync(
        IAsyncDocumentSession session,
        Location location,
        CancellationToken ct = default)
    {
        if (location.ClimateZone.HasValue)
        {
            return location.ClimateZone.Value;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { location.Id };
        var currentParentId = location.ParentLocationId;
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
