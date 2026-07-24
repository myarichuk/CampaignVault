using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

/// <summary>
/// Server-side scoping queries for rumors, quests, and factions in world-state context.
/// Session-0 fallback: if no location or faction affiliations exist, returns empty scoped results
/// (imminent-deadline union ensures time-critical items still surface). Mirrors PressureQueryHelper pattern.
/// </summary>
internal static class WorldStateScopeResolver
{
    private const int RelevantReputationThreshold = 10;

    /// <summary>
    /// Aggregates FactionReputations across all party characters (PCs and party companions).
    /// Returns a dictionary of factionId → reputation score for use in faction-scoping queries.
    /// </summary>
    public static async Task<Dictionary<string, int>> GetPartyFactionReputationsAsync(
        IAsyncDocumentSession session, string campaignName, CancellationToken ct = default)
    {
        var effective = campaignName ?? "";
        var partyMembers = await session.Query<Character, Character_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(c => (c.CampaignName == effective || c.CampaignName == null || c.CampaignName == "")
                        && (c.IsPc || c.IsPartyCompanion))
            .Select(c => c.Social)
            .ToListAsync();

        var aggregated = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var social in partyMembers)
        {
            if (social?.FactionReputations != null)
            {
                foreach (var kv in social.FactionReputations)
                {
                    if (!aggregated.ContainsKey(kv.Key))
                    {
                        aggregated[kv.Key] = 0;
                    }
                    aggregated[kv.Key] += kv.Value;
                }
            }
        }

        return aggregated;
    }

    /// <summary>
    /// Queries quests relevant to the party's current context via OR logic:
    /// quests related to the current region, related to factions the party knows,
    /// or visible to party members. Returns only matches; empty if no context yet.
    /// Note: imminent-deadline quests are queried separately and unioned in the caller.
    /// </summary>
    public static async Task<List<Quest>> QueryRelevantQuestsAsync(
        IAsyncDocumentSession session, string? campaignName, string? regionOrLocationId,
        IReadOnlyCollection<string> relevantFactionIds, IReadOnlyCollection<string> partyCharacterIds,
        int limit, CancellationToken ct = default)
    {
        var effective = campaignName ?? "";

        // Session-0 fallback: no context, return empty (imminent-deadline union handles it)
        var hasLocationContext = !string.IsNullOrEmpty(regionOrLocationId);
        var hasFactionContext = relevantFactionIds.Count > 0;
        var hasCharacterContext = partyCharacterIds.Count > 0;

        if (!hasLocationContext && !hasFactionContext && !hasCharacterContext)
        {
            return [];
        }

        // Query each criterion separately and union (RavenDB doesn't support OR in Where clauses)
        var questsByLocation = hasLocationContext
            ? await session.Query<Quest, Quest_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(q => (q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
                            && (q.CampaignName == effective || q.CampaignName == null || q.CampaignName == "")
                            && q.RelatedLocationIds.Contains(regionOrLocationId!))
                .Take(limit)
                .ToListAsync()
            : [];

        var questsByFaction = hasFactionContext
            ? (await session.Query<Quest, Quest_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(q => (q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
                            && (q.CampaignName == effective || q.CampaignName == null || q.CampaignName == ""))
                .Take(limit * 2)  // Fetch extra; client-side filter may reduce
                .ToListAsync())
                .Where(q => q.RelatedFactionIds.Any(fId => relevantFactionIds.Contains(fId)))
                .Take(limit)
                .ToList()
            : [];

        var questsByVisibility = hasCharacterContext
            ? (await session.Query<Quest, Quest_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(q => (q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
                            && (q.CampaignName == effective || q.CampaignName == null || q.CampaignName == "")
                            && q.VisibleToCharacterIds != null)
                .Take(limit * 2)  // Fetch extra; client-side filter may reduce
                .ToListAsync())
                .Where(q => q.VisibleToCharacterIds!.Any(cId => partyCharacterIds.Contains(cId)))
                .Take(limit)
                .ToList()
            : [];

        var quests = questsByLocation
            .Concat(questsByFaction)
            .Concat(questsByVisibility)
            .DistinctBy(q => q.Id)
            .Take(limit)
            .ToList();

        // Strip semantic vectors to reduce payload
        foreach (var quest in quests)
        {
            quest.SemanticVector = null;
            quest.EmbeddingTextHash = null;
        }
        return quests;
    }

    /// <summary>
    /// Queries quests with imminent deadlines (deadline within 3 days from now).
    /// These are always included in world state regardless of location/faction scoping,
    /// so the DM never misses a time-critical quest due to scope filtering.
    /// Threshold matches QuestDeadlinePressureContributor exactly.
    /// </summary>
    public static async Task<List<Quest>> QueryImminentDeadlineQuestsAsync(
        IAsyncDocumentSession session, string? campaignName, int currentDay, int limit,
        CancellationToken ct = default)
    {
        var effective = campaignName ?? "";
        var deadlineCutoff = currentDay + 3;

        var quests = await session.Query<Quest, Quest_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(q => (q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
                        && (q.CampaignName == effective || q.CampaignName == null || q.CampaignName == "")
                        && q.DeadlineDay != null
                        && q.DeadlineDay <= deadlineCutoff)
            .Take(limit)
            .ToListAsync();

        // Strip semantic vectors to reduce payload
        foreach (var q in quests)
        {
            q.SemanticVector = null;
            q.EmbeddingTextHash = null;
        }
        return quests;
    }

    /// <summary>
    /// Queries factions relevant to the party via OR logic: factions with territory in the current region,
    /// or factions the party has meaningful reputation with (|reputation| >= threshold).
    /// Returns only matches; empty if no context yet.
    /// </summary>
    public static async Task<List<Faction>> QueryRelevantFactionsAsync(
        IAsyncDocumentSession session, string? campaignName, string? regionOrLocationId,
        IReadOnlyDictionary<string, int> partyFactionReputations, int reputationThreshold, int limit,
        CancellationToken ct = default)
    {
        var effective = campaignName ?? "";

        var hasLocationContext = !string.IsNullOrEmpty(regionOrLocationId);
        var relevantFactionIds = partyFactionReputations
            .Where(kv => Math.Abs(kv.Value) >= reputationThreshold)
            .Select(kv => kv.Key)
            .ToList();
        var hasFactionContext = relevantFactionIds.Count > 0;

        // Session-0 fallback: no context, return empty
        if (!hasLocationContext && !hasFactionContext)
        {
            return [];
        }

        // Query factions by territory
        var factionsByTerritory = hasLocationContext
            ? await session.Query<Faction, Faction_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(f => (f.CampaignName == effective || f.CampaignName == null || f.CampaignName == "")
                            && (f.ControllingTerritory == regionOrLocationId || f.TerritoryLocationIds.Contains(regionOrLocationId!)))
                .Take(limit)
                .ToListAsync()
            : [];

        // Query factions by reputation
        var factionsByReputation = hasFactionContext
            ? await session.Query<Faction, Faction_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(f => (f.CampaignName == effective || f.CampaignName == null || f.CampaignName == "")
                            && relevantFactionIds.Contains(f.Id))
                .Take(limit)
                .ToListAsync()
            : [];

        var factions = factionsByTerritory
            .Concat(factionsByReputation)
            .DistinctBy(f => f.Id)
            .Take(limit)
            .ToList();

        // Strip semantic vectors to reduce payload
        foreach (var f in factions)
        {
            f.SemanticVector = null;
            f.EmbeddingTextHash = null;
        }
        return factions;
    }
}
