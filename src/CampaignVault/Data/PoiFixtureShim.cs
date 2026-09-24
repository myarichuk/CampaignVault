using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

/// <summary>
/// Compatibility for older prompts that still send <c>pointOfInterestDetails</c> on world_build: points of
/// interest are retired, so each detail becomes a fixture item held by the location. Names without a
/// detail are dropped (they carried nothing the description doesn't). Existing fixtures are left alone.
/// </summary>
internal static class PoiFixtureShim
{
    public static async Task ApplyAsync(
        IAsyncDocumentSession session, Location location, string campaignName, IReadOnlyDictionary<string, string>? details)
    {
        if (details is not { Count: > 0 })
        {
            return;
        }

        foreach (var (name, text) in details)
        {
            var itemId = Migrations.MigratePointsOfInterestToFixtures.FixtureItemId(location.Id, name);
            if (await session.LoadAsync<Item>(itemId) != null)
            {
                continue;
            }

            await session.StoreAsync(new Item
            {
                Id = itemId,
                Name = name,
                Description = text,
                HolderId = location.Id,
                CampaignName = campaignName,
                CoreCategory = ItemCategories.Other,
                Tags = ["fixture"]
            }, itemId);
        }
    }
}
