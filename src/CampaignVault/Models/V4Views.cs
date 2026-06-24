namespace CampaignVault.Models;

public class SceneView
{
    public Location Location { get; set; } = default!;

    /// <summary>
    /// True if the location exists in the persistent database (was loaded successfully).
    /// False for hallucinated / un-created location IDs: the returned Location is a minimal stub.
    /// When false, the caller (tool) should surface strong ENGINE WARNING pressure with a ready-to-paste location_create example.
    /// </summary>
    public bool IsLocationAnchored { get; set; } = true;

    /// <summary>
    /// Lightweight summaries of NPCs currently present (driven by simulated Schedule + Current* state).
    /// Much smaller than full Character objects, focused on what an LLM DM actually needs for roleplay.
    /// </summary>
    public IEnumerable<NpcPresenceSummary> PresentNPCs { get; set; } = [];
    public IEnumerable<RumorSummary> LocalRumors { get; set; } = [];
    public IEnumerable<Item> VisibleItems { get; set; } = [];
    public IEnumerable<Event> RecentEvents { get; set; } = [];
    
    /// <summary>
    /// If there is an active combat encounter in this location, its state is returned here.
    /// Informs the LLM of turn order and rounds.
    /// </summary>
    public CombatEncounter? ActiveCombat { get; set; }

    public IEnumerable<ActiveQuestSummary>? ActiveQuests { get; set; }
    public IEnumerable<FactionPresenceSummary>? RelevantFactions { get; set; }
    public string? LastKnownTravel { get; set; }
    public IEnumerable<string>? SuggestedCommitExamples { get; set; }
}

/// <summary>
/// Lightweight summary of an active quest relevant to the current scene/location.
/// Returned in SceneView so the DM sees stakes without full document bloat.
/// </summary>
public record ActiveQuestSummary(
    string QuestId,
    string Title,
    int OpenObjectiveCount,
    int TotalObjectiveCount,
    QuestUrgency Urgency,
    int? DeadlineDay = null,
    string? GiverId = null,
    int LastUpdatedDay = 0,
    /// <summary>Oldest open/in-progress objective anchor day (TotalDaysElapsed). Used for staleness pressures.</summary>
    int OldestOpenObjectiveDay = 0)
{
    public ActiveQuestSummary() : this(default!, default!, default!, default!, default!) { }
}

/// <summary>
/// Lightweight summary of a faction with presence/territory overlap at the current location.
/// LocalStance is best-effort. PlayerReputation pulled from a relevant character's Social.FactionReputations if available in context.
/// </summary>
public record FactionPresenceSummary(
    string FactionId,
    string Name,
    int InfluenceLevel,
    FactionStance LocalStance = FactionStance.Neutral,
    int? PlayerReputation = null,
    int TerritoryLocationCount = 0,
    Dictionary<string, float>? EconomicDemand = null)
{
    public FactionPresenceSummary() : this(default!, default!, default!) { }
}

/// <summary>
/// Lightweight view of an NPC for scene exploration.
/// Contains just enough psychological + situational data for the DM to roleplay without dumping entire documents.
/// </summary>
public record NpcPresenceSummary(
    string Id,
    string Name,
    string? CurrentActivity,
    string? CurrentMood,
    Dictionary<string, float> TopNeeds,
    /// <summary>
    /// Known needs for this NPC (key → current value). The vocabulary is open — the LLM is encouraged to invent new narrative-appropriate needs.
    /// </summary>
    Dictionary<string, float> KnownNeeds,
    /// <summary>
    /// Optional descriptions for the needs (when the world-builder or LLM has provided them).
    /// </summary>
    Dictionary<string, string> NeedDescriptors,
    string? BehavioralSummary = null,
    string? Notes = null,
    bool KeepAlive = false,
    bool IsPc = false,
    bool IsPartyCompanion = false,
    string? CurrentAppearance = null,
    List<string>? VisualTags = null,
    List<string>? DistinctiveFeatures = null,
    Dictionary<string, MemoryNode>? Memories = null,
    /// <summary>
    /// System-specific TTRPG stats (e.g. AC, Ability Scores, Skills). 
    /// Essential for the LLM to understand mechanical capabilities at a glance.
    /// </summary>
    SystemExtension? SystemStats = null,
    double BehavioralTension = 0,
    IReadOnlyList<InitiativeCandidate>? ActiveInitiatives = null,
    IReadOnlyList<MemoryNode>? RelevantMemories = null,
    /// <summary>
    /// Items held by this character (weapons, gear). Use for attack narration and ruleset_action parameters.
    /// </summary>
    IReadOnlyList<Item>? HeldItems = null)
{
    public NpcPresenceSummary() : this(default!, default!, default!, default!, default!, default!, default!) { }
}

public class CommitResult
{
    public bool Success { get; set; } = true;
    public int ChangesProcessed { get; set; }
    public List<string> Summary { get; set; } = [];
    public List<string> InvolvedEntities { get; set; } = [];
}

public class AdvanceResult
{
    public CampaignTime NewTime { get; set; } = default!;
    public List<string> SimulatorEvents { get; set; } = [];
    public List<WorldPressureItem> WorldPressure { get; set; } = [];
}

public record RumorSummary(string Subject, string CurrentText, RumorState State)
{
    public RumorSummary() : this(default!, default!, default!) { }
}

public record LocationSummary(string Id, string Name, LocationType Type)
{
    public LocationSummary() : this(default!, default!, default!) { }
}

public record NpcActivitySummary(string Name, string CurrentActivity)
{
    public NpcActivitySummary() : this(default!, default!) { }
}
