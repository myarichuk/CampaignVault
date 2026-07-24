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
    // ReSharper disable once InconsistentNaming
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

    /// <summary>
    /// Rich pressure items including Severity and optional SuggestedCommitJson for structured clients.
    /// The parallel ToolResult.WorldPressure contains the human-prefixed display strings (with JSONs appended).
    /// </summary>
    public List<WorldPressureItem> WorldPressureItems { get; set; } = [];

    /// <summary>
    /// Narrative hints for PCs who would recognize features in this location based on their skills/background.
    /// E.g., "Valen (ranger, Survival +5) would likely notice: the wolf tracks circling the campsite are too orderly to be natural."
    /// Purely read-time guidance; no persisted state. Empty if no PC skills/background match location features.
    /// </summary>
    public List<string>? RecognitionHints { get; set; }

    /// <summary>
    /// Climate summary for this scene (zone, ambient temperature, time of day).
    /// </summary>
    public SceneClimateSummary? Climate { get; set; }

    /// <summary>
    /// Nested container contents for visible container items in this scene.
    /// </summary>
    public List<ContainerContentsSummary> ContainerContents { get; set; } = [];

    /// <summary>
    /// Advisory only — never a hard gate, unlike combat's ActiveTurnId/NotYourTurn. Null means "open
    /// turn": the player can always act. Non-null names the present NPC with the highest
    /// BehavioralTension among those whose TurnIntent.Holder is "npc" — a hint, not a lock.
    /// </summary>
    public string? TurnIntentCharacterId { get; set; }
}

/// <summary>
/// Lightweight summary of a scene for quick lookups (get_scene_summary).
/// Slices the heavier SceneView to essentials: location, NPCs, rumors, and a binary combat flag.
/// </summary>
public class SceneSummaryView
{
    public Location Location { get; set; } = default!;
    public IEnumerable<NpcPresenceSummary> PresentNPCs { get; set; } = [];
    public IEnumerable<RumorSummary> LocalRumors { get; set; } = [];
    public bool ActiveCombat { get; set; }
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
    int OldestOpenObjectiveDay = 0,
    /// <summary>True when DeadlineDay has already passed — the quest is still surfaced (deadline misses are campaign-critical) but should be narrated as overdue, not merely urgent.</summary>
    bool IsOverdue = false)
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
    /// <summary>
    /// Maps a tag/feature/appearance text to the event ID(s) that established it — objective ground
    /// truth, distinct from this NPC's own subjective Memories below.
    /// </summary>
    Dictionary<string, List<string>>? TagProvenance = null,
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
    IReadOnlyList<Item>? HeldItems = null,
    List<ItemSummaryView>? EquippedItems = null,
    List<ItemSummaryView>? CarriedItems = null,
    /// <summary>
    /// Advisory-only "whose move is it" hint for this NPC — never a hard gate. Null means this NPC has
    /// no pressing reason to act/speak next.
    /// </summary>
    TurnIntentSignal? TurnIntent = null)
{
    public NpcPresenceSummary() : this(default!, default!, default!, default!, default!, default!, default!) { }
}

public class CommitResult
{
    public bool Success { get; set; } = true;
    public int ChangesProcessed { get; set; }
    public List<string> Summary { get; set; } = [];
    public List<string> InvolvedEntities { get; set; } = [];
    /// <summary>IDs of entities whose create-style change (e.g. character_create) resolved to an
    /// already-existing document and was merged into it instead of creating a new one.</summary>
    public List<string> EntityCollisions { get; set; } = [];
    /// <summary>Set when the batch contained combat/status changes but no EventOccurred. Reminder to log the narrative.</summary>
    public string? NarrativeReminder { get; set; }
    /// <summary>Remaining commit token budget (approximate). Replenishes 10 tokens/10s up to 50.</summary>
    public int? RateLimitTokensRemaining { get; set; }
}

/// <summary>Rich eviction record returned from advance_world for transient NPC departures.</summary>
public record EvictedNpcSummary(
    string CharacterId,
    string Name,
    string? FromLocationId,
    string? FromLocationName)
{
    public EvictedNpcSummary() : this(default!, default!, default, default) { }
}

public class AdvanceResult
{
    public CampaignTime NewTime { get; set; } = default!;
    public List<string> SimulatorEvents { get; set; } = [];
    public List<WorldPressureItem> WorldPressure { get; set; } = [];
    /// <summary>IDs of transient NPCs evicted during this advance. Re-introduce important ones via keepAlive or schedule_change.</summary>
    public List<string> EvictedNpcIds { get; set; } = [];
    /// <summary>Structured eviction details (names + source locations). Prefer over bare IDs for narration and recovery.</summary>
    public List<EvictedNpcSummary> EvictedNpcs { get; set; } = [];
    /// <summary>Set when the call used the 'hours' parameter instead of days/timeOfDay.</summary>
    public int? HoursAdvanced { get; set; }
    /// <summary>Whole calendar days actually crossed (may be 0 for a sub-day 'hours' call that doesn't cross midnight).</summary>
    public int DaysAdvanced { get; set; }
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

public record ItemDetailSummary(string Id, string Name, string? Status)
{
    public ItemDetailSummary() : this(default!, default!, default) { }
}

public record ItemSummaryView(
    string Id,
    string Name,
    int Quantity,
    string CoreCategory,
    bool IsEquipped,
    List<string> EquipZones,
    string? EquipLayer,
    string? CurrentState,
    int? CurrentCharges,
    int? MaxCharges,
    List<string>? VisualTags,
    string? AppearanceNote,
    List<ItemDetailSummary>? ItemDetails)
{
    public static ItemSummaryView From(Item item) => new(
        item.Id,
        item.Name,
        Math.Max(item.Quantity, 1),
        item.CoreCategory.ToString(),
        item.IsEquipped,
        item.EquipZones?.Select(z => z.ToString()).ToList() ?? [],
        item.EquipLayer?.ToString(),
        item.CurrentState,
        item.CurrentCharges,
        item.MaxCharges,
        item.VisualTags,
        item.AppearanceNote,
        item.ItemDetails.Where(d => !d.IsRetired).Select(d => new ItemDetailSummary(d.Id, d.Name, d.Status)).ToList() is { Count: > 0 } details ? details : null
    );
}

public record SceneClimateSummary(
    string EffectiveZone,
    float AmbientTemperatureC,
    string TimeOfDay);

public record PartyMemberView(
    Character Character,
    List<ItemSummaryView>? Equipped = null,
    List<ItemSummaryView>? Carried = null)
{
    public string Id => Character.Id;
    public string Name => Character.Name;
    public bool IsPc => Character.IsPc;
    public bool IsPartyCompanion => Character.IsPartyCompanion;
}

public record ContainedItemSummary(
    string Id,
    string Name,
    int Quantity,
    int Depth,
    List<ContainedItemSummary>? Contents = null);

public record ContainerContentsSummary(
    string ContainerId,
    string ContainerName,
    List<ContainedItemSummary> Contents,
    int MaxDepth);

