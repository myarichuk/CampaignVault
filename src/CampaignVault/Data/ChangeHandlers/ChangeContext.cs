using CampaignVault.Models;
using Microsoft.Extensions.Logging;
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
    public ILogger Logger { get; }
    public CombatEncounter? ActiveCombat { get; }

    /// <summary>
    /// The effective campaign name for this change context (for scoping entities like Characters/Locations on create).
    /// Propagated from dispatcher for create handlers to set CampaignName on new entities.
    /// </summary>
    public string? CampaignName { get; }

    /// <summary>
    /// Provides the current campaign time (used by EventOccurred and RumorEvolves handlers).
    /// </summary>
    public Func<Task<CampaignTime>> GetCurrentTimeAsync { get; }

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
    private bool _hasFailure;
    private readonly Dictionary<string, Character> _characters;
    private readonly Dictionary<string, Item> _items;
    private readonly Dictionary<string, Location> _locations;

    internal ChangeContext(
        IAsyncDocumentSession session,
        Dictionary<string, Character> characters,
        Dictionary<string, Item> items,
        Dictionary<string, Location> locations,
        ILogger logger,
        Func<Task<CampaignTime>> getCurrentTimeAsync,
        Func<Event, Task> logEventAsync,
        List<string> summary,
        WorldChangeDispatcher dispatcher,
        CombatEncounter? activeCombat = null,
        string? campaignName = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _characters = characters ?? throw new ArgumentNullException(nameof(characters));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        GetCurrentTimeAsync = getCurrentTimeAsync ?? throw new ArgumentNullException(nameof(getCurrentTimeAsync));
        LogEventAsync = logEventAsync ?? throw new ArgumentNullException(nameof(logEventAsync));
        _summary = summary ?? throw new ArgumentNullException(nameof(summary));
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ActiveCombat = activeCombat;
        CampaignName = campaignName;
    }

    /// <summary>
    /// Test-only constructor that allows null session for pure dispatcher selection / result aggregation tests
    /// that use fake handlers which never touch Session, GetCurrentTime, or LogEvent.
    /// </summary>
    internal ChangeContext(
        IAsyncDocumentSession? sessionForTests,
        Dictionary<string, Character> characters,
        Dictionary<string, Item> items,
        Dictionary<string, Location> locations,
        ILogger logger,
        List<string> summary,
        WorldChangeDispatcher dispatcher,
        CombatEncounter? activeCombat = null,
        string? campaignName = null)
    {
        Session = sessionForTests!; // may be null; only for tests with fake handlers
        _characters = characters ?? throw new ArgumentNullException(nameof(characters));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        GetCurrentTimeAsync = () => Task.FromResult(new CampaignTime());
        LogEventAsync = _ => Task.CompletedTask;
        _summary = summary ?? throw new ArgumentNullException(nameof(summary));
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ActiveCombat = activeCombat;
        CampaignName = campaignName;
    }

    public void RegisterNewLocation(Location loc) => _locations[loc.Id] = loc;
    public void RegisterNewCharacter(Character c) => _characters[c.Id] = c;
    public void RegisterNewItem(Item i) => _items[i.Id] = i;

    /// <summary>
    /// Records a message that will appear in CommitResult.Summary.
    /// </summary>
    public void RecordMessage(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            _summary.Add(message);
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
}