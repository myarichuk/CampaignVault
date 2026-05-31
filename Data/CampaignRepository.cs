using Raven.Client.Documents;
using CampaignVault.Models;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Indexes;
using Microsoft.Extensions.Logging;

namespace CampaignVault.Data;

public class CampaignRepository
{
    private readonly IDocumentStore _store;
    private readonly IWorldSimulationEngine _simulationEngine;
    private readonly ILogger<CampaignRepository> _logger;
    private readonly INpcBehaviorSynthesizer _behaviorSynthesizer;

    public CampaignRepository(
        IDocumentStore store, 
        IWorldSimulationEngine simulationEngine,
        ILogger<CampaignRepository> logger,
        INpcBehaviorSynthesizer behaviorSynthesizer)
    {
        _store = store;
        _simulationEngine = simulationEngine;
        _logger = logger;
        _behaviorSynthesizer = behaviorSynthesizer;
    }

    /// <summary>
    /// Convenience constructor primarily for test scenarios.
    /// In production, always use the two-parameter constructor via DI so the real simulation engine is injected.
    /// </summary>
    public CampaignRepository(IDocumentStore store)
        : this(store, 
               new NoOpSimulationEngine(), 
               Microsoft.Extensions.Logging.Abstractions.NullLogger<CampaignRepository>.Instance,
               new DefaultBehaviorSynthesizer())
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
    public async Task<CommitResult> StageChangesAsync(IAsyncDocumentSession session, WorldChange[] changes)
    {
        changes ??= Array.Empty<WorldChange>();
        _logger.LogDebug("StageChangesAsync called with {ChangeCount} changes", changes.Length);

        var summary = new List<string>();
        bool success = true;
        
        // 1. Pre-identify and batch-load all required entities to minimize round-trips
        var characterIds = new HashSet<string>();
        var itemIds = new HashSet<string>();

        foreach (var change in changes)
        {
            if (change is ItemTransfer it) itemIds.Add(it.ItemId);
            if (change is RelationshipChange rc) characterIds.Add(rc.SourceId);
            if (change is NeedChange nc) characterIds.Add(nc.CharacterId);
            if (change is AttributeChange ac) characterIds.Add(ac.CharacterId);
            if (change is ActivityChange act) characterIds.Add(act.CharacterId);
            if (change is MoodChange mc) characterIds.Add(mc.CharacterId);
            if (change is HpChange hc) characterIds.Add(hc.CharacterId);
        }

        var characters = await session.LoadAsync<Character>(characterIds);
        var items = await session.LoadAsync<Item>(itemIds);

        // 2. Process changes using loaded entities or atomic patches
        foreach (var change in changes)
        {
            try
            {
                switch (change)
                {
                    case HpChange hp:
                        if (characters.TryGetValue(hp.CharacterId, out var hpChar) && hpChar != null)
                        {
                            if (hpChar.MaxHp <= 0)
                            {
                                // MaxHp of 0 usually means the entity was created with default values
                                // (e.g. a location placeholder, faction token, or test fixture).
                                // Clamping to int.MaxValue would silently make the entity immortal.
                                // Skip and surface a clear warning instead.
                                _logger.LogWarning("HpChange skipped for {CharacterId}: MaxHp is {MaxHp} (not a combatant?)", hp.CharacterId, hpChar.MaxHp);
                                summary.Add($"WARNING: HpChange skipped for {hp.CharacterId} — MaxHp is {hpChar.MaxHp}. Set MaxHp > 0 to enable HP tracking.");
                                success = false;
                            }
                            else
                            {
                                hpChar.CurrentHp = Math.Clamp(hpChar.CurrentHp + hp.Delta, 0, hpChar.MaxHp);
                                summary.Add($"HP adjusted for {hp.CharacterId} by {hp.Delta} (now {hpChar.CurrentHp}/{hpChar.MaxHp})");
                            }
                        }
                        else
                        {
                            summary.Add($"WARNING: Character {hp.CharacterId} not found during HpChange.");
                            success = false;
                        }
                        break;

                    case ItemTransfer item:
                        if (items.TryGetValue(item.ItemId, out var itemDoc) && itemDoc != null)
                        {
                            itemDoc.HolderId = item.ToHolderId;
                            itemDoc.LastUpdated = DateTime.UtcNow;
                            summary.Add($"Item {item.ItemId} moved to {item.ToHolderId}");
                        }
                        break;

                    case StatusChange status:
                        session.Advanced.Patch<Character, string>(status.CharacterId, x => x.Status, x => x.Add(status.Status));
                        summary.Add($"Status '{status.Status}' added to {status.CharacterId}");
                        break;

                    case EventOccurred ev:
                        var currentTime = await GetTimeAsync(session);
                        var e = new Event { Id = "events/" + Guid.NewGuid(), Summary = ev.Summary, Category = ev.Category, Involved = ev.Involved ?? [], DayLogged = currentTime.TotalDaysElapsed };
                        await LogEventAsync(session, e);
                        summary.Add($"Event logged: {ev.Summary}");
                        break;

                    case RumorEvolves rumor:
                        session.Advanced.Patch<Rumor, RumorState>(rumor.RumorId, x => x.State, rumor.NewState);
                        if (rumor.NewText != null) session.Advanced.Patch<Rumor, string>(rumor.RumorId, x => x.CurrentText, rumor.NewText);
                        var rtime = await GetTimeAsync(session);
                        session.Advanced.Patch<Rumor, int>(rumor.RumorId, x => x.LastStateChangeDay, rtime.TotalDaysElapsed);
                        summary.Add($"Rumor {rumor.RumorId} evolved to {rumor.NewState}");
                        break;

                    case RelationshipChange rel:
                        if (characters.TryGetValue(rel.SourceId, out var source) && source != null)
                        {
                            source.Mind ??= new NpcMind();
                            source.Mind.Relationships ??= new Dictionary<string, int>();
                            
                            var currentVal = source.Mind.Relationships.GetValueOrDefault(rel.TargetId, 0);
                            source.Mind.Relationships[rel.TargetId] = Math.Clamp(currentVal + rel.Delta, -100, 100);
                            
                            summary.Add($"Relationship from {rel.SourceId} to {rel.TargetId} shifted by {rel.Delta} ({rel.Reason})");
                        }
                        break;

                    case NeedChange nc:
                        if (characters.TryGetValue(nc.CharacterId, out var needChar) && needChar?.Mind != null)
                        {
                            var current = needChar.Mind.Needs.GetValueOrDefault(nc.Need, 0f);
                            needChar.Mind.Needs[nc.Need] = Math.Clamp(current + nc.Delta, 0f, 100f);
                            summary.Add($"Need '{nc.Need}' adjusted for {nc.CharacterId} by {nc.Delta}");
                        }
                        else
                        {
                            summary.Add($"WARNING: Character {nc.CharacterId} not found or has no Mind during NeedChange.");
                            success = false;
                        }
                        break;

                    case AttributeChange attr:
                        if (characters.TryGetValue(attr.CharacterId, out var attrChar) && attrChar?.Mind != null)
                        {
                            var key = attr.Attribute.ToLowerInvariant();
                            switch (key)
                            {
                                case "willpower":
                                    attrChar.Mind.Willpower = Math.Clamp(attr.IsDelta ? attrChar.Mind.Willpower + attr.Value : attr.Value, 0f, 100f);
                                    break;
                                case "temperature":
                                    attrChar.Mind.Temperature = Math.Clamp(attr.IsDelta ? attrChar.Mind.Temperature + attr.Value : attr.Value, -50f, 100f);
                                    break;
                                case "morale":
                                    attrChar.Mind.Morale = Math.Clamp(attr.IsDelta ? attrChar.Mind.Morale + attr.Value : attr.Value, 0f, 100f);
                                    break;
                                default:
                                    // Open custom attribute (matches the documented "arbitrary" design in AttributeChange)
                                    var current = attrChar.Mind.Attributes.GetValueOrDefault(key, 0f);
                                    attrChar.Mind.Attributes[key] = Math.Clamp(attr.IsDelta ? current + attr.Value : attr.Value, 0f, 100f);
                                    break;
                            }
                            summary.Add($"Attribute '{attr.Attribute}' set for {attr.CharacterId}");
                        }
                        break;

                    case MoodChange mood:
                        if (characters.TryGetValue(mood.CharacterId, out var moodChar) && moodChar?.Mind != null)
                        {
                            moodChar.Mind.CurrentMood = mood.NewMood;
                            summary.Add($"Mood set to '{mood.NewMood}' for {mood.CharacterId}");
                        }
                        else
                        {
                            summary.Add($"WARNING: Character {mood.CharacterId} not found or has no Mind during MoodChange.");
                            success = false;
                        }
                        break;

                    case ActivityChange act:
                        if (characters.TryGetValue(act.CharacterId, out var actChar) && actChar != null)
                        {
                            if (act.NewActivity != null)
                                actChar.CurrentActivity = act.NewActivity;
                            if (act.NewLocationId != null)
                                actChar.CurrentLocationId = act.NewLocationId;
                            summary.Add($"Activity updated for {act.CharacterId}: {act.NewActivity ?? "(unchanged)"} @ {act.NewLocationId ?? "(unchanged)"}");
                        }
                        else
                        {
                            summary.Add($"WARNING: Character {act.CharacterId} not found during ActivityChange.");
                            success = false;
                        }
                        break;

                    default:
                        summary.Add($"WARNING: Unhandled change type: {change?.GetType().Name}");
                        success = false;
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing change of type {ChangeType}", change?.GetType().Name);
                summary.Add($"ERROR: Failed to process {change?.GetType().Name}: {ex.Message}");
                success = false;
            }
        }

        _logger.LogInformation("Commit applied {Processed} changes", changes.Length);
        return new CommitResult { Success = success, ChangesProcessed = changes.Length, Summary = summary };
    }

    // --- V4 Core: Scene Synthesis ---

    public async Task<SceneView> GetSceneAsync(IAsyncDocumentSession session, string locationId)
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

        var regionId = location.ParentLocationId ?? locationId;
        var subLocations = (await QueryLocationsAsync(session, null, null, locationId, 20)).ToList();
        
        var targetIds = new List<string> { locationId };
        targetIds.AddRange(subLocations.Select(l => l.Id));

        // Primary discovery via static schedule index (good for cold starts / world building)
        var npcsFromIndex = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .ContainsAny("Locations", targetIds)
            .Take(20)
            .ToListAsync();

        // Fallback: if the index hasn't caught up (common in fast tests), load recent characters and filter client-side
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

        var rumors = await QueryRumorsAsync(session, null, regionId, null, 5);
        var items = await session.Query<Item>().Where(x => x.HolderId == locationId).ToListAsync();
        foreach (var it in items) JsonSanitizer.Sanitize(it);

        JsonSanitizer.Sanitize(location);

        var events = (await QueryEventsAsync(session, null, null, 5))
            .Where(e => e.Involved.Contains(locationId))
            .OrderByDescending(e => e.Timestamp)
            .Take(5)
            .ToList();

        var time = await GetTimeAsync(session);

        // Load global descriptors once (cheap) so we can merge them into every NPC's view
        var globalDescriptors = await GetGlobalNeedDescriptorsAsync(session);

        // Project to lightweight presence summaries + behavioral synthesis.
        // This fulfills the V4 goal of giving the DM synthesized insight instead of raw data.
        var presenceSummaries = npcs.Select(npc =>
        {
            var mind = npc.Mind ?? new NpcMind();

            // Take the top 3 highest needs for a compact view (sorted descending)
            var topNeeds = mind.Needs
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            // Expose all known needs + descriptors (merged global + per-NPC, per-NPC wins)
            var knownNeeds = mind.Needs.ToDictionary(kv => kv.Key, kv => kv.Value);
            var needDescriptors = new Dictionary<string, string>(globalDescriptors, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in mind.NeedDescriptors ?? new Dictionary<string, string>())
                needDescriptors[kv.Key] = kv.Value;

            // Generate behavioral summary using the injected synthesizer
            var behavioralSummary = _behaviorSynthesizer.GenerateSummary(npc, time, events);

            return new NpcPresenceSummary(
                Id: npc.Id,
                Name: npc.Name,
                CurrentActivity: npc.CurrentActivity ?? npc.Schedule?.DefaultLocationId,
                CurrentMood: mind.CurrentMood,
                TopNeeds: topNeeds,
                KnownNeeds: knownNeeds,
                NeedDescriptors: needDescriptors,
                BehavioralSummary: behavioralSummary,
                Notes: npc.Notes
            );
        }).ToList();

        return new SceneView
        {
            Location = location,
            PresentNPCs = presenceSummaries,
            LocalRumors = rumors.Select(r => new RumorSummary(r.Subject, r.CurrentText, r.State)),
            VisibleItems = items,
            RecentEvents = events
        };
    }

    // --- Time & Simulator ---

    public async Task<AdvanceResult> AdvanceWorldAsync(IAsyncDocumentSession session, int days, TimeOfDay timeOfDay)
    {
        var time = await GetTimeAsync(session);

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

        var activeRumors = await session.Query<Rumor>()
            .Where(x => x.State != RumorState.Resolved && x.State != RumorState.Forgotten)
            .ToListAsync();
        var npcs = await session.Query<Character>().Where(x => x.Schedule != null).ToListAsync();

        // Build context and run the pluggable simulation engine (rules emit deltas)
        var simContext = new SimulationContext(time, activeRumors, npcs, session, days);

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
            });
        }

        // Apply any deltas produced by simulation rules through the unified Commit path.
        // This gives us clamping, optimistic concurrency, summary logging, etc. for free.
        if (simResult.Deltas.Count > 0)
        {
            _logger.LogDebug("Applying {DeltaCount} simulation deltas", simResult.Deltas.Count);
            await StageChangesAsync(session, simResult.Deltas.ToArray());
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

    public async Task<IEnumerable<object>> UnifiedSearchAsync(IAsyncDocumentSession session, string query)
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
        foreach (var l in locs) SanitizeLocation(l);

        var results = new List<object>();
        results.AddRange(chars);
        results.AddRange(lore);
        results.AddRange(locs);
        return results;
    }

    public async Task<IEnumerable<Event>> QueryEventsAsync(IAsyncDocumentSession session, string? query, EventCategory? category, int limit = 10)
    {
        var q = session.Advanced.AsyncDocumentQuery<Event, Event_Search>();
        if (!string.IsNullOrEmpty(query)) q = q.AndAlso().Search(x => x.Summary, $"*{query}*");
        if (category.HasValue) q = q.AndAlso().WhereEquals(x => x.Category, category.Value);
        var events = await q.OrderByDescending(x => x.Timestamp).Take(limit).ToListAsync();
        foreach (var ev in events) { if (ev.Details != null) ev.Details = SanitizeDetails(ev.Details); }
        return events;
    }

    // --- Base Helpers ---

    public async Task<Character?> GetCharacterAsync(IAsyncDocumentSession session, string identifier)
    {
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
            existing.Status = character.Status ?? [];
            existing.Notes = character.Notes;
            existing.Schedule = character.Schedule;
            existing.CurrentLocationId = character.CurrentLocationId;
            existing.CurrentActivity = character.CurrentActivity;
            existing.Mind = character.Mind ?? new NpcMind();
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

    public async Task<CampaignTime> GetTimeAsync(IAsyncDocumentSession session)
    {
        var time = await session.LoadAsync<CampaignTime>("state/time");
        if (time == null) { time = new CampaignTime(); await session.StoreAsync(time); }
        return time;
    }

    public async Task SaveTimeAsync(IAsyncDocumentSession session, CampaignTime time)
    {
        time.LastUpdated = DateTime.UtcNow;
        await session.StoreAsync(time);
    }

    /// <summary>
    /// Returns globally defined need descriptors (populated via the DefineNeedDescriptor tool).
    /// These act as a shared dictionary that individual NPCs can reference or override via Mind.NeedDescriptors.
    /// </summary>
    public async Task<Dictionary<string, string>> GetGlobalNeedDescriptorsAsync(IAsyncDocumentSession session)
    {
        const string docId = "config/need-descriptors";
        var config = await session.LoadAsync<NeedDescriptorsConfig>(docId);
        var source = config?.Descriptors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sets or updates a single global need descriptor. Creates the config document if it does not exist.
    /// </summary>
    public async Task SetNeedDescriptorAsync(IAsyncDocumentSession session, string needName, string descriptor)
    {
        const string docId = "config/need-descriptors";
        var config = await session.LoadAsync<NeedDescriptorsConfig>(docId) ?? new NeedDescriptorsConfig { Id = docId };
        config.Descriptors[needName.Trim()] = descriptor.Trim();
        await session.StoreAsync(config, docId);
    }

    public async Task LogEventAsync(IAsyncDocumentSession session, Event @event)
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

    public async Task<IEnumerable<Lore>> QueryLoreAsync(IAsyncDocumentSession session, string? query, string[]? tags, string? category, int limit = 5)
    {
        var q = session.Advanced.AsyncDocumentQuery<Lore, Lore_Search>();
        if (!string.IsNullOrEmpty(query)) q = q.OpenSubclause().WhereEquals(x => x.Title, query).Fuzzy(0.4m).OrElse().WhereEquals(x => x.Content, query).Fuzzy(0.4m).CloseSubclause();
        if (tags != null && tags.Length > 0) { foreach (var tag in tags) q = q.AndAlso().ContainsAny(x => x.Tags, new[] { tag }); }
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

    public async Task<IEnumerable<Location>> QueryLocationsAsync(IAsyncDocumentSession session, string? query, LocationType? type = null, string? parentId = null, int limit = 10)
    {
        var q = session.Advanced.AsyncDocumentQuery<Location, Location_Search>();
        if (!string.IsNullOrEmpty(query)) q = q.AndAlso().Search(x => x.Name, $"*{query}*").OrElse().Search(x => x.Description, $"*{query}*");
        if (type.HasValue) q = q.AndAlso().WhereEquals(x => x.Type, type.Value);
        if (!string.IsNullOrEmpty(parentId)) q = q.AndAlso().WhereEquals(x => x.ParentLocationId, parentId);
        var locations = await q.Take(limit).ToListAsync();
        foreach (var l in locations) SanitizeLocation(l);
        return locations;
    }

    public async Task UpsertRumorAsync(IAsyncDocumentSession session, Rumor rumor)
    {
        if (string.IsNullOrWhiteSpace(rumor.Id))
            throw new ArgumentException("Rumor.Id is required for upsert.");

        rumor.LastUpdated = DateTime.UtcNow;
        if (rumor.DayCreated == 0)
        {
            var t = await GetTimeAsync(session);
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

    public async Task<IEnumerable<Rumor>> QueryRumorsAsync(IAsyncDocumentSession session, string? query, string? regionId = null, RumorState? state = null, int limit = 5)
    {
        var q = session.Advanced.AsyncDocumentQuery<Rumor, Rumor_Search>();
        if (!string.IsNullOrEmpty(query)) q = q.AndAlso().Search(x => x.Subject, $"*{query}*").OrElse().Search(x => x.CurrentText, $"*{query}*");
        if (!string.IsNullOrEmpty(regionId)) q = q.AndAlso().WhereEquals(x => x.RegionLocationId, regionId);
        if (state.HasValue) q = q.AndAlso().WhereEquals(x => x.State, state.Value);
        return await q.Take(limit).ToListAsync();
    }

    public async Task<Location?> GetLocationAsync(IAsyncDocumentSession session, string id)
    {
        var loc = await session.LoadAsync<Location>(id);
        SanitizeLocation(loc);
        return loc;
    }

    public async Task<Item?> GetItemAsync(IAsyncDocumentSession session, string id) => await session.LoadAsync<Item>(id);

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
