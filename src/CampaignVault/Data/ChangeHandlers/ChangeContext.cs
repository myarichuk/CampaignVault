using System.Text.Json;
using CampaignVault.Data.Events;
using CampaignVault.Events;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Provides handlers with everything they need to apply a WorldChange without reaching into
/// the full CampaignRepository or performing their own pre-loading.
/// 
/// Pre-loaded entities are provided so handlers can work with tracked objects (preferred pattern)
/// instead of raw Patch operations.
/// </summary>
public sealed class ChangeContext : IChangeContext
{
    public IAsyncDocumentSession Session { get; }
    public IReadOnlyDictionary<string, Character> Characters => _characters;
    public IReadOnlyDictionary<string, Item> Items => _items;
    public IReadOnlyDictionary<string, Location> Locations => _locations;
    public IReadOnlyDictionary<string, Faction> Factions => _factions;
    public IReadOnlyDictionary<string, Quest> Quests => _quests;
    public ILogger Logger { get; }
    public CombatEncounter? ActiveCombat { get; }

    private readonly List<DomainEvent> _pendingEvents = [];

    /// <summary>Source prefix of whoever is running right now; set by the dispatcher around each handler call.</summary>
    internal string EventSource { get; set; } = CoreEvents.Source;

    /// <summary>Depth stamped on events published right now; the dispatcher raises it during event delivery.</summary>
    internal int EventDepth { get; set; }

    /// <summary>&gt; 0 while a ruleset_action's own side-effect mutations are being dispatched.</summary>
    internal int AutoApplyDepth { get; set; }

    /// <summary>
    /// Status effects added in this batch, keyed "characterId|name" → true when the engine added it
    /// (a ruleset_action side effect), false when the LLM sent it. Lets StatusChangeHandler collapse the
    /// same effect arriving from both origins without blocking deliberate stacking from one origin.
    /// </summary>
    internal Dictionary<string, bool> BatchStatusOrigins { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Publish(string topic, object? data = null)
    {
        if (!CoreEvents.IsOwnedBy(topic, EventSource))
        {
            Logger.LogWarning(
                "Rejected domain event '{Topic}' from '{Source}': a source may only publish under its own prefix '{Source}.'",
                topic, EventSource, EventSource);
            return;
        }

        DomainEvent domainEvent;
        try
        {
            domainEvent = DomainEvent.Create(topic, data) with { Source = EventSource, Depth = EventDepth };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            Logger.LogWarning(ex, "Rejected domain event '{Topic}' from '{Source}': payload is not JSON-serializable", topic, EventSource);
            return;
        }

        _pendingEvents.Add(domainEvent);
    }

    internal List<DomainEvent> TakePendingEvents()
    {
        var taken = _pendingEvents.ToList();
        _pendingEvents.Clear();
        return taken;
    }

    internal void DiscardPendingEvents() => _pendingEvents.Clear();

    /// <summary>Position in the pending-event queue; pass to <see cref="TruncatePendingEvents"/> to drop what came after.</summary>
    internal int PendingEventMark => _pendingEvents.Count;

    /// <summary>Drops events published after <paramref name="mark"/> (by a reaction that then faulted).</summary>
    internal void TruncatePendingEvents(int mark)
    {
        if (mark >= 0 && mark < _pendingEvents.Count)
        {
            _pendingEvents.RemoveRange(mark, _pendingEvents.Count - mark);
        }
    }

    /// <summary>Queues a core-sourced event directly (the dispatcher's own events, e.g. combat lifecycle).</summary>
    internal void EnqueueCoreEvent(string topic, object? data, int depth) =>
        _pendingEvents.Add(DomainEvent.Create(topic, data) with { Source = CoreEvents.Source, Depth = depth });

    /// <summary>
    /// plugin_faulted events wait here until normal delivery is done, then go out at depth 0 in a separate
    /// fault phase, so a fault near the depth cap still reaches its listeners.
    /// </summary>
    internal List<DomainEvent> PendingFaultEvents { get; } = [];

    /// <summary>True while fault events are delivered; faults raised then are reported but not republished.</summary>
    internal bool InFaultPhase { get; set; }

    /// <summary>Reaction faults this commit (isolated or not), for the take_turn response.</summary>
    internal List<PluginFault> PluginFaults { get; } = [];

    /// <summary>Follow-up changes domain-event subscribers applied this commit, in order.</summary>
    internal List<WorldChange> ReactionChanges { get; } = [];

    private readonly Dictionary<string, ModeEncounter> _activeModes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Most recently entered active mode (legacy single-mode view of <see cref="ActiveModes"/>).</summary>
    public ModeEncounter? ActiveMode { get; private set; }

    public IReadOnlyDictionary<string, ModeEncounter> ActiveModes => _activeModes;

    internal void EnterMode(ModeEncounter encounter)
    {
        _activeModes[encounter.ModeId] = encounter;
        ActiveMode = encounter;
    }

    internal void ExitMode(string modeId)
    {
        _activeModes.Remove(modeId);
        if (ActiveMode != null && string.Equals(ActiveMode.ModeId, modeId, StringComparison.OrdinalIgnoreCase))
        {
            ActiveMode = _activeModes.Values.FirstOrDefault();
        }
    }

    private void SeedActiveModes(ModeEncounter? activeMode, IEnumerable<ModeEncounter>? activeModes)
    {
        foreach (var encounter in activeModes ?? [])
        {
            _activeModes[encounter.ModeId] = encounter;
        }

        if (activeMode != null)
        {
            _activeModes[activeMode.ModeId] = activeMode;
        }

        ActiveMode = activeMode ?? _activeModes.Values.FirstOrDefault();
    }
    public CampaignConfig? Config { get; }

    /// <inheritdoc />
    public IRollService? Rolls { get; internal set; }

    /// <summary>
    /// The effective campaign name for this change context (for scoping entities like Characters/Locations on create).
    /// Propagated from dispatcher for create handlers to set CampaignName on new entities.
    /// </summary>
    public string? CampaignName { get; }
    public HashSet<string> InvolvedEntities { get; }

    /// <summary>
    /// The full commit batch and the current change's index within it, set by WorldChangeDispatcher
    /// before invoking each handler. Lets a handler peek ahead at later changes in the same batch (e.g.
    /// ItemEquipHandler's reorder nudge: "this batch also unequips the conflicting item later"). Null
    /// outside a real dispatch (e.g. isolated handler unit tests using the test constructor).
    /// </summary>
    public IReadOnlyList<WorldChange>? Batch { get; internal set; }

    /// <summary>Index of the change currently being handled within <see cref="Batch"/>.</summary>
    public int BatchIndex { get; internal set; }

    /// <summary>
    /// Resolves the current CampaignTime safely without binding directly to the session implementation.
    /// </summary>
    public Func<Task<CampaignTime>> GetCurrentTimeAsync { get; set; }

    /// <summary>
    /// Resolves the current Campaign SystemOptions.
    /// </summary>
    public Func<Task<Dictionary<string, string>>> GetSystemOptionsAsync { get; set; }

    /// <summary>
    /// Optional hook for handlers that need to persist events (used by EventOccurredHandler).
    /// The dispatcher supplies an implementation that performs sanitization + Store.
    /// </summary>
    public Func<Event, Task> LogEventAsync { get; }

    /// <summary>
    /// The dispatcher, allowing handlers to recursively dispatch child mutations.
    /// </summary>
    public WorldChangeDispatcher Dispatcher { get; }

    private readonly List<string> _summary;
    private readonly List<string> _physicalStateNudges;
    private readonly List<string> _entityCollisions = [];
    private readonly List<string> _committedIds = [];
    private int _failureCount;
    private readonly Dictionary<string, Character> _characters;
    private readonly Dictionary<string, Item> _items;
    private readonly Dictionary<string, Location> _locations;
    private readonly Dictionary<string, Faction> _factions;
    private readonly Dictionary<string, Quest> _quests;

    internal ChangeContext(
        IAsyncDocumentSession session,
        Dictionary<string, Character> characters,
        Dictionary<string, Item> items,
        Dictionary<string, Location> locations,
        Dictionary<string, Faction>? factions,
        Dictionary<string, Quest>? quests,
        ILogger logger,
        Func<Task<CampaignTime>> getCurrentTimeAsync,
        Func<Task<Dictionary<string, string>>> getSystemOptionsAsync,
        Func<Event, Task> logEventAsync,
        List<string> summary,
        WorldChangeDispatcher dispatcher,
        CombatEncounter? activeCombat = null,
        ModeEncounter? activeMode = null,
        string? campaignName = null,
        CampaignConfig? config = null,
        List<string>? physicalStateNudges = null,
        IEnumerable<ModeEncounter>? activeModes = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _characters = characters ?? throw new ArgumentNullException(nameof(characters));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        _factions = factions ?? new Dictionary<string, Faction>();
        _quests = quests ?? new Dictionary<string, Quest>();
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        GetCurrentTimeAsync = getCurrentTimeAsync ?? throw new ArgumentNullException(nameof(getCurrentTimeAsync));
        GetSystemOptionsAsync = getSystemOptionsAsync ?? throw new ArgumentNullException(nameof(getSystemOptionsAsync));
        LogEventAsync = logEventAsync ?? throw new ArgumentNullException(nameof(logEventAsync));
        _summary = summary ?? throw new ArgumentNullException(nameof(summary));
        _physicalStateNudges = physicalStateNudges ?? [];
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ActiveCombat = activeCombat;
        SeedActiveModes(activeMode, activeModes);
        CampaignName = campaignName;
        Config = config;
        InvolvedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }


    /// <summary>
    /// Test-only constructor that allows null session for dispatcher tests using fake handlers.
    /// </summary>
    internal ChangeContext(
        IAsyncDocumentSession? sessionForTests,
        Dictionary<string, Character> characters,
        Dictionary<string, Item> items,
        Dictionary<string, Location> locations,
        Dictionary<string, Faction>? factions,
        Dictionary<string, Quest>? quests,
        ILogger logger,
        List<string> summary,
        WorldChangeDispatcher dispatcher,
        CombatEncounter? activeCombat = null,
        ModeEncounter? activeMode = null,
        string? campaignName = null,
        CampaignConfig? config = null,
        List<string>? physicalStateNudges = null,
        IEnumerable<ModeEncounter>? activeModes = null)
    {
        Session = sessionForTests!;
        _characters = characters ?? throw new ArgumentNullException(nameof(characters));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        _factions = factions ?? new Dictionary<string, Faction>();
        _quests = quests ?? new Dictionary<string, Quest>();
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        GetCurrentTimeAsync = () => Task.FromResult(new CampaignTime());
        GetSystemOptionsAsync = () => Task.FromResult(new Dictionary<string, string>());
        LogEventAsync = _ => Task.CompletedTask;
        _summary = summary ?? throw new ArgumentNullException(nameof(summary));
        _physicalStateNudges = physicalStateNudges ?? [];
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ActiveCombat = activeCombat;
        SeedActiveModes(activeMode, activeModes);
        CampaignName = campaignName;
        Config = config;
        InvolvedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public void RegisterNewLocation(Location loc) => _locations[loc.Id] = loc;
    public void RegisterNewCharacter(Character c) => _characters[c.Id] = c;
    public void RegisterNewItem(Item i) => _items[i.Id] = i;
    public void RegisterNewFaction(Faction f) => _factions[f.Id] = f;
    public void RegisterNewQuest(Quest q) => _quests[q.Id] = q;

    /// <summary>
    /// Records a message that will appear in CommitResult.Summary.
    /// </summary>
    public void RecordMessage(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _summary.Add(message);
        }
    }

    /// <summary>
    /// Records a narration-facing reminder that a character's physical/visual state changed this
    /// turn (restraints, wounds, appearance/tags) — surfaced separately from <see cref="RecordMessage"/>
    /// so it stays salient (its own labeled field, not mixed into the generic commit log) even in a
    /// long session where earlier context is crowded with unrelated reference material. Phrase these
    /// as plain narrative facts ("Elara's wrists are no longer bound."), not technical log lines.
    /// </summary>
    public void RecordPhysicalStateNudge(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _physicalStateNudges.Add(message);
        }
    }

    internal IReadOnlyList<string> PhysicalStateNudges => _physicalStateNudges;

    /// <summary>
    /// Marks that at least one change in the batch failed or produced a warning.
    /// This contributes to CommitResult.Success = false.
    /// </summary>
    public void RecordFailure()
    {
        _failureCount++;
    }

    internal bool HasFailure => _failureCount > 0;

    /// <summary>Failures recorded so far; a reaction compares before/after to detect its own failure.</summary>
    internal int FailureCount => _failureCount;

    /// <summary>Forgets failures recorded after <paramref name="count"/> (an isolated reaction fault).</summary>
    internal void ResetFailureCount(int count) => _failureCount = Math.Min(_failureCount, Math.Max(0, count));

    /// <summary>
    /// Records that a create-style change (e.g. character_create) resolved to an ID that already
    /// existed and was merged into the existing document instead of creating a new one. Surfaced
    /// structurally via CommitResult.EntityCollisions (in addition to the human-readable
    /// RecordMessage entry) so a caller can detect this without string-matching Summary.
    /// </summary>
    public void RecordEntityCollision(string entityId, string message)
    {
        if (!string.IsNullOrWhiteSpace(entityId))
        {
            _entityCollisions.Add(entityId);
        }

        RecordMessage(message);
    }

    internal IReadOnlyList<string> EntityCollisions => _entityCollisions;

    /// <summary>
    /// Records an entity ID this change durably created or resolved (e.g. an auto-generated event ID)
    /// so the caller can reference it later (sourceEventIds, a follow-up commit) without parsing it back
    /// out of a human-readable Summary line. Surfaced via CommitResult.CommittedIds.
    /// </summary>
    public void RecordCommittedId(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            _committedIds.Add(id);
        }
    }

    internal IReadOnlyList<string> CommittedIds => _committedIds;

    public async Task<string?> SuggestLocationMatchAsync(string? nameQuery)
    {
        if (Session == null || string.IsNullOrWhiteSpace(nameQuery))
        {
            return null;
        }

        var cleanQuery = nameQuery;
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
            return null;
        }

        var suggestions = await Session.Query<Location, Location_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == CampaignName || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(nameQuery))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await Session.Query<Location, Location_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == CampaignName || x.CampaignName == null)
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

        if (suggestions.Any())
        {
            return string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Name})"));
        }

        return null;
    }

    public async Task<string?> SuggestCharacterMatchAsync(string? nameQuery)
    {
        if (Session == null || string.IsNullOrWhiteSpace(nameQuery))
        {
            return null;
        }

        var normalizedQuery = CanonicalId.NormalizeAlias(nameQuery);
        var cleanQuery = normalizedQuery;
        if (cleanQuery.StartsWith("chars/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["chars/".Length..];
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return null;
        }

        var suggestions = await Session.Query<Character, Character_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == CampaignName || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(normalizedQuery))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await Session.Query<Character, Character_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == CampaignName || x.CampaignName == null)
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

        if (suggestions.Any())
        {
            return string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Name})"));
        }

        return null;
    }

    public async Task<string?> SuggestItemMatchAsync(string? nameQuery)
    {
        if (Session == null || string.IsNullOrWhiteSpace(nameQuery))
        {
            return null;
        }

        var cleanQuery = nameQuery;
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
            return null;
        }

        var suggestions = await Session.Query<Item, Item_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == CampaignName || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(nameQuery))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await Session.Query<Item, Item_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == CampaignName || x.CampaignName == null)
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

        if (suggestions.Any())
        {
            return string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Name})"));
        }

        return null;
    }
    public async Task<string?> SuggestFactionMatchAsync(string? nameQuery)
    {
        if (Session == null || string.IsNullOrWhiteSpace(nameQuery))
        {
            return null;
        }

        var cleanQuery = nameQuery;
        if (cleanQuery.StartsWith("factions/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["factions/".Length..];
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return null;
        }

        var suggestions = await Session.Query<Faction, Faction_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == CampaignName || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(nameQuery))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await Session.Query<Faction, Faction_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == CampaignName || x.CampaignName == null)
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

        if (suggestions.Any())
        {
            return string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Name})"));
        }

        return null;
    }

    public async Task<string?> SuggestQuestMatchAsync(string? nameQuery)
    {
        if (Session == null || string.IsNullOrWhiteSpace(nameQuery))
        {
            return null;
        }

        var cleanQuery = nameQuery;
        if (cleanQuery.StartsWith("quests/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["quests/".Length..];
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return null;
        }

        var suggestions = await Session.Query<Quest, Quest_Search>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(x => x.CampaignName == CampaignName || x.CampaignName == null)
            .Where(x => x.Id.StartsWith(nameQuery))
            .Take(3).ToListAsync();

        if (suggestions.Count < 3)
        {
            var byName = await Session.Query<Quest, Quest_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == CampaignName || x.CampaignName == null)
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

        if (suggestions.Any())
        {
            return string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Title})"));
        }

        return null;
    }
}