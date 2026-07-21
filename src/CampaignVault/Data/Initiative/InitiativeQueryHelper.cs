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
        bool waitForNonStale = false,
        CancellationToken ct = default)
    {
        var query = session.Advanced.AsyncDocumentQuery<Item, Item_Search>();
        if (waitForNonStale)
        {
            // Equip/unequip validation (conflict + tag checks) reads this same index right after a prior
            // Commit() wrote to it — without this, a rapid follow-up equip can see stale results and miss
            // the item just (un)equipped. Scene/holder-summary callers don't need this guarantee and skip
            // it to avoid adding index-wait latency to read-heavy paths (e.g. one wait per scene NPC).
            query = query.WaitForNonStaleResults(TimeSpan.FromSeconds(5));
        }

        return await query
            .WhereEquals(x => x.HolderId, holderId)
            .Take(limit)
            .ToListAsync(ct);
    }

    public static async Task<Dictionary<string, List<Item>>> QueryItemsForHoldersAsync(
        IAsyncDocumentSession session,
        string? campaignName,
        IReadOnlyCollection<string> holderIds,
        int limitPerHolder = 20,
        CancellationToken ct = default)
    {
        if (holderIds.Count == 0)
        {
            return new Dictionary<string, List<Item>>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, List<Item>>(StringComparer.OrdinalIgnoreCase);
        foreach (var holderId in holderIds)
        {
            var items = await QueryItemsHeldByAsync(session, holderId, limitPerHolder, ct: ct);
            if (items.Count > 0)
            {
                result[holderId] = items;
            }
        }

        return result;
    }
}