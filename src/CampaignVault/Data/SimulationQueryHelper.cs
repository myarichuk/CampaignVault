using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

/// <summary>
/// Static-index queries for AdvanceWorld simulation context loading.
/// Avoids unscoped collection scans with arbitrary <c>Take(N)</c> truncation.
/// </summary>
internal static class SimulationQueryHelper
{
    private static readonly TimeSpan IndexWait = TimeSpan.FromSeconds(3);

    public static async Task<List<Character>> QueryCampaignCharactersAsync(
        IAsyncDocumentSession session,
        string? campaignName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(campaignName))
        {
            return await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
                .WaitForNonStaleResults(IndexWait)
                .ToListAsync(ct);
        }

        var indexed = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WaitForNonStaleResults(IndexWait)
            .WhereEquals(x => x.CampaignName, campaignName)
            .ToListAsync(ct);

        // Legacy shareable characters may have no CampaignName set.
        var shareable = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WaitForNonStaleResults(IndexWait)
            .Not.WhereExists(x => x.CampaignName)
            .ToListAsync(ct);

        return indexed.Concat(shareable).DistinctBy(c => c.Id).ToList();
    }

    public static async Task<List<Rumor>> QueryActiveRumorsAsync(
        IAsyncDocumentSession session,
        string? campaignName,
        CancellationToken ct = default)
    {
        var rumors = await session.Query<Rumor, Rumor_Search>()
            .Customize(x => x.WaitForNonStaleResults(IndexWait))
            .Where(r => r.State != RumorState.Resolved && r.State != RumorState.Forgotten)
            .ToListAsync(ct);

        if (string.IsNullOrWhiteSpace(campaignName))
        {
            return rumors;
        }

        return rumors
            .Where(r => string.IsNullOrEmpty(r.CampaignName)
                || string.Equals(r.CampaignName, campaignName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static async Task<List<Faction>> QueryCampaignFactionsAsync(
        IAsyncDocumentSession session,
        string? campaignName,
        CancellationToken ct = default)
    {
        var factions = await session.Query<Faction, Faction_Search>()
            .Customize(x => x.WaitForNonStaleResults(IndexWait))
            .ToListAsync(ct);

        if (string.IsNullOrWhiteSpace(campaignName))
        {
            return factions;
        }

        return factions
            .Where(f => string.IsNullOrEmpty(f.CampaignName)
                || string.Equals(f.CampaignName, campaignName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static async Task<List<Quest>> QueryActiveQuestsAsync(
        IAsyncDocumentSession session,
        string? campaignName,
        CancellationToken ct = default)
    {
        var quests = await session.Query<Quest, Quest_Search>()
            .Customize(x => x.WaitForNonStaleResults(IndexWait))
            .Where(q => q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
            .ToListAsync(ct);

        if (string.IsNullOrWhiteSpace(campaignName))
        {
            return quests;
        }

        return quests
            .Where(q => string.IsNullOrEmpty(q.CampaignName)
                || string.Equals(q.CampaignName, campaignName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static async Task<List<Character>> QueryEvictableTransientCharactersAsync(
        IAsyncDocumentSession session,
        string? campaignName,
        int limit = 200,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(campaignName))
        {
            return await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
                .WaitForNonStaleResults(IndexWait)
                .WhereEquals(x => x.KeepAlive, false)
                .AndAlso()
                .WhereEquals(x => x.IsPc, false)
                .AndAlso()
                .WhereEquals(x => x.IsPartyCompanion, false)
                .AndAlso()
                .WhereEquals("HasSchedule", false)
                .AndAlso()
                .WhereExists("CurrentLocationId")
                .Take(limit)
                .ToListAsync(ct);
        }

        var indexed = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WaitForNonStaleResults(IndexWait)
            .WhereEquals(x => x.CampaignName, campaignName)
            .AndAlso()
            .WhereEquals(x => x.KeepAlive, false)
            .AndAlso()
            .WhereEquals(x => x.IsPc, false)
            .AndAlso()
            .WhereEquals(x => x.IsPartyCompanion, false)
            .AndAlso()
            .WhereEquals("HasSchedule", false)
            .AndAlso()
            .WhereExists("CurrentLocationId")
            .Take(limit)
            .ToListAsync(ct);

        // Legacy shareable characters may have no CampaignName set.
        var shareable = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WaitForNonStaleResults(IndexWait)
            .Not.WhereExists(x => x.CampaignName)
            .AndAlso()
            .WhereEquals(x => x.KeepAlive, false)
            .AndAlso()
            .WhereEquals(x => x.IsPc, false)
            .AndAlso()
            .WhereEquals(x => x.IsPartyCompanion, false)
            .AndAlso()
            .WhereEquals("HasSchedule", false)
            .AndAlso()
            .WhereExists("CurrentLocationId")
            .Take(limit)
            .ToListAsync(ct);

        return indexed.Concat(shareable).DistinctBy(c => c.Id).Take(limit).ToList();
    }

    public static async Task<List<PlotThread>> QueryActivePlotThreadsAsync(
        IAsyncDocumentSession session,
        string? campaignName,
        CancellationToken ct = default)
    {
        var query = session.Query<PlotThread, PlotThread_Search>()
            .Customize(x => x.WaitForNonStaleResults(IndexWait))
            .Where(t => t.State != PlotThreadState.Resolved && t.State != PlotThreadState.Abandoned);

        // IsArchived is not an indexed field on PlotThread/Search, so filter it post-query
        // (same pattern already used below for campaign-name scoping).
        var threads = (await query.ToListAsync(ct)).Where(t => !t.IsArchived).ToList();

        if (string.IsNullOrWhiteSpace(campaignName))
            return threads;

        return threads
            .Where(t => string.IsNullOrEmpty(t.CampaignName)
                || string.Equals(t.CampaignName, campaignName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}