using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data.Pressure;

/// <summary>
/// Static-index queries for pressure contributors.
/// Avoids collection LINQ (<c>session.Query&lt;T&gt;()</c>) which spawns runtime auto-indexes
/// and causes WaitForIndexesAfterSaveChanges timeouts in embedded Raven under test load.
/// </summary>
internal static class PressureQueryHelper
{
    public static async Task<List<Event>> QueryUnresolvedEventsAsync(
        IAsyncDocumentSession session, string campaignName, int limit, CancellationToken ct = default)
    {
        return await session.Advanced.AsyncDocumentQuery<Event, Event_Search>()
            .WhereEquals(x => x.CampaignName, campaignName)
            .AndAlso()
            .WhereEquals(x => x.Category, EventCategory.Unresolved)
            .Take(limit)
            .ToListAsync(ct);
    }

    public static async Task<List<Event>> QueryRecentCampaignEventsAsync(
        IAsyncDocumentSession session,
        string campaignName,
        int minDayLogged,
        int limit,
        CancellationToken ct = default)
    {
        var simulation = await session.Advanced.AsyncDocumentQuery<Event, Event_Search>()
            .WhereEquals(x => x.CampaignName, campaignName)
            .AndAlso()
            .WhereEquals(x => x.Category, EventCategory.Simulation)
            .AndAlso()
            .WhereGreaterThanOrEqual(x => x.DayLogged, minDayLogged)
            .Take(limit)
            .ToListAsync(ct);

        var commits = await session.Advanced.AsyncDocumentQuery<Event, Event_Search>()
            .WhereEquals(x => x.CampaignName, campaignName)
            .AndAlso()
            .WhereEquals(x => x.Category, EventCategory.SceneCommit)
            .AndAlso()
            .WhereGreaterThanOrEqual(x => x.DayLogged, minDayLogged)
            .Take(limit)
            .ToListAsync(ct);

        return simulation.Concat(commits).ToList();
    }

    public static async Task<List<Character>> QueryCombatantCharactersAsync(
        IAsyncDocumentSession session, string campaignName, int limit, CancellationToken ct = default)
    {
        var indexed = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WhereEquals(x => x.CampaignName, campaignName)
            .Take(limit * 2)
            .ToListAsync(ct);

        var shareable = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .Not.WhereExists(x => x.CampaignName)
            .Take(limit)
            .ToListAsync(ct);

        return indexed.Concat(shareable)
            .Where(c => c.KeepAlive || c.MaxHp > 0 || c.IsPc || c.IsPartyCompanion)
            .DistinctBy(c => c.Id)
            .Take(limit)
            .ToList();
    }

    public static async Task<List<Character>> QueryKeepAliveCharactersAsync(
        IAsyncDocumentSession session, string campaignName, int limit, CancellationToken ct = default)
    {
        var indexed = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WhereEquals(x => x.CampaignName, campaignName)
            .AndAlso()
            .WhereEquals(x => x.KeepAlive, true)
            .Take(limit)
            .ToListAsync(ct);

        // Legacy shareable characters may have no CampaignName set.
        var shareable = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .Not.WhereExists(x => x.CampaignName)
            .AndAlso()
            .WhereEquals(x => x.KeepAlive, true)
            .Take(limit)
            .ToListAsync(ct);

        return indexed.Concat(shareable).DistinctBy(c => c.Id).Take(limit).ToList();
    }

    public static async Task<List<Character>> QueryTransientCharactersAsync(
        IAsyncDocumentSession session, string campaignName, int limit, CancellationToken ct = default)
    {
        return await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WhereEquals(x => x.CampaignName, campaignName)
            .AndAlso()
            .WhereEquals(x => x.KeepAlive, false)
            .AndAlso()
            .WhereEquals("HasSchedule", false)
            .Take(limit)
            .ToListAsync(ct);
    }

    public static async Task<List<Character>> QueryCharactersWithActivityAsync(
        IAsyncDocumentSession session, string campaignName, int limit, CancellationToken ct = default)
    {
        var indexed = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WhereEquals(x => x.CampaignName, campaignName)
            .AndAlso()
            .WhereExists(x => x.CurrentActivity)
            .Take(limit)
            .ToListAsync(ct);

        var shareable = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .Not.WhereExists(x => x.CampaignName)
            .AndAlso()
            .WhereExists(x => x.CurrentActivity)
            .Take(limit)
            .ToListAsync(ct);

        return indexed.Concat(shareable).DistinctBy(c => c.Id).Take(limit).ToList();
    }

    public static async Task<List<Item>> QueryCampaignItemsAsync(
        IAsyncDocumentSession session, string campaignName, int limit, CancellationToken ct = default)
    {
        return await session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
            .WhereEquals(x => x.CampaignName, campaignName)
            .Take(limit)
            .ToListAsync(ct);
    }

    public static async Task<bool> HasSceneInterruptTodayAsync(
        IAsyncDocumentSession session,
        string? campaignName,
        string locationId,
        int currentDay,
        CancellationToken ct = default)
    {
        var query = session.Advanced.AsyncDocumentQuery<Event, Event_Search>()
            .WhereEquals(x => x.Category, EventCategory.SceneInterrupt)
            .AndAlso()
            .WhereEquals(x => x.DayLogged, currentDay);

        if (!string.IsNullOrWhiteSpace(campaignName))
        {
            query = query.AndAlso().WhereEquals(x => x.CampaignName, campaignName);
        }

        var events = await query.Take(20).ToListAsync(ct);

        return events.Any(e =>
            e.Involved != null
            && e.Involved.Contains(locationId, StringComparer.OrdinalIgnoreCase));
    }
}