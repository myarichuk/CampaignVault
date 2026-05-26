using Raven.Client.Documents;
using CampaignVault.Models;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class CampaignRepository
{
    private readonly IDocumentStore _store;
    private readonly WorldSimulator _simulator = new();

    public CampaignRepository(IDocumentStore store)
    {
        _store = store;
    }

    public IAsyncDocumentSession OpenSession()
    {
        var session = _store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;
        return session;
    }

    public async Task<CommitResult> CommitChangesAsync(IAsyncDocumentSession session, WorldChange[] changes)
    {
        var summary = new List<string>();
        
        // 1. Pre-identify and batch-load all required entities to minimize round-trips
        var characterIds = new HashSet<string>();
        var itemIds = new HashSet<string>();

        foreach (var change in changes)
        {
            if (change is ItemTransfer it) itemIds.Add(it.ItemId);
            if (change is RelationshipChange rc) characterIds.Add(rc.SourceId);
            if (change is NeedChange nc) characterIds.Add(nc.CharacterId);
            if (change is AttributeChange ac) characterIds.Add(ac.CharacterId);
        }

        var characters = await session.LoadAsync<Character>(characterIds);
        var items = await session.LoadAsync<Item>(itemIds);

        // 2. Process changes using loaded entities or atomic patches
        foreach (var change in changes)
        {
            switch (change)
            {
                case HpChange hp:
                    session.Advanced.Increment<Character, int>(hp.CharacterId, x => x.CurrentHp, hp.Delta);
                    summary.Add($"HP adjusted for {hp.CharacterId} by {hp.Delta}");
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
                    var e = new Event { Id = "events/" + Guid.NewGuid(), Summary = ev.Summary, Type = ev.Type, Involved = ev.Involved ?? [], DayLogged = currentTime.TotalDaysElapsed };
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
                    break;

                case AttributeChange attr:
                    if (characters.TryGetValue(attr.CharacterId, out var attrChar) && attrChar?.Mind != null)
                    {
                        switch (attr.Attribute.ToLowerInvariant())
                        {
                            case "willpower": attrChar.Mind.Willpower = Math.Clamp(attr.Value, 0f, 100f); break;
                            case "temperature": attrChar.Mind.Temperature = Math.Clamp(attr.Value, -50f, 100f); break;
                            case "morale": attrChar.Mind.Morale = Math.Clamp(attr.Value, 0f, 100f); break;
                        }
                        summary.Add($"Attribute '{attr.Attribute}' set for {attr.CharacterId}");
                    }
                    break;

                default:
                    summary.Add($"WARNING: Unhandled change type: {change?.GetType().Name}");
                    break;
            }
        }
        return new CommitResult { ChangesProcessed = changes.Length, Summary = summary };
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

        var npcs = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .ContainsAny("Locations", targetIds)
            .Take(20)
            .ToListAsync();

        var rumors = await QueryRumorsAsync(session, null, regionId, null, 5);
        var items = await session.Query<Item>().Where(x => x.HolderId == locationId).ToListAsync();
        var events = await session.Query<Event>().Where(x => x.Involved.Contains(locationId)).OrderByDescending(x => x.Timestamp).Take(5).ToListAsync();

        var time = await GetTimeAsync(session);

        return new SceneView
        {
            Location = location,
            PresentNPCs = npcs, // Return full objects
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
        time.Day += days;
        while (time.Day > 30) { time.Day -= 30; time.Month++; }
        while (time.Month > 12) { time.Month -= 12; time.Year++; }
        time.TimeOfDay = timeOfDay;

        await session.StoreAsync(time);

        var activeRumors = await session.Query<Rumor>().Where(x => x.State != RumorState.Resolved).ToListAsync();
        var npcs = await session.Query<Character>().Where(x => x.Schedule != null).ToListAsync();
        
        // Use the time-aware simulator
        var simEvents = _simulator.Run(time, activeRumors, npcs, days);

        // Persist simulator events to the global event history
        foreach (var narrative in simEvents)
        {
            await LogEventAsync(session, new Event 
            { 
                Id = "events/" + Guid.NewGuid(), 
                Summary = narrative, 
                Type = "simulation",
                DayLogged = time.TotalDaysElapsed 
            });
        }

        // RavenDB change tracking handles the mutations on npcs and rumors automatically
        // as they were loaded via the session. No explicit StoreAsync loops needed.

        return new AdvanceResult { NewTime = time, SimulatorEvents = simEvents };
    }

    // --- Search & Recall ---

    public async Task<IEnumerable<object>> UnifiedSearchAsync(IAsyncDocumentSession session, string query)
    {
        var charsTask = session.Advanced.AsyncDocumentQuery<Character, Character_Search>().Search(x => x.Name, $"*{query}*").Take(5).ToListAsync();
        var loreTask = session.Advanced.AsyncDocumentQuery<Lore, Lore_Search>().Search(x => x.Title, $"*{query}*").Take(5).ToListAsync();
        var locsTask = session.Advanced.AsyncDocumentQuery<Location, Location_Search>().Search(x => x.Name, $"*{query}*").Take(5).ToListAsync();
        
        await Task.WhenAll(charsTask, loreTask, locsTask);

        // Crucial: Extract results while inside the session scope
        var chars = await charsTask;
        var lore = await loreTask;
        var locs = await locsTask;

        var results = new List<object>();
        results.AddRange(chars);
        results.AddRange(lore);
        results.AddRange(locs);
        return results;
    }

    public async Task<IEnumerable<Event>> QueryEventsAsync(IAsyncDocumentSession session, string? query, string? type, int limit = 10)
    {
        var q = session.Advanced.AsyncDocumentQuery<Event, Event_Search>();
        if (!string.IsNullOrEmpty(query)) q = q.AndAlso().Search(x => x.Summary, $"*{query}*");
        if (!string.IsNullOrEmpty(type)) q = q.AndAlso().WhereEquals(x => x.Type, type);
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
        character.LastUpdated = DateTime.UtcNow;
        await session.StoreAsync(character, null, character.Id);
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

    public async Task LogEventAsync(IAsyncDocumentSession session, Event @event)
    {
        if (@event.Details != null) @event.Details = SanitizeDetails(@event.Details);
        await session.StoreAsync(@event);
    }

    private IDictionary<string, object> SanitizeDetails(IDictionary<string, object> details)
    {
        var sanitized = new Dictionary<string, object>();
        foreach (var (key, value) in details) sanitized[key] = SanitizeValue(value);
        return sanitized;
    }

    private object SanitizeValue(object value)
    {
        if (value is System.Text.Json.JsonElement je)
        {
            switch (je.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String: return je.GetString()!;
                case System.Text.Json.JsonValueKind.Number: if (je.TryGetInt32(out var i)) return i; return je.GetDouble();
                case System.Text.Json.JsonValueKind.True: return true;
                case System.Text.Json.JsonValueKind.False: return false;
                case System.Text.Json.JsonValueKind.Null: return null!;
                case System.Text.Json.JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in je.EnumerateObject()) dict[prop.Name] = SanitizeValue(prop.Value);
                    return dict;
                case System.Text.Json.JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var item in je.EnumerateArray()) list.Add(SanitizeValue(item));
                    return list;
                default: return je.GetRawText();
            }
        }
        return value;
    }

    /// <summary>
    /// Applies JSON sanitization to an Event's Details (prevents JsonElement leakage).
    /// Used by QueryEventsAsync, LogEventAsync, and GetNpcContext.
    /// </summary>
    internal void SanitizeEvent(Event ev)
    {
        if (ev.Details != null) ev.Details = SanitizeDetails(ev.Details);
    }

    public async Task UpsertLoreAsync(IAsyncDocumentSession session, Lore lore) { lore.LastUpdated = DateTime.UtcNow; await session.StoreAsync(lore); }

    public async Task<IEnumerable<Lore>> QueryLoreAsync(IAsyncDocumentSession session, string? query, string[]? tags, string? category, int limit = 5)
    {
        var q = session.Advanced.AsyncDocumentQuery<Lore, Lore_Search>();
        if (!string.IsNullOrEmpty(query)) q = q.OpenSubclause().WhereEquals(x => x.Title, query).Fuzzy(0.4m).OrElse().WhereEquals(x => x.Content, query).Fuzzy(0.4m).CloseSubclause();
        if (tags != null && tags.Length > 0) { foreach (var tag in tags) q = q.AndAlso().ContainsAny(x => x.Tags, new[] { tag }); }
        if (!string.IsNullOrEmpty(category)) q = q.AndAlso().WhereEquals(x => x.Category, category);
        return await q.Take(limit).ToListAsync();
    }

    public async Task UpsertLocationAsync(IAsyncDocumentSession session, Location location) { location.LastUpdated = DateTime.UtcNow; await session.StoreAsync(location); }

    public async Task<IEnumerable<Location>> QueryLocationsAsync(IAsyncDocumentSession session, string? query, LocationType? type = null, string? parentId = null, int limit = 10)
    {
        var q = session.Advanced.AsyncDocumentQuery<Location, Location_Search>();
        if (!string.IsNullOrEmpty(query)) q = q.AndAlso().Search(x => x.Name, $"*{query}*").OrElse().Search(x => x.Description, $"*{query}*");
        if (type.HasValue) q = q.AndAlso().WhereEquals(x => x.Type, type.Value);
        if (!string.IsNullOrEmpty(parentId)) q = q.AndAlso().WhereEquals(x => x.ParentLocationId, parentId);
        return await q.Take(limit).ToListAsync();
    }

    public async Task UpsertRumorAsync(IAsyncDocumentSession session, Rumor rumor)
    {
        rumor.LastUpdated = DateTime.UtcNow;
        if (rumor.DayCreated == 0) { var t = await GetTimeAsync(session); rumor.DayCreated = t.TotalDaysElapsed; rumor.LastStateChangeDay = t.TotalDaysElapsed; }
        await session.StoreAsync(rumor);
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
        return await session.LoadAsync<Location>(id);
    }

    public async Task<Item?> GetItemAsync(IAsyncDocumentSession session, string id) => await session.LoadAsync<Item>(id);

    public async Task UpsertItemAsync(IAsyncDocumentSession session, Item item) { item.LastUpdated = DateTime.UtcNow; await session.StoreAsync(item); }
}
