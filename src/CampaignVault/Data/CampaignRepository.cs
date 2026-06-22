using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Initiative;
using CampaignVault.Data.Scenes;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
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
    private readonly ICurrentCampaignContext? _currentCampaign;
    private readonly INpcInitiativeService _initiativeService;
    private readonly SceneAssembler _sceneAssembler;

    private string ResolveCampaign(string? campaignName)
    {
        if (!string.IsNullOrWhiteSpace(campaignName))
        {
            return campaignName;
        }

        if (!string.IsNullOrWhiteSpace(_currentCampaign?.CurrentCampaignName))
        {
            return _currentCampaign.CurrentCampaignName;
        }

        throw new InvalidOperationException(
            "A campaign name must be provided explicitly or via ICurrentCampaignContext.");
    }

    private static bool IsVisibleInCampaign(string? entityCampaignName, string effectiveCampaign) =>
        string.IsNullOrEmpty(entityCampaignName)
        || string.Equals(entityCampaignName, effectiveCampaign, StringComparison.OrdinalIgnoreCase);

    private static string BuildCanonicalIdPrefix(string cleanQuery, string prefix) =>
        cleanQuery.Contains('/', StringComparison.Ordinal) ? cleanQuery : prefix + cleanQuery;

    public CampaignRepository(
        IDocumentStore store,
        IWorldSimulationEngine simulationEngine,
        ILogger<CampaignRepository> logger,
        INpcBehaviorSynthesizer behaviorSynthesizer,
        CampaignDocumentKeys keys,
        ICurrentCampaignContext currentCampaign,
        ChangeHandlers.WorldChangeDispatcher changeDispatcher,
        SceneAssembler sceneAssembler,
        INpcInitiativeService initiativeService)
    {
        _store = store;
        _simulationEngine = simulationEngine;
        _logger = logger;
        _behaviorSynthesizer = behaviorSynthesizer;
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _currentCampaign = currentCampaign;
        _initiativeService = initiativeService ?? throw new ArgumentNullException(nameof(initiativeService));
        _sceneAssembler = sceneAssembler ?? throw new ArgumentNullException(nameof(sceneAssembler));
        _changeDispatcher = changeDispatcher ?? throw new ArgumentNullException(nameof(changeDispatcher));
    }


    /// <summary>
    /// Opens an asynchronous document session with Optimistic Concurrency enabled.
    /// </summary>
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
    public async Task<CommitResult> StageChangesAsync(IAsyncDocumentSession session, WorldChange[] changes,
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
            ev => LogEventAsync(session, ev));

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
        var location = await session
            .Include<Location>(x => x.ParentLocationId)
            .LoadAsync<Location>(locationId);

        if (location == null)
        {
            return _sceneAssembler.CreateUnanchoredScene(locationId);
        }

        var effective = ResolveCampaign(campaignName);
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
        var events = await LoadSceneEventsAsync(session, locationId, effectiveCampaign);

        JsonSanitizer.Sanitize(location);

        var time = await GetTimeAsync(session, effectiveCampaign);
        var globalDescriptors = await GetGlobalNeedDescriptorsAsync(session, effectiveCampaign);
        var config = await GetCampaignConfigAsync(session, effectiveCampaign);
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
            .Where(i => IsVisibleInCampaign(i.CampaignName, effectiveCampaign))
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
        string effectiveCampaign)
    {
        return (await QueryEventsAsync(session, null, null, 5, effectiveCampaign))
            .Where(e => e.Involved.Contains(locationId))
            .OrderByDescending(e => e.Timestamp)
            .Take(5)
            .ToList();
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

        // Build context and run the pluggable simulation engine (rules emit deltas)
        var config = await GetCampaignConfigAsync(session, effective);
        var simContext = new SimulationContext(time, activeRumors, npcs, session, days, effective, activeFactions,
            activeQuests, config);

        _logger.LogInformation("Starting world simulation for {Days} days at time {CurrentTime}", days, time);

        var simResult = await _simulationEngine.RunAsync(simContext);

        _logger.LogInformation(
            "Simulation complete. Narratives: {NarrativeCount}, Deltas: {DeltaCount}, PressureItems: {PressureCount}",
            simResult.NarrativeEvents.Count,
            simResult.Deltas.Count,
            simResult.WorldPressure.Count);

        // Persist simulation narrative events
        foreach (var narrative in simResult.NarrativeEvents)
        {
            await LogEventAsync(session, new()
            {
                Id = "events/" + Guid.NewGuid(),
                Summary = narrative,
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

        // WorldPressure from the engine is surfaced to the caller (AdvanceWorld tool).
        return new()
        {
            NewTime = time,
            SimulatorEvents = simResult.NarrativeEvents.ToList(),
            WorldPressure = simResult.WorldPressure.ToList()
        };
    }

    // --- Search & Recall ---

    /// <summary>
    /// Performs a parallel, fuzzy search across Characters, Lore, and Locations for the given query.
    /// Returns a mixed collection of documents matching the search string.
    /// </summary>
    public async Task<IEnumerable<object>> UnifiedSearchAsync(IAsyncDocumentSession session, string query,
        string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);

        var charsQuery = session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .Search(x => x.Name, $"*{query}*");

        var loreQuery = session.Advanced.AsyncDocumentQuery<Lore, Lore_Search>()
            .Search(x => x.Title, $"*{query}*");

        var locsQuery = session.Advanced.AsyncDocumentQuery<Location, Location_Search>()
            .Search(x => x.Name, $"*{query}*");

        if (!string.IsNullOrEmpty(effective))
        {
            charsQuery = charsQuery.AndAlso().OpenSubclause()
                .WhereEquals("CampaignName", string.Empty).OrElse()
                .WhereEquals("CampaignName", (string)null).OrElse()
                .WhereEquals("CampaignName", effective)
                .CloseSubclause();

            loreQuery = loreQuery.AndAlso().OpenSubclause()
                .WhereEquals("CampaignName", string.Empty).OrElse()
                .WhereEquals("CampaignName", (string)null).OrElse()
                .WhereEquals("CampaignName", effective)
                .CloseSubclause();

            locsQuery = locsQuery.AndAlso().OpenSubclause()
                .WhereEquals("CampaignName", string.Empty).OrElse()
                .WhereEquals("CampaignName", (string)null).OrElse()
                .WhereEquals("CampaignName", effective)
                .CloseSubclause();
        }

        // Await queries individually. The previous Task-capture + WhenAll + re-await pattern
        // could leave RavenDB session tracking "active async tasks" after the method returned,
        // causing "Disposing session with active async task is forbidden" on ExecuteAsync disposal.
        var chars = await charsQuery.Take(5).ToListAsync();
        var lore = await loreQuery.Take(5).ToListAsync();
        var locs = await locsQuery.Take(5).ToListAsync();

        // Critical: Locations returned to the LLM via SearchWorld can contain Metadata dictionaries
        // that hold JsonElement (from STJ inbound or legacy data). Without sanitization here,
        // STJ serialization of the tool response in the MCP layer blows up with
        // "Operation is not valid due to the current state of the object" (dead JsonElement).
        foreach (var l in locs)
        {
            SanitizeLocation(l);
        }

        var results = new List<object>();
        results.AddRange(chars);
        results.AddRange(lore);
        results.AddRange(locs);
        return results;
    }

    /// <summary>
    /// Retrieves historical narrative events, optionally filtered by search query or event category.
    /// </summary>
    public async Task<IEnumerable<Event>> QueryEventsAsync(IAsyncDocumentSession session, string? query,
        EventCategory? category, int limit = 10, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var q = session.Advanced.AsyncDocumentQuery<Event, Event_Search>();

        if (!string.IsNullOrEmpty(effective))
        {
            q = q.WhereEquals(x => x.CampaignName, effective);
        }

        if (!string.IsNullOrEmpty(query))
        {
            q = q.AndAlso().Search(x => x.Summary, $"*{query}*");
        }

        if (category.HasValue)
        {
            q = q.AndAlso().WhereEquals(x => x.Category, category.Value);
        }

        var events = await q.OrderByDescending(x => x.Timestamp).Take(limit).ToListAsync();
        foreach (var ev in events)
        {
            if (ev.Details != null)
            {
                ev.Details = SanitizeDetails(ev.Details);
            }
        }

        return events;
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
        _ = ResolveCampaign(campaignName);
        var character = await session.LoadAsync<Character>(identifier);
        if (character != null)
        {
            return character;
        }

        character = await session.Query<Character>().FirstOrDefaultAsync(x => x.Name == identifier);
        if (character != null)
        {
            return character;
        }

        return await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WhereEquals(x => x.Name, identifier).Fuzzy(0.4m).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Inserts or updates a character in the database, safely mutating tracked entities to preserve concurrency.
    /// Also waits for the Character/Search index to catch up to prevent stale queries.
    /// </summary>
    public async Task UpsertCharacterAsync(IAsyncDocumentSession session, Character character,
        string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(character.Id))
        {
            throw new ArgumentException("Character.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(character.CampaignName))
        {
            character.CampaignName = effective;
        }

        character.LastUpdated = DateTime.UtcNow;

        var existing = await session.LoadAsync<Character>(character.Id);
        if (existing != null)
        {
            // Mutate the already-tracked entity in place. This is the safest pattern
            // with OptimisticConcurrencyMode.Writes + Raven change tracking.
            // We get full overwrite semantics without ever having two objects for the same ID.
            existing.Name = character.Name;
            existing.ClassLevel = character.ClassLevel;
            existing.CurrentHp = character.CurrentHp;
            existing.MaxHp = character.MaxHp;

            existing.Notes = character.Notes;
            existing.Schedule = character.Schedule;
            existing.CurrentLocationId = character.CurrentLocationId;
            existing.CurrentActivity = character.CurrentActivity;
            existing.Psychology = character.Psychology ?? new PsychologyProfile();
            existing.Social = character.Social ?? new SocialProfile();
            existing.Needs = character.Needs ?? new NeedsProfile();
            existing.SystemStats = character.SystemStats ?? new SystemExtension();
            existing.KeepAlive = character.KeepAlive;
            existing.LastUpdated = character.LastUpdated;
            existing.CampaignName = character.CampaignName; // ensure set/copied for scoping
        }
        else
        {
            await session.StoreAsync(character, null, character.Id);
        }

        // Help keep the Character/Search index fresh after writes that affect Schedule or CurrentLocation.
        session.Advanced.WaitForIndexesAfterSaveChanges(
            timeout: TimeSpan.FromSeconds(3),
            throwOnTimeout: false,
            indexes: ["Character/Search"]);
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
    public async Task UpsertLoreAsync(IAsyncDocumentSession session, Lore lore, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(lore.Id))
        {
            throw new ArgumentException("Lore.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(lore.CampaignName))
        {
            lore.CampaignName = effective;
        }

        lore.LastUpdated = DateTime.UtcNow;

        var existing = await session.LoadAsync<Lore>(lore.Id);
        if (existing != null)
        {
            existing.Title = lore.Title;
            existing.Content = lore.Content;
            existing.Tags = lore.Tags ?? [];
            existing.Keywords = lore.Keywords ?? [];
            existing.Category = lore.Category;
            existing.LastUpdated = lore.LastUpdated;
            existing.CampaignName = lore.CampaignName; // ensure set/copied for scoping
        }
        else
        {
            await session.StoreAsync(lore);
        }
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
    public async Task UpsertLocationAsync(IAsyncDocumentSession session, Location location, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(location.Id))
        {
            throw new ArgumentException("Location.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(location.CampaignName))
        {
            location.CampaignName = effective;
        }

        SanitizeLocation(location);
        location.LastUpdated = DateTime.UtcNow;

        var existing = await session.LoadAsync<Location>(location.Id);
        if (existing != null)
        {
            // Mutate the tracked entity (safest with optimistic concurrency).
            existing.Name = location.Name;
            existing.Description = location.Description;
            existing.Type = location.Type;
            existing.ParentLocationId = location.ParentLocationId;
            existing.Exits = location.Exits ?? [];
            existing.PointsOfInterest = location.PointsOfInterest ?? [];
            existing.AmbientCrowd = location.AmbientCrowd;
            existing.LastVisitedDay = location.LastVisitedDay;
            existing.Metadata = location.Metadata ?? [];
            existing.LastUpdated = location.LastUpdated;
            existing.CampaignName = location.CampaignName; // ensure set/copied for scoping
            existing.ControllingFactionId = location.ControllingFactionId; // Phase 7.1
        }
        else
        {
            await session.StoreAsync(location);
        }
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
            existing.CampaignName = rumor.CampaignName; // ensure for scoping (strict for rumors)
        }
        else
        {
            await session.StoreAsync(rumor);
        }
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

        return list;
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
    /// </summary>
    public async Task UpsertItemAsync(IAsyncDocumentSession session, Item item, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
        {
            throw new ArgumentException("Item.Id is required for upsert.");
        }

        var effective = ResolveCampaign(campaignName);
        if (string.IsNullOrEmpty(item.CampaignName))
        {
            item.CampaignName = effective;
        }

        SanitizeItem(item);
        item.LastUpdated = DateTime.UtcNow;

        var existing = await session.LoadAsync<Item>(item.Id);
        if (existing != null)
        {
            existing.Name = item.Name;
            existing.Description = item.Description;
            existing.Properties = item.Properties ?? [];
            existing.HolderId = item.HolderId;
            existing.LastUpdated = item.LastUpdated;
            existing.CampaignName = item.CampaignName; // ensure set/copied for scoping
            // Note: original missed copying Tags; added for completeness in hardening pass
            existing.Tags = item.Tags ?? existing.Tags ?? [];
        }
        else
        {
            await session.StoreAsync(item);
        }
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

        var suggestions = await session.Query<Character, Character_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == effective || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await session.Query<Character, Character_Search>()
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

        var suggestions = await session.Query<Item, Item_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == effective || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await session.Query<Item, Item_Search>()
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

        var suggestions = await session.Query<Faction, Faction_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == effective || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await session.Query<Faction, Faction_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == effective || x.CampaignName == null)
                .Search(x => x.Name, cleanQuery + "*")
                .Take(3).ToListAsync();

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

        var suggestions = await session.Query<Quest, Quest_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == effective || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await session.Query<Quest, Quest_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == effective || x.CampaignName == null)
                .Search(x => x.Title, cleanQuery + "*")
                .Take(3).ToListAsync();

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
        }
        else
        {
            await session.StoreAsync(faction);
        }
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
        }
        else
        {
            await session.StoreAsync(quest);
        }
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
            .Where(q => q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
            .Take(20).ToListAsync();

        return quests
            .Where(q => (string.IsNullOrEmpty(q.CampaignName) || q.CampaignName == effective)
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
            .Take(50).ToListAsync();

        return factions
            .Where(f => (string.IsNullOrEmpty(f.CampaignName) || f.CampaignName == effective)
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