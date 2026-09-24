using CampaignVault.Models;
// IRollService lives in CampaignVault.Data (this assembly)
using Microsoft.Extensions.Logging;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Raven-free mutation context exposed to plugin handlers and observers.
/// Host core handlers receive the sealed host <c>ChangeContext</c> and downcast at Handle entry:
/// <c>var ctx = (ChangeContext)context;</c> — safe because only <c>WorldChangeDispatcher</c> (host)
/// constructs <c>ChangeContext</c>; no other <see cref="IChangeContext"/> implementation reaches core handlers.
/// Do not "fix" this into a Raven query-abstraction layer. Plugins must not reference the host assembly
/// and therefore cannot downcast. <c>IMutationDispatcher</c> is intentionally omitted from MVP.
/// </summary>
public interface IChangeContext
{
    IReadOnlyDictionary<string, Character> Characters { get; }
    IReadOnlyDictionary<string, Item> Items { get; }
    IReadOnlyDictionary<string, Location> Locations { get; }
    IReadOnlyDictionary<string, Faction> Factions { get; }
    IReadOnlyDictionary<string, Quest> Quests { get; }
    ILogger Logger { get; }
    CombatEncounter? ActiveCombat { get; }
    /// <summary>
    /// Most recently entered active mode. Several modes can be active at once (crafting mid-astral, ...), so
    /// mode verb handlers should look up their own mode in <see cref="ActiveModes"/> instead.
    /// </summary>
    ModeEncounter? ActiveMode { get; }

    /// <summary>Every active mode encounter in the campaign, keyed by ModeId (case-insensitive).</summary>
    IReadOnlyDictionary<string, ModeEncounter> ActiveModes { get; }
    CampaignConfig? Config { get; }

    /// <summary>Optional dice service for handlers that resolve rolls in-process. Null in tests or hosts that omit wiring; plugins must degrade to pre-resolved fields.</summary>
    IRollService? Rolls { get; }
    string? CampaignName { get; }
    HashSet<string> InvolvedEntities { get; }
    IReadOnlyList<WorldChange>? Batch { get; }
    int BatchIndex { get; }

    Func<Task<CampaignTime>> GetCurrentTimeAsync { get; }
    Func<Task<Dictionary<string, string>>> GetSystemOptionsAsync { get; }
    Func<Event, Task> LogEventAsync { get; }

    void RegisterNewLocation(Location loc);
    void RegisterNewCharacter(Character c);
    void RegisterNewItem(Item i);
    void RegisterNewFaction(Faction f);
    void RegisterNewQuest(Quest q);

    /// <summary>
    /// Publishes a domain event (see <c>CampaignVault.Events.DomainEvent</c>) for delivery once the current
    /// change succeeds; dropped if it fails. <paramref name="topic"/> must start with your own source prefix
    /// (your manifest id + "."); anything else is rejected and logged. <paramref name="data"/> must serialize
    /// to a JSON object of plain values.
    /// </summary>
    void Publish(string topic, object? data = null);

    void RecordMessage(string message);
    void RecordPhysicalStateNudge(string message);
    void RecordFailure();
    void RecordEntityCollision(string entityId, string message);
    void RecordCommittedId(string id);

    Task<string?> SuggestLocationMatchAsync(string? nameQuery);
    Task<string?> SuggestCharacterMatchAsync(string? nameQuery);
    Task<string?> SuggestItemMatchAsync(string? nameQuery);
    Task<string?> SuggestFactionMatchAsync(string? nameQuery);
    Task<string?> SuggestQuestMatchAsync(string? nameQuery);
}
