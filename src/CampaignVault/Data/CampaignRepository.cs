using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Initiative;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Scenes;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
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
    private readonly ClassDefinitionProvider _classProvider;
    private readonly BackgroundDefinitionProvider _backgroundProvider;
    private readonly IEntitySuggester _entitySuggester;

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
        ILocalEmbeddingService embeddingService,
        ClassDefinitionProvider classProvider,
        BackgroundDefinitionProvider backgroundProvider,
        IEntitySuggester entitySuggester)
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
        _classProvider = classProvider ?? throw new ArgumentNullException(nameof(classProvider));
        _backgroundProvider = backgroundProvider ?? throw new ArgumentNullException(nameof(backgroundProvider));
        _entitySuggester = entitySuggester ?? throw new ArgumentNullException(nameof(entitySuggester));
    }

    private Task EnrichSemanticVectorAsync(IHasSemanticVector entity)
        => SemanticEnrichmentHelper.EnrichAsync(entity, _embeddingService, _logger);

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
    public async Task<CommitResult> StageChangesAsync(CampaignSession campaignSession, WorldChange[]? changes)
    {
        changes ??= [];
        var effective = campaignSession.EffectiveCampaign;
        var session = campaignSession.Session;

        _logger.LogDebug("StageChangesAsync called with {ChangeCount} changes for campaign {Campaign}", changes.Length,
            effective);

        // Snapshot the clock before dispatch so we can detect handlers (RestChangeHandler,
        // TravelChangeHandler) that move CampaignTime.TotalDaysElapsed directly via AdvanceHours.
        // GetTimeAsync returns the same session-tracked instance handlers mutate, so this reads
        // whatever value the dispatch left behind — no extra query needed.
        var time = await GetTimeAsync(campaignSession);
        var daysBefore = time.TotalDaysElapsed;

        var result = await _changeDispatcher.DispatchAsync(
            session,
            changes,
            effective,
            () => GetTimeAsync(campaignSession),
            async () =>
            {
                var camp = await session.LoadAsync<Campaign>(_keys.Meta(effective));
                return camp?.SystemOptions ?? new();
            },
            ev => LogEventAsync(session, ev, effective));

        var elapsedDays = 0;
        if (result.Success)
        {
            elapsedDays = time.TotalDaysElapsed - daysBefore;
            if (elapsedDays > 0)
            {
                // A commit (rest, travel, ...) advanced the calendar past a day boundary — run the
                // same simulation tick AdvanceWorld runs, so needs/decay/staleness can't be outrun by
                // rest-driven time skips. Sim deltas never move the clock themselves, so the recursive
                // StageChangesAsync call below for those deltas will see elapsedDays == 0 and stop here.
                _logger.LogInformation(
                    "Commit advanced the calendar by {ElapsedDays} day(s) for campaign {Campaign}; running simulation tick",
                    elapsedDays, effective);
                var ambientResult = await RunSimulationTickAsync(session, effective, time, elapsedDays);
                if (ambientResult.Deltas.Count > 0)
                {
                    result.AmbientDeltas.AddRange(ambientResult.Deltas);
                    var ambientInvolved = ambientResult.Deltas
                        .SelectMany(_changeDispatcher.ExtractInvolvedEntityIds)
                        .Where(id => !string.IsNullOrEmpty(id));
                    result.InvolvedEntities = result.InvolvedEntities
                        .Concat(ambientInvolved)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                result.AmbientNarrativeSummaries.AddRange(
                    ambientResult.Narratives.Where(n => n.Persist).Select(n => n.Text));
            }
        }

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

                // Time-staleness tracking (feeds TimeStalenessPressureContributor): a commit "records
                // time passage" either by crossing a day boundary or by carrying MinutesElapsed on any
                // non-rest/travel change (mirrors ApplyMicroTimeNudgeAsync's own filter).
                var minutesRecorded = changes
                    .Where(c => c is not RestChange and not TravelChange)
                    .Sum(c => c.MinutesElapsed ?? 0) > 0;
                campaign.CommitsSinceTimeRecorded = elapsedDays > 0 || minutesRecorded
                    ? 0
                    : campaign.CommitsSinceTimeRecorded + 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Fetches the synthesized state of a location, including NPCs present, visible items, local rumors, and recent events.
    /// This is the primary read operation used by the LLM when entering a new scene.
    /// </summary>
    public async Task<SceneView> GetSceneAsync(CampaignSession campaignSession, string locationId,
        bool markVisited = false)
    {
        var effective = campaignSession.EffectiveCampaign;
        var session = campaignSession.Session;
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
        var campaignSession = new CampaignSession(session, effectiveCampaign);
        var regionId = location.ParentLocationId ?? locationId;
        var targetIds = await GetSceneTargetIdsAsync(session, locationId, effectiveCampaign);
        var npcsFromIndex = await LoadSceneNpcsFromIndexAsync(session, targetIds);
        var npcsFromSimulation = await LoadSceneNpcsFromSimulationAsync(session, targetIds);
        var rumors = (await QueryRumorsAsync(session, null, regionId, null, 5, effectiveCampaign)).ToList();
        var items = await LoadVisibleSceneItemsAsync(session, locationId, effectiveCampaign);
        var config = await GetCampaignConfigAsync(campaignSession);
        var events = await LoadSceneEventsAsync(session, locationId, effectiveCampaign, config.EventContextBudgetAmbient);

        foreach (var npc in npcsFromIndex.Concat(npcsFromSimulation))
        {
            await UpgradeSystemStatsIfNeededAsync(session, npc, effectiveCampaign);
        }

        JsonSanitizer.Sanitize(location);

        var time = await GetTimeAsync(campaignSession);
        var globalDescriptors = await GetGlobalNeedDescriptorsAsync(campaignSession);
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

        var containerContents = new List<ContainerContentsSummary>();
        var containerItems = items.Where(i => i.CoreCategory == ItemCategory.Container).ToList();
        foreach (var container in containerItems)
        {
            var contents = await ContainerResolver.GetRecursiveContentsSummariesAsync(session, container.Id, maxDepth: 3);
            if (contents.Count > 0)
            {
                containerContents.Add(new ContainerContentsSummary(
                    container.Id,
                    container.Name,
                    contents,
                    MaxDepth: 3
                ));
            }
        }

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
            MarkVisited = markVisited,
            ContainerContents = containerContents
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
        var npcs = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .ContainsAny("Locations", targetIds)
            .Take(20)
            .ToListAsync();
        return npcs.ToList();
    }

    private async Task<List<Character>> LoadSceneNpcsFromSimulationAsync(IAsyncDocumentSession session,
        IReadOnlyCollection<string> targetIds)
    {
        var npcs = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .WhereIn("CurrentLocationId", targetIds)
            .Take(20)
            .ToListAsync();
        return npcs.ToList();
    }

    /// <summary>
    /// Cheap "who's present at this location" lookup — the same NPC set get_scene would enrich, without
    /// the rest of scene assembly (items, rumors, quests, factions). Used by take_turn's capped
    /// initiative/memory selection so it doesn't need a full GetSceneAsync just to build a candidate pool.
    /// </summary>
    public async Task<List<Character>> GetPresentNpcsAsync(IAsyncDocumentSession session, string locationId, string campaignName)
    {
        var targetIds = await GetSceneTargetIdsAsync(session, locationId, campaignName);
        var npcsFromIndex = await LoadSceneNpcsFromIndexAsync(session, targetIds);
        var npcsFromSimulation = await LoadSceneNpcsFromSimulationAsync(session, targetIds);
        return npcsFromIndex
            .Concat(npcsFromSimulation)
            .DistinctBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Entity-ID extraction used elsewhere for InvolvedEntities tracking, exposed for callers
    /// (e.g. take_turn's initiative candidate pool) that need it outside the dispatch loop.</summary>
    public IEnumerable<string> ExtractInvolvedEntityIds(WorldChange change) =>
        _changeDispatcher.ExtractInvolvedEntityIds(change);

    private async Task<List<Item>> LoadVisibleSceneItemsAsync(
        IAsyncDocumentSession session,
        string locationId,
        string effectiveCampaign)
    {
        var items = await session.Query<Item, Item_Search>()
            .Where(x => x.HolderId == locationId)
            .Customize(x => x.WaitForNonStaleResults())
            .ToListAsync();
        items = items
            .Where(i => IsVisibleInCampaign(i.CampaignName, effectiveCampaign) && !i.IsArchived)
            .ToList();

        foreach (var item in items)
        {
            JsonSanitizer.Sanitize(item);
        }

        return items.ToList();
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
        var events = await q.Customize(x => x.WaitForNonStaleResults()).OrderByDescending(x => x.Importance).ThenByDescending(x => x.Timestamp).Take(budget).ToListAsync();
        SanitizeEventDetails(events);
        return events.ToList();
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
            q = q.Where(x => x.LocationId == locationId || (x.RelatedLocationIds != null && x.RelatedLocationIds.Contains(locationId)));
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
        var config = await GetCampaignConfigAsync(new CampaignSession(session, effective));
        var campaign = await LoadOrCreateCampaignMetaAsync(session, effective);
        var time = await GetTimeAsync(new CampaignSession(session, effective));

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
    public async Task<AdvanceResult> AdvanceWorldAsync(IAsyncDocumentSession session, int days, int? resultingHour,
        string? campaignName = null, int? hours = null)
    {
        var effective = ResolveCampaign(campaignName);
        var time = await GetTimeAsync(new CampaignSession(session, effective));

        var daysBefore = time.TotalDaysElapsed;
        double daysPassedForSim;

        if (hours is > 0)
        {
            // Sub-day/overnight path: let CampaignTime derive the resulting hour from the
            // CURRENT time instead of requiring the caller to do that math (e.g. "rest 8 hours").
            time.AdvanceHours(hours.Value);
            daysPassedForSim = hours.Value / 24.0;
        }
        else
        {
            // Rolls Day/Month/Year forward from CampaignTime's own current values (seeded from the
            // campaign's LoreSettings at creation), not a hardcoded epoch — see CampaignTime.AdvanceDays.
            time.AdvanceDays(days);

            if (resultingHour.HasValue)
            {
                time.Hour = resultingHour.Value;
            }

            daysPassedForSim = days;
        }

        var daysDelta = time.TotalDaysElapsed - daysBefore;

        await session.StoreAsync(time);

        var simResult = await RunSimulationTickAsync(session, effective, time, daysPassedForSim);

        // 4d: Cap PressureCooldowns dictionary size (e.g. 500 entries), evicting oldest-surfaced entries beyond the cap
        var campaignDoc = await session.LoadAsync<Campaign>(_keys.Meta(effective));
        if (campaignDoc != null)
        {
            // Explicit day-skip always counts as "time recorded", even at days=0 (an explicit sweep).
            campaignDoc.CommitsSinceTimeRecorded = 0;

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
            EvictedNpcs = simResult.EvictedNpcSummaries.ToList(),
            HoursAdvanced = hours,
            DaysAdvanced = daysDelta
        };
    }

    /// <summary>
    /// Runs the pluggable simulation engine (needs, decay, staleness, faction/plot evolution, ...) for
    /// <paramref name="daysPassed"/> days at the given <paramref name="time"/>, persists its narratives,
    /// and applies its deltas through the unified commit path.
    ///
    /// Shared by <see cref="AdvanceWorldAsync"/> (the explicit day-skip tool) and
    /// <see cref="StageChangesAsync"/> (which calls this whenever a handler — e.g. RestChangeHandler,
    /// TravelChangeHandler — advances CampaignTime.TotalDaysElapsed directly), so a day passing has the
    /// same simulation consequences regardless of which tool moved the clock.
    /// </summary>
    private async Task<SimulationResult> RunSimulationTickAsync(
        IAsyncDocumentSession session, string effective, CampaignTime time, double daysPassed)
    {
        // Scoping hardened: entity queries now filter by CampaignName (see code_review.md and plan).
        // For shareables (NPCs/locs) loose filter allows cross-camp if desired; events/rumors strict.
        // Per user feedback: no BC for play data (none exists), don't support global where doesn't make sense (e.g. no global events).
        var activeRumors = await SimulationQueryHelper.QueryActiveRumorsAsync(session, effective, ct: CancellationToken.None);
        var npcs = await SimulationQueryHelper.QueryCampaignCharactersAsync(session, effective, ct: CancellationToken.None);

        // Phase 7.1: Load active factions and quests so simulation rules can reason about them.
        var activeFactions = await SimulationQueryHelper.QueryCampaignFactionsAsync(session, effective, ct: CancellationToken.None);
        var activeQuests = await SimulationQueryHelper.QueryActiveQuestsAsync(session, effective, ct: CancellationToken.None);
        var activePlotThreads = await SimulationQueryHelper.QueryActivePlotThreadsAsync(session, effective, ct: CancellationToken.None);
        var activeWorldEvents = await SimulationQueryHelper.QueryPendingWorldEventsAsync(session, effective, ct: CancellationToken.None);

        // Build context and run the pluggable simulation engine (rules emit deltas)
        var config = await GetCampaignConfigAsync(new CampaignSession(session, effective));
        var simContext = new SimulationContext(time, activeRumors, npcs, session, daysPassed, effective, activeFactions,
            activeQuests, config, activePlotThreads, activeWorldEvents);

        _logger.LogInformation("Starting world simulation for {Days} days at time {CurrentTime}", daysPassed, time);

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
            await StageChangesAsync(new CampaignSession(session, effective), simResult.Deltas.ToArray());
        }

        return simResult;
    }

    // --- Search & Recall ---

    private const int UnifiedSearchPerTypeLimit = 3;

    // MiniLM-L6-v2 cosine similarities for genuinely related text typically land around 0.5+;
    // unrelated text is usually well below that. Tune if searches feel too loose/strict.
    private const float VectorSearchMinimumSimilarity = 0.5f;

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
            JsonSanitizer.Sanitize(l);
        }

        foreach (var item in items)
        {
            JsonSanitizer.Sanitize(item);
        }

        foreach (var ev in events)
        {
            if (ev.Details != null)
            {
                ev.Details = SanitizeDetails(ev.Details);
            }
        }

        var results = new List<object>();
        results.AddRange(chars.Select(c => new SearchMatch("character", CharacterSearchSummary.From(c))));
        results.AddRange(lore.Select(l => new SearchMatch("lore", LoreSearchSummary.From(l))));
        results.AddRange(locs.Where(l => !l.IsArchived).Select(l => new SearchMatch("location", LocationSearchSummary.From(l))));
        results.AddRange(rumors.Where(r => !r.IsArchived).Select(r => new SearchMatch("rumor", RumorSearchSummary.From(r))));
        results.AddRange(factions.Where(f => !f.IsArchived).Select(f => new SearchMatch("faction", FactionSearchSummary.From(f))));
        results.AddRange(quests.Where(q => !q.IsArchived).Select(q => new SearchMatch("quest", QuestSearchSummary.From(q))));
        results.AddRange(events.Select(e => new SearchMatch("event", EventSummaryView.From(e))));
        results.AddRange(items.Where(i => !i.IsArchived).Select(i => new SearchMatch("item", ItemSummaryView.From(i))));
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
                searchTerm => searchTerm.ByEmbedding(queryVector),
                minimumSimilarity: VectorSearchMinimumSimilarity),
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
    public async Task<IEnumerable<Event>> QueryEventsAsync(CampaignSession campaignSession, string? query,
        EventCategory? category, int limit = 10, string? locationId = null,
        string? involvedCharacterId = null)
    {
        var effective = campaignSession.EffectiveCampaign;
        var session = campaignSession.Session;
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
                q = q.Where(x => x.LocationId == locationId || (x.RelatedLocationIds != null && x.RelatedLocationIds.Contains(locationId)));
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
    /// Migrates legacy SystemExtension to the appropriate derived type (Dnd5eExtension/Pf2eExtension) based
    /// on campaign's active ruleset, ensuring access to ruleset-specific properties like SkillModifiers.
    /// </summary>
    public async Task<Character?> GetCharacterAsync(CampaignSession campaignSession, string identifier)
    {
        var effective = campaignSession.EffectiveCampaign;
        var character = await campaignSession.Session.LoadAsync<Character>(identifier);
        if (character != null)
        {
            if (!IsVisibleInCampaign(character.CampaignName, effective))
                return null;
            await UpgradeSystemStatsIfNeededAsync(campaignSession.Session, character, effective);
            return character;
        }

        character = await campaignSession.Session.Query<Character>().FirstOrDefaultAsync(x => x.Name == identifier);
        if (character != null && IsVisibleInCampaign(character.CampaignName, effective))
        {
            await UpgradeSystemStatsIfNeededAsync(campaignSession.Session, character, effective);
            return character;
        }

        var fuzzy = await campaignSession.Session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .Search(x => x.Name, "*" + identifier + "*").FirstOrDefaultAsync();
        if (fuzzy != null && IsVisibleInCampaign(fuzzy.CampaignName, effective))
        {
            await UpgradeSystemStatsIfNeededAsync(campaignSession.Session, fuzzy, effective);
            return fuzzy;
        }

        return null;
    }

    internal async Task UpgradeSystemStatsIfNeededAsync(IAsyncDocumentSession session, Character character, string campaignName)
    {
        if (character.SystemStats == null)
        {
            return;
        }

        var config = await session.LoadAsync<CampaignConfig>(_keys.Config(campaignName));
        var activeSystem = config?.ActiveSystem ?? RulesetSystem.Dnd5e;

        await SystemStatsUpgradeHelper.UpgradeSystemStatsIfNeededAsync(
            session, character, activeSystem, _classProvider, _backgroundProvider, _keys, campaignName);
    }

    /// <summary>
    /// Inserts or updates a character in the database, safely mutating tracked entities to preserve concurrency.
    /// Also waits for the Character/Search index to catch up to prevent stale queries.
    /// </summary>
    public async Task<Character> UpsertCharacterAsync(CampaignSession campaignSession, CharacterUpsertRequest character)
    {
        if (string.IsNullOrWhiteSpace(character.Id))
        {
            throw new ArgumentException("Character.Id is required for upsert.");
        }

        character.Id = CanonicalId.Normalize(character.Id, CanonicalId.Characters);

        var effective = campaignSession.EffectiveCampaign;
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

        var existing = await campaignSession.Session.LoadAsync<Character>(character.Id);
        Character result;
        if (existing != null)
        {
            existing.Name = character.Name;
            existing.ClassLevel = character.ClassLevel;
            existing.CurrentHp = character.CurrentHp;
            existing.MaxHp = character.MaxHp;

            existing.Notes = character.Notes;
            existing.CurrentAppearance = character.CurrentAppearance ?? existing.CurrentAppearance;
            existing.VisualTags = character.VisualTags ?? existing.VisualTags;
            existing.DistinctiveFeatures = character.DistinctiveFeatures ?? existing.DistinctiveFeatures;
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
                CurrentAppearance = character.CurrentAppearance,
                VisualTags = character.VisualTags ?? [],
                DistinctiveFeatures = character.DistinctiveFeatures ?? [],
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
            await campaignSession.Session.StoreAsync(result, null, result.Id);
        }

        await EnrichSemanticVectorAsync(result);

        campaignSession.Session.Advanced.WaitForIndexesAfterSaveChanges(
            timeout: TimeSpan.FromSeconds(3),
            throwOnTimeout: false,
            indexes: ["Character/Search"]);

        return result;
    }

    /// <summary>
    /// Retrieves the current time for the specified campaign. Returns a new time object initialized with
    /// campaign lore settings if none exists yet.
    /// </summary>
    public async Task<CampaignTime> GetTimeAsync(CampaignSession campaignSession)
    {
        var effective = campaignSession.EffectiveCampaign;
        var id = _keys.StateTime(effective);
        var time = await campaignSession.Session.LoadAsync<CampaignTime>(id);
        if (time == null)
        {
            var campaignId = _keys.Meta(effective);
            var campaign = await campaignSession.Session.LoadAsync<Campaign>(campaignId);
            var lore = campaign?.LoreSettings ?? new();

            time = new()
            {
                Id = id,
                Epoch = lore.Epoch,
                Year = lore.Year,
                Month = lore.Month,
                Day = lore.Day,
                Hour = lore.StartingHour
            };
            await campaignSession.Session.StoreAsync(time, id);
        }

        return time;
    }

    public async Task SaveTimeAsync(CampaignSession campaignSession, CampaignTime time)
    {
        var effective = campaignSession.EffectiveCampaign;
        time.Id = _keys.StateTime(effective);
        time.LastUpdated = DateTime.UtcNow;
        await campaignSession.Session.StoreAsync(time);
    }

    public async Task<CampaignConfig> GetCampaignConfigAsync(CampaignSession campaignSession)
    {
        var effective = campaignSession.EffectiveCampaign;
        var id = _keys.Config(effective);
        var config = await campaignSession.Session.LoadAsync<CampaignConfig>(id);
        if (config == null)
        {
            config = new() { Id = id };
            await campaignSession.Session.StoreAsync(config, id);
        }

        return config;
    }

    public async Task<SessionLog?> GetSessionLogAsync(CampaignSession campaignSession)
    {
        var effective = campaignSession.EffectiveCampaign;
        var id = $"{effective}/state/sessions";
        var sessionLog = await campaignSession.Session.LoadAsync<SessionLog>(id);
        return sessionLog;
    }

    public async Task<CombatEncounter?> GetActiveCombatAsync(CampaignSession campaignSession)
    {
        var effective = campaignSession.EffectiveCampaign;
        var id = _keys.CombatCurrent(effective);
        var encounter = await campaignSession.Session.LoadAsync<CombatEncounter>(id);
        return encounter?.IsActive == true ? encounter : null;
    }

    /// <summary>
    /// Loads the campaign's take_turn reseed cursor, or null if take_turn has never been called for
    /// this campaign (the caller should treat that as "first-ever call = Full" rather than auto-creating
    /// here, since the absence of the document is itself meaningful).
    /// </summary>
    public async Task<TurnCursor?> GetTurnCursorAsync(CampaignSession campaignSession)
    {
        var effective = campaignSession.EffectiveCampaign;
        var id = _keys.StateTurnCursor(effective);
        return await campaignSession.Session.LoadAsync<TurnCursor>(id);
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
    public async Task<Dictionary<string, string>> GetGlobalNeedDescriptorsAsync(CampaignSession campaignSession)
    {
        var effective = campaignSession.EffectiveCampaign;
        var session = campaignSession.Session;
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

        // Set SessionId to the currently open session (if one exists)
        if (string.IsNullOrEmpty(@event.SessionId))
        {
            var sessionLog = await GetSessionLogAsync(new CampaignSession(session, effective));
            var openSession = sessionLog?.Sessions.FirstOrDefault(s => s.IsOpen);
            if (openSession != null)
            {
                @event.SessionId = openSession.Number.ToString();
            }
        }

        // Extract locationId from involved list if not explicitly set
        if (string.IsNullOrEmpty(@event.LocationId) && @event.Involved != null && @event.Involved.Count > 0)
        {
            var locationId = @event.Involved.FirstOrDefault(id => !string.IsNullOrEmpty(id) && id.StartsWith("locations/", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(locationId))
            {
                @event.LocationId = locationId;
            }
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
    /// <summary>
    /// Creates or updates a piece of Lore, handling creation/update timestamps.
    /// </summary>
    public async Task<Lore> UpsertLoreAsync(CampaignSession campaignSession, LoreUpsertRequest lore)
    {
        var effective = campaignSession.EffectiveCampaign;
        var session = campaignSession.Session;

        if (string.IsNullOrWhiteSpace(lore.Id))
        {
            throw new ArgumentException("Lore.Id is required for upsert.");
        }

        lore.Id = CanonicalId.Normalize(lore.Id, CanonicalId.Lore);
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
    public async Task<Location> UpsertLocationAsync(CampaignSession campaignSession, LocationUpsertRequest location)
    {
        var effective = campaignSession.EffectiveCampaign;
        var session = campaignSession.Session;

        if (string.IsNullOrWhiteSpace(location.Id))
        {
            throw new ArgumentException("Location.Id is required for upsert.");
        }

        location.Id = CanonicalId.Normalize(location.Id, CanonicalId.Locations);
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
            existing.ClimateZone = location.ClimateZone ?? existing.ClimateZone;
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
                ClimateZone = location.ClimateZone,
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

        JsonSanitizer.Sanitize(result);
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
            JsonSanitizer.Sanitize(l);
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

        rumor.Id = CanonicalId.Normalize(rumor.Id, CanonicalId.Rumors);

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(rumor.CampaignName))
        {
            rumor.CampaignName = effective; // strict for rumors (campaign-specific per feedback)
        }

        rumor.LastUpdated = DateTime.UtcNow;
        if (rumor.DayCreated == 0)
        {
            var t = await GetTimeAsync(new CampaignSession(session, effective));
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

        rumor.Id = CanonicalId.Normalize(rumor.Id, CanonicalId.Rumors);

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
            var t = await GetTimeAsync(new CampaignSession(session, effective));
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
        var q = session.Advanced.AsyncDocumentQuery<Rumor, Rumor_Search>()
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5));
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

        JsonSanitizer.Sanitize(loc);
        return loc;
    }

    /// <summary>
    /// Retrieves a specific Item by ID.
    /// </summary>
    public async Task<Item?> GetItemAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var item = await session.LoadAsync<Item>(id);
        if (item != null && !IsVisibleInCampaign(item.CampaignName, effective))
            return null;
        return item;
    }

    /// <summary>
    /// Inserts or updates an Item, sanitizing arbitrary properties and preserving optimistic concurrency on edits.
    /// Rich collection fields (Tags/DistinctiveFeatures/Properties) are preserved when omitted from the request.
    /// </summary>
    public async Task<Item> UpsertItemAsync(CampaignSession campaignSession, ItemUpsertRequest item)
    {
        var effective = campaignSession.EffectiveCampaign;
        var session = campaignSession.Session;

        if (string.IsNullOrWhiteSpace(item.Id))
        {
            throw new ArgumentException("Item.Id is required for upsert.");
        }

        item.Id = CanonicalId.Normalize(item.Id, CanonicalId.Items);
        item.HolderId = CanonicalId.NormalizeAlias(item.HolderId);
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
            existing.EquipZones = item.EquipZones ?? existing.EquipZones;
            existing.EquipLayer = item.EquipLayer ?? existing.EquipLayer;
            if (item.TwoHanded.HasValue) existing.TwoHanded = item.TwoHanded.Value;
            if (item.IsEquipped.HasValue) existing.IsEquipped = item.IsEquipped.Value;
            existing.Capacity = item.Capacity ?? existing.Capacity;
            existing.CapacityUnit = item.CapacityUnit ?? existing.CapacityUnit;
            existing.MaxCharges = item.MaxCharges ?? existing.MaxCharges;
            existing.ChargeUnit = item.ChargeUnit ?? existing.ChargeUnit;
            existing.StackGroup = item.StackGroup ?? existing.StackGroup;
            existing.RequiresEquippedTags = item.RequiresEquippedTags ?? existing.RequiresEquippedTags;
            existing.IncompatibleWithEquippedTags = item.IncompatibleWithEquippedTags ?? existing.IncompatibleWithEquippedTags;
            existing.VisualTags = item.VisualTags ?? existing.VisualTags;
            existing.AppearanceNote = item.AppearanceNote ?? existing.AppearanceNote;
            result = existing;
        }
        else
        {
            var currentDay = (await GetTimeAsync(new CampaignSession(session, effectiveCampaignName))).TotalDaysElapsed;
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
                EquipZones = item.EquipZones ?? [],
                EquipLayer = item.EquipLayer,
                TwoHanded = item.TwoHanded ?? false,
                IsEquipped = item.IsEquipped ?? false,
                Capacity = item.Capacity,
                CapacityUnit = item.CapacityUnit,
                MaxCharges = item.MaxCharges,
                ChargeUnit = item.ChargeUnit,
                StackGroup = item.StackGroup,
                RequiresEquippedTags = item.RequiresEquippedTags,
                IncompatibleWithEquippedTags = item.IncompatibleWithEquippedTags,
                VisualTags = item.VisualTags,
                AppearanceNote = item.AppearanceNote,
                // id/participants from the request are intentionally dropped here: a freshly
                // created item has no existing details to match by id and no in-fiction moment
                // to push a participant memory for (see ItemDetailUpsertRequest doc comment).
                ItemDetails = (item.ItemDetails ?? []).Select(d => new ItemDetail
                {
                    Id = "detail-" + Guid.NewGuid(),
                    Name = d.Name,
                    Description = d.Description,
                    Status = d.Status,
                    Intent = d.Intent,
                    Origin = d.Origin,
                    TetheredToId = string.IsNullOrEmpty(d.TetheredToId) ? null : d.TetheredToId,
                    Participants = [],
                    CreatedOnDay = currentDay,
                    UpdatedOnDay = currentDay,
                    ReviewIntervalDays = d.ReviewIntervalDays,
                }).ToList(),
            };
            await session.StoreAsync(result);
        }
        // Note: item.ItemDetails is intentionally NOT applied in the existing-item branch above —
        // creating details at upsert_item/world_build time is creation-only. Use commit's
        // item_update/upsertItemDetail for incremental changes to an existing item's details.

        // Validate equip conflicts if being equipped on a character
        if (result.IsEquipped && result.HolderId?.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (result.EquipZones.Count > 0 && result.EquipLayer != null)
            {
                // Query equipped items for conflict detection
                var equipped = await session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
                    .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
                    .WhereEquals(x => x.HolderId, result.HolderId)
                    .Take(50)
                    .ToListAsync();

                var equippedList = equipped.Where(i => i.IsEquipped && !i.Id.Equals(result.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                var conflictResult = EquipSlotRules.FindConflicts(result, equippedList);

                if (conflictResult.HasConflicts)
                {
                    var conflictNames = string.Join(", ", conflictResult.Items.Select(c => $"{c.Name} ({c.Id})"));
                    throw new ArgumentException(
                        $"Cannot equip '{result.Name}': conflicts with {conflictNames}. " +
                        "Use the item_equip commit with replaceConflicts:true to auto-unequip conflicts.");
                }
            }
        }

        JsonSanitizer.Sanitize(result);
        foreach (var detail in result.ItemDetails)
        {
            await EnrichSemanticVectorAsync(detail);
        }
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

        creature.Id = CanonicalId.Normalize(creature.Id, CanonicalId.Creatures);

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
    /// Campaign visibility and archive filters are applied server-side, before the take-limit.
    /// </summary>
    public async Task<List<CustomCreature>> GetCustomCreaturesForSystemAsync(IAsyncDocumentSession session, string system, string? campaignName = null, int take = 500)
    {
        var effective = ResolveCampaign(campaignName);
        var creatures = await session.Query<CustomCreature>()
            .Where(c => c.System == system
                        && !c.IsArchived
                        && (string.IsNullOrEmpty(c.CampaignName) || c.CampaignName == effective))
            .Take(take)
            .ToListAsync();

        return creatures;
    }

    public async Task<CustomSpell> UpsertCustomSpellAsync(IAsyncDocumentSession session, CustomSpellUpsertRequest spell, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(spell.Id))
        {
            throw new ArgumentException("CustomSpell.Id is required for upsert.");
        }

        spell.Id = CanonicalId.Normalize(spell.Id, CanonicalId.Spells);

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

    public async Task<List<CustomSpell>> GetCustomSpellsForSystemAsync(IAsyncDocumentSession session, string system, string? campaignName = null, int take = 500)
    {
        var effective = ResolveCampaign(campaignName);
        var spells = await session.Query<CustomSpell, CustomSpell_Search>()
            .Where(s => s.System == system)
            .Customize(x => x.WaitForNonStaleResults())
            .Take(take)
            .ToListAsync();

        var result = spells.Where(s => IsVisibleInCampaign(s.CampaignName, effective) && !s.IsArchived).ToList();
        return result;
    }

    public async Task<CustomFeat> UpsertCustomFeatAsync(IAsyncDocumentSession session, CustomFeatUpsertRequest feat, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(feat.Id))
        {
            throw new ArgumentException("CustomFeat.Id is required for upsert.");
        }

        feat.Id = CanonicalId.Normalize(feat.Id, CanonicalId.Feats);

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

    public async Task<List<CustomFeat>> GetCustomFeatsForSystemAsync(IAsyncDocumentSession session, string system, string? campaignName = null, int take = 500)
    {
        var effective = ResolveCampaign(campaignName);
        var feats = await session.Query<CustomFeat, CustomFeat_Search>()
            .Where(f => f.System == system)
            .Customize(x => x.WaitForNonStaleResults())
            .Take(take)
            .ToListAsync();

        var result = feats.Where(f => IsVisibleInCampaign(f.CampaignName, effective) && !f.IsArchived).ToList();
        return result;
    }

    public async Task<List<Location>> SuggestLocationsAsync(IAsyncDocumentSession session, string nameQuery,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        return await _entitySuggester.SuggestLocationsAsync(session, nameQuery, effective);
    }

    public async Task<List<Character>> SuggestCharactersAsync(IAsyncDocumentSession session, string nameQuery,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        return await _entitySuggester.SuggestCharactersAsync(session, nameQuery, effective);
    }

    public async Task<List<Item>> SuggestItemsAsync(IAsyncDocumentSession session, string nameQuery,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        return await _entitySuggester.SuggestItemsAsync(session, nameQuery, effective);
    }

    /// <summary>
    /// Suggests Factions by fuzzy name match or ID prefix. Used in error messages and views.
    /// </summary>
    public async Task<List<Faction>> SuggestFactionsAsync(IAsyncDocumentSession session, string nameQuery,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        return await _entitySuggester.SuggestFactionsAsync(session, nameQuery, effective);
    }

    /// <summary>
    /// Suggests Quests by fuzzy name match or ID prefix. Used in error messages for get_quest_details and views.
    /// </summary>
    public async Task<List<Quest>> SuggestQuestsAsync(IAsyncDocumentSession session, string nameQuery,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        return await _entitySuggester.SuggestQuestsAsync(session, nameQuery, effective);
    }

    public async Task<List<Quest>> GetActiveQuestsAsync(IAsyncDocumentSession session, string? campaignName = null,
        int limit = 20)
    {
        var effective = ResolveCampaign(campaignName);
        var quests = await session.Query<Quest, Quest_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(q => (q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
                        && (q.CampaignName == effective || q.CampaignName == null || q.CampaignName == ""))
            .Take(limit).ToListAsync();
        return quests;
    }

    public async Task<List<Faction>> GetActiveFactionsAsync(IAsyncDocumentSession session, string? campaignName = null,
        int limit = 20)
    {
        var effective = ResolveCampaign(campaignName);
        var factions = await session.Query<Faction, Faction_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(f => f.CampaignName == effective || f.CampaignName == null || f.CampaignName == "")
            .Take(limit).ToListAsync();
        return factions;
    }

    /// <summary>
    /// Lightweight session-0 signal for start_session: counts + a short gap list. Uses server-side
    /// count via query Statistics with Take(0) — no entity IDs or documents are materialized, so the
    /// cost stays flat regardless of campaign size.
    /// </summary>
    internal async Task<SeedCoverageSummary> BuildSeedCoverageAsync(
        IAsyncDocumentSession session, string effective, string? partyLocationId)
    {
        static async Task<int> CountAsync<T>(IRavenQueryable<T> query)
        {
            await query
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Statistics(out QueryStatistics stats)
                .Take(0)
                .ToListAsync();
            return (int)stats.TotalResults;
        }

        var locationCount = await CountAsync(session.Query<Location>()
            .Where(l => (l.CampaignName == effective || l.CampaignName == null) && l.IsArchived == false));
        var pcCharacterCount = await CountAsync(session.Query<Character>()
            .Where(c => (c.CampaignName == effective || c.CampaignName == null) && c.IsPc == true));
        var factionCount = await CountAsync(session.Query<Faction>()
            .Where(f => (f.CampaignName == effective || f.CampaignName == null) && f.IsArchived == false));
        var openQuestCount = await CountAsync(session.Query<Quest>()
            .Where(q => (q.CampaignName == effective || q.CampaignName == null)
                        && (q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)));
        var activePlotThreadCount = await CountAsync(session.Query<PlotThread>()
            .Where(p => (p.CampaignName == effective || p.CampaignName == null)
                        && (p.State == PlotThreadState.Active || p.State == PlotThreadState.Escalating || p.State == PlotThreadState.Climax)));

        Location? partyLocation = string.IsNullOrEmpty(partyLocationId)
            ? null
            : await GetLocationAsync(session, partyLocationId, effective);

        var gaps = new List<string>();
        if (locationCount == 0) gaps.Add("no locations yet");
        if (pcCharacterCount == 0) gaps.Add("no PC characters yet");
        if (partyLocation != null && partyLocation.ClimateZone == null) gaps.Add($"starting location '{partyLocation.Id}' has no climateZone set");

        return new SeedCoverageSummary
        {
            Locations = locationCount,
            PcCharacters = pcCharacterCount,
            Factions = factionCount,
            OpenQuests = openQuestCount,
            ActivePlotThreads = activePlotThreadCount,
            Gaps = gaps,
        };
    }

    /// <summary>
    /// Builds a consolidated WorldStateView for both GetWorldState kickoff and take_turn's IncludeWorldState path.
    /// Consolidates duplication previously split between ExplorationTools.GetWorldState and MutationTools.TakeTurn.
    /// Includes the stuck-party query logic from ExplorationTools, ensuring consistent behavior across call sites.
    /// Returns full event list; callers decide how much to display (ExplorationTools uses all, take_turn takes first 5).
    /// </summary>
    /// <summary>
    /// Builds a complete WorldStateView including scene assembly, pressure evaluation, and NPC synthesis.
    ///
    /// Note: IPressureOrchestrator is passed as a parameter rather than injected because:
    /// - PressureOrchestrator doesn't depend on CampaignRepository (no cycle blocking injection)
    /// - This is a design pattern: making the dependency explicit at the call site
    /// - Allows callers to control which orchestrator instance is used
    ///
    /// Future refactoring (Phase 5.1.4): CampaignSession unit-of-work will own the session lifecycle
    /// and reduce this parameter to improve the method signature.
    /// </summary>
    public async Task<WorldStateView> BuildWorldStateAsync(
        IAsyncDocumentSession session,
        string? campaignName,
        string? partyLocationId,
        IPressureOrchestrator pressureOrchestrator,
        CancellationToken ct = default)
    {
        var effective = ResolveCampaign(campaignName);

        // Core state queries
        var time = await GetTimeAsync(new CampaignSession(session, effective));
        var config = await GetCampaignConfigAsync(new CampaignSession(session, effective));

        // Party location (optional; resolved first for region-scoped queries)
        Location? location = null;
        if (!string.IsNullOrEmpty(partyLocationId))
        {
            location = await GetLocationAsync(session, partyLocationId, effective);
        }

        // Resolve region for location-scoped queries (single-hop: location's parent or location itself)
        var regionId = location?.ParentLocationId ?? partyLocationId;

        // Rumors: Spreading + Peak with location scoping
        var spreading = await QueryRumorsAsync(session, null, regionId, RumorState.Spreading, 3, effective);
        var peak = await QueryRumorsAsync(session, null, regionId, RumorState.Peak, 3, effective);
        var rumors = peak.Concat(spreading).ToList();

        // Recent events (full list; callers decide truncation)
        var events = await SelectRecentEventsAsync(session, effective, config.EventContextBudgetAmbient);

        // Scoped quests and factions: resolve party affiliations and location context
        var partyCharacterIds = await session.Query<Character>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
            .Where(c => (string.IsNullOrEmpty(c.CampaignName) || c.CampaignName == effective)
                        && (c.IsPc || c.IsPartyCompanion))
            .Select(c => c.Id)
            .ToListAsync();

        var partyFactionReputations = await WorldStateScopeResolver.GetPartyFactionReputationsAsync(session, effective);

        // Query scoped quests: imminent-deadline quests always included, then scoped-relevance quests
        var imminentQuests = await WorldStateScopeResolver.QueryImminentDeadlineQuestsAsync(
            session, effective, time.TotalDaysElapsed, limit: 10);
        // Same relevance threshold as faction scoping — a faction the party barely knows shouldn't pull its quests in.
        var relevantFactionIds = WorldStateScopeResolver.GetRelevantFactionIds(partyFactionReputations);
        var scopedQuests = await WorldStateScopeResolver.QueryRelevantQuestsAsync(
            session, effective, regionId, relevantFactionIds, partyCharacterIds, limit: 10);

        // Union imminent (prioritized first) with scoped, removing duplicates
        var worldActiveQuests = imminentQuests
            .Concat(scopedQuests)
            .DistinctBy(q => q.Id)
            .Take(10)
            .ToList();

        // Query scoped factions with reputation threshold
        var worldActiveFactions = await WorldStateScopeResolver.QueryRelevantFactionsAsync(
            session, effective, regionId, partyFactionReputations,
            reputationThreshold: WorldStateScopeResolver.RelevantReputationThreshold, limit: 6);

        // Stuck-party query (from ExplorationTools, consolidating here to fix MutationTools divergence)
        var stuck = await session.Query<Character>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
            .Where(c => (string.IsNullOrEmpty(c.CampaignName) || c.CampaignName == effective)
                        && c.CurrentActivity != null
                        && (c.CurrentActivity.StartsWith("Travel interrupted en route") || c.CurrentActivity.StartsWith("interrupted en route")))
            .Take(5)
            .ToListAsync();

        // Build pressure context for orchestrator
        var pressureCtx = new PressureContext(
            effective,
            time,
            config,
            session,
            ActiveRumors: rumors,
            RecentEvents: events.ToList(),
            QuestDeadlines: worldActiveQuests.Select(q => new QuestDeadlineInfo(q.Id, q.Title, q.DeadlineDay)).ToList());

        var pressureItems = await pressureOrchestrator.CollectAndCapAsync(PressureScope.World, pressureCtx, ct);

        var suggestedExamples = SuggestedCommitExampleBuilder.Build(
            pressureItems,
            worldActiveQuests.FirstOrDefault()?.Id,
            stuck.FirstOrDefault()?.Id);

        // Build faction summaries with stance calculation
        var factionSummaries = worldActiveFactions.Select(f =>
        {
            var overallStance = FactionStance.Neutral;
            if (f.StanceToward != null && f.StanceToward.Count > 0)
            {
                if (f.StanceToward.Values.Contains(FactionStance.AtWar))
                    overallStance = FactionStance.AtWar;
                else if (f.StanceToward.Values.Contains(FactionStance.Hostile))
                    overallStance = FactionStance.Hostile;
                else if (f.StanceToward.Values.Contains(FactionStance.Allied))
                    overallStance = FactionStance.Allied;
            }
            return new FactionPresenceSummary(f.Id, f.Name, f.InfluenceLevel, overallStance, null, f.TerritoryLocationIds.Count);
        }).ToList();

        // Find travel event from recent history
        var travelEvent = events.FirstOrDefault(e =>
            e.Category == EventCategory.Travel ||
            (e.Category == EventCategory.Simulation &&
             (e.Summary.Contains("Travel interrupted", StringComparison.OrdinalIgnoreCase) ||
              e.Summary.Contains("en route", StringComparison.OrdinalIgnoreCase))));

        // Build location summary
        var locSummary = location != null ? new LocationSummary(location.Id, location.Name, location.Type) : null;

        // Build the view
        var view = new WorldStateView(
            time,
            rumors.Select(r => new RumorSummary(r.Subject, r.CurrentText, r.State)),
            events.Select(EventSummaryView.From),
            locSummary,
            PressureManager.ToDisplayStrings(pressureItems),
            worldActiveQuests.Select(q => ToActiveQuestSummary(q) with
            {
                IsOverdue = q.DeadlineDay != null && q.DeadlineDay < time.TotalDaysElapsed
            }),
            factionSummaries,
            travelEvent?.Summary,
            suggestedExamples
        );

        // Attach rich pressure items. SeedCoverage is intentionally NOT computed here — it is a
        // session-0 signal that start_session attaches once per kickoff, not a per-turn cost.
        view.WorldPressureItems = pressureItems;

        return view;
    }

    /// <summary>
    /// Retrieves a specific Faction by ID.
    /// </summary>
    public async Task<Faction?> GetFactionAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var faction = await session.LoadAsync<Faction>(id);
        if (faction != null && !IsVisibleInCampaign(faction.CampaignName, effective))
            return null;
        return faction;
    }

    /// <summary>
    /// Creates or updates a Faction document.
    /// </summary>
    public async Task UpsertFactionAsync(CampaignSession campaignSession, Faction faction)
    {
        var effective = campaignSession.EffectiveCampaign;
        var session = campaignSession.Session;

        if (string.IsNullOrWhiteSpace(faction.Id))
        {
            throw new ArgumentException("Faction.Id is required for upsert.");
        }

        faction.Id = CanonicalId.Normalize(faction.Id, CanonicalId.Factions);

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
            existing.InfluenceLevel = Math.Clamp(faction.InfluenceLevel, 0, 100);
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
    public async Task<Faction> UpsertFactionAsync(CampaignSession campaignSession, FactionUpsertRequest faction)
    {
        var effective = campaignSession.EffectiveCampaign;
        var session = campaignSession.Session;

        if (string.IsNullOrWhiteSpace(faction.Id))
        {
            throw new ArgumentException("Faction.Id is required for upsert.");
        }

        faction.Id = CanonicalId.Normalize(faction.Id, CanonicalId.Factions);

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
                existing.InfluenceLevel = Math.Clamp(faction.InfluenceLevel.Value, 0, 100);
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
                InfluenceLevel = Math.Clamp(faction.InfluenceLevel ?? 50, 0, 100),
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
        if (quest != null && !IsVisibleInCampaign(quest.CampaignName, effective))
            return null;
        return quest;
    }

    public async Task<PlotThread?> GetPlotThreadAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var thread = await session.LoadAsync<PlotThread>(id);
        if (thread != null && !IsVisibleInCampaign(thread.CampaignName, effective))
            return null;
        return thread;
    }

    public async Task<List<PlotThread>> GetActivePlotThreadsAsync(IAsyncDocumentSession session, string? campaignName = null)
    {
        return await SimulationQueryHelper.QueryActivePlotThreadsAsync(session, ResolveCampaign(campaignName));
    }

    /// <summary>
    /// Find all plot threads that reference a specific entity (by ID).
    /// Searches both thread-level InvolvedEntityIds and clue-level InvolvedEntityIds.
    /// Results are scoped to the campaign.
    /// </summary>
    public async Task<List<PlotThread>> GetPlotThreadsReferencingEntityAsync(
        IAsyncDocumentSession session,
        string entityId,
        string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return [];

        var effective = ResolveCampaign(campaignName);

        return await session.Query<PlotThread, PlotThread_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(t => (t.CampaignName == effective || t.CampaignName == null || t.CampaignName == "")
                        && t.AllInvolvedEntityIds.Contains(entityId))
            .ToListAsync();
    }

    /// <summary>
    /// Validate all entity IDs referenced in a plot thread's clues.
    /// Returns list of entity IDs that are referenced but do not exist in the database.
    /// </summary>
    public async Task<List<string>> ValidateClueEntityReferencesAsync(
        IAsyncDocumentSession session,
        PlotThread thread,
        string? campaignName = null)
    {
        var missingEntityIds = new List<string>();
        if (thread?.Clues == null || thread.Clues.Count == 0)
            return missingEntityIds;

        var allReferencedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clue in thread.Clues.Where(c => c?.InvolvedEntityIds != null))
        {
            foreach (var id in clue.InvolvedEntityIds.Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                allReferencedIds.Add(id);
            }
        }

        if (allReferencedIds.Count == 0)
            return missingEntityIds;

        var effective = ResolveCampaign(campaignName);

        // Check each entity type
        foreach (var id in allReferencedIds)
        {
            var exists = false;

            if (id.StartsWith("chars/", StringComparison.OrdinalIgnoreCase))
            {
                var char_ = await session.LoadAsync<Character>(id);
                exists = char_ != null && IsVisibleInCampaign(char_.CampaignName, effective);
            }
            else if (id.StartsWith("locations/", StringComparison.OrdinalIgnoreCase))
            {
                var loc = await session.LoadAsync<Location>(id);
                exists = loc != null && IsVisibleInCampaign(loc.CampaignName, effective);
            }
            else if (id.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
            {
                var item = await session.LoadAsync<Item>(id);
                exists = item != null && IsVisibleInCampaign(item.CampaignName, effective);
            }
            else if (id.StartsWith("factions/", StringComparison.OrdinalIgnoreCase))
            {
                var faction = await session.LoadAsync<Faction>(id);
                exists = faction != null && IsVisibleInCampaign(faction.CampaignName, effective);
            }
            else if (id.StartsWith("quests/", StringComparison.OrdinalIgnoreCase))
            {
                var quest = await session.LoadAsync<Quest>(id);
                exists = quest != null && IsVisibleInCampaign(quest.CampaignName, effective);
            }

            if (!exists)
            {
                missingEntityIds.Add(id);
            }
        }

        return missingEntityIds;
    }

    /// <summary>
    /// Creates or updates a PlotThread, e.g. bulk-seeding clues or bumping TensionLevel. Rich collection
    /// fields (Clues/InvolvedEntityIds/ForeshadowingHooks) are preserved when omitted from the request.
    /// </summary>
    public async Task<PlotThread> UpsertPlotThreadAsync(CampaignSession campaignSession, PlotThreadUpsertRequest thread)
    {
        var effective = campaignSession.EffectiveCampaign;
        var session = campaignSession.Session;

        if (string.IsNullOrWhiteSpace(thread.Id))
        {
            throw new ArgumentException("PlotThread.Id is required for upsert.");
        }

        thread.Id = CanonicalId.Normalize(thread.Id, CanonicalId.PlotThreads);
        var effectiveCampaignName = thread.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective;
        }

        var currentDay = (await GetTimeAsync(new CampaignSession(session, effective))).TotalDaysElapsed;

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

    public async Task<WorldEvent> UpsertWorldEventAsync(IAsyncDocumentSession session, WorldEventUpsertRequest eventRequest, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(eventRequest.Id))
        {
            throw new ArgumentException("WorldEvent.Id is required for upsert.");
        }

        eventRequest.Id = CanonicalId.Normalize(eventRequest.Id, CanonicalId.WorldEvents);

        var effective = ResolveCampaign(campaignName);
        var effectiveCampaignName = eventRequest.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective;
        }

        var currentDay = (await GetTimeAsync(new CampaignSession(session, effective))).TotalDaysElapsed;

        var existing = await session.LoadAsync<WorldEvent>(eventRequest.Id);
        WorldEvent result;
        if (existing != null)
        {
            existing.Title = eventRequest.Title;
            existing.Description = eventRequest.Description;
            existing.ActorId = eventRequest.ActorId;
            existing.InvolvedEntityIds = eventRequest.InvolvedEntityIds ?? existing.InvolvedEntityIds;
            existing.TriggerType = eventRequest.TriggerType;
            existing.IntervalDays = eventRequest.IntervalDays;
            existing.TargetDay = eventRequest.TargetDay;
            existing.Condition = eventRequest.Condition ?? existing.Condition;
            existing.Effects = eventRequest.Effects ?? existing.Effects;
            existing.Status = eventRequest.Status;
            existing.IsPlayerVisible = eventRequest.IsPlayerVisible;
            existing.DmNotes = eventRequest.DmNotes;
            existing.CampaignName = effectiveCampaignName;
            existing.LastUpdatedDay = currentDay;
            if (eventRequest.IsArchived.HasValue)
            {
                existing.IsArchived = eventRequest.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            result = new WorldEvent
            {
                Id = eventRequest.Id,
                Title = eventRequest.Title,
                Description = eventRequest.Description,
                ActorId = eventRequest.ActorId,
                InvolvedEntityIds = eventRequest.InvolvedEntityIds ?? [],
                TriggerType = eventRequest.TriggerType,
                IntervalDays = eventRequest.IntervalDays,
                TargetDay = eventRequest.TargetDay,
                Condition = eventRequest.Condition,
                Effects = eventRequest.Effects ?? [],
                Status = eventRequest.Status,
                IsPlayerVisible = eventRequest.IsPlayerVisible,
                DmNotes = eventRequest.DmNotes,
                CampaignName = effectiveCampaignName,
                DayCreated = currentDay,
                LastUpdatedDay = currentDay,
                IsArchived = eventRequest.IsArchived ?? false,
            };
            await session.StoreAsync(result);
        }

        await EnrichSemanticVectorAsync(result);
        return result;
    }

    /// <summary>
    /// Creates or updates a Quest document.
    /// </summary>
    public async Task UpsertQuestAsync(CampaignSession campaignSession, Quest quest)
    {
        var effective = campaignSession.EffectiveCampaign;
        var session = campaignSession.Session;

        if (string.IsNullOrWhiteSpace(quest.Id))
        {
            throw new ArgumentException("Quest.Id is required for upsert.");
        }

        quest.Id = CanonicalId.Normalize(quest.Id, CanonicalId.Quests);

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
    public async Task<Quest> UpsertQuestAsync(CampaignSession campaignSession, QuestUpsertRequest quest)
    {
        var effective = campaignSession.EffectiveCampaign;
        var session = campaignSession.Session;

        if (string.IsNullOrWhiteSpace(quest.Id))
        {
            throw new ArgumentException("Quest.Id is required for upsert.");
        }

        quest.Id = CanonicalId.Normalize(quest.Id, CanonicalId.Quests);

        var effectiveCampaignName = quest.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = effective;
        }

        var currentDay = (await GetTimeAsync(new CampaignSession(session, effective))).TotalDaysElapsed;

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
            .Where(q => (q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
                        && !q.IsArchived
                        && (string.IsNullOrEmpty(q.CampaignName) || q.CampaignName == effective)
                        && q.RelatedLocationIds.Contains(locationId))
            .Take(20).ToListAsync();

        return quests;
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
            .Where(f => !f.IsArchived
                        && (string.IsNullOrEmpty(f.CampaignName) || f.CampaignName == effective)
                        && (f.ControllingTerritory == locationId || f.TerritoryLocationIds.Contains(locationId)))
            .Take(50).ToListAsync();

        return factions;
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


    internal async Task<NpcSummaryView?> BuildNpcSummaryAsync(IAsyncDocumentSession session, string characterId, string campaignName)
    {
        var npc = await GetCharacterAsync(new CampaignSession(session, campaignName), characterId);
        if (npc == null)
            return null;

        var recentEvents = await SelectRecentEventsAsync(session, campaignName, budget: 3, involvedCharacterId: characterId);
        var behavioralSummary = _behaviorSynthesizer.GenerateSummary(npc, null, recentEvents);

        var heldItems = await session.Query<Item>()
            .Where(i => i.HolderId == characterId && !i.IsArchived)
            .Customize(x => x.WaitForNonStaleResults())
            .ToListAsync();

        var summary = new NpcSummaryView
        {
            CharacterId = npc.Id,
            Name = npc.Name,
            CurrentAppearance = npc.CurrentAppearance ?? "",
            BehavioralSummary = behavioralSummary,
            KnownNeeds = npc.Needs?.ActiveNeeds ?? new Dictionary<string, float>(),
            Equipped = heldItems.Where(i => i.IsEquipped).Select(ItemSummaryView.From).ToList(),
            Carried = heldItems.Where(i => !i.IsEquipped).Select(ItemSummaryView.From).ToList()
        };

        return summary;
    }

    internal async Task<SceneSummaryView?> BuildSceneSummaryAsync(IAsyncDocumentSession session, string locationId, string campaignName)
    {
        var scene = await GetSceneAsync(new CampaignSession(session, campaignName), locationId, markVisited: false);
        if (scene?.Location == null)
            return null;

        var summary = new SceneSummaryView
        {
            Location = scene.Location,
            PresentNPCs = scene.PresentNPCs ?? [],
            LocalRumors = scene.LocalRumors ?? [],
            ActiveCombat = scene.ActiveCombat != null
        };

        return summary;
    }

    /// <summary>
    /// Loads the in-progress onboarding state for a campaign, or null if none exists.
    /// </summary>
    public async Task<OnboardingState?> GetOnboardingStateAsync(IAsyncDocumentSession session, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var id = _keys.StateOnboarding(effective);
        return await session.LoadAsync<OnboardingState>(id);
    }

    /// <summary>
    /// Saves or updates the onboarding state for a campaign.
    /// </summary>
    public async Task UpsertOnboardingStateAsync(IAsyncDocumentSession session, OnboardingState state, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var id = _keys.StateOnboarding(effective);
        state.Id = id;
        state.CampaignSlug = effective;
        state.LastUpdatedAt = DateTime.UtcNow;
        await session.StoreAsync(state, id);
    }

    /// <summary>
    /// Deletes the onboarding state for a campaign (typically called after finalization).
    /// </summary>
    public async Task DeleteOnboardingStateAsync(IAsyncDocumentSession session, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var id = _keys.StateOnboarding(effective);
        session.Delete(id);
    }
}
