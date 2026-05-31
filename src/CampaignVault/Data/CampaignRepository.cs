using Raven.Client.Documents;
using CampaignVault.Models;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Indexes;
using Microsoft.Extensions.Logging;
using CampaignVault.Data.ChangeHandlers;

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

    private string ResolveCampaign(string? campaignName)
    {
        if (!string.IsNullOrWhiteSpace(campaignName)) return campaignName;
        if (!string.IsNullOrWhiteSpace(_currentCampaign?.CurrentCampaignName)) return _currentCampaign.CurrentCampaignName;
        return "default";
    }

    public CampaignRepository(
        IDocumentStore store, 
        IWorldSimulationEngine simulationEngine,
        ILogger<CampaignRepository> logger,
        INpcBehaviorSynthesizer behaviorSynthesizer,
        CampaignDocumentKeys keys,
        ICurrentCampaignContext? currentCampaign = null,
        IEnumerable<IWorldChangeHandler>? changeHandlers = null)
    {
        _store = store;
        _simulationEngine = simulationEngine;
        _logger = logger;
        _behaviorSynthesizer = behaviorSynthesizer;
        _keys = keys ?? new CampaignDocumentKeys();
        _currentCampaign = currentCampaign;

        var handlersList = (changeHandlers ?? Array.Empty<IWorldChangeHandler>()).ToList();

        if (handlersList.Count == 0) //TODO: consider what should be done here - this is brittle as fuk
        {
            // Default to full production handler set so simulation tests and legacy 4-arg constructions continue to work
            handlersList =
            [
                new HpChangeHandler(),
                new ItemTransferHandler(),
                new StatusChangeHandler(),
                new EventOccurredHandler(),
                new RumorEvolvesHandler(),
                new RelationshipChangeHandler(),
                new NeedChangeHandler(),
                new AttributeChangeHandler(),
                new MoodChangeHandler(),
                new ActivityChangeHandler()
            ];
        }

        _changeDispatcher = new WorldChangeDispatcher(handlersList, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorldChangeDispatcher>.Instance);
    }

    /// <summary>
    /// Convenience constructor primarily for test scenarios.
    /// Supplies the full set of production handlers so the legacy fallback can be removed.
    /// </summary>
    public CampaignRepository(IDocumentStore store)
        : this(store, 
               new NoOpSimulationEngine(), 
               Microsoft.Extensions.Logging.Abstractions.NullLogger<CampaignRepository>.Instance,
               new DefaultBehaviorSynthesizer(),
               new CampaignDocumentKeys(),
               currentCampaign: null)
    {
    }

    /// <summary>
    /// Legacy 4-argument constructor used extensively by existing tests.
    /// Supplies modern defaults for CampaignDocumentKeys and ICurrentCampaignContext so tests continue to compile
    /// after the multi-campaign / deep-propagation changes without requiring mass updates to test setup code.
    /// </summary>
    public CampaignRepository(
        IDocumentStore store,
        IWorldSimulationEngine simulationEngine,
        ILogger<CampaignRepository> logger,
        INpcBehaviorSynthesizer behaviorSynthesizer)
        : this(store, simulationEngine, logger, behaviorSynthesizer, new CampaignDocumentKeys(), currentCampaign: null)
    {
    }

    /// <summary>
    /// Minimal no-op implementation so existing tests that do not care about simulation behavior continue to compile.
    /// AdvanceWorld tests will still need to construct a real engine (or we will update them in verification phase).
    /// </summary>
    private sealed class NoOpSimulationEngine : IWorldSimulationEngine
    {
        public Task<SimulationResult> RunAsync(SimulationContext context, CancellationToken ct = default)
            => Task.FromResult(new SimulationResult([], [], []));
    }

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
    public Task<CommitResult> StageChangesAsync(IAsyncDocumentSession session, WorldChange[] changes, string? campaignName = null)
    {
        changes ??= Array.Empty<WorldChange>();
        var effective = ResolveCampaign(campaignName);

        _logger.LogDebug("StageChangesAsync called with {ChangeCount} changes for campaign {Campaign}", changes.Length, effective);

        return _changeDispatcher.DispatchAsync(
            session,
            changes,
            effective,
            () => GetTimeAsync(session, effective),
            ev => LogEventAsync(session, ev));
    }

    public async Task<SceneView> GetSceneAsync(IAsyncDocumentSession session, string locationId, string? campaignName = null)
    {
        var location = await session
            .Include<Location>(x => x.ParentLocationId)
            .LoadAsync<Location>(locationId);

        if (location == null)
        {
            // Explicit guard instead of relying on location! below.
            // Prevents NullReferenceException when an LLM passes a bad or deleted locationId.
            throw new KeyNotFoundException($"Location '{locationId}' not found.");
        }

        var effective = ResolveCampaign(campaignName);
        var regionId = location.ParentLocationId ?? locationId;
        var subLocations = (await QueryLocationsAsync(session, null, null, locationId, 20, effective)).ToList();
        
        var targetIds = new List<string> { locationId };
        targetIds.AddRange(subLocations.Select(l => l.Id));

        // Primary discovery via static schedule index (good for cold starts / world building)
        // NOTE: Raw queries for entities are location/schedule scoped, not campaign-filtered.
        // Entities remain ID-controlled for now; singletons and context provide the isolation boundary.
        var npcsFromIndex = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .ContainsAny("Locations", targetIds)
            .Take(20)
            .ToListAsync();

        // Fallback: if the index hasn't caught up (common in fast tests), load recent characters and filter client-side
        //TODO: add a warning log - so I can catch it in live instances
        if (npcsFromIndex.Count == 0)
        {
            var recentChars = await session.Query<Character>().Take(200).ToListAsync();
            npcsFromIndex = recentChars
                .Where(x => x.Schedule != null &&
                            (targetIds.Contains(x.Schedule.DefaultLocationId) ||
                             x.Schedule.Routines.Any(r => targetIds.Contains(r.LocationId))))
                .Take(20)
                .ToList();
        }

        // Efficient query for simulation-updated locations using the extended Character_Search index.
        // This replaces the previous unconditional .Take(100) + client-side LINQ filter (O(n) scan).
        var npcsFromSimulation = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WhereIn("CurrentLocationId", targetIds)
            .Take(20)
            .ToListAsync();

        // Merge, dedupe by Id, prefer simulation-updated versions when both exist
        var npcMap = npcsFromIndex.ToDictionary(n => n.Id, n => n);
        foreach (var simNpc in npcsFromSimulation)
        {
            npcMap[simNpc.Id] = simNpc; // simulation state wins
        }
        var npcs = npcMap.Values.ToList();

        var rumors = await QueryRumorsAsync(session, null, regionId, null, 5, effective);
        
        // Items and characters are currently scoped by location, not campaign.
        var items = await session.Query<Item>().Where(x => x.HolderId == locationId).ToListAsync();
        foreach (var it in items)
        {
            JsonSanitizer.Sanitize(it);
        }

        JsonSanitizer.Sanitize(location);

        var events = (await QueryEventsAsync(session, null, null, 5, effective))
            .Where(e => e.Involved.Contains(locationId))
            .OrderByDescending(e => e.Timestamp)
            .Take(5)
            .ToList();

        var time = await GetTimeAsync(session, effective);

        // Load global descriptors once (cheap) so we can merge them into every NPC's view
        var globalDescriptors = await GetGlobalNeedDescriptorsAsync(session, effective);

        // Project to lightweight presence summaries + behavioral synthesis.
        // This fulfills the V4 goal of giving the DM synthesized insight instead of raw data.
        var presenceSummaries = npcs.Select(npc =>
        {
            var npcNeeds = npc.Needs ?? new NeedsProfile();
            var npcPsych = npc.Psychology ?? new PsychologyProfile();

            // Take the top 3 highest needs for a compact view (sorted descending)
            var topNeeds = npcNeeds.ActiveNeeds
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            // Expose all known needs + descriptors (merged global + per-NPC, per-NPC wins)
            var knownNeeds = npcNeeds.ActiveNeeds.ToDictionary(kv => kv.Key, kv => kv.Value);
            var needDescriptors = new Dictionary<string, string>(globalDescriptors, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in npcNeeds.NeedDescriptors ?? new Dictionary<string, string>())
            {
                needDescriptors[kv.Key] = kv.Value;
            }

            // Generate behavioral summary using the injected synthesizer
            var behavioralSummary = _behaviorSynthesizer.GenerateSummary(npc, time, events);

            return new NpcPresenceSummary(
                Id: npc.Id,
                Name: npc.Name,
                CurrentActivity: npc.CurrentActivity ?? npc.Schedule?.DefaultLocationId,
                CurrentMood: npcPsych.CurrentMood,
                TopNeeds: topNeeds,
                KnownNeeds: knownNeeds,
                NeedDescriptors: needDescriptors,
                BehavioralSummary: behavioralSummary,
                Notes: npc.Notes,
                SystemStats: npc.SystemStats
            );
        }).ToList();

        var activeCombat = await session.LoadAsync<CombatEncounter>(_keys.CombatCurrent(effective));
        if (activeCombat != null && (!activeCombat.IsActive || activeCombat.LocationId != locationId))
        {
            activeCombat = null;
        }

        return new SceneView
        {
            Location = location,
            PresentNPCs = presenceSummaries,
            LocalRumors = rumors.Select(r => new RumorSummary(r.Subject, r.CurrentText, r.State)),
            VisibleItems = items,
            RecentEvents = events,
            ActiveCombat = activeCombat
        };
    }

    // --- Time & Simulator ---

    public async Task<AdvanceResult> AdvanceWorldAsync(IAsyncDocumentSession session, int days, TimeOfDay timeOfDay, string? campaignName = null)
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

        // NOTE (multi-campaign): These simulation queries are currently global (entity IDs are caller-controlled, not namespaced).
        // Campaign isolation for world entities is provided via the CampaignName in simContext (rules may filter in future)
        // and per-campaign singletons (time/config/combat). See code-review-fix-plan.md for scoping policy notes.
        var activeRumors = await session.Query<Rumor>()
            .Where(x => x.State != RumorState.Resolved && x.State != RumorState.Forgotten)
            .ToListAsync();
        var npcs = await session.Query<Character>().Where(x => x.Schedule != null).ToListAsync();

        // Build context and run the pluggable simulation engine (rules emit deltas)
        var simContext = new SimulationContext(time, activeRumors, npcs, session, days, effective);

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
            await LogEventAsync(session, new Event 
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

        // WorldPressure from the engine can be surfaced by the caller (AdvanceWorld tool) if desired.
        // For now we keep AdvanceResult focused on time + narratives (matching prior contract).

        return new AdvanceResult 
        { 
            NewTime = time, 
            SimulatorEvents = simResult.NarrativeEvents.ToList() 
        };
    }

    // --- Search & Recall ---

    public async Task<IEnumerable<object>> UnifiedSearchAsync(IAsyncDocumentSession session, string query, string? campaignName = null)
    {
        // Await queries individually. The previous Task-capture + WhenAll + re-await pattern
        // could leave RavenDB session tracking "active async tasks" after the method returned,
        // causing "Disposing session with active async task is forbidden" on ExecuteAsync disposal.
        var chars = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .Search(x => x.Name, $"*{query}*").Take(5).ToListAsync();

        var lore = await session.Advanced.AsyncDocumentQuery<Lore, Lore_Search>()
            .Search(x => x.Title, $"*{query}*").Take(5).ToListAsync();

        var locs = await session.Advanced.AsyncDocumentQuery<Location, Location_Search>()
            .Search(x => x.Name, $"*{query}*").Take(5).ToListAsync();

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

    public async Task<IEnumerable<Event>> QueryEventsAsync(IAsyncDocumentSession session, string? query, EventCategory? category, int limit = 10, string? campaignName = null)
    {
        var q = session.Advanced.AsyncDocumentQuery<Event, Event_Search>();
        if (!string.IsNullOrEmpty(query)) q = q.AndAlso().Search(x => x.Summary, $"*{query}*");
        if (category.HasValue) q = q.AndAlso().WhereEquals(x => x.Category, category.Value);
        var events = await q.OrderByDescending(x => x.Timestamp).Take(limit).ToListAsync();
        foreach (var ev in events) { if (ev.Details != null) ev.Details = SanitizeDetails(ev.Details); }
        return events;
    }

    // --- Base Helpers ---

    public async Task<Character?> GetCharacterAsync(IAsyncDocumentSession session, string identifier, string? campaignName = null)
    {
        // campaignName accepted for API consistency / future entity namespacing or filtering.
        // Current implementation uses direct ID or name lookup (entities are caller-ID-controlled).
        _ = ResolveCampaign(campaignName);
        var character = await session.LoadAsync<Character>(identifier);
        if (character != null) return character;
        character = await session.Query<Character>().FirstOrDefaultAsync(x => x.Name == identifier);
        if (character != null) return character;
        return await session.Advanced.AsyncDocumentQuery<Character, Character_Search>().WhereEquals(x => x.Name, identifier).Fuzzy(0.4m).FirstOrDefaultAsync();
    }

    public async Task UpsertCharacterAsync(IAsyncDocumentSession session, Character character)
    {
        if (string.IsNullOrWhiteSpace(character.Id))
            throw new ArgumentException("Character.Id is required for upsert.");

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
            existing.LastUpdated = character.LastUpdated;
        }
        else
        {
            await session.StoreAsync(character, null, character.Id);
        }

        // Help keep the Character/Search index fresh after writes that affect Schedule or CurrentLocation.
        session.Advanced.WaitForIndexesAfterSaveChanges(
            timeout: TimeSpan.FromSeconds(3),
            throwOnTimeout: false,
            indexes: new[] { "Character/Search" });
    }

    public async Task<CampaignTime> GetTimeAsync(IAsyncDocumentSession session, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var id = _keys.StateTime(effective);
        var time = await session.LoadAsync<CampaignTime>(id);
        if (time == null) { time = new CampaignTime { Id = id }; await session.StoreAsync(time, id); }
        return time;
    }

    public async Task SaveTimeAsync(IAsyncDocumentSession session, CampaignTime time, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        time.Id = _keys.StateTime(effective);
        time.LastUpdated = DateTime.UtcNow;
        await session.StoreAsync(time);
    }

    public async Task<CampaignConfig> GetCampaignConfigAsync(IAsyncDocumentSession session, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var id = _keys.Config(effective);
        var config = await session.LoadAsync<CampaignConfig>(id);
        if (config == null)
        {
            config = new CampaignConfig { Id = id };
            await session.StoreAsync(config, id);
        }
        return config;
    }

    public async Task UpsertCampaignConfigAsync(IAsyncDocumentSession session, CampaignConfig config, string? campaignName = null)
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
    public async Task<Dictionary<string, string>> GetGlobalNeedDescriptorsAsync(IAsyncDocumentSession session, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var docId = _keys.NeedDescriptors(effective);
        var config = await session.LoadAsync<NeedDescriptorsConfig>(docId);
        var source = config?.Descriptors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sets or updates a single need descriptor for a campaign.
    /// </summary>
    public async Task SetNeedDescriptorAsync(IAsyncDocumentSession session, string needName, string descriptor, string? campaignName = null)
    {
        var effective = ResolveCampaign(campaignName);
        var docId = _keys.NeedDescriptors(effective);
        var config = await session.LoadAsync<NeedDescriptorsConfig>(docId) ?? new NeedDescriptorsConfig { Id = docId };
        config.Descriptors[needName.Trim()] = descriptor.Trim();
        await session.StoreAsync(config, docId);
    }

    public async Task LogEventAsync(IAsyncDocumentSession session, Event @event, string? campaignName = null)
    {
        if (@event.Details != null) @event.Details = SanitizeDetails(@event.Details);
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

    public async Task UpsertLoreAsync(IAsyncDocumentSession session, Lore lore)
    {
        if (string.IsNullOrWhiteSpace(lore.Id))
            throw new ArgumentException("Lore.Id is required for upsert.");

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
        }
        else
        {
            await session.StoreAsync(lore);
        }
    }

    public async Task<IEnumerable<Lore>> QueryLoreAsync(IAsyncDocumentSession session, string? query, string[]? tags, string? category, int limit = 5, string? campaignName = null)
    {
        var q = session.Advanced.AsyncDocumentQuery<Lore, Lore_Search>();
        if (!string.IsNullOrEmpty(query)) q = q.OpenSubclause().WhereEquals(x => x.Title, query).Fuzzy(0.4m).OrElse().WhereEquals(x => x.Content, query).Fuzzy(0.4m).CloseSubclause();
        if (tags != null && tags.Length > 0) { foreach (var tag in tags)
            {
                q = q.AndAlso().ContainsAny(x => x.Tags, new[] { tag });
            }
        }
        if (!string.IsNullOrEmpty(category)) q = q.AndAlso().WhereEquals(x => x.Category, category);
        return await q.Take(limit).ToListAsync();
    }

    public async Task UpsertLocationAsync(IAsyncDocumentSession session, Location location)
    {
        if (string.IsNullOrWhiteSpace(location.Id))
            throw new ArgumentException("Location.Id is required for upsert.");

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
            existing.Metadata = location.Metadata ?? [];
            existing.LastUpdated = location.LastUpdated;
        }
        else
        {
            await session.StoreAsync(location);
        }
    }

    public async Task<IEnumerable<Location>> QueryLocationsAsync(IAsyncDocumentSession session, string? query, LocationType? type = null, string? parentId = null, int limit = 10, string? campaignName = null)
    {
        var q = session.Advanced.AsyncDocumentQuery<Location, Location_Search>();
        if (!string.IsNullOrEmpty(query)) q = q.AndAlso().Search(x => x.Name, $"*{query}*").OrElse().Search(x => x.Description, $"*{query}*");
        if (type.HasValue) q = q.AndAlso().WhereEquals(x => x.Type, type.Value);
        if (!string.IsNullOrEmpty(parentId)) q = q.AndAlso().WhereEquals(x => x.ParentLocationId, parentId);
        var locations = await q.Take(limit).ToListAsync();
        foreach (var l in locations)
        {
            SanitizeLocation(l);
        }

        return locations;
    }

    public async Task UpsertRumorAsync(IAsyncDocumentSession session, Rumor rumor, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(rumor.Id))
            throw new ArgumentException("Rumor.Id is required for upsert.");

        var effective = ResolveCampaign(campaignName);
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
        }
        else
        {
            await session.StoreAsync(rumor);
        }
    }

    public async Task<IEnumerable<Rumor>> QueryRumorsAsync(IAsyncDocumentSession session, string? query, string? regionId = null, RumorState? state = null, int limit = 5, string? campaignName = null)
    {
        var q = session.Advanced.AsyncDocumentQuery<Rumor, Rumor_Search>();
        if (!string.IsNullOrEmpty(query)) q = q.AndAlso().Search(x => x.Subject, $"*{query}*").OrElse().Search(x => x.CurrentText, $"*{query}*");
        if (!string.IsNullOrEmpty(regionId)) q = q.AndAlso().WhereEquals(x => x.RegionLocationId, regionId);
        if (state.HasValue) q = q.AndAlso().WhereEquals(x => x.State, state.Value);
        return await q.Take(limit).ToListAsync();
    }

    public async Task<Location?> GetLocationAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        var loc = await session.LoadAsync<Location>(id);
        SanitizeLocation(loc);
        return loc;
    }

    public async Task<Item?> GetItemAsync(IAsyncDocumentSession session, string id, string? campaignName = null)
    {
        // campaignName accepted for API consistency / future entity namespacing.
        _ = ResolveCampaign(campaignName);
        return await session.LoadAsync<Item>(id);
    }

    public async Task UpsertItemAsync(IAsyncDocumentSession session, Item item)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
            throw new ArgumentException("Item.Id is required for upsert.");

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
        }
        else
        {
            await session.StoreAsync(item);
        }
    }
}
