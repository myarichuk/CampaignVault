using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// Wire-facing projection of <see cref="Location"/> for response views (SceneView, SceneSummaryView) —
/// drops CampaignName, LastUpdated, IsArchived (an archived location surfacing in an active scene is a
/// bug worth seeing in logs, not narrating), and TagProvenance (event-id provenance for tags/features,
/// not narrative content).
/// </summary>
public record LocationDetailView(
    string Id,
    string Name,
    string Description,
    LocationType Type,
    string? ParentLocationId,
    List<LocationExit> Exits,
    List<string> PointsOfInterest,
    Dictionary<string, string> PointOfInterestDetails,
    string? AmbientCrowd,
    int? LastVisitedDay,
    List<DepartedNpcRecord> RecentlyDeparted,
    Dictionary<string, object> Metadata,
    string? CurrentState,
    List<string> VisualTags,
    List<string> DistinctiveFeatures,
    string? ControllingFactionId,
    int DangerModifier,
    ClimateZone? ClimateZone)
{
    public static LocationDetailView From(Location l) => new(
        l.Id,
        l.Name,
        l.Description,
        l.Type,
        l.ParentLocationId,
        l.Exits,
        l.PointsOfInterest,
        l.PointOfInterestDetails,
        l.AmbientCrowd,
        l.LastVisitedDay,
        l.RecentlyDeparted,
        l.Metadata,
        l.CurrentState,
        l.VisualTags,
        l.DistinctiveFeatures,
        l.ControllingFactionId,
        l.DangerModifier,
        l.ClimateZone);
}

public class SceneView
{
    public LocationDetailView Location { get; set; } = null!;

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

    /// <summary>
    /// Lightweight summaries of items visible/present at this location. Not full Item documents —
    /// see ItemSummaryView.
    /// </summary>
    public IEnumerable<ItemSummaryView> VisibleItems { get; set; } = [];

    /// <summary>
    /// Full Event objects for in-process pressure heuristics (need Timestamp and other fields not on
    /// EventSummaryView) — kept for in-process consumers (AmbientCrowdPressureContributor,
    /// SceneVulnerabilityHeuristics, etc.) but not sent over the wire. LLM-facing recent-events summary
    /// is RecentEventSummaries below (same "internal full copy + wire summary" pattern as WorldPressureItems).
    /// </summary>
    [JsonIgnore]
    public IEnumerable<Event> RecentEvents { get; set; } = [];

    /// <summary>Lightweight recent-event summaries actually sent to the LLM.</summary>
    public IEnumerable<EventSummaryView> RecentEventSummaries { get; set; } = [];

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
    /// Structured pressure items (Severity, GroupingKey, SuggestedCommitJson) — kept for in-process
    /// consumers (tests assert on these fields directly) but not sent over the wire: the LLM already
    /// gets the same content via the parallel ToolResult.WorldPressure display strings.
    /// </summary>
    [JsonIgnore]
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

    /// <summary>Plot threads associated with this location (referenced via thread-level or clue-level involvedEntityIds).</summary>
    public List<PlotThreadMinimal> AssociatedPlotThreads { get; set; } = [];
}

/// <summary>
/// Lightweight summary of a scene for quick lookups (get_scene_summary).
/// Slices the heavier SceneView to essentials: location, NPCs, rumors, and a binary combat flag.
/// </summary>
public class SceneSummaryView
{
    public LocationDetailView Location { get; set; } = null!;
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
    public ActiveQuestSummary() : this(null!, null!, 0!, 0!, default!) { }
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
    public FactionPresenceSummary() : this(null!, null!, 0!) { }
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
    /// <summary>
    /// Full memory dict — kept for in-process consumers (MemoryDecayPressureContributor scans every
    /// memory for decay, not just the top-ranked ones) but not sent over the wire. LLM-facing subset is
    /// RelevantMemories below (same "internal full copy + wire summary" pattern as WorldPressureItems).
    /// </summary>
    [property: JsonIgnore]
    Dictionary<string, MemoryNode>? Memories = null,
    /// <summary>
    /// System-specific TTRPG stats (e.g. AC, Ability Scores, Skills).
    /// Essential for the LLM to understand mechanical capabilities at a glance.
    /// </summary>
    SystemExtension? SystemStats = null,
    double BehavioralTension = 0,
    IReadOnlyList<InitiativeCandidate>? ActiveInitiatives = null,
    /// <summary>
    /// Top memories scored relevant to this moment (present entities, location, recency) — not this
    /// NPC's complete memory history. More may exist; deep-dive with get_entity (chars/&lt;id&gt;) or
    /// take_turn's fullDetailCharacterId for the full set.
    /// </summary>
    IReadOnlyList<MemoryNode>? RelevantMemories = null,
    List<ItemSummaryView>? EquippedItems = null,
    List<ItemSummaryView>? CarriedItems = null,
    /// <summary>
    /// Advisory-only "whose move is it" hint for this NPC — never a hard gate. Null means this NPC has
    /// no pressing reason to act/speak next.
    /// </summary>
    TurnIntentSignal? TurnIntent = null)
{
    public NpcPresenceSummary() : this(null!, null!, null!, null!, null!, null!, null!) { }
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
    /// <summary>WorldChanges applied by the simulation tick (needs/memory decay, staleness, etc.) that
    /// ran synchronously because this commit crossed a day boundary. Empty unless a RestChange/TravelChange
    /// (or similar) advanced the calendar. Populated so callers (e.g. take_turn's delta mode) can see ambient
    /// drift that would otherwise be silently persisted with no trace in the response.</summary>
    public List<WorldChange> AmbientDeltas { get; set; } = [];
    /// <summary>Persisted narrative text from the same simulation tick that produced <see cref="AmbientDeltas"/>.</summary>
    public List<string> AmbientNarrativeSummaries { get; set; } = [];
}

/// <summary>Rich eviction record returned from advance_world for transient NPC departures.</summary>
public record EvictedNpcSummary(
    string CharacterId,
    string Name,
    string? FromLocationId,
    string? FromLocationName)
{
    public EvictedNpcSummary() : this(null!, null!, null, null) { }
}

public class AdvanceResult
{
    public CampaignTime NewTime { get; set; } = null!;
    public List<string> SimulatorEvents { get; set; } = [];
    /// <summary>Structured pressure items (Severity, GroupingKey, SuggestedCommitJson) — kept for in-process
    /// use but not serialized to the LLM: the ToolResult.WorldPressure display strings carry the same content.</summary>
    [JsonIgnore]
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
    public RumorSummary() : this(null!, null!, default!) { }
}

public record LocationSummary(string Id, string Name, LocationType Type)
{
    public LocationSummary() : this(null!, null!, default!) { }
}

public record NpcActivitySummary(string Name, string CurrentActivity)
{
    public NpcActivitySummary() : this(null!, null!) { }
}

public record ItemDetailSummary(string Id, string Name, string? Status)
{
    public ItemDetailSummary() : this(null!, null!, null) { }
}

public record ItemSummaryView(
    string Id,
    string Name,
    string Description,
    int Quantity,
    string CoreCategory,
    bool IsEquipped,
    List<string> EquipZones,
    string? EquipLayer,
    string? CurrentState,
    int? CurrentCharges,
    int? MaxCharges,
    List<string> Tags,
    List<string> DistinctiveFeatures,
    List<string>? VisualTags,
    string? AppearanceNote,
    List<ItemDetailSummary>? ItemDetails)
{
    public static ItemSummaryView From(Item item) => new(
        item.Id,
        item.Name,
        item.Description,
        Math.Max(item.Quantity, 1),
        item.CoreCategory.ToString(),
        item.IsEquipped,
        item.EquipZones?.Select(z => z.ToString()).ToList() ?? [],
        item.EquipLayer?.ToString(),
        item.CurrentState,
        item.CurrentCharges,
        item.MaxCharges,
        item.Tags ?? [],
        item.DistinctiveFeatures ?? [],
        item.VisualTags,
        item.AppearanceNote,
        item.ItemDetails.Where(d => !d.IsRetired).Select(d => new ItemDetailSummary(d.Id, d.Name, d.Status)).ToList() is { Count: > 0 } details ? details : null
    );
}

/// <summary>
/// Lightweight event projection for DM narration — summary/category/involved/location/day/importance
/// plus EmotionalBeat (see DmHelpManual's Patterns section: "one of the highest-fidelity ways to make
/// relationships feel alive across sessions" — dropping it would sever that continuity for any DM
/// reading this view). Not full Event documents (no Details, Timestamp, SemanticVector, etc.) — full
/// event history with those fields is still available on-demand via recall_history. Production logic
/// that needs the full Event (e.g. pressure heuristics reading SceneView.RecentEvents) keeps using
/// Event directly.
/// </summary>
public record EventSummaryView(
    string Id,
    string Summary,
    EventCategory Category,
    List<string> Involved,
    string? LocationId,
    int DayLogged,
    MemoryImportance Importance,
    string? EmotionalBeat)
{
    public static EventSummaryView From(Event ev) => new(
        ev.Id,
        ev.Summary,
        ev.Category,
        ev.Involved,
        ev.LocationId,
        ev.DayLogged,
        ev.Importance,
        ev.EmotionalBeat);
}

public record SceneClimateSummary(
    string EffectiveZone,
    float AmbientTemperatureC,
    string TimeOfDay);

public record PartyMemberView(
    CharacterDetailView Character,
    List<ItemSummaryView>? Equipped = null,
    List<ItemSummaryView>? Carried = null,
    /// <summary>RP-advisory initiative/memory enrichment, present only for the up-to-2 NPCs selected this
    /// take_turn call. Always null for player characters (RP initiative is NPC-only).</summary>
    NpcInitiativeEnrichment? Initiative = null)
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

/// <summary>
/// Wraps a search_world hit with an explicit type tag. Needed because Character/Location/Faction/Item
/// all share a "Name" field — without this, the LLM has no reliable way to tell them apart by shape
/// alone. Each Match is a lean summary, not the full document: search_world is for ID discovery (see
/// get_entity's own description — "Use search_world first when you only know a name, not the ID"), and
/// most types have a get_entity full-detail fallback (chars/, locations/, factions/, quests/, items/).
/// Rumor and Lore have no get_entity route, so their summaries keep full narrative content intact.
/// </summary>
public record SearchMatch(string EntityType, object Match);

/// <summary>Search-result projection of <see cref="Character"/> — see get_entity (chars/) for full detail.</summary>
public record CharacterSearchSummary(
    string Id,
    string Name,
    bool IsPc,
    bool IsPartyCompanion,
    string? CurrentAppearance,
    string? CurrentActivity,
    string? CurrentLocationId)
{
    public static CharacterSearchSummary From(Character c) => new(
        c.Id, c.Name, c.IsPc, c.IsPartyCompanion, c.CurrentAppearance, c.CurrentActivity, c.CurrentLocationId);
}

/// <summary>Search-result projection of <see cref="Location"/> — see get_entity (locations/) for full detail.</summary>
public record LocationSearchSummary(
    string Id,
    string Name,
    LocationType Type,
    string Description,
    string? ParentLocationId)
{
    public static LocationSearchSummary From(Location l) => new(l.Id, l.Name, l.Type, l.Description, l.ParentLocationId);
}

/// <summary>Search-result projection of <see cref="Faction"/> — see get_entity (factions/) for full detail.</summary>
public record FactionSearchSummary(
    string Id,
    string Name,
    string? Description,
    FactionType FactionType,
    int InfluenceLevel)
{
    public static FactionSearchSummary From(Faction f) => new(f.Id, f.Name, f.Description, f.FactionType, f.InfluenceLevel);
}

/// <summary>Search-result projection of <see cref="Quest"/> — see get_entity (quests/) for full detail.</summary>
public record QuestSearchSummary(
    string Id,
    string Title,
    QuestState OverallState,
    QuestUrgency Urgency,
    int? DeadlineDay,
    string? GiverId)
{
    public static QuestSearchSummary From(Quest q) => new(q.Id, q.Title, q.OverallState, q.Urgency, q.DeadlineDay, q.GiverId);
}

/// <summary>
/// Wire-facing projection of <see cref="Quest"/> for get_entity (quests/) full-detail responses —
/// carries every narrative/mechanical field the raw entity has, except DmNotes is fenced under
/// GmOnly (see that type's doc comment for how to treat it) instead of shipped as a flat field.
/// </summary>
public record QuestDetailView(
    string Id,
    string Title,
    string? GiverId,
    List<QuestObjective> Objectives,
    QuestState OverallState,
    string? Category,
    QuestUrgency Urgency,
    List<string> RelatedLocationIds,
    List<string> RelatedFactionIds,
    GmOnly GmOnly,
    List<string>? VisibleToCharacterIds,
    int? DeadlineDay,
    int LastUpdatedDay,
    List<PlotThreadMinimal> AssociatedPlotThreads)
{
    public static QuestDetailView From(Quest q) => new(
        q.Id,
        q.Title,
        q.GiverId,
        q.Objectives,
        q.OverallState,
        q.Category,
        q.Urgency,
        q.RelatedLocationIds,
        q.RelatedFactionIds,
        new GmOnly(q.DmNotes),
        q.VisibleToCharacterIds,
        q.DeadlineDay,
        q.LastUpdatedDay,
        q.AssociatedPlotThreads);
}

/// <summary>
/// Wire-facing projection of <see cref="PlotThread"/> for get_entity (plot-threads/) full-detail
/// responses — carries every narrative/mechanical field the raw entity has, except DmNotes is fenced
/// under GmOnly (see that type's doc comment for how to treat it) instead of shipped as a flat field.
/// </summary>
public record PlotThreadDetailView(
    string Id,
    string Title,
    string? Summary,
    PlotThreadState State,
    int TensionLevel,
    List<PlotClue> Clues,
    List<string> InvolvedEntityIds,
    string? ResolutionCondition,
    List<string> ForeshadowingHooks,
    GmOnly GmOnly,
    int DayCreated,
    int LastUpdatedDay,
    int? DeadlineDay,
    int? ClimaxEnteredDay,
    bool IsPlayerVisible)
{
    public static PlotThreadDetailView From(PlotThread t) => new(
        t.Id,
        t.Title,
        t.Summary,
        t.State,
        t.TensionLevel,
        t.Clues,
        t.InvolvedEntityIds,
        t.ResolutionCondition,
        t.ForeshadowingHooks,
        new GmOnly(t.DmNotes),
        t.DayCreated,
        t.LastUpdatedDay,
        t.DeadlineDay,
        t.ClimaxEnteredDay,
        t.IsPlayerVisible);
}

/// <summary>
/// Wire-facing projection of <see cref="WorldEvent"/> for world_build's UpsertWorldEvent echo —
/// carries every field the raw entity has, except DmNotes is fenced under GmOnly (see that type's
/// doc comment for how to treat it) instead of shipped as a flat field.
/// </summary>
public record WorldEventDetailView(
    string Id,
    string Title,
    string? Description,
    string? ActorId,
    List<string> InvolvedEntityIds,
    WorldEventTriggerType TriggerType,
    int? IntervalDays,
    int? TargetDay,
    WorldEventCondition? Condition,
    List<WorldEventEffect> Effects,
    WorldEventStatus Status,
    int? LastTriggeredDay,
    int? TriggeredOnDay,
    string? PreventedByEntityId,
    WorldEventCondition? PreventionCondition,
    int DayCreated,
    int LastUpdatedDay,
    bool IsPlayerVisible,
    GmOnly GmOnly)
{
    public static WorldEventDetailView From(WorldEvent e) => new(
        e.Id,
        e.Title,
        e.Description,
        e.ActorId,
        e.InvolvedEntityIds,
        e.TriggerType,
        e.IntervalDays,
        e.TargetDay,
        e.Condition,
        e.Effects,
        e.Status,
        e.LastTriggeredDay,
        e.TriggeredOnDay,
        e.PreventedByEntityId,
        e.PreventionCondition,
        e.DayCreated,
        e.LastUpdatedDay,
        e.IsPlayerVisible,
        new GmOnly(e.DmNotes));
}

/// <summary>
/// Search-result projection of <see cref="Lore"/>. No get_entity route exists for lore/ — this keeps
/// Content intact (only drops CampaignName/LastUpdated bookkeeping) since there's no full-detail fallback.
/// </summary>
public record LoreSearchSummary(
    string Id,
    string Title,
    string Content,
    string? Category)
{
    public static LoreSearchSummary From(Lore l) => new(l.Id, l.Title, l.Content, l.Category);
}

/// <summary>
/// Search-result projection of <see cref="Rumor"/>. No get_entity route exists for rumors/ — this keeps
/// CurrentText intact (only drops CampaignName/LastUpdated/IsArchived bookkeeping) since there's no
/// full-detail fallback.
/// </summary>
public record RumorSearchSummary(
    string Id,
    string Subject,
    string CurrentText,
    RumorState State,
    string RegionLocationId)
{
    public static RumorSearchSummary From(Rumor r) => new(r.Id, r.Subject, r.CurrentText, r.State, r.RegionLocationId);
}

