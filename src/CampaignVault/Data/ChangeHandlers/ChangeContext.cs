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
public sealed class ChangeContext
{
    public IAsyncDocumentSession Session { get; }
    public IReadOnlyDictionary<string, Character> Characters => _characters;
    public IReadOnlyDictionary<string, Item> Items => _items;
    public IReadOnlyDictionary<string, Location> Locations => _locations;
    public IReadOnlyDictionary<string, Faction> Factions => _factions;
    public IReadOnlyDictionary<string, Quest> Quests => _quests;
    public ILogger Logger { get; }
    public CombatEncounter? ActiveCombat { get; }
    public CampaignConfig? Config { get; }

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
    private readonly List<string> _entityCollisions = [];
    private bool _hasFailure;
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
        string? campaignName = null,
        CampaignConfig? config = null)
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
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ActiveCombat = activeCombat;
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
        string? campaignName = null,
        CampaignConfig? config = null)
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
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ActiveCombat = activeCombat;
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
    /// Marks that at least one change in the batch failed or produced a warning.
    /// This contributes to CommitResult.Success = false.
    /// </summary>
    public void RecordFailure()
    {
        _hasFailure = true;
    }

    internal bool HasFailure => _hasFailure;

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