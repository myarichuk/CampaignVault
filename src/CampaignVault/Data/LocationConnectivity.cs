using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

/// <summary>
/// Reciprocal-exit repair shared by location_update (handler) and world_build (repository): a forward exit
/// that is not marked OneWay gets a matching exit back, so the party can return the way it came without a
/// corrective commit. Travel cost, terrain and secrecy mirror the forward exit.
/// </summary>
public static class LocationConnectivity
{
    /// <summary>The exit on the far side of <paramref name="forward"/>, leading back to <paramref name="source"/>.</summary>
    public static LocationExit ReverseOf(LocationExit forward, Location source) =>
        new(source.Id, $"Leads back to {source.Name}",
            TravelCostHours: forward.TravelCostHours,
            Terrain: forward.Terrain,
            // The far side of a secret passage is just as secret until someone finds or uses it.
            Hidden: forward.Hidden, DiscoverDc: forward.DiscoverDc, Intent: forward.Intent);

    /// <summary>
    /// For every non-OneWay exit of <paramref name="location"/> whose target exists, adds the missing exit back.
    /// Targets are read through <paramref name="session"/> (tracked, so a save persists them) with
    /// <paramref name="known"/> consulted first for documents created in the same batch. Returns the ids
    /// of the targets that were repaired.
    /// </summary>
    public static async Task<List<string>> EnsureReverseExitsAsync(
        IAsyncDocumentSession session, Location location, IReadOnlyDictionary<string, Location>? known = null,
        CancellationToken ct = default)
    {
        var repaired = new List<string>();
        if (location.Exits is not { Count: > 0 })
        {
            return repaired;
        }

        foreach (var exit in location.Exits.ToList())
        {
            if (exit.OneWay || string.IsNullOrWhiteSpace(exit.TargetLocationId)
                || string.Equals(exit.TargetLocationId, location.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Location? target = null;
            known?.TryGetValue(exit.TargetLocationId, out target);
            target ??= await session.LoadAsync<Location>(exit.TargetLocationId, ct);
            if (target is null)
            {
                continue; // created later; its own upsert will link back, or the pressure contributor reports it
            }

            target.Exits ??= [];
            if (target.Exits.Any(e => string.Equals(e.TargetLocationId, location.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            target.Exits.Add(ReverseOf(exit, location));
            target.LastUpdated = DateTime.UtcNow;
            repaired.Add(target.Id);
        }

        return repaired;
    }
}
