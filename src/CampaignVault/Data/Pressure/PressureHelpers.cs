using CampaignVault.Models;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data.Pressure;

internal static class PressureHelpers
{
    private static string BuildCanonicalIdPrefix(string cleanQuery, string prefix) =>
        cleanQuery.Contains('/', StringComparison.Ordinal) ? cleanQuery : prefix + cleanQuery;

    public static bool ItemMatchesEconomicDemand(Item item, string demand) =>
        item.CoreCategory.ToString().Equals(demand, StringComparison.OrdinalIgnoreCase)
        || item.Tags.Any(t => t.Equals(demand, StringComparison.OrdinalIgnoreCase));

    public static async Task<List<Item>> LoadPartyInventoryAsync(IAsyncDocumentSession session, SceneView scene, string campaignName)
    {
        var pcHolderIds = scene.PresentNPCs?
            .Where(n => n.KeepAlive)
            .Select(n => n.Id)
            .Distinct()
            .ToList() ?? [];

        var partyItems = await session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
            .WhereEquals(x => x.HolderId, "party")
            .Take(100)
            .ToListAsync();

        var memberItems = new List<Item>();
        foreach (var holderId in pcHolderIds)
        {
            var batch = await session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
                .WhereEquals(x => x.HolderId, holderId)
                .Take(50)
                .ToListAsync();
            memberItems.AddRange(batch);
        }

        return partyItems.Concat(memberItems)
            .Where(i => string.IsNullOrEmpty(i.CampaignName) || i.CampaignName == campaignName)
            .DistinctBy(i => i.Id)
            .ToList();
    }

    public static async Task<List<Location>> SuggestLocationsAsync(IAsyncDocumentSession session, string nameQuery, string? campaignName = null)
    {
        var effective = string.IsNullOrWhiteSpace(campaignName) ? "default" : campaignName;
        var rawQuery = nameQuery.Trim();
        var cleanQuery = rawQuery;
        if (cleanQuery.StartsWith("locations/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery.Substring("locations/".Length);
        }
        else if (cleanQuery.StartsWith("locs/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery.Substring("locs/".Length);
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var canonicalIdPrefix = BuildCanonicalIdPrefix(cleanQuery, "locations/");

        var suggestions = await session.Query<Location, Location_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == effective || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await session.Query<Location, Location_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == effective || x.CampaignName == null)
                .Search(x => x.Name, cleanQuery + "*")
                .Take(3).ToListAsync();

            foreach (var item in byName)
            {
                if (suggestions.All(s => s.Id != item.Id) && suggestions.Count < 3)
                {
                    suggestions.Add(item);
                }
            }
        }

        return suggestions;
    }
}
