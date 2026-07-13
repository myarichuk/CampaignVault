using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Initiative;
using CampaignVault.Data.Scenes;
using CampaignVault.Models;
using CampaignVault.Services;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

public class CampaignRepository
{
    private readonly IDocumentStore _store;
    private readonly IWorldSimulationEngine _simulationEngine;
    private readonly ILogger<CampaignRepository> _logger;
    private readonly INpcBehaviorSynthesizer _behaviorSynthesizer;
    private readonly WorldChangeDispatcher _changeDispatcher;
    private readonly CampaignDocumentKeys _keys;
    private readonly INpcInitiativeService _initiativeService;
    private readonly SceneAssembler _sceneAssembler;
    private readonly ILocalEmbeddingService _embeddingService;

    private string ResolveCampaign(string? campaignName)
    {
        if (CampaignSlug.TryCanonicalize(campaignName, out var explicitSlug))
        {
            return explicitSlug;
        }

        throw new CampaignNotSelectedException();
    }

    private static bool IsVisibleInCampaign(string? entityCampaignName, string effectiveCampaign) =>
        CampaignEntityVisibility.IsVisibleInCampaign(entityCampaignName, effectiveCampaign);

    private static string BuildCanonicalIdPrefix(string cleanQuery, string prefix) =>
        cleanQuery.Contains('/', StringComparison.Ordinal) ? cleanQuery : prefix + cleanQuery;

    public CampaignRepository(
        IDocumentStore store,
        IWorldSimulationEngine simulationEngine,
        ILogger<CampaignRepository> logger,
        INpcBehaviorSynthesizer behaviorSynthesizer,
        CampaignDocumentKeys keys,
        ChangeHandlers.WorldChangeDispatcher changeDispatcher,
        SceneAssembler sceneAssembler,
        INpcInitiativeService initiativeService,
        ILocalEmbeddingService embeddingService)
    {
        _store = store;
        _simulationEngine = simulationEngine;
        _logger = logger;
        _behaviorSynthesizer = behaviorSynthesizer;
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _initiativeService = initiativeService ?? throw new ArgumentNullException(nameof(initiativeService));
        _sceneAssembler = sceneAssembler ?? throw new ArgumentNullException(nameof(sceneAssembler));
        _changeDispatcher = changeDispatcher ?? throw new ArgumentNullException(nameof(changeDispatcher));
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
    }

    private async Task EnrichSemanticVectorAsync(IHasSemanticVector entity)
    {
        var textToEmbed = entity.BuildEmbeddingText();
        if (string.IsNullOrWhiteSpace(textToEmbed))
        {
            entity.SemanticVector = null;
            entity.EmbeddingTextHash = null;
            return;
        }

        var hash = ComputeEmbeddingHash(textToEmbed);
        if (hash == entity.EmbeddingTextHash)
            return;

        try
        {
            entity.SemanticVector = await _embeddingService.GenerateEmbeddingAsync(textToEmbed);
            entity.EmbeddingTextHash = hash;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding generation failed for {EntityType}; semantic search unavailable for this entity.", entity.GetType().Name);
            entity.SemanticVector = null;
            entity.EmbeddingTextHash = null;
        }
    }

    private static string ComputeEmbeddingHash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    public IAsyncDocumentSession OpenSession()
    {
        var session = _store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        return session;
    }

    /// <summary>
    /// Stages a batch of WorldChange deltas into the provided session (applies clamping, atomic patches,
    /// relationship/need/attribute updates, etc.) and returns a summary.
    ///
    /// <para><b>Important:</b> This method does <b>not</b> call <c>SaveChangesAsync</c>. The caller
    /// (typically <c>CampaignTools.ExecuteAsync</c> or an explicit test block) is responsible for
    /// persisting. This keeps the method usable inside larger transactions and makes the contract explicit.</para>
    ///
    /// Use this for all atomic world mutations coming from tools or simulation rules.
    /// </summary>
    public async Task<CommitResult> StageChangesAsync(IAsyncDocumentSession session, WorldChange[]? changes,
        string? campaignName = null)
    {
        changes ??= [];
        var effective = ResolveCampaign(campaignName);

        _logger.LogDebug("StageChangesAsync called with {ChangeCount} changes for campaign {Campaign}", changes.Length,
            effective);

        var result = await _changeDispatcher.DispatchAsync(
            session,
            changes,
            effective,
            () => GetTimeAsync(session, effective),
            async () =>
            {
                var camp = await session.LoadAsync<Campaign>(_keys.Meta(effective));
                return camp?.SystemOptions ?? new();
            },
            ev => LogEventAsync(session, ev, effective));

        if (result.Success && changes.Length > 0)
        {
            var metaId = _keys.Meta(effective);
            var campaign = await session.LoadAsync<Campaign>(metaId);
            if (campaign != null)
            {
                var involvedEntities = result.InvolvedEntities;

                if (involvedEntities.Count > 0)
                {
                    var keysToRemove = campaign.PressureCooldowns.Keys
                        .Where(k => involvedEntities.Any(e => k.EndsWith($":{e}", StringComparison.OrdinalIgnoreCase)))
                        .ToList();

                    foreach (var k in keysToRemove)
                    {
                        campaign.PressureCooldowns.Remove(k);
                    }
                }

                result.InvolvedEntities = involvedEntities.ToList();
            }
        }

        return result;
    }

    /// <summary>
    /// Fetches the synthesized state of a location, including NPCs present, visible items, local rumors, and recent events.
    /// This is the primary read operation used by the LLM when entering a new scene.
    /// </summary>
    public async Task<SceneView> GetSceneAsync(IAsyncDocumentSession session, string locationId,
        string? campaignName = null, bool markVisited = false)
    {
        var effective = ResolveCampaign(campaignName);
        var location = await session
            .Include<Location>(x => x.ParentLocationId)
            .LoadAsync<Location>(locationId);

        if (location == null || !IsVisibleInCampaign(location.CampaignName, effective))
        {
            return _sceneAssembler.CreateUnanchoredScene(locationId);
        }
        var sceneContext = await LoadSceneAssemblyContextAsync(session, location, locationId, effective, markVisited);
        return _sceneAssembler.Assemble(sceneContext);
    }

    private async Task<SceneAssemblyContext> LoadSceneAssemblyContextAsync(
        IAsyncDocumentSession session,
        Location location,
        string locationId,
        string effectiveCampaign,
        bool markVisited)
    {
        var regionId = location.ParentLocationId ?? locationId;
        var targetIds = await GetSceneTargetIdsAsync(session, locationId, effectiveCampaign);
        var npcsFromIndex = await LoadSceneNpcsFromIndexAsync(session, targetIds);
        var npcsFromSimulation = await LoadSceneNpcsFromSimulationAsync(session, targetIds);
        var rumors = (await QueryRumorsAsync(session, null, regionId, null, 5, effectiveCampaign)).ToList();
        var items = await LoadVisibleSceneItemsAsync(session, locationId, effectiveCampaign);
        var config = await GetCampaignConfigAsync(session, effectiveCampaign);
        var events = await LoadSceneEventsAsync(session, locationId, effectiveCampaign, config.EventContextBudgetAmbient);

        JsonSanitizer.Sanitize(location);

        var time = await GetTimeAsync(session, effectiveCampaign);
        var globalDescriptors = await GetGlobalNeedDescriptorsAsync(session, effectiveCampaign);
        var campaign = await LoadOrCreateCampaignMetaAsync(session, effectiveCampaign);
        var recentCampaignEvents = await InitiativeQueryHelper.QueryRecentCampaignEventsAsync(
            session, effectiveCampaign, time.TotalDaysElapsed);
        var itemsByHolder = await InitiativeQueryHelper.QueryItemsForHoldersAsync(
            session,
            effectiveCampaign,
            GatherSceneNpcIds(npcsFromIndex, npcsFromSimulation));
        var activeCombat = await session.LoadAsync<CombatEncounter>(_keys.CombatCurrent(effectiveCampaign));
        var activeQuests = await GetActiveQuestsForLocationAsync(session, locationId, effectiveCampaign);
        var relevantFactions = await GetFactionsForLocationAsync(session, locationId, effectiveCampaign);

        return new SceneAssemblyContext
        {
            RequestedLocationId = locationId,
            EffectiveCampaign = effectiveCampaign,
            Location = location,
            NpcsFromIndex = npcsFromIndex,
            NpcsFromSimulation = npcsFromSimulation,
            Rumors = rumors,
            Items = items,
            Events = events,
            Time = time,
            GlobalNeedDescriptors = globalDescriptors,
            Config = config,
            Campaign = campaign,
            RecentCampaignEvents = recentCampaignEvents,
            ItemsByHolder = itemsByHolder,
            ActiveCombat = activeCombat,
            ActiveQuests = activeQuests,
            RelevantFactions = relevantFactions,
            MarkVisited = markVisited
        };
    }

    private async Task<List<string>> GetSceneTargetIdsAsync(
        IAsyncDocumentSession session,
        string locationId,
        string effectiveCampaign)
    {
        var subLocations = (await QueryLocationsAsync(session, null, null, locationId, 20, effectiveCampaign)).ToList();
        var targetIds = new List<string> { locationId };
        targetIds.AddRange(subLocations.Select(l => l.Id));
        return targetIds;
    }

    private async Task<List<Character>> LoadSceneNpcsFromIndexAsync(IAsyncDocumentSession session,
        IReadOnlyCollection<string> targetIds)
    {
        return await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .ContainsAny("Locations", targetIds)
            .Take(20)
            .ToListAsync();
    }

    private async Task<List<Character>> LoadSceneNpcsFromSimulationAsync(IAsyncDocumentSession session,
        IReadOnlyCollection<string> targetIds)
    {
        return await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .WhereIn("CurrentLocationId", targetIds)
            .Take(20)
            .ToListAsync();
    }

    private async Task<List<Item>> LoadVisibleSceneItemsAsync(
        IAsyncDocumentSession session,
        string locationId,
        string effectiveCampaign)
    {
        var items = await session.Query<Item>().Where(x => x.HolderId == locationId).ToListAsync();
        items = items
            .Where(i => IsVisibleInCampaign(i.CampaignName, effectiveCampaign) && !i.IsArchived)
            .ToList();

        foreach (var item in items)
        {
            JsonSanitizer.Sanitize(item);
        }

        return items;
    }

    private async Task<List<Event>> LoadSceneEventsAsync(
        IAsyncDocumentSession session,
        string locationId,
        string effectiveCampaign,
        int budget)
    {
        // Primary query: use indexed locationId parameter
        var primary = await SelectRecentEventsAsync(session, effectiveCampaign, budget, locationId: locationId);

        // Fallback for legacy events that only recorded location via Involved
        var legacy = (await SelectRecentEventsAsync(session, effectiveCampaign, budget))
            .Where(e => string.IsNullOrEmpty(e.LocationId) && (e.RelatedLocationIds == null || !e.RelatedLocationIds.Any())
                && e.Involved.Contains(locationId))
            .ToList();

        // Merge, dedupe by Id, re-rank by importance then recency, take budget
        return primary.Concat(legacy)
            .DistinctBy(e => e.Id)
            .OrderByDescending(e => e.Importance)
            .ThenByDescending(e => e.Timestamp)
            .Take(budget)
            .ToList();
    }

    /// <summary>
    /// Centralized importance-ranked event retrieval for ambient "story so far" context
    /// (get_world_state, get_scene, get_npc_context). Orders by Importance (Core/Important survive
    /// a flood of recent Trivial bookkeeping events) then recency, unlike QueryEventsAsync's pure-recency
    /// ordering (which remains for on-demand search via recall_history).
    /// </summary>
    public async Task<List<Event>> SelectRecentEventsAsync(
        IAsyncDocumentSession session,
        string? campaignName,
        int budget,
        string? locationId = null,
        string? involvedCharacterId = null)
    {
        var effective = ResolveCampaign(campaignName);
        var q = ApplyEventScalarFilters(session.Query<Event, Event_Search>(), effective, null, locationId, involvedCharacterId);
        var events = await q.OrderByDescending(x => x.Importance).ThenByDescending(x => x.Timestamp).Take(budget).ToListAsync();
        SanitizeEventDetails(events);
        return events;
    }

    private static IRavenQueryable<Event> ApplyEventScalarFilters(
        IRavenQueryable<Event> q,
        string? effective,
        EventCategory? category,
        string? locationId,
        string? involvedCharacterId)
    {
        if (!string.IsNullOrEmpty(effective))
        {
            q = q.Where(x => x.CampaignName == effective);
        }

        if (category.HasValue)
        {
            q = q.Where(x => x.Category == category.Value);
        }

        if (!string.IsNullOrEmpty(locationId))
        {
            q = q.Where(x => x.LocationId == locationId || x.RelatedLocationIds!.Contains(locationId));
        }

        if (!string.IsNullOrEmpty(involvedCharacterId))
        {
            q = q.Where(x => x.Involved.Contains(involvedCharacterId));
        }

        return q;
    }

    private static List<string> GatherSceneNpcIds(
        IEnumerable<Character> npcsFromIndex,
        IEnumerable<Character> npcsFromSimulation)
    {
        return npcsFromIndex
            .Concat(npcsFromSimulation)
            .Select(n => n.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<NpcInitiativeEnrichment> EnrichNpcInitiativeAsync(
        IAsyncDocumentSession session,
        Character npc,
        string? campaignName,
        string surfacedViaTool,
        bool includeTensionBreakdown,
        IReadOnlyList<Character>? presentEntities = null,
        IReadOnlyList<Event>? recentEvents = null)
    {
        var effective = ResolveCampaign(campaignName);
        var config = await GetCampaignConfigAsync(session, effective);
        var campaign = await LoadOrCreateCampaignMetaAsync(session, effective);
        var time = await GetTimeAsync(session, effective);

        Location? location = null;
        if (!string.IsNullOrWhiteSpace(npc.CurrentLocationId))
        {
            location = await session.LoadAsync<Location>(npc.CurrentLocationId);
        }

        var currentDay = (int)time.TotalDaysElapsed;
        var recentCampaignEvents = await InitiativeQueryHelper.QueryRecentCampaignEventsAsync(
            session, effective, currentDay);
        if (recentEvents is { Count: > 0 })
        {
            recentCampaignEvents = recentCampaignEvents
                .Concat(recentEvents)
                .DistinctBy(e => e.Id)
                .ToList();
        }

        var npcItems = await InitiativeQueryHelper.QueryItemsHeldByAsync(session, npc.Id);

        var ctx = new NpcInitiativeContext
        {
            Npc = npc,
            Location = location,
            PresentEntities = presentEntities ?? [npc],
            RecentEvents = recentEvents ?? [],
            NpcRecentEvents = recentCampaignEvents
                .Where(e => e.Involved.Contains(npc.Id))
                .ToList(),
            NpcHeldItems = npcItems,
            Config = config,
            CurrentDay = currentDay,
            SurfacedViaTool = surfacedViaTool,
            IncludeTensionBreakdown = includeTensionBreakdown
        };

        return _initiativeService.Enrich(ctx, campaign);
    }

    private async Task<Campaign> LoadOrCreateCampaignMetaAsync(IAsyncDocumentSession session, string campaignName)
    {
        var metaId = _keys.Meta(campaignName);
        var campaign = await session.LoadAsync<Campaign>(metaId);
        if (campaign != null)
        {
            return campaign;
        }

        campaign = new Campaign
        {
            Id = metaId,
            Name = campaignName,
            DisplayName = campaignName
        };
        await session.StoreAsync(campaign, metaId);
        return campaign;
    }

    // --- Time & Simulator ---

    /// <summary>
    /// Fast-forwards the campaign time by the specified number of days and runs the background world simulation.
    /// Returns the updated time and narrative events generated by the simulation.
    /// </summary>
    public async Task<AdvanceResult> AdvanceWorldAsync(IAsyncDocumentSession session, int days, TimeOfDay timeOfDay,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var time = await GetTimeAsync(session, effective);

        time.TotalDaysElapsed += days;

        // Recompute Year/Month/Day from TotalDaysElapsed using the fixed 360-day (12×30) fantasy calendar.
        // TotalDaysElapsed is the single source of truth (used by simulation rules, rumor expiry, etc.).
        // This eliminates drift and long loops from the previous Day += + while-loop approach.
        var total = time.TotalDaysElapsed;
        time.Year = 1492 + (total / 360);
        time.Month = ((total % 360) / 30) + 1;
        time.Day = (total % 30) + 1;

        time.TimeOfDay = timeOfDay;

        await session.StoreAsync(time);

        // Scoping hardened: entity queries now filter by CampaignName (see code_review.md and plan).
        // For shareables (NPCs/locs) loose filter allows cross-camp if desired; events/rumors strict.
        // Per user feedback: no BC for play data (none exists), don't support global where doesn't make sense (e.g. no global events).
        var activeRumors = await SimulationQueryHelper.QueryActiveRumorsAsync(session, effective, ct: default);
        var npcs = await SimulationQueryHelper.QueryCampaignCharactersAsync(session, effective, ct: default);

        // Phase 7.1: Load active factions and quests so simulation rules can reason about them.
        var activeFactions = await SimulationQueryHelper.QueryCampaignFactionsAsync(session, effective, ct: default);
        var activeQuests = await SimulationQueryHelper.QueryActiveQuestsAsync(session, effective, ct: default);
        var activePlotThreads = await SimulationQueryHelper.QueryActivePlotThreadsAsync(session, effective, ct: default);

        // Build context and run the pluggable simulation engine (rules emit deltas)
        var config = await GetCampaignConfigAsync(session, effective);
        var simContext = new SimulationContext(time, activeRumors, npcs, session, days, effective, activeFactions,
            activeQuests, config, activePlotThreads);

        _logger.LogInformation("Starting world simulation for {Days} days at time {CurrentTime}", days, time);

        var simResult = await _simulationEngine.RunAsync(simContext);

        _logger.LogInformation(
            "Simulation complete. Narratives: {NarrativeCount}, Deltas: {DeltaCount}, PressureItems: {PressureCount}",
            simResult.NarrativeEvents.Count,
            simResult.Deltas.Count,
            simResult.WorldPressure.Count);

        // Persist simulation narrative events (only those marked as Persist: true)
        foreach (var narrative in simResult.Narratives.Where(n => n.Persist))
        {
            await LogEventAsync(session, new()
            {
                Id = "events/" + Guid.NewGuid(),
                Summary = narrative.Text,
                Category = EventCategory.Simulation,
                DayLogged = time.TotalDaysElapsed
            }, effective);
        }

        // Apply any deltas produced by simulation rules through the unified Commit path.
        // This gives us clamping, optimistic concurrency, summary logging, etc. for free.
        if (simResult.Deltas.Count > 0)
        {
            _logger.LogDebug("Applying {DeltaCount} simulation deltas", simResult.Deltas.Count);
            await StageChangesAsync(session, simResult.Deltas.ToArray(), effective);
        }

        // 4d: Cap PressureCooldowns dictionary size (e.g. 500 entries), evicting oldest-surfaced entries beyond the cap
        var campaignDoc = await session.LoadAsync<Campaign>(_keys.Meta(effective));
        if (campaignDoc != null)
        {
            const int maxCooldownEntries = 500;
            if (campaignDoc.PressureCooldowns.Count > maxCooldownEntries)
            {
                var entriesToEvict = campaignDoc.PressureCooldowns.Count - maxCooldownEntries;
                var oldestEntries = campaignDoc.PressureCooldowns
                    .OrderBy(kvp => kvp.Value.LastSurfacedDay)
                    .Take(entriesToEvict)
                    .ToList();

                foreach (var entry in oldestEntries)
                {
                    campaignDoc.PressureCooldowns.Remove(entry.Key);
                }
            }
        }

        // WorldPressure from the engine is surfaced to the caller (AdvanceWorld tool).
        return new()
        {
            NewTime = time,
            SimulatorEvents = simResult.NarrativeEvents.ToList(),
            WorldPressure = simResult.WorldPressure.ToList(),
            EvictedNpcIds = simResult.EvictedNpcIds.ToList(),
            EvictedNpcs = simResult.EvictedNpcSummaries.ToList()
        };
    }

    // --- Search & Recall ---

    private const int UnifiedSearchPerTypeLimit = 3;

    /// <summary>
    /// Performs hybrid keyword + vector search across all semantically-indexed narrative entity types.
    /// Returns a mixed collection of documents matching the search string.
    /// </summary>
    public async Task<IEnumerable<object>> UnifiedSearchAsync(IAsyncDocumentSession session, string query,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var queryVector = await _embeddingService.GenerateEmbeddingAsync(query);

        // Await queries individually. The previous Task-capture + WhenAll + re-await pattern
        // could leave RavenDB session tracking "active async tasks" after the method returned,
        // causing "Disposing session with active async task is forbidden" on ExecuteAsync disposal.
        var chars = await ApplyHybridSearchAsync<Character, Character_Search>(
            session, queryVector, effective,
            q => q.Search(x => x.Name, $"*{query}*"), UnifiedSearchPerTypeLimit);
        var lore = await ApplyHybridSearchAsync<Lore, Lore_Search>(
            session, queryVector, effective,
            q => q.Search(x => x.Title, $"*{query}*"), UnifiedSearchPerTypeLimit);
        var locs = await ApplyHybridSearchAsync<Location, Location_Search>(
            session, queryVector, effective,
            q => q.Search(x => x.Name, $"*{query}*"), UnifiedSearchPerTypeLimit);
        var rumors = await ApplyHybridSearchAsync<Rumor, Rumor_Search>(
            session, queryVector, effective,
            q => q.Search(x => x.Subject, $"*{query}*"), UnifiedSearchPerTypeLimit);
        var factions = await ApplyHybridSearchAsync<Faction, Faction_Search>(
            session, queryVector, effective,
            q => q.Search(x => x.Name, $"*{query}*"), UnifiedSearchPerTypeLimit);
        var quests = await ApplyHybridSearchAsync<Quest, Quest_Search>(
            session, queryVector, effective,
            q => q.Search(x => x.Title, $"*{query}*"), UnifiedSearchPerTypeLimit);
        var events = await ApplyHybridSearchAsync<Event, Event_Search>(
            session, queryVector, effective,
            q => q.Search(x => x.Summary, $"*{query}*"), UnifiedSearchPerTypeLimit);
        var items = await ApplyHybridSearchAsync<Item, Item_Search>(
            session, queryVector, effective,
            q => q.Search(x => x.Name, $"*{query}*"), UnifiedSearchPerTypeLimit);

        foreach (var l in locs)
        {
            SanitizeLocation(l);
        }

        foreach (var item in items)
        {
            SanitizeItem(item);
        }

        foreach (var ev in events)
        {
            if (ev.Details != null)
            {
                ev.Details = SanitizeDetails(ev.Details);
            }
        }

        var results = new List<object>();
        results.AddRange(chars);
        results.AddRange(lore);
        results.AddRange(locs.Where(l => !l.IsArchived));
        results.AddRange(rumors.Where(r => !r.IsArchived));
        results.AddRange(factions.Where(f => !f.IsArchived));
        results.AddRange(quests.Where(q => !q.IsArchived));
        results.AddRange(events);
        results.AddRange(items.Where(i => !i.IsArchived));
        return results;
    }

    private static async Task<List<T>> ApplyHybridSearchAsync<T, TIndex>(
        IAsyncDocumentSession session,
        float[]? queryVector,
        string effective,
        Func<IRavenQueryable<T>, IRavenQueryable<T>> buildTextQuery,
        int limit)
        where T : class, ICampaignScopedEntity
        where TIndex : AbstractIndexCreationTask, new()
    {
        var textQuery = ApplyCampaignScope(buildTextQuery(session.Query<T, TIndex>()), effective);
        var textResults = await textQuery.Take(limit).ToListAsync();

        if (queryVector is not { Length: EmbeddingModelPaths.VectorDimensions })
        {
            return textResults;
        }

        var vectorQuery = ApplyCampaignScope(
            session.Query<T, TIndex>().VectorSearch(
                field => field.WithField(x => x.SemanticVector),
                searchTerm => searchTerm.ByEmbedding(queryVector)),
            effective);
        var vectorResults = await vectorQuery.Take(limit).ToListAsync();
        return MergeSearchResults(textResults, vectorResults, limit);
    }

    private static IRavenQueryable<T> ApplyCampaignScope<T>(IRavenQueryable<T> query, string effective)
        where T : class, ICampaignScopedEntity
    {
        if (string.IsNullOrEmpty(effective))
        {
            return query;
        }

        return query.Where(x =>
            x.CampaignName == string.Empty || x.CampaignName == null || x.CampaignName == effective);
    }

    private static List<T> MergeSearchResults<T>(IReadOnlyList<T> textResults, IReadOnlyList<T> vectorResults, int limit)
        where T : class, ICampaignScopedEntity
    {
        var merged = new List<T>(limit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in textResults.Concat(vectorResults))
        {
            if (!seen.Add(result.Id))
            {
                continue;
            }

            merged.Add(result);
            if (merged.Count >= limit)
            {
                break;
            }
        }

        return merged;
    }

    /// <summary>
    /// Retrieves historical narrative events, optionally filtered by hybrid keyword/semantic search or event category.
    /// </summary>
    public async Task<IEnumerable<Event>> QueryEventsAsync(IAsyncDocumentSession session, string? query,
        EventCategory? category, int limit = 10, string? campaignName = null, string? locationId = null,
        string? involvedCharacterId = null)
    {
        var effective = ResolveCampaign(campaignName);
        List<Event> events;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var queryVector = await _embeddingService.GenerateEmbeddingAsync(query);
            events = await QueryEventsHybridAsync(session, query, queryVector, effective, category, limit,
                locationId, involvedCharacterId);
        }
        else
        {
            var q = ApplyEventScalarFilters(session.Query<Event, Event_Search>(), effective, category, locationId, involvedCharacterId);
            events = await q.OrderByDescending(x => x.Timestamp).Take(limit).ToListAsync();
        }

        SanitizeEventDetails(events);
        return events;
    }

    private static async Task<List<Event>> QueryEventsHybridAsync(
        IAsyncDocumentSession session,
        string query,
        float[]? queryVector,
        string effective,
        EventCategory? category,
        int limit,
        string? locationId = null,
        string? involvedCharacterId = null)
    {
        var fetchLimit = Math.Max(limit * 2, limit);

        IRavenQueryable<Event> ApplyEventFilters(IRavenQueryable<Event> q)
        {
            if (!string.IsNullOrEmpty(effective))
            {
                q = q.Where(x => x.CampaignName == effective);
            }

            if (category.HasValue)
            {
                q = q.Where(x => x.Category == category.Value);
            }

            if (!string.IsNullOrEmpty(locationId))
            {
                q = q.Where(x => x.LocationId == locationId || x.RelatedLocationIds!.Contains(locationId));
            }

            if (!string.IsNullOrEmpty(involvedCharacterId))
            {
                q = q.Where(x => x.Involved.Contains(involvedCharacterId));
            }

            return q;
        }

        var textResults = await ApplyEventFilters(
                session.Query<Event, Event_Search>().Search(x => x.Summary, $"*{query}*"))
            .Take(fetchLimit)
            .ToListAsync();

        List<Event> merged;
        if (queryVector is { Length: EmbeddingModelPaths.VectorDimensions })
        {
            var vectorResults = await ApplyEventFilters(
                    session.Query<Event, Event_Search>().VectorSearch(
                        field => field.WithField(x => x.SemanticVector),
                        searchTerm => searchTerm.ByEmbedding(queryVector)))
                .Take(fetchLimit)
                .ToListAsync();
            // Text matches are always included first (keyword hit = relevant regardless of recency).
            // Vector-only results fill remaining slots, sorted by timestamp.
            var textIds = new HashSet<string>(textResults.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            var vectorOnly = vectorResults
                .Where(e => !textIds.Contains(e.Id))
                .OrderByDescending(e => e.Timestamp)
                .Take(Math.Max(0, limit - textResults.Count))
                .ToList();
            merged = textResults.Concat(vectorOnly).Take(limit).ToList();
        }
        else
        {
            merged = textResults.Take(limit).ToList();
        }

        return merged;
    }

    private void SanitizeEventDetails(IEnumerable<Event> events)
    {
        foreach (var ev in events)
        {
            if (ev.Details != null)
            {
                ev.Details = SanitizeDetails(ev.Details);
            }
        }
    }

    // --- Base Helpers ---

    /// <summary>
    /// Looks up a character by explicit document ID or performs a fuzzy search on the character's name.
    /// </summary>
    public async Task<Character?> GetCharacterAsync(IAsyncDocumentSession session, string identifier,
        string? campaignName = null)
    {
        // campaignName accepted for API consistency / future entity namespacing or filtering.
        // Current implementation uses direct ID or name lookup (entities are caller-ID-controlled).
        var effective = ResolveCampaign(campaignName);
        var character = await session.LoadAsync<Character>(identifier);
        if (character != null)
        {
            return IsVisibleInCampaign(character.CampaignName, effective) ? character : null;
        }

        character = await session.Query<Character>().FirstOrDefaultAsync(x => x.Name == identifier);
        if (character != null && IsVisibleInCampaign(character.CampaignName, effective))
        {
            return character;
        }

        // Corax does not support .Fuzzy(); use wildcard substring as last-resort name match.
        // Misspelled names from LLM tool calls are caught by semantic vector search in UnifiedSearchAsync.
        var fuzzy = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .Search(x => x.Name, "*" + identifier + "*").FirstOrDefaultAsync();
        return fuzzy != null && IsVisibleInCampaign(fuzzy.CampaignName, effective) ? fuzzy : null;
    }

    /// <summary>
    /// Inserts or updates a character in the database, safely mutating tracked entities to preserve concurrency.
    /// Also waits for the Character/Search index to catch up to prevent stale queries.
    /// </summary>
    public async Task<Character> UpsertCharacterAsync(IAsyncDocumentSession session, CharacterUpsertRequest character,
        string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(character.Id))
        {
            throw new ArgumentException("Character.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        var effectiveCampaignName = character.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective;
        }

        if (!CharacterPartyRules.TryValidate(character.IsPc, character.IsPartyCompanion, effectiveCampaignName,
                out var partyError))
        {
            throw new ArgumentException(partyError);
        }

        var existing = await session.LoadAsync<Character>(character.Id);
        Character result;
        if (existing != null)
        {
            // Mutate the already-tracked entity in place. This is the safest pattern
            // with OptimisticConcurrencyMode.Writes + Raven change tracking.
            // Scalars always overwrite; rich sub-objects preserve the existing value when omitted.
            existing.Name = character.Name;
            existing.ClassLevel = character.ClassLevel;
            existing.CurrentHp = character.CurrentHp;
            existing.MaxHp = character.MaxHp;

            existing.Notes = character.Notes;
            existing.Schedule = character.Schedule;
            existing.CurrentLocationId = character.CurrentLocationId;
            existing.CurrentActivity = character.CurrentActivity;
            existing.Psychology = character.Psychology ?? existing.Psychology;
            existing.Social = character.Social ?? existing.Social;
            existing.Needs = character.Needs ?? existing.Needs;
            existing.SystemStats = character.SystemStats ?? existing.SystemStats;
            existing.KeepAlive = character.KeepAlive;
            existing.IsPc = character.IsPc;
            existing.IsPartyCompanion = character.IsPartyCompanion;
            existing.LastUpdated = DateTime.UtcNow;
            existing.CampaignName = effectiveCampaignName;
            result = existing;
        }
        else
        {
            result = new Character
            {
                Id = character.Id,
                Name = character.Name,
                ClassLevel = character.ClassLevel,
                CurrentHp = character.CurrentHp,
                MaxHp = character.MaxHp,
                Notes = character.Notes,
                Schedule = character.Schedule,
                CurrentLocationId = character.CurrentLocationId,
                CurrentActivity = character.CurrentActivity,
                Psychology = character.Psychology ?? new PsychologyProfile(),
                Social = character.Social ?? new SocialProfile(),
                Needs = character.Needs ?? new NeedsProfile(),
                SystemStats = character.SystemStats ?? new SystemExtension(),
                KeepAlive = character.KeepAlive,
                IsPc = character.IsPc,
                IsPartyCompanion = character.IsPartyCompanion,
                LastUpdated = DateTime.UtcNow,
                CampaignName = effectiveCampaignName,
            };
            await session.StoreAsync(result, null, result.Id);
        }

        await EnrichSemanticVectorAsync(result);

        // Help keep the Character/Search index fresh after writes that affect Schedule or CurrentLocation.
        session.Advanced.WaitForIndexesAfterSaveChanges(
            timeout: TimeSpan.FromSeconds(3),
            throwOnTimeout: false,
            indexes: ["Character/Search"]);

        return result;
    }

    /// <summary>
    /// Retrieves the current time for the specified campaign. Returns a new zeroed time object if none exists.
    /// </summary>
    public async Task<CampaignTime> GetTimeAsync(IAsyncDocumentSession session, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var id = _keys.StateTime(effective);
        var time = await session.LoadAsync<CampaignTime>(id);
        if (time == null)
        {
            time = new() { Id = id };
            await session.StoreAsync(time, id);
        }

        return time;
    }

    /// <summary>
    /// Saves the provided time object for the specified campaign, updating its last modified timestamp.
    /// </summary>
    public async Task SaveTimeAsync(IAsyncDocumentSession session, CampaignTime time, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        time.Id = _keys.StateTime(effective);
        time.LastUpdated = DateTime.UtcNow;
        await session.StoreAsync(time);
    }

    /// <summary>
    /// Retrieves the configuration for the specified campaign, initializing a new default config if missing.
    /// </summary>
    public async Task<CampaignConfig> GetCampaignConfigAsync(IAsyncDocumentSession session, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var id = _keys.Config(effective);
        var config = await session.LoadAsync<CampaignConfig>(id);
        if (config == null)
        {
            config = new() { Id = id };
            await session.StoreAsync(config, id);
        }

        return config;
    }

    /// <summary>
    /// Updates the configuration settings (like the active ruleset) for the specified campaign.
    /// </summary>
    public async Task UpsertCampaignConfigAsync(IAsyncDocumentSession session, CampaignConfig config,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var id = _keys.Config(effective);
        config.Id = id;
        await session.StoreAsync(config, id);
    }

    /// <summary>
    /// Returns globally defined need descriptors (populated via the DefineNeedDescriptor tool).
    /// These act as a shared dictionary that individual NPCs can reference or override via Mind.NeedDescriptors.
    /// </summary>
    public async Task<Dictionary<string, string>> GetGlobalNeedDescriptorsAsync(IAsyncDocumentSession session,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var docId = _keys.NeedDescriptors(effective);
        var config = await session.LoadAsync<NeedDescriptorsConfig>(docId);
        var source = config?.Descriptors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new(source, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sets or updates a single need descriptor for a campaign.
    /// </summary>
    public async Task SetNeedDescriptorAsync(IAsyncDocumentSession session, string needName, string descriptor,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var docId = _keys.NeedDescriptors(effective);
        var config = await session.LoadAsync<NeedDescriptorsConfig>(docId) ?? new NeedDescriptorsConfig { Id = docId };
        config.Descriptors[needName.Trim()] = descriptor.Trim();
        await session.StoreAsync(config, docId);
    }

    /// <summary>
    /// Logs a narrative event into the campaign's history, securely sanitizing any complex JSON details.
    /// </summary>
    public async Task LogEventAsync(IAsyncDocumentSession session, Event @event, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(@event.CampaignName))
        {
            @event.CampaignName = effective; // strict for events (campaign-specific per feedback)
        }

        if (@event.Details != null)
        {
            @event.Details = SanitizeDetails(@event.Details);
        }

        await EnrichSemanticVectorAsync(@event);
        await session.StoreAsync(@event);
    }

    // All sanitization logic is now centralized in JsonSanitizer.
    // These methods are thin wrappers for backward compatibility inside the repository
    // and for explicit calls from tools / tests.

    private IDictionary<string, object> SanitizeDetails(IDictionary<string, object> details)
        => (IDictionary<string, object>?)JsonSanitizer.SanitizeDictionary(details) ?? details;

    private Dictionary<string, object> SanitizeDictionary(IDictionary<string, object> source)
        => (Dictionary<string, object>)JsonSanitizer.SanitizeDictionary(source)!;

    private object SanitizeValue(object? value)
        => JsonSanitizer.SanitizeValue(value) ?? value!;

    /// <summary>
    /// Applies JSON sanitization to an Event's Details (prevents JsonElement leakage).
    /// </summary>
    public void SanitizeEvent(Event ev)
    {
        JsonSanitizer.Sanitize(ev);
    }

    /// <summary>
    /// Sanitizes Location.Metadata. Safe to call multiple times.
    /// </summary>
    public void SanitizeLocation(Location? loc)
    {
        JsonSanitizer.Sanitize(loc);
    }

    /// <summary>
    /// Sanitizes Item.Properties. Safe to call multiple times.
    /// </summary>
    public void SanitizeItem(Item? item)
    {
        JsonSanitizer.Sanitize(item);
    }

    /// <summary>
    /// Universal sanitization entry point. Delegates to the central JsonSanitizer.
    /// </summary>
    public void SanitizeEntity(object? entity) => JsonSanitizer.Sanitize(entity);

    /// <summary>
    /// Best-effort deep sanitization of tool response payloads before STJ serialization
    /// in the MCP layer. Delegates to the central JsonSanitizer.
    /// </summary>
    public void SanitizeForToolResponse(object? value) => JsonSanitizer.SanitizeForToolResponse(value);

    /// <summary>
    /// Creates or updates a piece of Lore, handling creation/update timestamps.
    /// </summary>
    public async Task<Lore> UpsertLoreAsync(IAsyncDocumentSession session, LoreUpsertRequest lore, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(lore.Id))
        {
            throw new ArgumentException("Lore.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        var effectiveCampaignName = lore.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective;
        }

        var existing = await session.LoadAsync<Lore>(lore.Id);
        Lore result;
        if (existing != null)
        {
            existing.Title = lore.Title;
            existing.Content = lore.Content;
            existing.Tags = lore.Tags ?? existing.Tags;
            existing.Keywords = lore.Keywords ?? existing.Keywords;
            existing.Category = lore.Category;
            existing.LastUpdated = DateTime.UtcNow;
            existing.CampaignName = effectiveCampaignName;
            result = existing;
        }
        else
        {
            result = new Lore
            {
                Id = lore.Id,
                Title = lore.Title,
                Content = lore.Content,
                Tags = lore.Tags ?? [],
                Keywords = lore.Keywords ?? [],
                Category = lore.Category,
                LastUpdated = DateTime.UtcNow,
                CampaignName = effectiveCampaignName,
            };
            await session.StoreAsync(result);
        }

        await EnrichSemanticVectorAsync(result);
        return result;
    }

    /// <summary>
    /// Searches for Lore entries by fuzzy title/content match, or strictly by tags and category.
    /// </summary>
    public async Task<IEnumerable<Lore>> QueryLoreAsync(IAsyncDocumentSession session, string? query, string[]? tags,
        string? category, int limit = 5, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var q = session.Advanced.AsyncDocumentQuery<Lore, Lore_Search>();
        if (!string.IsNullOrEmpty(query))
        {
            q = q.OpenSubclause().WhereEquals(x => x.Title, query).Fuzzy(0.4m).OrElse()
                .WhereEquals(x => x.Content, query).Fuzzy(0.4m).CloseSubclause();
        }

        if (tags is { Length: > 0 })
        {
            foreach (var tag in tags)
            {
                q = q.AndAlso().ContainsAny(x => x.Tags, [tag]);
            }
        }

        if (!string.IsNullOrEmpty(category))
        {
            q = q.AndAlso().WhereEquals(x => x.Category, category);
        }

        var list = await q.Take(limit).ToListAsync();
        if (!string.IsNullOrEmpty(effective))
        {
            list = list.Where(l => string.IsNullOrEmpty(l.CampaignName) || l.CampaignName == effective)
                .ToList(); // loose for lore (may share)
        }

        return list;
    }

    /// <summary>
    /// Creates or updates a Location, handling sanitization of arbitrary metadata.
    /// </summary>
    public async Task<Location> UpsertLocationAsync(IAsyncDocumentSession session, LocationUpsertRequest location, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(location.Id))
        {
            throw new ArgumentException("Location.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        var effectiveCampaignName = location.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective;
        }

        var existing = await session.LoadAsync<Location>(location.Id);
        var isNew = existing == null;
        Location result;
        if (existing != null)
        {
            // Mutate the tracked entity (safest with optimistic concurrency).
            existing.Name = location.Name;
            existing.Description = location.Description;
            existing.Type = location.Type;
            existing.ParentLocationId = location.ParentLocationId;
            existing.Exits = location.Exits ?? existing.Exits;
            existing.PointsOfInterest = location.PointsOfInterest ?? existing.PointsOfInterest;
            existing.PointOfInterestDetails = location.PointOfInterestDetails != null
                ? new Dictionary<string, string>(location.PointOfInterestDetails, StringComparer.OrdinalIgnoreCase)
                : existing.PointOfInterestDetails;
            existing.AmbientCrowd = location.AmbientCrowd;
            existing.LastVisitedDay = location.LastVisitedDay;
            existing.Metadata = location.Metadata ?? existing.Metadata;
            existing.LastUpdated = DateTime.UtcNow;
            existing.CampaignName = effectiveCampaignName;
            existing.ControllingFactionId = location.ControllingFactionId; // Phase 7.1
            existing.CurrentState = location.CurrentState;
            if (location.DangerModifier.HasValue)
            {
                existing.DangerModifier = Math.Clamp(location.DangerModifier.Value, -50, 50);
            }
            if (location.IsArchived.HasValue)
            {
                existing.IsArchived = location.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            result = new Location
            {
                Id = location.Id,
                Name = location.Name,
                Description = location.Description,
                Type = location.Type,
                ParentLocationId = location.ParentLocationId,
                Exits = location.Exits ?? [],
                PointsOfInterest = location.PointsOfInterest ?? [],
                PointOfInterestDetails = location.PointOfInterestDetails != null
                    ? new Dictionary<string, string>(location.PointOfInterestDetails, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase),
                AmbientCrowd = location.AmbientCrowd,
                LastVisitedDay = location.LastVisitedDay,
                Metadata = location.Metadata ?? [],
                LastUpdated = DateTime.UtcNow,
                CampaignName = effectiveCampaignName,
                ControllingFactionId = location.ControllingFactionId,
                CurrentState = location.CurrentState,
                DangerModifier = Math.Clamp(location.DangerModifier ?? 0, -50, 50),
                IsArchived = location.IsArchived ?? false,
            };
            await session.StoreAsync(result);
        }

        if (isNew && !string.IsNullOrEmpty(location.ConnectedFromLocationId))
        {
            var parentLoc = await session.LoadAsync<Location>(location.ConnectedFromLocationId);
            if (parentLoc != null)
            {
                // Per design: forward exit (on parent) uses the supplied connectionDescription.
                // Reverse exit (on child) uses derived "Leads back toward..." including the connection text.
                var connDesc = location.ConnectionDescription ?? $"Leads back to {parentLoc.Name}";

                parentLoc.Exits ??= [];
                if (!parentLoc.Exits.Any(e => e.TargetLocationId == result.Id))
                {
                    parentLoc.Exits.Add(new LocationExit(result.Id, connDesc));
                }

                result.Exits ??= [];
                if (!result.Exits.Any(e => e.TargetLocationId == parentLoc.Id))
                {
                    var revDesc = $"Leads back toward {parentLoc.Name} ({connDesc})";
                    result.Exits.Add(new LocationExit(parentLoc.Id, revDesc));
                }
            }
        }

        SanitizeLocation(result);
        await EnrichSemanticVectorAsync(result);
        return result;
    }

    /// <summary>
    /// Queries Locations by fuzzy name/description, or strictly by type and parent region.
    /// </summary>
    public async Task<IEnumerable<Location>> QueryLocationsAsync(IAsyncDocumentSession session, string? query,
        LocationType? type = null, string? parentId = null, int limit = 10, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var q = session.Advanced.AsyncDocumentQuery<Location, Location_Search>();
        if (!string.IsNullOrEmpty(query))
        {
            q = q.AndAlso().Search(x => x.Name, $"*{query}*").OrElse().Search(x => x.Description, $"*{query}*");
        }

        if (type.HasValue)
        {
            q = q.AndAlso().WhereEquals(x => x.Type, type.Value);
        }

        if (!string.IsNullOrEmpty(parentId))
        {
            q = q.AndAlso().WhereEquals(x => x.ParentLocationId, parentId);
        }

        var locations = await q.Take(limit).ToListAsync();
        foreach (var l in locations)
        {
            SanitizeLocation(l);
        }

        if (!string.IsNullOrEmpty(effective))
        {
            locations = locations.Where(l => string.IsNullOrEmpty(l.CampaignName) || l.CampaignName == effective)
                .ToList(); // loose for locations (may share)
        }

        return locations;
    }

    /// <summary>
    /// Inserts or updates a Rumor, initializing its creation day to the current campaign time if omitted.
    /// </summary>
    public async Task UpsertRumorAsync(IAsyncDocumentSession session, Rumor rumor, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(rumor.Id))
        {
            throw new ArgumentException("Rumor.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(rumor.CampaignName))
        {
            rumor.CampaignName = effective; // strict for rumors (campaign-specific per feedback)
        }

        rumor.LastUpdated = DateTime.UtcNow;
        if (rumor.DayCreated == 0)
        {
            var t = await GetTimeAsync(session, effective);
            rumor.DayCreated = t.TotalDaysElapsed;
            rumor.LastStateChangeDay = t.TotalDaysElapsed;
        }

        await EnrichSemanticVectorAsync(rumor);

        var existing = await session.LoadAsync<Rumor>(rumor.Id);
        if (existing != null)
        {
            existing.Subject = rumor.Subject;
            existing.CurrentText = rumor.CurrentText;
            existing.State = rumor.State;
            existing.RegionLocationId = rumor.RegionLocationId;
            existing.DayCreated = rumor.DayCreated;
            existing.LastStateChangeDay = rumor.LastStateChangeDay;
            existing.LastUpdated = rumor.LastUpdated;
            existing.CampaignName = rumor.CampaignName;
            existing.SemanticVector = rumor.SemanticVector; // ensure for scoping (strict for rumors)
            existing.EmbeddingTextHash = rumor.EmbeddingTextHash;
        }
        else
        {
            await session.StoreAsync(rumor);
        }
    }

    /// <summary>
    /// Creates or updates a Rumor from a tool-facing request.
    /// </summary>
    public async Task<Rumor> UpsertRumorAsync(IAsyncDocumentSession session, RumorUpsertRequest rumor, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(rumor.Id))
        {
            throw new ArgumentException("Rumor.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        var effectiveCampaignName = rumor.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective; // strict for rumors (campaign-specific)
        }

        var existing = await session.LoadAsync<Rumor>(rumor.Id);
        Rumor result;
        if (existing != null)
        {
            existing.Subject = rumor.Subject;
            existing.CurrentText = rumor.CurrentText;
            existing.State = rumor.State;
            existing.TruthValue = rumor.TruthValue;
            existing.RegionLocationId = rumor.RegionLocationId ?? existing.RegionLocationId;
            existing.LastUpdated = DateTime.UtcNow;
            existing.CampaignName = effectiveCampaignName;
            if (rumor.IsArchived.HasValue)
            {
                existing.IsArchived = rumor.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            var t = await GetTimeAsync(session, effective);
            result = new Rumor
            {
                Id = rumor.Id,
                Subject = rumor.Subject,
                CurrentText = rumor.CurrentText,
                State = rumor.State,
                TruthValue = rumor.TruthValue,
                RegionLocationId = rumor.RegionLocationId!,
                DayCreated = t.TotalDaysElapsed,
                LastStateChangeDay = t.TotalDaysElapsed,
                CampaignName = effectiveCampaignName,
                LastUpdated = DateTime.UtcNow,
                IsArchived = rumor.IsArchived ?? false,
            };
            await session.StoreAsync(result);
        }

        await EnrichSemanticVectorAsync(result);
        return result;
    }

    /// <summary>
    /// Queries active Rumors by fuzzy subject/text, or strictly by region and state.
    /// </summary>
    public async Task<IEnumerable<Rumor>> QueryRumorsAsync(IAsyncDocumentSession session, string? query,
        string? regionId = null, RumorState? state = null, int limit = 5, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var q = session.Advanced.AsyncDocumentQuery<Rumor, Rumor_Search>();
        if (!string.IsNullOrEmpty(query))
        {
            q = q.AndAlso().Search(x => x.Subject, $"*{query}*").OrElse().Search(x => x.CurrentText, $"*{query}*");
        }

        if (!string.IsNullOrEmpty(regionId))
        {
            q = q.AndAlso().WhereEquals(x => x.RegionLocationId, regionId);
        }

        if (state.HasValue)
        {
            q = q.AndAlso().WhereEquals(x => x.State, state.Value);
        }

        var list = await q.Take(limit).ToListAsync();
        if (!string.IsNullOrEmpty(effective))
        {
            // strict for rumors (no legacy global cross-camp)
            list = list.Where(r => r.CampaignName == effective).ToList();
        }

        return list.Where(r => !r.IsArchived).ToList();
    }

    /// <summary>
    /// Retrieves a specific Location by ID, sanitizing any metadata upon read.
    /// </summary>
    public async Task<Location?> GetLocationAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var loc = await session.LoadAsync<Location>(id);
        if (loc != null && !IsVisibleInCampaign(loc.CampaignName, effective))
        {
            return null;
        }

        SanitizeLocation(loc);
        return loc;
    }

    /// <summary>
    /// Retrieves a specific Item by ID.
    /// </summary>
    public async Task<Item?> GetItemAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var item = await session.LoadAsync<Item>(id);
        return item != null && !IsVisibleInCampaign(item.CampaignName, effective) ? null : item;
    }

    /// <summary>
    /// Inserts or updates an Item, sanitizing arbitrary properties and preserving optimistic concurrency on edits.
    /// Rich collection fields (Tags/DistinctiveFeatures/Properties) are preserved when omitted from the request.
    /// </summary>
    public async Task<Item> UpsertItemAsync(IAsyncDocumentSession session, ItemUpsertRequest item, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
        {
            throw new ArgumentException("Item.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        var effectiveCampaignName = item.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective;
        }

        var existing = await session.LoadAsync<Item>(item.Id);
        Item result;
        if (existing != null)
        {
            existing.Name = item.Name;
            existing.Description = item.Description;
            existing.HolderId = item.HolderId;
            existing.Quantity = item.Quantity;
            existing.CurrentState = item.CurrentState;
            existing.DistinctiveFeatures = item.DistinctiveFeatures ?? existing.DistinctiveFeatures;
            existing.CoreCategory = item.CoreCategory;
            existing.Tags = item.Tags ?? existing.Tags;
            existing.Properties = item.Properties ?? existing.Properties;
            existing.LastUpdated = DateTime.UtcNow;
            existing.CampaignName = effectiveCampaignName;
            if (item.IsArchived.HasValue)
            {
                existing.IsArchived = item.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            result = new Item
            {
                Id = item.Id,
                Name = item.Name,
                Description = item.Description,
                HolderId = item.HolderId,
                Quantity = item.Quantity,
                CurrentState = item.CurrentState,
                DistinctiveFeatures = item.DistinctiveFeatures ?? [],
                CoreCategory = item.CoreCategory,
                Tags = item.Tags ?? [],
                Properties = item.Properties ?? [],
                LastUpdated = DateTime.UtcNow,
                CampaignName = effectiveCampaignName,
                IsArchived = item.IsArchived ?? false,
            };
            await session.StoreAsync(result);
        }

        SanitizeItem(result);
        await EnrichSemanticVectorAsync(result);
        return result;
    }

    /// <summary>
    /// Retrieves a specific CustomCreature by ID.
    /// </summary>
    public async Task<CustomCreature?> GetCustomCreatureAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var creature = await session.LoadAsync<CustomCreature>(id);
        return creature != null && !IsVisibleInCampaign(creature.CampaignName, effective) ? null : creature;
    }

    /// <summary>
    /// Inserts or updates a CustomCreature, preserving list fields when omitted from the request.
    /// </summary>
    public async Task<CustomCreature> UpsertCustomCreatureAsync(IAsyncDocumentSession session, CustomCreatureUpsertRequest creature, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(creature.Id))
        {
            throw new ArgumentException("CustomCreature.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        var effectiveCampaignName = creature.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective;
        }

        var existing = await session.LoadAsync<CustomCreature>(creature.Id);
        CustomCreature result;
        if (existing != null)
        {
            existing.Name = creature.Name;
            existing.System = creature.System;
            existing.Description = creature.Description;
            existing.Level = creature.Level;
            existing.ChallengeRating = creature.ChallengeRating;
            existing.Hp = creature.Hp;
            existing.Defense = creature.Defense;
            existing.Skills = creature.Skills ?? existing.Skills;
            existing.Abilities = creature.Abilities ?? existing.Abilities;
            existing.LastUpdated = DateTime.UtcNow;
            existing.CampaignName = effectiveCampaignName;
            if (creature.IsArchived.HasValue)
            {
                existing.IsArchived = creature.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            result = new CustomCreature
            {
                Id = creature.Id,
                Name = creature.Name,
                System = creature.System,
                Description = creature.Description,
                Level = creature.Level,
                ChallengeRating = creature.ChallengeRating,
                Hp = creature.Hp,
                Defense = creature.Defense,
                Skills = creature.Skills ?? [],
                Abilities = creature.Abilities ?? [],
                LastUpdated = DateTime.UtcNow,
                CampaignName = effectiveCampaignName,
                IsArchived = creature.IsArchived ?? false,
            };
            await session.StoreAsync(result);
        }

        await EnrichSemanticVectorAsync(result);
        return result;
    }

    /// <summary>
    /// Retrieves CustomCreatures for a given ruleset system and campaign, with a safety-bounded query.
    /// Applies campaign visibility filter in memory.
    /// </summary>
    public async Task<List<CustomCreature>> GetCustomCreaturesForSystemAsync(IAsyncDocumentSession session, RulesetSystem system, string? campaignName = null, int take = 500)
    {
        var effective = ResolveCampaign(campaignName);
        var creatures = await session.Query<CustomCreature>()
            .Where(c => c.System == system)
            .Take(take)
            .ToListAsync();

        return creatures.Where(c => IsVisibleInCampaign(c.CampaignName, effective)).ToList();
    }

    public async Task<CustomSpell> UpsertCustomSpellAsync(IAsyncDocumentSession session, CustomSpellUpsertRequest spell, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(spell.Id))
        {
            throw new ArgumentException("CustomSpell.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        var effectiveCampaignName = spell.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective;
        }

        var existing = await session.LoadAsync<CustomSpell>(spell.Id);
        CustomSpell result;
        if (existing != null)
        {
            existing.Name = spell.Name;
            existing.System = spell.System;
            existing.Description = spell.Description;
            existing.Level = spell.Level;
            existing.Classes = spell.Classes ?? existing.Classes;
            existing.Concentration = spell.Concentration;
            existing.CastingTime = spell.CastingTime;
            existing.LastUpdated = DateTime.UtcNow;
            existing.CampaignName = effectiveCampaignName;
            if (spell.IsArchived.HasValue)
            {
                existing.IsArchived = spell.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            result = new CustomSpell
            {
                Id = spell.Id,
                Name = spell.Name,
                System = spell.System,
                Description = spell.Description,
                Level = spell.Level,
                Classes = spell.Classes ?? [],
                Concentration = spell.Concentration,
                CastingTime = spell.CastingTime,
                CampaignName = effectiveCampaignName,
                LastUpdated = DateTime.UtcNow,
                IsArchived = spell.IsArchived ?? false,
            };
            await session.StoreAsync(result);
        }

        await EnrichSemanticVectorAsync(result);
        return result;
    }

    public async Task<List<CustomSpell>> GetCustomSpellsForSystemAsync(IAsyncDocumentSession session, RulesetSystem system, string? campaignName = null, int take = 500)
    {
        var effective = ResolveCampaign(campaignName);
        var spells = await session.Query<CustomSpell>()
            .Where(s => s.System == system)
            .Take(take)
            .ToListAsync();

        return spells.Where(s => IsVisibleInCampaign(s.CampaignName, effective)).ToList();
    }

    public async Task<CustomFeat> UpsertCustomFeatAsync(IAsyncDocumentSession session, CustomFeatUpsertRequest feat, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(feat.Id))
        {
            throw new ArgumentException("CustomFeat.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        var effectiveCampaignName = feat.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective;
        }

        var existing = await session.LoadAsync<CustomFeat>(feat.Id);
        CustomFeat result;
        if (existing != null)
        {
            existing.Name = feat.Name;
            existing.System = feat.System;
            existing.Description = feat.Description;
            existing.Prerequisite = feat.Prerequisite;
            existing.MechanicalSummary = feat.MechanicalSummary;
            existing.LastUpdated = DateTime.UtcNow;
            existing.CampaignName = effectiveCampaignName;
            if (feat.IsArchived.HasValue)
            {
                existing.IsArchived = feat.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            result = new CustomFeat
            {
                Id = feat.Id,
                Name = feat.Name,
                System = feat.System,
                Description = feat.Description,
                Prerequisite = feat.Prerequisite,
                MechanicalSummary = feat.MechanicalSummary,
                IsArchived = feat.IsArchived ?? false,
                CampaignName = effectiveCampaignName,
                LastUpdated = DateTime.UtcNow,
            };
            await session.StoreAsync(result);
        }

        await EnrichSemanticVectorAsync(result);
        return result;
    }

    public async Task<List<CustomFeat>> GetCustomFeatsForSystemAsync(IAsyncDocumentSession session, RulesetSystem system, string? campaignName = null, int take = 500)
    {
        var effective = ResolveCampaign(campaignName);
        var feats = await session.Query<CustomFeat>()
            .Where(f => f.System == system)
            .Take(take)
            .ToListAsync();

        return feats.Where(f => IsVisibleInCampaign(f.CampaignName, effective)).ToList();
    }

    public async Task<List<Location>> SuggestLocationsAsync(IAsyncDocumentSession session, string nameQuery,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var rawQuery = nameQuery.Trim();
        var cleanQuery = rawQuery;
        if (cleanQuery.StartsWith("locations/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["locations/".Length..];
        }
        else if (cleanQuery.StartsWith("locs/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["locs/".Length..];
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var canonicalIdPrefix = BuildCanonicalIdPrefix(cleanQuery, "locations/");

        try
        {
            var suggestions = await session.Query<Location, Location_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == effective || x.CampaignName == null)
                .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
                .Take(3).ToListAsync();

            if (suggestions.Count < 3)
            {
                var queryVector = await _embeddingService.GenerateEmbeddingAsync(cleanQuery);
                var byNameQuery = session.Query<Location, Location_Search>()
                    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                    .Where(x => x.CampaignName == effective || x.CampaignName == null)
                    .Search(x => x.Name, cleanQuery + "*");

                if (queryVector is { Length: EmbeddingModelPaths.VectorDimensions })
                {
                    byNameQuery = byNameQuery.VectorSearch(f => f.WithField(x => x.SemanticVector), v => v.ByEmbedding(queryVector));
                }

                var byName = await byNameQuery.Take(3).ToListAsync();

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
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "SuggestLocationsAsync timed out waiting for index; returning empty results.");
            return [];
        }
    }

    public async Task<List<Character>> SuggestCharactersAsync(IAsyncDocumentSession session, string nameQuery,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var rawQuery = nameQuery.Trim();
        var cleanQuery = rawQuery;
        if (cleanQuery.StartsWith("chars/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["chars/".Length..];
        }
        else if (cleanQuery.StartsWith("characters/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["characters/".Length..];
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var canonicalIdPrefix = BuildCanonicalIdPrefix(cleanQuery, "chars/");

        try
        {
            var suggestions = await session.Query<Character, Character_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == effective || x.CampaignName == null)
                .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
                .Take(3).ToListAsync();

            if (suggestions.Count < 3)
            {
                var queryVector = await _embeddingService.GenerateEmbeddingAsync(cleanQuery);
                var byNameQuery = session.Query<Character, Character_Search>()
                    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                    .Where(x => x.CampaignName == effective || x.CampaignName == null)
                    .Search(x => x.Name, cleanQuery + "*");

                if (queryVector is { Length: EmbeddingModelPaths.VectorDimensions })
                {
                    byNameQuery = byNameQuery.VectorSearch(f => f.WithField(x => x.SemanticVector), v => v.ByEmbedding(queryVector));
                }

                var byName = await byNameQuery.Take(3).ToListAsync();

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
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "SuggestCharactersAsync timed out waiting for index; returning empty.");
            return [];
        }
    }

    public async Task<List<Item>> SuggestItemsAsync(IAsyncDocumentSession session, string nameQuery,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var rawQuery = nameQuery.Trim();
        var cleanQuery = rawQuery;
        if (cleanQuery.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["items/".Length..];
        }
        else if (cleanQuery.StartsWith("item/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["item/".Length..];
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var canonicalIdPrefix = BuildCanonicalIdPrefix(cleanQuery, "items/");

        try
        {
            var suggestions = await session.Query<Item, Item_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == effective || x.CampaignName == null)
                .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
                .Take(3).ToListAsync();

            if (suggestions.Count < 3)
            {
                var queryVector = await _embeddingService.GenerateEmbeddingAsync(cleanQuery);
                var byNameQuery = session.Query<Item, Item_Search>()
                    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                    .Where(x => x.CampaignName == effective || x.CampaignName == null)
                    .Search(x => x.Name, cleanQuery + "*");

                if (queryVector is { Length: EmbeddingModelPaths.VectorDimensions })
                {
                    byNameQuery = byNameQuery.VectorSearch(f => f.WithField(x => x.SemanticVector), v => v.ByEmbedding(queryVector));
                }

                var byName = await byNameQuery.Take(3).ToListAsync();

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
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "SuggestItemsAsync timed out waiting for index; returning empty.");
            return [];
        }
    }

    /// <summary>
    /// Suggests Factions by fuzzy name match or ID prefix. Used in error messages and views.
    /// </summary>
    public async Task<List<Faction>> SuggestFactionsAsync(IAsyncDocumentSession session, string nameQuery,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var rawQuery = nameQuery.Trim();
        var cleanQuery = rawQuery;
        if (cleanQuery.StartsWith("factions/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["factions/".Length..];
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var canonicalIdPrefix = BuildCanonicalIdPrefix(cleanQuery, "factions/");

        try
        {
            var suggestions = await session.Query<Faction, Faction_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == effective || x.CampaignName == null)
                .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
                .Take(3).ToListAsync();

            if (suggestions.Count < 3)
            {
                var queryVector = await _embeddingService.GenerateEmbeddingAsync(cleanQuery);
                var byNameQuery = session.Query<Faction, Faction_Search>()
                    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                    .Where(x => x.CampaignName == effective || x.CampaignName == null)
                    .Search(x => x.Name, cleanQuery + "*");

                if (queryVector is { Length: EmbeddingModelPaths.VectorDimensions })
                {
                    byNameQuery = byNameQuery.VectorSearch(f => f.WithField(x => x.SemanticVector), v => v.ByEmbedding(queryVector));
                }

                var byName = await byNameQuery.Take(3).ToListAsync();

                foreach (var f in byName)
                {
                    if (suggestions.All(s => s.Id != f.Id) && suggestions.Count < 3)
                    {
                        suggestions.Add(f);
                    }
                }
            }

            return suggestions;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "SuggestFactionsAsync timed out waiting for index; returning empty.");
            return [];
        }
    }

    /// <summary>
    /// Suggests Quests by fuzzy name match or ID prefix. Used in error messages for get_quest_details and views.
    /// </summary>
    public async Task<List<Quest>> SuggestQuestsAsync(IAsyncDocumentSession session, string nameQuery,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var rawQuery = nameQuery.Trim();
        var cleanQuery = rawQuery;
        if (cleanQuery.StartsWith("quests/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["quests/".Length..];
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var canonicalIdPrefix = BuildCanonicalIdPrefix(cleanQuery, "quests/");

        try
        {
            var suggestions = await session.Query<Quest, Quest_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == effective || x.CampaignName == null)
                .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
                .Take(3).ToListAsync();

            if (suggestions.Count < 3)
            {
                var queryVector = await _embeddingService.GenerateEmbeddingAsync(cleanQuery);
                var byNameQuery = session.Query<Quest, Quest_Search>()
                    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                    .Where(x => x.CampaignName == effective || x.CampaignName == null)
                    .Search(x => x.Title, cleanQuery + "*");

                if (queryVector is { Length: EmbeddingModelPaths.VectorDimensions })
                {
                    byNameQuery = byNameQuery.VectorSearch(f => f.WithField(x => x.SemanticVector), v => v.ByEmbedding(queryVector));
                }

                var byName = await byNameQuery.Take(3).ToListAsync();

                foreach (var q in byName)
                {
                    if (suggestions.All(s => s.Id != q.Id) && suggestions.Count < 3)
                    {
                        suggestions.Add(q);
                    }
                }
            }

            return suggestions;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "SuggestQuestsAsync timed out waiting for index; returning empty.");
            return [];
        }
    }

    public async Task<List<Quest>> GetActiveQuestsAsync(IAsyncDocumentSession session, string? campaignName = null,
        int limit = 20)
    {
        var effective = ResolveCampaign(campaignName);
        var quests = await session.Query<Quest, Quest_Search>()
            .Where(q => q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
            .Take(limit).ToListAsync();
        return quests.Where(q => string.IsNullOrEmpty(q.CampaignName) || q.CampaignName == effective).ToList();
    }

    public async Task<List<Faction>> GetActiveFactionsAsync(IAsyncDocumentSession session, string? campaignName = null,
        int limit = 20)
    {
        var effective = ResolveCampaign(campaignName);
        var factions = await session.Query<Faction, Faction_Search>().Take(limit).ToListAsync();
        return factions.Where(f => string.IsNullOrEmpty(f.CampaignName) || f.CampaignName == effective).ToList();
    }

    /// <summary>
    /// Retrieves a specific Faction by ID.
    /// </summary>
    public async Task<Faction?> GetFactionAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var faction = await session.LoadAsync<Faction>(id);
        return faction != null && !IsVisibleInCampaign(faction.CampaignName, effective) ? null : faction;
    }

    /// <summary>
    /// Creates or updates a Faction document.
    /// </summary>
    public async Task UpsertFactionAsync(IAsyncDocumentSession session, Faction faction, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(faction.Id))
        {
            throw new ArgumentException("Faction.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(faction.CampaignName))
        {
            faction.CampaignName = effective;
        }

        faction.LastUpdated = DateTime.UtcNow;

        await EnrichSemanticVectorAsync(faction);

        var existing = await session.LoadAsync<Faction>(faction.Id);
        if (existing != null)
        {
            existing.Name = faction.Name;
            existing.Description = faction.Description;
            existing.FactionType = faction.FactionType;
            existing.ControllingTerritory = faction.ControllingTerritory;
            existing.TerritoryLocationIds = faction.TerritoryLocationIds ?? [];
            existing.KnownLeaderIds = faction.KnownLeaderIds ?? [];
            existing.InfluenceLevel = faction.InfluenceLevel;
            existing.StanceToward = faction.StanceToward ?? [];
            existing.Metadata = faction.Metadata ?? [];
            existing.LastUpdated = faction.LastUpdated;
            existing.CampaignName = faction.CampaignName;
            existing.SemanticVector = faction.SemanticVector;
            existing.EmbeddingTextHash = faction.EmbeddingTextHash;
        }
        else
        {
            await session.StoreAsync(faction);
        }
    }

    /// <summary>
    /// Creates or updates a Faction from a tool-facing request. List fields are preserved when omitted.
    /// </summary>
    public async Task<Faction> UpsertFactionAsync(IAsyncDocumentSession session, FactionUpsertRequest faction, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(faction.Id))
        {
            throw new ArgumentException("Faction.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        var effectiveCampaignName = faction.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective;
        }

        var existing = await session.LoadAsync<Faction>(faction.Id);
        Faction result;
        if (existing != null)
        {
            existing.Name = faction.Name;
            existing.Description = faction.Description;
            existing.FactionType = faction.FactionType;
            existing.ControllingTerritory = faction.ControllingTerritory;
            existing.TerritoryLocationIds = faction.TerritoryLocationIds ?? existing.TerritoryLocationIds;
            existing.KnownLeaderIds = faction.KnownLeaderIds ?? existing.KnownLeaderIds;
            if (faction.InfluenceLevel.HasValue)
            {
                existing.InfluenceLevel = faction.InfluenceLevel.Value;
            }
            existing.LastUpdated = DateTime.UtcNow;
            existing.CampaignName = effectiveCampaignName;
            if (faction.IsArchived.HasValue)
            {
                existing.IsArchived = faction.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            result = new Faction
            {
                Id = faction.Id,
                Name = faction.Name,
                Description = faction.Description,
                FactionType = faction.FactionType,
                ControllingTerritory = faction.ControllingTerritory,
                TerritoryLocationIds = faction.TerritoryLocationIds ?? [],
                KnownLeaderIds = faction.KnownLeaderIds ?? [],
                InfluenceLevel = faction.InfluenceLevel ?? 50,
                CampaignName = effectiveCampaignName,
                LastUpdated = DateTime.UtcNow,
                IsArchived = faction.IsArchived ?? false,
            };
            await session.StoreAsync(result);
        }

        await EnrichSemanticVectorAsync(result);
        return result;
    }

    /// <summary>
    /// Retrieves a specific Quest by ID.
    /// </summary>
    public async Task<Quest?> GetQuestAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var quest = await session.LoadAsync<Quest>(id);
        return quest != null && !IsVisibleInCampaign(quest.CampaignName, effective) ? null : quest;
    }

    public async Task<PlotThread?> GetPlotThreadAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var thread = await session.LoadAsync<PlotThread>(id);
        return thread != null && !IsVisibleInCampaign(thread.CampaignName, effective) ? null : thread;
    }

    public async Task<List<PlotThread>> GetActivePlotThreadsAsync(IAsyncDocumentSession session, string? campaignName = null)
    {
        return await SimulationQueryHelper.QueryActivePlotThreadsAsync(session, ResolveCampaign(campaignName));
    }

    /// <summary>
    /// Creates or updates a PlotThread, e.g. bulk-seeding clues or bumping TensionLevel. Rich collection
    /// fields (Clues/InvolvedEntityIds/ForeshadowingHooks) are preserved when omitted from the request.
    /// </summary>
    public async Task<PlotThread> UpsertPlotThreadAsync(IAsyncDocumentSession session, PlotThreadUpsertRequest thread, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(thread.Id))
        {
            throw new ArgumentException("PlotThread.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        var effectiveCampaignName = thread.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective;
        }

        var currentDay = (await GetTimeAsync(session, effective)).TotalDaysElapsed;

        var existing = await session.LoadAsync<PlotThread>(thread.Id);
        PlotThread result;
        if (existing != null)
        {
            existing.Title = thread.Title;
            existing.Summary = thread.Summary;
            existing.State = thread.State;
            existing.TensionLevel = thread.TensionLevel;
            existing.Clues = thread.Clues ?? existing.Clues;
            existing.InvolvedEntityIds = thread.InvolvedEntityIds ?? existing.InvolvedEntityIds;
            existing.ResolutionCondition = thread.ResolutionCondition;
            existing.ForeshadowingHooks = thread.ForeshadowingHooks ?? existing.ForeshadowingHooks;
            existing.DmNotes = thread.DmNotes;
            existing.DeadlineDay = thread.DeadlineDay;
            existing.IsPlayerVisible = thread.IsPlayerVisible;
            existing.CampaignName = effectiveCampaignName;
            existing.LastUpdatedDay = currentDay;
            if (thread.IsArchived.HasValue)
            {
                existing.IsArchived = thread.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            result = new PlotThread
            {
                Id = thread.Id,
                Title = thread.Title,
                Summary = thread.Summary,
                State = thread.State,
                TensionLevel = thread.TensionLevel,
                Clues = thread.Clues ?? [],
                InvolvedEntityIds = thread.InvolvedEntityIds ?? [],
                ResolutionCondition = thread.ResolutionCondition,
                ForeshadowingHooks = thread.ForeshadowingHooks ?? [],
                DmNotes = thread.DmNotes,
                DeadlineDay = thread.DeadlineDay,
                IsPlayerVisible = thread.IsPlayerVisible,
                CampaignName = effectiveCampaignName,
                DayCreated = currentDay,
                LastUpdatedDay = currentDay,
                IsArchived = thread.IsArchived ?? false,
            };
            await session.StoreAsync(result);
        }

        await EnrichSemanticVectorAsync(result);
        return result;
    }

    /// <summary>
    /// Creates or updates a Quest document.
    /// </summary>
    public async Task UpsertQuestAsync(IAsyncDocumentSession session, Quest quest, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(quest.Id))
        {
            throw new ArgumentException("Quest.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(quest.CampaignName))
        {
            quest.CampaignName = effective;
        }

        quest.LastUpdated = DateTime.UtcNow;

        await EnrichSemanticVectorAsync(quest);

        var existing = await session.LoadAsync<Quest>(quest.Id);
        if (existing != null)
        {
            existing.Title = quest.Title;
            existing.GiverId = quest.GiverId;
            existing.Objectives = quest.Objectives ?? [];
            existing.OverallState = quest.OverallState;
            existing.Category = quest.Category;
            existing.Urgency = quest.Urgency;
            existing.RelatedLocationIds = quest.RelatedLocationIds ?? [];
            existing.RelatedFactionIds = quest.RelatedFactionIds ?? [];
            existing.DmNotes = quest.DmNotes;
            existing.VisibleToCharacterIds = quest.VisibleToCharacterIds;
            existing.LastUpdatedDay = quest.LastUpdatedDay;
            existing.LastUpdated = quest.LastUpdated;
            existing.CampaignName = quest.CampaignName;
            existing.SemanticVector = quest.SemanticVector;
            existing.EmbeddingTextHash = quest.EmbeddingTextHash;
        }
        else
        {
            await session.StoreAsync(quest);
        }
    }

    /// <summary>
    /// Creates or updates a Quest from a tool-facing request. List fields are preserved when omitted.
    /// </summary>
    public async Task<Quest> UpsertQuestAsync(IAsyncDocumentSession session, QuestUpsertRequest quest, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(quest.Id))
        {
            throw new ArgumentException("Quest.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        var effectiveCampaignName = quest.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective;
        }

        var currentDay = (await GetTimeAsync(session, effective)).TotalDaysElapsed;

        var existing = await session.LoadAsync<Quest>(quest.Id);
        Quest result;
        if (existing != null)
        {
            existing.Title = quest.Title;
            existing.GiverId = quest.GiverId;
            existing.Objectives = quest.Objectives ?? existing.Objectives;
            existing.Category = quest.Category;
            existing.Urgency = quest.Urgency;
            existing.RelatedLocationIds = quest.RelatedLocationIds ?? existing.RelatedLocationIds;
            existing.RelatedFactionIds = quest.RelatedFactionIds ?? existing.RelatedFactionIds;
            existing.DmNotes = quest.DmNotes;
            existing.DeadlineDay = quest.DeadlineDay;
            existing.LastUpdated = DateTime.UtcNow;
            existing.LastUpdatedDay = currentDay;
            existing.CampaignName = effectiveCampaignName;
            if (quest.IsArchived.HasValue)
            {
                existing.IsArchived = quest.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            result = new Quest
            {
                Id = quest.Id,
                Title = quest.Title,
                GiverId = quest.GiverId,
                Objectives = quest.Objectives ?? [],
                Category = quest.Category,
                Urgency = quest.Urgency,
                RelatedLocationIds = quest.RelatedLocationIds ?? [],
                RelatedFactionIds = quest.RelatedFactionIds ?? [],
                DmNotes = quest.DmNotes,
                DeadlineDay = quest.DeadlineDay,
                CampaignName = effectiveCampaignName,
                LastUpdated = DateTime.UtcNow,
                LastUpdatedDay = currentDay,
                IsArchived = quest.IsArchived ?? false,
            };
            await session.StoreAsync(result);
        }

        await EnrichSemanticVectorAsync(result);
        return result;
    }

    /// <summary>
    /// Queries active quests relevant to a specific location (RelatedLocationIds overlap).
    /// Used by GetScene to surface quest summaries.
    /// </summary>
    public async Task<List<Quest>> GetActiveQuestsForLocationAsync(IAsyncDocumentSession session, string locationId,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var quests = await session.Query<Quest, Quest_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(q => q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
            .Take(20).ToListAsync();

        return quests
            .Where(q => IsVisibleInCampaign(q.CampaignName, effective)
                        && !q.IsArchived
                        && q.RelatedLocationIds.Contains(locationId))
            .ToList();
    }

    /// <summary>
    /// Queries active factions that have territory overlapping with a given location ID.
    /// Used by GetScene to surface relevant faction context.
    /// </summary>
    public async Task<List<Faction>> GetFactionsForLocationAsync(IAsyncDocumentSession session, string locationId,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var factions = await session.Query<Faction, Faction_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Take(50).ToListAsync();

        return factions
            .Where(f => IsVisibleInCampaign(f.CampaignName, effective)
                        && !f.IsArchived
                        && (f.ControllingTerritory == locationId || f.TerritoryLocationIds.Contains(locationId)))
            .ToList();
    }

    public static ActiveQuestSummary ToActiveQuestSummary(Quest q)
    {
        var oldestOpen = q.Objectives
            .Where(o => o.State is QuestState.Open or QuestState.InProgress)
            .Select(o => o.DayStarted ?? q.LastUpdatedDay)
            .DefaultIfEmpty(q.LastUpdatedDay)
            .Min();

        return new ActiveQuestSummary(
            q.Id,
            q.Title,
            q.Objectives.Count(o => o.State is QuestState.Open or QuestState.InProgress),
            q.Objectives.Count,
            q.Urgency,
            q.DeadlineDay,
            q.GiverId,
            q.LastUpdatedDay,
            oldestOpen);
    }
}
