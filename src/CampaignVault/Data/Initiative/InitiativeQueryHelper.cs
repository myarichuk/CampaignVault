using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data.Initiative;

internal static class InitiativeQueryHelper
{
    private const int DefaultEventLookbackDays = 2;

    public static async Task<List<Event>> QueryRecentCampaignEventsAsync(
        IAsyncDocumentSession session,
        string campaignName,
        int currentDay,
        int lookbackDays = DefaultEventLookbackDays,
        int limit = 50,
        CancellationToken ct = default)
    {
        var minDay = Math.Max(0, currentDay - lookbackDays);
        var query = session.Advanced.AsyncDocumentQuery<Event, Event_Search>();
        if (!string.IsNullOrWhiteSpace(campaignName))
        {
            query = query.WhereEquals(x => x.CampaignName, campaignName);
        }

        var events = await query.Take(limit).ToListAsync(ct);
        return events.Where(e => e.DayLogged >= minDay).ToList();
    }

    public static async Task<List<Item>> QueryItemsHeldByAsync(
        IAsyncDocumentSession session,
        string holderId,
        int limit = 20,
        CancellationToken ct = default)
    {
        return await session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
            .WhereEquals(x => x.HolderId, holderId)
            .Take(limit)
            .ToListAsync(ct);
    }

    public static async Task<Dictionary<string, List<Item>>> QueryItemsForHoldersAsync(
        IAsyncDocumentSession session,
        string? campaignName,
        IReadOnlyCollection<string> holderIds,
        int limit = 200,
        CancellationToken ct = default)
    {
        if (holderIds.Count == 0)
        {
            return new Dictionary<string, List<Item>>(StringComparer.OrdinalIgnoreCase);
        }

        List<Item> items;
        if (!string.IsNullOrWhiteSpace(campaignName))
        {
            items = await session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
                .WhereEquals(x => x.CampaignName, campaignName)
                .Take(limit)
                .ToListAsync(ct);
        }
        else
        {
            items = await session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
                .Take(limit)
                .ToListAsync(ct);
        }

        return items
            .Where(i => holderIds.Contains(i.HolderId))
            .GroupBy(i => i.HolderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }
}