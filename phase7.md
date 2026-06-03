# Phase 7: Deep Open-World Mechanics, Factions, Quests & Sustained Laziness Mitigation

**Status:** Authoritative detailed plan and task breakdown for the next major body of work after Phase 6 (persistence, transients, auto-GC, creation surfaces, and foundational anti-laziness pressures/nags).

**Date:** June 2026 (immediately following review + iteration work on laziness amplification in the current session).

**Source / Context:** Builds directly on:
- The original "Missing Open World Features" problem statement (travel/spatial, factions/ecosystems, structured quests + the "API Surface & LLM Laziness Evaluation" / "Silly Factor" complaint about forcing perfect polymorphic `commit` JSON arrays even for pure flavor narration).
- `docs/Phase6_OpenWorld_Design.md` (the "Schrödinger's World" philosophy, opt-in persistence, engine-owned invariants + GC, immediate Co-DM `WorldPressure` nags with copy-paste JSON, `IsLocationAnchored`, `KeepAlive`, `TransientEvictionRule`, `location_create` auto-link, etc.).
- `detailed-implementation-plan.md` (all prior phases completed; Phase 6 marked with model + handler + GC + basic pressures work; recent "Laziness Mitigation Iterations" section added for the GetHelp expansion, additional pressure cases (reverse-link detection, flavor vacuum nudges, enhanced rumor/event/char hints with JSON), test additions, README/recommended-prompt updates).
- The concrete iterations performed in this task (expanded `get_help`, richer pressures in `GetScene` + `GetWorldState`, new regression test for broken links + vacuum, doc updates) which were executed first as "iterate on more laziness mitigation".
- User confirmation that the prior suggestions/possible next steps are liked and should be included.

**Guiding Philosophy (recap + evolution):** The engine must continue to do the hard/boring parts LLMs are bad at (invariants, cleanup, maintenance, immediate actionable feedback) so the LLM DM can be creative, exploratory, or "lazy" without producing broken or bloated worlds. Phase 6 proved the value with ready-JSON pressures on `get_scene`. Phase 7 extends this protection to the new deep mechanics (a travel action that forgets to `commit` a location change or quest progress must produce a nag; a faction war that should affect local reps must surface pressure; a quest objective completion must be easy to record without perfect state machine memory).

New mechanics (travel, factions, quests) must be **laziness-resistant by design**:
- High-level "narrate a journey" or "the guild declares war" should be possible with minimal or guided `commit` usage.
- Engine rules + views + pressures provide the structure and the "what to paste next" hints.
- Pure flavor (scenic travel descriptions, background faction flavor text, quest rumors) stays cheap (PoIs, events, lore) until the LLM or sim decides it is structural.

**Overall Goals for Phase 7:**
- Deliver the three explicitly called-out missing open-world pillars so the system feels like a living, explorable, factional, quest-driven world rather than a room graph + rumor log.
- Significantly reduce remaining LLM friction ("silly factor", ID recall, exact `$type` + shape memory, ignoring pressures) through more surfaces, richer nags, better ergonomics, and adversarial testing.
- Keep backward compatibility for existing campaigns, `commit` flows, and simulation.
- Make the new features first-class citizens of the pressure/nag system and the "get_help + recommended prompt" story.
- Provide a clear, granular, verifiable task list so implementation (human or agent) can proceed without re-inventing edge cases.

**Success Criteria (measurable):**
- A party can travel between two locations with explicit or implicit distance/time cost, terrain effects on needs/rolls, and a random encounter table that can produce transients or events (all via engine-supported paths that produce pressures if the LLM is incomplete).
- Factions exist as first-class entities; NPCs can have reputation with them; background sim can evolve faction state (war/peace/trade) and emit observable effects (rumors, relationship deltas, local prices via attributes, new events).
- Quests exist with objectives (list of `{description, state: Open/InProgress/Complete/Failed, optional reward hint}`), can be given via `character_create` or dedicated, progress recorded via simple `commit` mutations, visible in `get_scene`/`get_world_state`/`get_npc_context`, and drive narrative pressure when stale.
- Every significant new action (travel, faction action, quest progress) that is omitted or half-done produces an immediate, copy-pasteable `WorldPressure` item containing the minimal correct `commit` JSON on the next relevant read (`get_scene` on the area, `get_world_state`, or `advance_world`).
- `get_help` contains comprehensive, copy-pasteable examples and "lazy vs correct" walkthroughs for travel, a faction war, and a 3-objective quest chain.
- The recommended system prompt and README prominently call out the pressure discipline and the new primitives.
- Adversarial "LazyLLM" tests (extended harness) deliberately omit the second half of a travel/quest/faction beat and assert that the engine compensated (world state correct, pressure was shown with usable JSON, no crash or silent failure).
- Full test suite green; `dotnet build` clean; manual "MCP client" mental model check (or actual if available) shows the new `$type`s and pressure examples are discoverable.
- No regression in Phase 0-6 behavior (multi-campaign, combat, existing transients, etc.).
- Performance: a map with 200 locations + 50 factions + 30 active quests + 100 transients does not cause noticeable slowdown on `get_scene` or a 5-day `advance_world` (new indexes/queries must be added where needed).

**Non-Goals (for this phase):** Full procedural worldgen, complex diplomacy UI, persistent economy simulation with prices changing per vendor, 3D coordinates or full hex grid rendering (graph + optional distance metadata is enough), deleting transients (keep docs as in Phase 6).

---

## 1. Travel & Spatial Distance (Hex-Crawl / Overland / Dungeon Travel)

**Problem (from original quote):** "Locations are currently graph nodes connected by exits. There is no concept of physical distance, travel time, weather, terrain types, or random encounter tables (hex-crawl mechanics)."

**Phase 7 Goal:** Make travel a first-class, simulatable, pressure-aware action that feels like exploration without forcing the LLM to manually track days/movement points every time.

**Key Design Decisions:**
- Keep the core model as a directed graph (exits) for simplicity and compatibility. Add optional *metadata* for distance/time rather than requiring a full coordinate system.
- Extend the existing `LocationExit` record in `Location.cs` (currently `record LocationExit(string TargetLocationId, string Description, string? LockCondition = null)`) with additional optional constructor parameters:
  - `int? TravelCostHours = 0` — default 0 = "instant / adjacent room" to maintain full backward compatibility.
  - `string? Terrain = null` — open enum: "road", "forest", "mountain", "swamp", "dungeon_corridor", etc. Used by rules for modifiers and encounter table seeding.
  - `string? EncounterHint = null` — e.g., "low", "wilderness_bandits", "dungeon_undead". Points to a lightweight table or just a seed for the rule.
  - **Record must stay a positional record** so existing deserialized data (with just the three original args) does not break. New fields are keyword-only and nullable/defaulted.
- New dedicated `TravelChange` WorldChange derived type (preferred over repurposing `activity` for clarity and to carry distance metadata + narrative):
  - Registered as `$type: "travel"` in the `[JsonPolymorphic]` attribute list on `WorldChange`.
  - Records the move (updates `CurrentLocationId` + `CurrentActivity`).
  - Carries travel metadata used by `TravelEncounterRule` (terrain, hours spent, encounter hint override).
  - The handler applies time cost by emitting a small `CampaignTime` delta consumed by existing Advance logic, rather than calling time advance directly.
  - Optionally triggers needs (fatigue, hunger) based on distance/terrain via child `NeedChange` deltas within the same batch — uses the existing intra-batch child-mutation dispatch from Phase 6.
  - Optionally rolls for encounter using the exit metadata + ruleset dice (produces transient NPCs via internal `CharacterCreate` child mutations, or `EventOccurred` deltas).
- New `ISimulationRule` `TravelEncounterRule` implementing the existing `ISimulationRule` contract with `Order = 25` (after `ScheduleEvaluationRule` at ~10, before `NeedsAccumulationRule` at ~50, and well before `TransientEvictionRule` at 100). Consults exit metadata + current `CampaignTime` + random to produce:
  - Narrative events.
  - Deltas (new transients placed via the Phase 6 registration mechanism, or status/need changes, or new PoIs discovered).
  - `WorldPressure` items when the LLM "forgets" to record the arrival properly.
- Weather / global state: lightweight per-campaign `WorldConditions` document (season, current weather) that travel rules consult. Can be mutated via `attribute` on a singleton "world" character or a dedicated small change type, or advanced by rules. Intentionally kept out of scope for the first implementation pass — stub it as `null`-tolerant so the rule degrades gracefully.
- Dungeon vs Overland distinction: use the existing `LocationType` enum (`Wilderness`/`Region` vs `Room`/`Building`) + exit terrain metadata. No new enum needed initially.
- `get_scene` on a location after travel should reflect any new PoIs discovered en-route (engine can add via internal updates or events).
- Pressure integration (laziness mitigation):
  - If party "narrates travel" but no `commit` / activity change recorded for the destination: next `get_scene` at claimed location emits a travel pressure with ready `activity` (or `travel`) + `need` example.
  - If travel distance was large (`TravelCostHours > 8`) but no needs advanced or time passed: pressure.
  - Encounter that should have produced a transient but wasn't anchored: nag on next `get_scene`.
- Tool surface: optional thin `quick_travel` tool that internally builds the correct small commit array (LLM still sees the result and the underlying mutations). This is a *forgiveness / ergonomics* win without weakening the explicit model (the underlying mutations are still produced and visible).
- Backward compat: existing 0-cost exits (`LocationExit` records with only the original three args) continue to work exactly as before. All new fields are optional/defaulted.

**Model Changes (concrete):**
```csharp
// Location.cs — extend the existing record:
public record LocationExit(
    string TargetLocationId,
    string Description,
    string? LockCondition = null,
    int? TravelCostHours = 0,      // NEW — 0 = instant/adjacent
    string? Terrain = null,        // NEW — "road", "forest", "mountain", etc.
    string? EncounterHint = null   // NEW — "low", "wilderness_bandits", etc.
);

// WorldChanges.cs — new derived type:
[Description("Record a party or character travel between two connected locations. " +
  "The engine applies time cost, terrain-based need deltas, and optionally rolls a random encounter. " +
  "Always supply both characterId (or party IDs) and destinationLocationId. " +
  "Example: { \"$type\": \"travel\", \"characterId\": \"chars/pc1\", " +
  "\"destinationLocationId\": \"locations/highpass\", \"narrative\": \"Crossed the High Pass over 3 days\" }")]
public class TravelChange : WorldChange
{
    [JsonPropertyName("characterId")]
    public string CharacterId { get; set; } = default!;
    
    [JsonPropertyName("destinationLocationId")]
    public string DestinationLocationId { get; set; } = default!;
    
    [JsonPropertyName("narrative")]
    public string? Narrative { get; set; }
    
    // Optional override — if omitted, engine reads from the LocationExit metadata
    [JsonPropertyName("travelCostHoursOverride")]
    public int? TravelCostHoursOverride { get; set; }
    
    [JsonPropertyName("terrainOverride")]
    public string? TerrainOverride { get; set; }
}
```
- Optional: new `TravelLog` event category for long campaigns (audit trail of "what did we cross?").

**Handlers / Rules:**
- `TravelChangeHandler` (new `IWorldChangeHandler`) — updates `CurrentLocationId`, emits child `NeedChange` deltas based on terrain + hours, stamps `LastVisitedDay` on the destination.
- `TravelEncounterRule` (`ISimulationRule`, `Order = 25`) — on `advance_world` or triggered by a `TravelChange` commit, consults exit metadata + random + ruleset dice, produces narrative + optional transient NPCs or events.

**Views / Pressure:**
- `SceneView` / `WorldStateView` surface "last travel" or "known routes" summary (synthesized from recent events).
- `GetScene` pressures gain travel-specific nags: e.g., "You described a 3-day mountain crossing but the model still thinks you are in the foothills. Commit a travel + time + fatigue change."

**Laziness Examples in Pressure:**
```
ENGINE WARNING: Travel to "locations/highpass" was narrated but no corresponding activity/location
change + time cost was recorded. Use:
[
  { "$type": "travel", "characterId": "chars/pc1", "destinationLocationId": "locations/highpass",
    "narrative": "Party crossed the High Pass (3 days, snow, minor frost nip)." },
  { "$type": "need", "characterId": "chars/pc1", "need": "tiredness", "delta": 25 },
  { "$type": "event", "category": "Travel", "summary": "Party crossed the High Pass (3 days, snow, minor frost nip).",
    "involved": ["chars/pc1", "locations/highpass"] }
]
```

**Testing:** Deterministic travel scenario in the existing `SimulationHarness/DeterministicScenarios.cs`; random encounter matrix test; pressure fires when LLM does "we travel" narration without commit.

---

## 2. Factions & Ecosystems

**Problem:** "The world tracks individual NPC relationships, but lacks a macro-level faction system (e.g., Guilds, Kingdoms) to track global reputations or simulate wars/economy in the background."

**Goal:** Factions are observable, influence individual NPCs, and can evolve via background rules, producing pressures and observable world changes without the LLM having to manually maintain 50 relationship entries.

**Design:**
- New model: `Faction` (new file `Models/Faction.cs`)
  - `Id` (e.g. "factions/thieves-guild"), `Name`, `Description`, `FactionType` (enum: Guild, Kingdom, Cult, MerchantHouse, MilitaryOrder, ...).
  - `string? ControllingTerritory` — optional location ID that is the faction's "seat". For index queries.
  - `List<string> TerritoryLocationIds` — location IDs the faction controls or has influence in.
  - `List<string> KnownLeaderIds` — Character IDs.
  - `int InfluenceLevel` (0–100) — abstract power/reach.
  - `Dictionary<string, FactionStance> StanceToward` — factionId → `FactionStance` (Neutral/Allied/Hostile/TradePartner/AtWar).
  - `Dictionary<string, string> Metadata` — custom (motto, symbol, color, etc.).
  - `DateTime LastUpdated`.
- Per-Character extension: add `Dictionary<string, int> FactionReputations` to the existing `SocialProfile` class (factionId → score, -100 to +100). Merged alongside `Relationships`. Keeps the existing social profile pattern.
- New WorldChange types:
  - `faction_reputation` — `FactionReputationChange` { `characterId`, `factionId`, `delta`, `reason` }. Adjusts `Character.Social.FactionReputations`.
  - `faction_state` — `FactionStateChange` { `factionId`, `newStance`, `targetFactionId`, `influenceDelta`, `narrative` }. Adjusts stance + influence.
- New `ISimulationRule`: `FactionEcosystemRule` (`Order = 40`, after travel encounters, before `TransientEvictionRule`).
  - On `advance_world`, looks at faction states + world conditions + random + NPC aggregate rep in the region.
  - Emits:
    - `EventOccurred` (war declared, caravan raided, trade deal signed).
    - `RumorEvolves` or new rumors (via `CampaignRepository` rumor upsert path).
    - `RelationshipChange` or `FactionReputationChange` deltas for visible NPCs in affected territories.
    - `NeedChange` or `AttributeChange` (e.g., "prices high because of embargo" → attribute on merchants).
    - Internal transients or PoI updates for "refugees in the square" (using Phase 6 child mutation infra).
  - Produces narratives + `WorldPressure` items.
- Integration with existing:
  - `get_scene` / `get_npc_context` / `get_world_state` surface relevant faction reps + current faction events for the region (filter by `TerritoryLocationIds` overlap with current location's parent chain).
  - `Character.Psychology.KnowledgeGraph` can be auto-seeded with faction facts by `FactionEcosystemRule`.
  - `TravelEncounterRule` consults faction control of a region for encounter bias or travel risk.
- Pressure / laziness mitigation:
  - If a major faction event happened in sim but LLM didn't surface it in narration or record local effects: pressure on next `get_world_state` or `get_scene` in affected area, with sample `event` + `faction_state` + `relationship` JSON.
  - "High-rep PC with Thieves Guild but no schedule for guild contact" → optional promotion nudge after X days.
  - Orphaned faction rep on a GC'd transient → engine ignores (character is gone; rep stays on the faction side if modeled there, otherwise it simply vanishes with the document). Document this policy clearly in the model comment.
- Tooling: `get_faction_context` (new thin tool, similar shape to `get_npc_context`) returning faction doc + recent faction events + territory NPCs. Or surface purely via `search_world` + existing views — **decide during 7.1: prefer the thin tool to keep discoverability high**.
- Upsert / create path: `faction_create` WorldChange type (same pattern as `location_create`) or use `event` + rule to bootstrap.

**Models to add:** `Faction.cs`, `FactionType` enum, `FactionStance` enum; extend `SocialProfile` in `Character.cs` (add `FactionReputations`); possibly extend `Location` with `ControllingFactionId` (nullable, no-break).

**Concrete model skeleton:**
```csharp
// Models/Faction.cs
public class Faction
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public FactionType FactionType { get; set; }
    public string? ControllingTerritory { get; set; }
    public List<string> TerritoryLocationIds { get; set; } = [];
    public List<string> KnownLeaderIds { get; set; } = [];
    public int InfluenceLevel { get; set; } = 50;
    public Dictionary<string, FactionStance> StanceToward { get; set; } = [];
    public Dictionary<string, string> Metadata { get; set; } = [];
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public enum FactionType { Guild, Kingdom, Cult, MerchantHouse, MilitaryOrder, Criminal, Religious }
public enum FactionStance { Neutral, Allied, TradePartner, Hostile, AtWar, Subjugated }
```

**Laziness Example Pressure:**
```
ENGINE WARNING: The Iron League faction has raised tariffs after last week's sim event. Local traders
(including Bram) have +15 'economic_pressure' attribute and worse mood. If your narrative didn't
reflect this, record via commit:
[
  { "$type": "attribute", "characterId": "chars/bram-ironarm", "attribute": "economic_pressure", "value": 15, "isDelta": true },
  { "$type": "mood", "characterId": "chars/bram-ironarm", "newMood": "worried about the new tariffs" },
  { "$type": "rumor", "rumorId": "rumors/iron-league-tariffs", "newState": "Spreading",
    "newText": "The Iron League raised tariffs on river trade. Merchants are nervous." }
]
```

**Future-proof:** The rule is pluggable; simple version does random + rep-threshold triggers. Later versions can be more sophisticated without touching the model.

---

## 3. Structured Quest Tracking

**Problem:** "The system relies heavily on 'Rumors' and 'Events' for narrative momentum. A formal quest state machine (objectives, quest givers, completion states) would anchor long-running campaigns better than relying purely on ambient memory."

**Goal:** Quests are first-class, durable, queryable, progressable with low-friction `commit`, visible where relevant, and generate pressure when neglected.

**Design:**
- New model: `Quest` (new file `Models/Quest.cs`)
  - `Id` ("quests/dragon_hoard_01"), `Title`, `GiverId` (char or faction ID).
  - `List<QuestObjective> Objectives` (each: `Description`, `State` (Open/InProgress/Complete/Failed/Skipped), `RewardHint?`, `List<string>? InvolvedIds`, `int? DayStarted`, `int? DayCompleted`).
  - `QuestState OverallState` (derived or explicit — start derived, override if needed).
  - `string? Category`, `QuestUrgency Urgency`, `List<string> RelatedLocationIds`, `List<string> RelatedFactionIds`.
  - `string? DmNotes` (LLM DM only), `List<string>? VisibleToCharacterIds`.
  - `int LastUpdatedDay`.
- New WorldChange types:
  - `quest_create` — `QuestCreate` { `questId`, `title`, `giverId`, `objectives: List<{description, rewardHint?}>`, `category?`, `urgency?`, `relatedLocationIds?`, `relatedFactionIds?` }.
  - `quest_progress` — `QuestProgress` { `questId`, `objectiveIndex` (int, 0-based) OR `objectiveName` (string match), `newState`, `narrativeNote?`, `involvedIds?` }. Handler resolves by index first, then by name prefix match. This keeps the surface small and LLM-friendly — one mutation advances one objective; engine validates state machine or just warns on impossible transitions.
- **Quest Giver Eviction Safety:** If a quest giver is a transient NPC (no `Schedule` and no `KeepAlive`) and the quest is active (any objective in Open/InProgress), `TransientEvictionRule` must skip eviction and emit a `WorldPressure` instead: "Quest '{title}' giver '{name}' is transient but has an active quest. Set `keepAlive: true` or assign a schedule." The rule checks active quests for the character's ID via the new `Quest_Search` index before evicting. This check must be a lightweight index query, not a full scan.
- Handlers: `QuestCreateHandler`, `QuestProgressHandler`. Each loads/creates the quest doc, updates the specific objective, emits `EventOccurred` deltas if state transitions are "complete" or "failed", clamps/warns on impossible transitions (e.g., Complete → Open is a warning, not a hard fail).
- Integration:
  - `get_scene` (for location-relevant quests: `RelatedLocationIds` overlap), `get_world_state`, `get_npc_context` (giver's active quests), `search_world` all surface active quests (light summaries + objective counts or full when focused).
  - `NpcPresenceSummary` or new `QuestSummary` records (keep compact: `{ questId, title, openObjectiveCount, urgency }`).
  - Behavioral synthesizer can mention "owes the party a favor for completing the rat quest."
  - Rumors/Events can reference or auto-create light quest seeds ("Unresolved" events with a convention can be turned into quests via a helper rule or LLM `quest_create` action).
- Simulation rule: `QuestStalenessRule` (`Order = 45`) that for quests with stale open objectives emits narrative pressure + "consider progressing or failing" hints with ready JSON.
- Pressure / laziness (key):
  - Stale open quest in the current region or with a present NPC: "Quest 'Clear the Cellar Rats' has had its first objective open for 12 days. Progress or abandon: `[ { \"$type\": \"quest_progress\", \"questId\": \"quests/rats_01\", \"objectiveIndex\": 0, \"newState\": \"Complete\", \"narrativeNote\": \"Rats cleared during tavern brawl.\" } ]`"
  - Quest giver present but no acknowledgment of completed objective: pressure.
  - Completed quest with no reward recorded: gentle nag.
- Tool: optional `get_quest_details(questId)`, but most via existing views. `commit` is the write path.
- World building: `quest_create` inside `commit` at campaign start, or via `upsert` style if needed.
- State machine: objectives are independent enough for LLM flexibility (no over-constrained engine rules initially). Handler emits warnings on impossible transitions but does NOT hard-reject.

**Concrete model skeleton:**
```csharp
// Models/Quest.cs
public class Quest
{
    public string Id { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string? GiverId { get; set; }
    public List<QuestObjective> Objectives { get; set; } = [];
    public QuestState OverallState { get; set; } = QuestState.Open;
    public string? Category { get; set; }
    public QuestUrgency Urgency { get; set; } = QuestUrgency.Normal;
    public List<string> RelatedLocationIds { get; set; } = [];
    public List<string> RelatedFactionIds { get; set; } = [];
    public string? DmNotes { get; set; }
    public List<string>? VisibleToCharacterIds { get; set; }
    public int LastUpdatedDay { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public record QuestObjective(
    string Description,
    QuestState State = QuestState.Open,
    string? RewardHint = null,
    List<string>? InvolvedIds = null,
    int? DayStarted = null,
    int? DayCompleted = null
);

public enum QuestState { Open, InProgress, Complete, Failed, Skipped }
public enum QuestUrgency { Low, Normal, Urgent, Critical }
```

**Laziness Win:** LLM can say "we finished the rats for Bram" and then `commit` a single small `quest_progress` (or even just an `event` + `relationship` if they want to stay lazy); the engine keeps the durable record and will remind on future visits.

---

## 4. Further / Sustained Laziness Mitigation (Cross-Cutting + New)

Build on the Phase 6 foundation and the iterations already performed in this session (GetHelp expansion with Lazy Tavern example, additional GetScene pressures for one-way links + flavor vacuum, GetWorldState rumor/event/char pressures with embedded JSON snippets, README + recommended prompt updates, dedicated regression test).

**Specific further items to implement in Phase 7 (or as 7.0 polish):**

### 4.1 Pressure Cap & Deduplication (NEW — Critical)

Currently there is no cap on how many `WorldPressure` items can be returned in a single `get_scene` or `get_world_state` response. With Phase 7 adding travel, faction, and quest nags on top of existing checks, an LLM could receive 15+ pressure items simultaneously — causing context overflow and LLM truncation of the exact JSON it needs to paste.

**Rules to implement:**
- **Hard cap of 5 pressure items per response** (configurable via `CampaignConfig`, default 5). When more than 5 would fire, prioritize by severity: `ENGINE WARNING` > `NARRATIVE PROMPT` > `SUGGESTION`. Within the same tier, prefer newer issues.
- **Deduplication by (type, entity-id) pair with a cooldown of 3 campaign days**: if a pressure for the same issue was already surfaced within the last 3 days, suppress it (or collapse it to a one-liner "still broken — JSON below still applies"). Track in a lightweight `PressureCooldowns: Dictionary<string, int>` on the `Campaign` meta document (key = `"{type}:{entityId}"`, value = day last surfaced). This avoids a new collection and keeps it co-located with the campaign.
- **Escalation after 3+ cooldown cycles:** after the same pressure has been suppressed 3 times, escalate the prefix from `NARRATIVE PROMPT` to `ENGINE WARNING` and append "This has been flagged for {N} days."
- On `commit` success, invalidate relevant cooldown entries for the entities that were changed (so fixing an issue clears the nag immediately on the next read).

### 4.2 Nag Amplification & Quality

More detection cases in `GetScene`/`GetWorldState` (beyond Phase 6):
- Dangling item holders after eviction: an `Item` whose `HolderId` no longer exists (location or character GC'd) → pressure with `item_transfer` example to a real location.
- "Created location never visited but has transients" — a location whose `LastVisitedDay == null` but has characters anchored to it → prompt to visit or evict.
- Quest-giver in scene with open objectives for the party (see §3 above for giver eviction check; here it's a lighter nag).
- Faction-controlled area with mismatched rep — a PC with very negative reputation in a faction that controls the current area should nudge the LLM to add consequences.
- Travel distance recorded (`TravelCostHours > 8`) but no time/need cost → pressure (see §1 for the travel pressure template).
- "Contextual help" in pressure: when possible, include 1-2 real entity IDs from the current scene (already doing for some; expand to new nag types).

### 4.3 Ergonomics & Forgiveness

- Optional "lenient" mode helpers: `location_create` on an existing ID becomes an upsert with a loud summary warning rather than hard rejection. The pressure is cleared if the operation was otherwise valid.
- A thin convenience tool `quick_travel` that internally builds the correct small commit array (LLM still sees the result and the underlying mutations).
- Better error messages on bad commit JSON: include the closest valid example from `get_help` for the offending `$type`. The existing `WorldChangeDispatcher` error path is the right place.
- ID normalization / suggestion in errors: "Did you mean 'locations/tavern' (existing)?" — leverage the existing `Location_Search` index to find the closest Levenshtein match for unknown IDs. Keep it as a best-effort hint, not a hard dependency.

### 4.4 Discoverability & Memory Aids

- Expand `get_help` further with Phase 7 examples (travel + encounter, faction war, full quest chain).
- Enrich `get_world_state` / `get_scene` with a `SuggestedCommitExamples` field (populated from the current pressures + context). This is the `WorldStateView` and `SceneView` extension point.
- `get_current_campaign` / `get_config` can return "active pressures summary" or "open quests count."

### 4.5 Docs & Prompt Surface (Non-Negotiable)

- Update `get_help`, recommended-system-prompt.md, README, this plan, Phase6 design (add "Phase 7 builds on..." note).
- Add "Pressure Discipline" as a top-level sacred rule in the prompt.
- 3–5 full end-to-end lazy-safe examples per major new feature.

### 4.6 Adversarial Testing (the proof)

- Create / extend `SimulationHarness/LazyLlmScenarios.cs` (complement the existing `DeterministicScenarios.cs` and `LlmSimulator.cs`).
- Scenarios that deliberately do the "wrong" thing (narrate travel without commit, accept quest but never record progress, cause a faction event via sim but ignore the pressure) and assert:
  - Engine still produces correct final state (via the rule/nag compensation).
  - The exact pressure text contains usable JSON.
  - After the "fix commit" using the pressure example, the pressure disappears on next read.
  - Pressure cap fires when more than 5 issues are simultaneously active (assert exactly 5 returned, assert correct priority ordering).
- Include new mechanics in the existing `LlmSimulator` + `DeterministicScenarios`.
- Add fuzz/property tests for the new WorldChange types (bad objective states, negative travel cost, missing required fields, etc.).

---

## 5. Architecture, Implementation & Phasing

**High-Level Approach:**
- Additive: new models + optional fields on existing (`LocationExit`, `Character`, etc.) + new polymorphic WorldChange derived types (with excellent `[Description]` and JSON examples in comments).
- Handlers registered in `Program.cs` (same pattern as Phase 6).
- New `ISimulationRule`(s) registered; they emit deltas (including child creates) + narratives.
- Pressures remain a *tool-layer* concern (or repo returns data + tool turns it into Co-DM voice with ready JSON). This keeps repo tests simple.
- Use Phase 6 registration + `ChangeContext` infra for same-batch create-then-mutate (e.g., encounter creates a transient then immediately applies a status).
- Views extended (add `ActiveQuests`, `RelevantFactions`, travel metadata to `SceneView`, etc.). Keep responses compact (summaries + counts; full details via dedicated or on-demand).
- All new public surfaces get rich XML + tool `[Description]`.
- Sanitization, multi-campaign notes, JsonPolymorphic, etc. followed exactly.

**Rule Order Reference (must not conflict):**

| Order | Rule |
|-------|------|
| 10 | `ScheduleEvaluationRule` (existing) |
| 25 | `TravelEncounterRule` (new §1) |
| 40 | `FactionEcosystemRule` (new §2) |
| 45 | `QuestStalenessRule` (new §3) |
| 50 | `NeedsAccumulationRule` (existing) |
| 70 | `RumorDecayRule` (existing) |
| 80 | `StatusExpiryRule` (existing) |
| 100 | `TransientEvictionRule` (existing, extended with quest-giver guard) |

**Granular Phased Breakdown (do in this order; verify build + relevant tests after each logical group):**

### 7.0 Polish / Laziness Amplification (can land early, independent of new mechanics)
- Implement pressure cap (§4.1): add `PressureCooldowns` to `Campaign` meta, implement cap logic in `CampaignTools.GetScene` + `GetWorldState` helper.
- More pressure cases (§4.2, excluding quest/faction/travel which require 7.1+ models): dangling item holders, never-visited locations with transients.
- Nag deduplication + cooldown tracking.
- GetHelp + prompt + README further updates with Phase 7 preview examples.
- `LazyLlmScenarios.cs` skeleton + 2–3 basic adversarial tests (even before new types exist).
- Small forgiveness: idempotent creates with warning (upsert path).
- **Verification:** new tests pass, manual review of sample pressure text + get_help output, full suite green.

### 7.1 Data Model & Core Types
- Add/extend `LocationExit` (new optional constructor params).
- Add `Faction.cs`, `Quest.cs`, `QuestObjective` record; add enums (`QuestState`, `QuestUrgency`, `FactionType`, `FactionStance`).
- Extend `SocialProfile` in `Character.cs` with `FactionReputations`.
- Add the new `WorldChange` derived types (`TravelChange`, `FactionReputationChange`, `FactionStateChange`, `QuestCreate`, `QuestProgress`, optionally `FactionCreate`) with full `[Description]` + examples.
- Update `WorldChanges.cs` `[JsonPolymorphic]` / `[JsonDerivedType]` attribute list + big comment block.
- Update `JsonSanitizer` for any new types that need it.
- Add `Quest_Search` and `Faction_Search` indexes (needed by rules and views).
- Add `PressureCooldowns: Dictionary<string, int>` to `Campaign.cs`.
- **Also:** Update GetHelp string and recommended prompt with early examples.
- **Verification:** serialization tests, build clean, `WorldChangeDispatcher` tests updated for new type list.

### 7.2 Handlers + Dispatcher + Registration
- Implement `TravelChangeHandler`, `FactionReputationChangeHandler`, `FactionStateChangeHandler`, `QuestCreateHandler`, `QuestProgressHandler`. Use registration for intra-batch create-then-mutate.
- Wire into `Program.cs`.
- Enhance existing handlers (`ActivityChangeHandler`, `EventOccurredHandler`) if they need to interact with new types.
- Update `WorldChangeDispatcher` pre-load logic to batch-load `Faction` and `Quest` documents for the new IDs found in a change batch (same pattern as character/item pre-load in §78-80 of the dispatcher).
- **Verification:** unit tests for each handler (happy + error paths, auto side effects), dispatcher duplicate-claim tests, intra-batch `character_create` + `quest_progress` in same commit.

### 7.3 Simulation Rules + Engine Integration
- Implement `TravelEncounterRule` (Order=25), `FactionEcosystemRule` (Order=40), `QuestStalenessRule` (Order=45).
- Extend `TransientEvictionRule` (Order=100) with the quest-giver guard (§3 above).
- Register in DI (`Program.cs` + test harnesses).
- Wire into `DefaultSimulationEngine` (already prepared — just DI registration).
- Handle time costs inside travel handler (emit `CampaignTime` advance delta or use existing path).
- **Verification:** rule unit tests (with fake `SimulationContext`), integration in `AdvanceWorld` tests, eviction + quest-giver guard tests.

### 7.4 Read Paths, Views & Tool Wiring (including Pressures)
- Extend `SceneView`, `WorldStateView`, `NpcPresenceSummary` (add `ActiveQuestSummaries`, `RelevantFactions`, `LastKnownTravel`; keep them small).
- Update `GetSceneAsync`, `GetWorldState`, `GetNpcContext`, `SearchWorld` to include new data (filter by region/location relevance where possible).
- New tools if valuable: `get_faction_context`, `get_quest_details` — **implement both as thin wrappers** to keep discoverability high (LLMs can find them via `get_help`; they reduce the need to parse `search_world` results).
- **Pressure wiring (the laziness heart):** in `CampaignTools.GetScene`, `GetWorldState`, `AdvanceWorld` (and new travel tool) — add the new `ENGINE WARNING` / `NARRATIVE PROMPT` cases with ready JSON for travel, faction, and quest mutations. Apply the pressure cap + cooldown from §4.1.
- Implement `SuggestedCommitExamples` array on the views (§4.4), populated from current pressures + context.
- `Visit stamping / LastVisited` still works; `TravelChangeHandler` stamps destination.
- **Verification:** repo + tool tests (including the style of the ones added in this session), pressure text assertions, view shape tests, pressure cap assertion (>5 issues → exactly 5 returned, correct priority).

### 7.5 Documentation, Prompts & Discoverability (Non-Negotiable)
- Massive updates to `get_help()` (new top-level sections + full worked examples for a 2-leg travel with encounter, a faction war affecting a PC, a 3-objective quest from giver to reward).
- Update `docs/recommended-system-prompt.md` (add bullets for the three pillars + "use the pressures for the new types" + "pressure cap — do not flood").
- README: features, recent updates, recommended usage, table if needed.
- Update `detailed-implementation-plan.md` and `docs/Phase6_OpenWorld_Design.md` (add "Phase 7 builds on..." note).
- Add copy-paste "lazy-safe" patterns for every new primitive.
- **Verification:** `get_help` test (string contains the key sections), manual skim of docs.

### 7.6 Tests & Harness (Adversarial Focus)
- Unit + handler tests (as above for each phase).
- New / extended `LazyLlmScenarios.cs` + `DeterministicScenarios.cs` covering:
  - Travel with deliberate omission → pressure fires with correct JSON → fix commit → pressure clears.
  - Faction event via sim → LLM ignores → pressure fires → fix commit → pressure clears.
  - Quest chain with 3 objectives — complete 2, leave 1 stale → staleness pressure fires correctly.
  - Transient quest-giver eviction blocked by guard → pressure suggests `keepAlive: true`.
  - Pressure cap: >5 simultaneous issues → exactly 5 returned, correct priority ordering.
- Full E2E via `LlmSimulator` (setup quest, "ignore" it for a few advances, assert pressure + fix works).
- Performance / scale test (large map + factions + quests + advance, assert no `get_scene` slowdown).
- Fuzz on the new change shapes.
- Update existing combat/status/etc. tests if they interact.
- **Verification:** all new tests added and green before marking phase complete; run full suite.

### 7.7 Polish, Perf, Multi-Campaign, Edge Cases
- Re-home orphaned items (small rule as Phase 6 follow-up — character GC'd but item `HolderId` still points to them).
- Indexes for new queries (quests by location/giver/faction, factions by territory, etc.) — measure query plans before and after.
- Ensure every new load/query respects `effective` campaign where the pattern exists (follow `EffectiveCampaign()` convention from `CampaignTools`).
- Edge cases from Phase 6 design (exploratory `get_scene`, nag fatigue, bulk transients, item limbo) re-audited for new features.
- Optional: `quick_travel` convenience tool (builds commit internally).
- Rate limit / batch size already covers new types (existing `_commitRateLimiter` in `CampaignTools`).
- **Verification:** full test run, manual "pretend LLM session" with MCP-style calls if possible, perf spot checks.

**Order Rule:** Do not start 7.2–7.4 until 7.1 models are solid and serialization tests pass. Docs (7.5) can be interleaved but the final "update everything" pass must happen last. Laziness tests (7.6) should be written against the design early (use `[Fact(Skip = "Not yet implemented")]` stubs) and made to pass as implementation lands.

**Commit Discipline:** Semantic, one logical change per commit. Update this plan (or a tracking section) with "Completed in `<hash>`" after each.

---

## 6. Verification Checklist (Run After Major Milestones)

- `dotnet build` clean (src + tests).
- Relevant + full `dotnet test` green (no new failures, new tests added and passing).
- Sample pressure output inspected (contains usable JSON for new types, no more than 5 items in response).
- Pressure cooldown: same issue on same entity suppressed after fix for the cooldown window.
- `get_help` output contains the new sections + at least one full worked example per pillar.
- README + recommended prompt render cleanly and mention the new concepts + pressure discipline.
- At least one end-to-end "lazy path + engine compensation + fix via pressure JSON" scenario passes in the harness for each pillar.
- Quest-giver eviction guard: a transient NPC with an active quest is NOT evicted; a pressure fires instead.
- Pressure cap: a `get_scene` with >5 simultaneous issues returns exactly 5, with correct priority ordering.
- Mental model: an LLM given only the updated tool schemas + `get_help` + recommended prompt can correctly use travel, advance a quest, and react to a faction pressure without having read the C#.
- No silent breakage of Phase 6 transients/GC or existing combat.
- Performance: `get_scene` + 5-day `advance_world` with 200 locations + 50 factions + 30 quests + 100 transients completes without timeout.

---

## 7. Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Scope creep (quests + factions + travel is a lot) | Strict phase ordering + "minimum viable" first (basic models + one rule + pressure for each, then polish) |
| LLM adoption / "too many new $types" | Excellent descriptions + examples in schema + `get_help` + pressures that teach by example; start with optional/enhancement fields |
| Performance (many new queries on every scene/advance) | Add indexes early; keep views summarized; measure before and after; `Quest_Search` + `Faction_Search` must use static indexes |
| Multi-campaign debt | Do not make it worse; use existing patterns and `EffectiveCampaign()` and call out full namespacing as follow-up |
| Over-constraining the quest state machine | Keep objectives flexible; engine warns rather than rejects most transitions |
| Nag fatigue on new pressures | Cap at 5 per response (§4.1) + dedup/cooldown + concise messages with JSON only on first occurrence |
| Backward compat for existing worlds | All new fields optional/defaulted; `LocationExit` record backward-compat via nullable/defaulted new params; `FactionReputations` on `SocialProfile` defaults to `[]` |
| Quest-giver GC collision | `TransientEvictionRule` extended with lightweight index check before evicting; design documented in §3 |
| Pressure cap hiding critical nags | Priority ordering ensures `ENGINE WARNING` is always surfaced first; critical structural breaks (hallucinated location, soft-lock) keep existing prefixes |

---

## 8. How to Use This Plan

1. Update `detailed-implementation-plan.md` to point here and mark "Phase 7 planning complete."
2. Pick the next highest-priority unchecked sub-task (start with 7.0 or 7.1).
3. Implement + add tests + run verification.
4. Semantic commit + update this file with "Completed in `<short hash>` - brief note."
5. Move to the next.
6. Never declare Phase 7 complete until the verification checklist (especially the adversarial laziness tests + docs + pressure cap behavior) is fully satisfied.

**This plan + the Phase 6 design + the laziness iterations already landed are the single source of truth for making CampaignVault a true open-world engine that is kind to LLM creativity while protecting the world model.**

---

## Appendix (to be expanded during implementation)

### A. Concrete C# Sketches

See §1–§3 in this document for model skeletons. Full sketches to be added here during implementation:
- `TravelEncounterRule` — encounter roll logic + child mutation emission.
- `FactionEcosystemRule` — rep-threshold triggers + war declaration logic.
- `QuestStalenessRule` — staleness window + pressure template.
- `TransientEvictionRule` extension — quest-giver guard index query.
- Pressure cap + cooldown helper (extract to `PressureHelper` static class used by `CampaignTools`).

### B. Example Pressure Strings

See §1–§4 for per-feature pressure examples. Canonical test strings for the adversarial harness go here during 7.6.

### C. Recommended Additions to Views

```csharp
// SceneView extensions:
public record ActiveQuestSummary(string QuestId, string Title, int OpenObjectiveCount, QuestUrgency Urgency);
public record FactionPresenceSummary(string FactionId, string Name, FactionStance LocalStance, int? PlayerReputation);

// Additional SceneView fields:
IEnumerable<ActiveQuestSummary> ActiveQuests { get; }       // quests with RelatedLocationIds overlap
IEnumerable<FactionPresenceSummary> RelevantFactions { get; } // factions with territory overlap
string? LastKnownTravel { get; }                             // synthesized from recent Travel events
```

### D. Migration Notes

None required for existing data — all changes are additive. `LocationExit` record gains new nullable/defaulted parameters; old documents without the new fields deserialize with defaults (0 hours, null terrain, null encounter hint). `SocialProfile.FactionReputations` defaults to `[]` on old character documents.

### E. Phase 8 Ideas

Deeper economy (per-vendor prices, supply/demand), full procedural worldgen, player-facing quest log export, faction diplomatic event tree, weather system with `WorldConditions` first-class document, `quick_travel` tool promotion to first-class.

---

End of Phase 7 plan.