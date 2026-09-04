using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>Whether a take_turn response is a full state snapshot or a delta since the last snapshot.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TurnMode
{
    /// <summary>Full section payloads (Party, WorldState) — same shape as pre-delta-mode take_turn.</summary>
    Full,
    /// <summary>Only what changed this turn (PartyDelta, WorldStateDelta) — full detail remains available via get_entity, or by setting includeParty/includeWorldState on the same take_turn call.</summary>
    Delta
}

/// <summary>
/// Request to take_turn: optional mutations + optional refresh/query specifications.
/// Null/empty Changes = pure query; populated Changes = mutation with auto-refresh of touched entities.
/// </summary>
public class TakeTurnRequest
{
    [Description(
        "Array of world changes to commit (optional for pure queries, but then MUST pass at least one refresh parameter). *** REQUIRED FIELD: '$type' *** Every single change object MUST include a '$type' discriminator field — this is not optional; see WorldChange's own description for the full list of valid values. Omitting '$type' on ANY item will cause the entire batch to fail deserialization. If Changes is null/empty, you MUST pass at least one of: includeWorldState, includeParty, extraCharacterIds, extraLocationIds, fullDetailCharacterId, or fullDetailLocationId.")]
    [JsonPropertyName("changes")]
    public WorldChange[]? Changes { get; set; }

    [Description(
        "Narrative summary of what happened. Required if Changes is provided; omit for pure queries.")]
    [JsonPropertyName("narrative")]
    public string? Narrative { get; set; }

    [Description(
        "Batch duration in minutes ('this exchange took about 5 minutes'), applied to the first eligible change so needs/time advance even if no per-change minutesElapsed was set. Ignored if any change in the batch already has its own minutesElapsed, and ignored for rest/travel changes (they advance time via their own hour fields).")]
    [JsonPropertyName("minutesElapsed")]
    public int? MinutesElapsed { get; set; }

    [Description(
        "Automatically refresh summary state for entities touched by Changes (default true). Set false for bulk/seeding commits to save bandwidth.")]
    [JsonPropertyName("autoRefreshInvolved")]
    public bool AutoRefreshInvolved { get; set; } = true;

    [Description(
        "Additional NPC IDs to refresh even if not touched by Changes (e.g. to keep other party members' state current).")]
    [JsonPropertyName("extraCharacterIds")]
    public string[]? ExtraCharacterIds { get; set; }

    [Description(
        "Additional location IDs to refresh even if not touched by Changes (e.g. to monitor adjacent rooms).")]
    [JsonPropertyName("extraLocationIds")]
    public string[]? ExtraLocationIds { get; set; }

    [Description(
        "Include full Party member summaries (all player characters' summary state) in response (default false).")]
    [JsonPropertyName("includeParty")]
    public bool IncludeParty { get; set; } = false;

    [Description(
        "Include WorldState (rumors, quests, factions, time) in response (default false). Set to true when you need overall campaign state context.")]
    [JsonPropertyName("includeWorldState")]
    public bool IncludeWorldState { get; set; } = false;

    [Description(
        "Location ID anchoring WorldState scoping (used if IncludeWorldState=true) and the capped NPC initiative/memory candidate pool (see take_turn's own description). Omit to skip location-based WorldState scoping — rumors/quests/factions are then filtered only by party affiliations, PartyLocation comes back null — and the initiative pool falls back to a PC's CurrentLocationId, then to NPCs touched by this turn's Changes.")]
    [JsonPropertyName("partyLocationId")]
    public string? PartyLocationId { get; set; }

    [Description(
        "NPC ID to fetch in full detail (NpcContextView with all relationships, history, needs) instead of summary. Use sparingly; only one full detail per call.")]
    [JsonPropertyName("fullDetailCharacterId")]
    public string? FullDetailCharacterId { get; set; }

    [Description(
        "Location ID to fetch in full detail (SceneView with all details) instead of summary. Use sparingly; only one full detail per call.")]
    [JsonPropertyName("fullDetailLocationId")]
    public string? FullDetailLocationId { get; set; }

    [Description(
        "Force a full-detail response (Party/WorldState instead of PartyDelta/WorldStateDelta) and reset the campaign's periodic reseed counter, regardless of how many delta turns have elapsed. Use after your own context was compacted/summarized, or at the start of a fresh session, so you aren't reasoning from a stale partial state. Default false.")]
    [JsonPropertyName("forceFullReseed")]
    public bool ForceFullReseed { get; set; } = false;

    [Description(
        "Only meaningful when the response mode ends up Delta (ignored on Full). Trims NpcSummaryView entries (Npcs[], PartyDelta[]) further, capping KnownNeeds to the top 2 movers this turn instead of every need that moved >= 2 points — useful for long roommate/party scenes with many NPCs where even a lean delta adds up. Default false.")]
    [JsonPropertyName("leanMode")]
    public bool LeanMode { get; set; } = false;

    [Description(
        "Override the auto-created SceneCommit event's importance (default Important). Use Trivial for pure flavor/banter beats with no new information; omit otherwise.")]
    [JsonPropertyName("narrativeImportance")]
    public MemoryImportance? NarrativeImportance { get; set; }

    [Description(
        "Echo back the 'partyFingerprint' value from the PREVIOUS take_turn response, unchanged. Lets the server detect narrative " +
        "drift (e.g. a delta you missed) independent of the periodic reseed cadence: if this doesn't match what the server computed " +
        "last turn, the response is forced to Full and a resync advisory is added. Omit on your very first call for a session, or " +
        "whenever you don't have a prior value handy — an omitted value is never treated as a mismatch.")]
    [JsonPropertyName("clientPartyFingerprint")]
    public string? ClientPartyFingerprint { get; set; }
}

/// <summary>
/// Response from take_turn: mutation outcome + fresh entity state bundled together.
/// Committed=false and ChangesProcessed=0 for pure-query calls; all other fields match Commit's behavior.
/// </summary>
public class TurnResult
{
    [Description("Full or Delta. Full: Party/WorldState carry complete snapshots (same shape as before delta mode existed). " +
        "Delta: PartyDelta/WorldStateDelta carry only what changed this turn instead — call get_entity/get_scene for full detail " +
        "on anything not covered by the delta, or pass forceFullReseed=true on the next call for a complete resync.")]
    public TurnMode Mode { get; set; } = TurnMode.Full;

    [Description("True if a mutation was successfully committed; false if this was a query-only call or commit failed.")]
    public bool Committed { get; set; }

    [Description("Number of WorldChanges processed by the mutation (0 for query-only calls).")]
    public int ChangesProcessed { get; set; }

    [Description("Narrative summary of each change processed.")]
    public List<string> Summary { get; set; } = [];

    [Description("IDs of all entities touched or created (mixed types: chars/, locations/, items/, etc.). " +
        "Characters/locations already itemized in Npcs/PartyDelta/Party/Scenes/FullScene/FullNpcContext are omitted here to avoid repeating them with no extra detail; check those sections for such IDs. " +
        "IDs dropped by the refresh cap (see RefreshTruncatedIds) and entity types with no detail section (items/, quests/, factions/, ...) always remain here.")]
    public List<string> InvolvedEntities { get; set; } = [];

    [Description("Entity IDs where a create-style change hit an existing document and was merged instead of creating new.")]
    public List<string> EntityCollisions { get; set; } = [];

    [Description("Optional reminder about the commit outcome (e.g. 'missing narrative event', 'missing PoI detail').")]
    public string? NarrativeReminder { get; set; }

    [Description("Remaining rate-limit tokens for this campaign after this commit.")]
    public int? RateLimitTokensRemaining { get; set; }

    [Description("Bundled fresh NPC summaries for entities in InvolvedEntities (if autoRefreshInvolved=true) or ExtraCharacterIds. Capped at 6 NPCs.")]
    public List<NpcSummaryView>? Npcs { get; set; }

    [Description("Bundled fresh scene summaries for entities in InvolvedEntities (if autoRefreshInvolved=true) or ExtraLocationIds. Capped at 3 scenes.")]
    public List<SceneSummaryView>? Scenes { get; set; }

    [Description("Entity IDs that were dropped from Npcs/Scenes due to refresh caps (6 NPCs / 3 scenes). Re-request these explicitly via extraCharacterIds/extraLocationIds if needed.")]
    public List<string>? RefreshTruncatedIds { get; set; }

    [Description("Full party member summaries (if includeParty=true AND mode=full); otherwise null. See PartyDelta for mode=delta.")]
    public List<PartyMemberView>? Party { get; set; }

    [Description("Delta-mode party entries (if includeParty=true AND mode=delta); otherwise null. One entry per party member with " +
        "a change this turn, plus any member selected for initiative/memory enrichment even with zero changes. Call get_entity for a " +
        "member's full current state.")]
    public List<EntityChangeDelta>? PartyDelta { get; set; }

    [Description("World state including rumors, active quests, faction standings, and campaign time (if includeWorldState=true AND mode=full); otherwise null. See WorldStateDelta for mode=delta.")]
    public WorldStateView? WorldState { get; set; }

    [Description("Delta-mode world state (if includeWorldState=true AND mode=delta); otherwise null. Only rumor/quest/faction changes " +
        "from this turn, plus current time/pressure (always populated). Call get_world_state for the full picture.")]
    public WorldStateDeltaView? WorldStateDelta { get; set; }

    [Description("Full NPC context view for the requested NPC (if fullDetailCharacterId was provided); includes all relationships, history, and behavior synthesis. Otherwise null.")]
    public NpcContextView? FullNpcContext { get; set; }

    [Description("Full scene view for the requested location (if fullDetailLocationId was provided); includes all NPCs, items, and environmental details. Otherwise null.")]
    public SceneView? FullScene { get; set; }

    [Description("Non-fatal problems encountered while assembling this response (failed refreshes, missing entities, world-state errors). Null when every requested section succeeded. Check this whenever an expected section came back null.")]
    public List<string>? Warnings { get; set; }

    [Description("Concrete follow-up tool calls worth making before narrating further — populated when a memoryHint fired (get_entity/recall_history) or entities were dropped from Npcs/Scenes by the refresh cap (RefreshTruncatedIds). Models respond more reliably to an explicit suggested call than to silently querying more; null when nothing is flagged this turn.")]
    public List<string>? QuerySuggestions { get; set; }

    [Description("Readable fingerprint of current party state ('charId:hp/maxHp@locationId', one per PC/companion, sorted by ID) — pass this back " +
        "as clientPartyFingerprint on your NEXT take_turn call so the server can catch drift (a missed or misread delta) before it compounds. " +
        "Also useful to self-check your own narrative model against right now: if this doesn't match what you believe about the party, trust this.")]
    public string? PartyFingerprint { get; set; }

    [Description("Monotonically increasing counter, bumped once per committed take_turn mutation for this campaign (never on pure-query calls). " +
        "Not currently used for gap detection server-side — informational, for logging/debugging state sync issues.")]
    public long WorldSequence { get; set; }
}

/// <summary>
/// Delta-mode world-state view: only the rumor/quest/faction WorldChanges actually applied this turn,
/// rather than the full active-rumors/quests/factions lists BuildWorldStateAsync would return. Time and
/// WorldPressure are always populated (small, fixed-shape, and needed every turn regardless of mode).
/// </summary>
public class WorldStateDeltaView
{
    [Description("Current campaign time — always populated.")]
    public CampaignTimeView? Time { get; set; }

    [Description("Active world pressure nags — always populated, same as WorldStateView.WorldPressure.")]
    public IEnumerable<string> WorldPressure { get; set; } = [];

    [Description("Rumors that changed state this turn (RumorEvolves commits applied). Empty if none.")]
    public List<RumorEvolves>? RumorChanges { get; set; }

    [Description("Quest objectives that progressed this turn (QuestProgress commits applied). Empty if none.")]
    public List<QuestProgress>? QuestChanges { get; set; }

    [Description("Character-faction reputation changes applied this turn. Empty if none.")]
    public List<FactionReputationChange>? FactionReputationChanges { get; set; }

    [Description("Faction stance/influence changes applied this turn. Empty if none.")]
    public List<FactionStateChange>? FactionStateChanges { get; set; }

    [Description("Ambient narrative summaries from world simulation this turn (not the caller's own narrative, which is already in context).")]
    public List<string>? NewEvents { get; set; }
}

/// <summary>
/// Delta-mode entry for one party/scene entity: only the WorldChanges applied to it this turn, plus
/// (for NPCs selected this call) RP-initiative/memory enrichment. Call get_entity for full current state.
/// </summary>
public class EntityChangeDelta
{
    [Description("Character ID this delta is for.")]
    public string EntityId { get; set; } = null!;

    [Description("Character name, for display without a follow-up lookup.")]
    public string? Name { get; set; }

    [Description("WorldChanges applied to this entity this turn (echoes the committed change objects — each is already a delta, e.g. NeedChange carries {Need, Delta}).")]
    public List<WorldChange> Changes { get; set; } = [];

    [Description("RP-advisory initiative/memory enrichment, present only for the up-to-2 NPCs selected this call (see take_turn's tool description). Null otherwise, and always null for player characters.")]
    public NpcInitiativeEnrichment? Initiative { get; set; }

    [Description("Set when this NPC has a high-salience memory that exists but wasn't surfaced via Initiative this turn (wasn't one of the up-to-2 selected) — a nudge to call get_entity/recall_history rather than assume nothing relevant is aging in the background. Null otherwise.")]
    public string? MemoryHint { get; set; }
}
