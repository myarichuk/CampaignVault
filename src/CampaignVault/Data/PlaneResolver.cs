using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

/// <summary>
/// Resolves a location's effective plane of existence by walking ParentLocationId ancestors
/// (depth-capped like ContainerResolver's nesting walk). Unset locations inherit from the
/// nearest ancestor that has one set; "Material Plane" if none in the chain.
/// </summary>
public static class PlaneResolver
{
    public const int MaxAncestryDepth = 8;

    /// <summary>Default plane when nothing in the ancestry chain names one.</summary>
    public const string MaterialPlane = "Material Plane";

    public static Task<string> ResolveEffectivePlaneAsync(
        IAsyncDocumentSession session,
        Location location,
        CancellationToken ct = default) =>
        ResolveEffectivePlaneAsync(session, location.Id, location.ParentLocationId, location.Plane, ct);

    public static Task<string> ResolveEffectivePlaneAsync(
        IAsyncDocumentSession session,
        LocationDetailView location,
        CancellationToken ct = default) =>
        ResolveEffectivePlaneAsync(session, location.Id, location.ParentLocationId, location.Plane, ct);

    private static async Task<string> ResolveEffectivePlaneAsync(
        IAsyncDocumentSession session,
        string locationId,
        string? parentLocationId,
        string? ownPlane,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(ownPlane))
        {
            return ownPlane.Trim();
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

            if (!string.IsNullOrWhiteSpace(parent.Plane))
            {
                return parent.Plane.Trim();
            }

            currentParentId = parent.ParentLocationId;
            depth++;
        }

        return MaterialPlane;
    }
}
