using System.ComponentModel;
using System.Text.Json.Serialization;
using CampaignVault.Models.Converters;

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
        "World changes to persist (optional for pure queries). Each object needs '$type' — see WorldChange. Empty Changes requires at least one refresh param (includeWorldState, includeParty, extraCharacterIds, extraLocationIds, fullDetailCharacterId, fullDetailLocationId, memoriesOnlyCharacterId, forceFullReseed).")]
    [JsonPropertyName("changes")]
    [JsonConverter(typeof(WorldChangeArrayJsonConverter))]
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
        "Rebuild and include WorldState (world pressure, rumors, quests, factions, time) in the response (default false). Expensive — pressure evaluation + serialization run even for delta suppression, so use when pressure/verification/new-location context actually matters.")]
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
        "NPC ID to fetch ONLY Psychology.Memories for — cheaper than fullDetailCharacterId when you just need to check/refresh what an NPC remembers (e.g. before NPC-driven dialogue in delta mode) and don't need behavioral summary, items, or recent-interactions. Skip this if you're already calling fullDetailCharacterId for the same NPC this turn — that already includes memories. Use sparingly; only one per call.")]
    [JsonPropertyName("memoriesOnlyCharacterId")]
    public string? MemoriesOnlyCharacterId { get; set; }

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
        "Echo back the narrow 'partyFingerprint' value (party HP/location only) from the PREVIOUS take_turn response, unchanged. " +
        "Lets the server detect HP/location drift (e.g. a delta you missed) independent of the periodic reseed cadence: if this doesn't match what the server computed " +
        "last turn, the response is forced to Full and a resync advisory is added. NPC/need/memory/rumor drift is NOT covered by this hash " +
        "(covered by the periodic reseed + integrity pressure instead). Omit on your very first call for a session, or " +
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
        "Delta: PartyDelta/WorldStateDelta carry only what changed this turn instead — call get_entity for full detail " +
        "on anything not covered by the delta, or pass forceFullReseed=true on the next call for a complete resync.")]
    public TurnMode Mode { get; set; } = TurnMode.Full;

    [Description("True if a mutation was successfully committed; false if this was a query-only call or commit failed.")]
    public bool Committed { get; set; }

    [Description("Number of WorldChanges processed by the mutation (0 for query-only calls).")]
    public int ChangesProcessed { get; set; }

    [Description("Narrative summary of each change processed — includes randomized/computed combat outcomes (hit/miss, " +
        "damage rolled) and validation notices you couldn't have known just from the Changes you sent; explicit " +
        "confirmations of what you already specified (e.g. 'HP adjusted by -5') are included too but add nothing new.")]
    public List<string> Summary { get; set; } = [];

    [Description("Entity IDs where a create-style change hit an existing document and was merged instead of creating new.")]
    public List<string> EntityCollisions { get; set; } = [];

    [Description("IDs this commit durably created/resolved this turn (currently: event IDs, including a collision fallback " +
        "when a client-chosen eventId already existed) — reference these later (e.g. sourceEventIds) instead of re-deriving " +
        "them from Summary text. Empty when nothing in the batch created an ID.")]
    public List<string> CommittedIds { get; set; } = [];

    [Description("Optional reminder about the commit outcome (e.g. 'missing narrative event').")]
    public string? NarrativeReminder { get; set; }

    [Description("Plain-narrative reminders of physical/visual state that changed this turn — restraints removed/applied, " +
        "wounds, blood/gore, appearance or tag changes. Carry these into your next narration even if the underlying " +
        "detail scrolls out of the visible conversation; the server is the source of truth for whether e.g. a character " +
        "is still restrained or still marked with combat residue, not your running narrative memory. Null when nothing " +
        "physical/visual changed this turn.")]
    public List<string>? PhysicalStateNudges { get; set; }

    [Description("Character guidance hints collected this turn (rules/pattern nudges from guidance contributors, " +
        "e.g. first-commit quickstart, rest/travel patterns). Separate from PhysicalStateNudges, which carries " +
        "physical/visual state only. Null when no characters surfaced this turn, when guidance is disabled " +
        "(CampaignConfig.GuidanceEnabled), or when no contributor fired. Budgeted by " +
        "CampaignConfig.MaxGuidanceHintsPerResponse/MaxGuidanceCharsPerResponse.")]
    public List<string>? GuidanceHints { get; set; }

    [Description("Remaining rate-limit tokens for this campaign after this commit.")]
    public int? RateLimitTokensRemaining { get; set; }

    [Description("Bundled fresh NPC summaries for entities in InvolvedEntities (if autoRefreshInvolved=true) or ExtraCharacterIds. Capped at 6 NPCs.")]
    public List<NpcSummaryView>? Npcs { get; set; }

    [Description("NPC cards (traits, wants, fears, stance toward the party, stats, gear, pressing needs, key memories), each sent " +
        "once per session: on arrival for NPCs in the spotlight, and on the first commit that involves anyone else. Scene rosters carry " +
        "only id/name/activity/mood; keep a card in mind for the rest of the session. get_entity returns everything.")]
    public List<NpcCard>? Cards { get; set; }

    [Description("One-line facts this beat needs, pushed once per session: a memory the topic brings up, a relationship tier " +
        "crossing, a pressing need, the party's gold on a trade, passive Perception on a stealth roll, what a searched room holds.")]
    public List<string>? Context { get; set; }

    [Description("Bundled fresh scene summaries for entities in InvolvedEntities (if autoRefreshInvolved=true) or ExtraLocationIds. Capped at 3 scenes.")]
    public List<SceneSummaryView>? Scenes { get; set; }

    [Description("Entity IDs that were dropped from Npcs/Scenes due to refresh caps (6 NPCs / 3 scenes). Re-request these explicitly via extraCharacterIds/extraLocationIds if needed.")]
    public List<string>? RefreshTruncatedIds { get; set; }

    [Description("Full party member summaries (if includeParty=true AND mode=full); otherwise null. See PartyDelta for mode=delta.")]
    public List<PartyMemberView>? Party { get; set; }

    [Description("Delta-mode party entries (if includeParty=true AND mode=delta); otherwise null. One entry per party member with " +
        "a change this turn (ambient non-need changes, need movers past the significance/cumulative-drift gates, initiative/memory enrichment). " +
        "OMISSION CONTRACT: an absent PartyDelta on a quiet turn means 'no party change worth surfacing', not 'no party' — call get_entity for a " +
        "member's full current state when you need ground truth.")]
    public List<EntityChangeDelta>? PartyDelta { get; set; }

    [Description("World state including rumors, active quests, faction standings, and campaign time (if includeWorldState=true AND mode=full); otherwise null. See WorldStateDelta for mode=delta.")]
    public WorldStateView? WorldState { get; set; }

    [Description("Delta-mode world state (if includeWorldState=true AND mode=delta); otherwise null. Only rumor/quest/faction changes " +
        "from this turn, plus Time only when day/time-of-day shifted since last surfaced and WorldPressure only when the evaluated set differs " +
        "(new/changed always sends; identical-to-last suppresses to pressureUnchanged:true). includeWorldState is expensive — use when pressure/verification actually matters. " +
        "Pass forceFullReseed=true on the next take_turn call for the full picture.")]
    public WorldStateDeltaView? WorldStateDelta { get; set; }

    [Description("Full NPC context view for the requested NPC (if fullDetailCharacterId was provided); includes all relationships, history, and behavior synthesis. Otherwise null.")]
    public NpcContextView? FullNpcContext { get; set; }

    [Description("NPC's full memory set only (if memoriesOnlyCharacterId was provided) — Psychology.Memories, no behavioral summary/items/recent-interactions. Cheaper than FullNpcContext when memory is all you need. Otherwise null.")]
    public NpcMemoriesView? MemoriesOnly { get; set; }

    [Description("Full scene view for the requested location (if fullDetailLocationId was provided); includes all NPCs, items, and environmental details. Otherwise null.")]
    public SceneView? FullScene { get; set; }

    [Description("Non-fatal problems encountered while assembling this response (failed refreshes, missing entities, world-state errors). Null when every requested section succeeded. Check this whenever an expected section came back null.")]
    public List<string>? Warnings { get; set; }

    [Description("Concrete follow-up tool calls worth making before narrating further — populated when a memoryHint fired (get_entity/recall_history) or entities were dropped from Npcs/Scenes by the refresh cap (RefreshTruncatedIds). Models respond more reliably to an explicit suggested call than to silently querying more; null when nothing is flagged this turn.")]
    public List<string>? QuerySuggestions { get; set; }

    [Description("Narrow party HP/location fingerprint ('charId:hp/maxHp@locationId', one per PC/companion, sorted by ID) — pass this back " +
        "as clientPartyFingerprint on your NEXT take_turn call so the server can catch HP/location drift (a missed or misread delta) before it compounds. " +
        "Does NOT cover NPC/need/memory/rumor drift (covered by the periodic reseed + integrity pressure). " +
        "Also useful to self-check your own narrative model against right now: if this doesn't match what you believe about the party, trust this.")]
    public string? PartyFingerprint { get; set; }

    [Description("Monotonically increasing counter, bumped once per committed take_turn mutation for this campaign (never on pure-query calls). " +
        "Not currently used for gap detection server-side — informational, for logging/debugging state sync issues.")]
    public long WorldSequence { get; set; }

}

/// <summary>
/// Delta-mode world-state view (P2-13 pure suppression): only the rumor/quest/faction WorldChanges
/// actually applied this turn, rather than the full lists BuildWorldStateAsync would return. Time sends
/// only when day/time-of-day shifted since last surfaced; WorldPressure sends only when the evaluated
/// set differs from the last-surfaced set (PressureUnchanged:true otherwise). New/changed pressure
/// always sends — only identical-to-last may drop.
/// </summary>
public class WorldStateDeltaView
{
    [Description("Current campaign time — sent only when day/time-of-day shifted since last surfaced; null means unchanged since the last delta that carried it.")]
    public CampaignTimeView? Time { get; set; }

    [Description("True when the evaluated pressure set was identical to the last-surfaced set, so WorldPressure was suppressed to save tokens. New/changed pressure always sends.")]
    public bool PressureUnchanged { get; set; }

    [Description("Active world pressure nags — populated only when the evaluated set differs from the last-surfaced set (grouping-key keyed; new/changed always sends). Empty + PressureUnchanged:true means 'same as last time'.")]
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

    [Description("WorldChanges applied to this party member this turn — including the member's own applied movers from this call's Changes[] (a PartyDelta entry exists precisely to say 'this member moved'; NeedsMoved carries the live post-commit values as receipt). Only background engine-authored need/attribute tick noise stays filtered.")]
    public List<WorldChange> Changes { get; set; } = [];

    [Description("P2-12: live need values for this member's needs that crossed the significance or cumulative-drift gates this turn (same ChangedNeedsKeys machinery as Npcs[] KnownNeeds filtering) — includes the caller's own applied NeedChange movers for this member, not just ambient drift. Null when no need moved enough to mention — an absent PartyDelta entry likewise means 'no party change worth surfacing', not 'no party'.")]
    public Dictionary<string, float>? NeedsMoved { get; set; }

    [Description("RP-advisory initiative/memory enrichment, present only for the up-to-2 NPCs selected this call (see take_turn's tool description). Null otherwise, and always null for player characters.")]
    public NpcInitiativeEnrichment? Initiative { get; set; }

    [Description("Set when this NPC has a high-salience memory that exists but wasn't surfaced via Initiative this turn (wasn't one of the up-to-2 selected) — a nudge to call get_entity/recall_history rather than assume nothing relevant is aging in the background. Null otherwise.")]
    public string? MemoryHint { get; set; }
}
