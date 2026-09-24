using System.Text;
using CampaignVault.Models;

namespace CampaignVault.Data.Migrations;

/// <summary>
/// Retires points of interest (see TAKE_TURN_PLAN.md, "Remove points of interest"). A POI that has a
/// recorded detail becomes a real fixture <see cref="Item"/> held by its location (the detail text is
/// its description). A name-only POI carried no information the location description doesn't, so it is
/// dropped; if the location has no description at all, the names are folded into it instead. Then the
/// three legacy POI fields are cleared.
///
/// Idempotent: a location with no POI data is untouched, and a fixture item that already exists is not
/// recreated. Runs before the semantic-vector bootstrap, which embeds the new items.
/// </summary>
public class MigratePointsOfInterestToFixtures
{
    private readonly IDocumentStore _documentStore;

    public MigratePointsOfInterestToFixtures(IDocumentStore documentStore)
    {
        _documentStore = documentStore;
    }

    public async Task<(int Locations, int Items)> ExecuteAsync(CancellationToken ct = default)
    {
        using var session = _documentStore.OpenAsyncSession();

        var locations = await session.Query<Location>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
            .Where(l => l.CampaignName != null)
            .ToListAsync(ct);

        var migratedLocations = 0;
        var createdItems = 0;

        foreach (var location in locations)
        {
            var names = location.PointsOfInterest ?? [];
            var details = location.PointOfInterestDetails ?? [];
            var used = location.PoisUsedByActivity ?? [];
            if (names.Count == 0 && details.Count == 0 && used.Count == 0)
            {
                continue;
            }

            foreach (var (poiName, text) in details)
            {
                var itemId = FixtureItemId(location.Id, poiName);
                if (await session.LoadAsync<Item>(itemId, ct) != null)
                {
                    continue;
                }

                await session.StoreAsync(new Item
                {
                    Id = itemId,
                    Name = poiName,
                    Description = text,
                    HolderId = location.Id,
                    CampaignName = location.CampaignName,
                    CoreCategory = ItemCategories.Other,
                    Tags = ["fixture"]
                }, itemId, ct);
                createdItems++;
            }

            if (string.IsNullOrWhiteSpace(location.Description))
            {
                var undetailed = names.Where(n => !details.ContainsKey(n)).ToList();
                if (undetailed.Count > 0)
                {
                    location.Description = "Notable features: " + string.Join(", ", undetailed) + ".";
                }
            }

            location.PointsOfInterest = [];
            location.PointOfInterestDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            location.PoisUsedByActivity = [];
            migratedLocations++;
        }

        if (migratedLocations > 0)
        {
            await session.SaveChangesAsync(ct);
        }

        return (migratedLocations, createdItems);
    }

    /// <summary>Sibling of the location's own id: "…/locations/tavern" + "Notice Board" → "…/items/tavern-notice-board".</summary>
    internal static string FixtureItemId(string locationId, string poiName)
    {
        const string marker = "locations/";
        var at = locationId.LastIndexOf(marker, StringComparison.Ordinal);
        var prefix = at >= 0 ? locationId[..at] : "";
        var tail = at >= 0 ? locationId[(at + marker.Length)..] : locationId;
        return $"{prefix}items/{tail}-{Slug(poiName)}";
    }

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        return sb.ToString().Trim('-');
    }
}
