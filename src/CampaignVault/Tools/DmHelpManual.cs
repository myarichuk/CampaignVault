namespace CampaignVault.Tools;

/// <summary>
/// Canonical get_help manual body — composed at runtime with ToolCatalog and CommitEnumCheatSheet.
/// </summary>
internal static class DmHelpManual
{
    internal const string Body = @"# CampaignVault DM Manual

Welcome to the CampaignVault engine. Your role as the AI DM is to drive the narrative while letting the MCP engine handle the persistence, math, and simulation.

## Quickstart for Models
1. **Call `get_help`** (this document) and **`list_tools`** if search-based discovery only showed a subset.
2. **Establish campaign context**: `list_campaigns` → `create_campaign` (if needed), then pass `campaignName` on every tool call.
3. **Call `get_current_campaign(campaignName)`** to confirm slug, ruleset, and lock-in.
4. **Call `get_world_state`** at session start to sync time, rumors, events, and **WorldPressure**.
5. **Call `get_scene`** whenever the party enters a location. Action any `ENGINE WARNING` / `NARRATIVE PROMPT` immediately.
6. **Call `commit`** at the end of every meaningful beat (combat, conversation, discovery, persistence).
7. **Call `advance_world`** for travel, rests, or downtime skips.

## Campaign slug scoping

`campaignName` (e.g. ""dragon-heist"") is **required** on every campaign-scoped tool call. There is no per-session selection or ""current campaign"" magic.

**Workflow:**
1. `list_campaigns` to discover existing slugs.
2. `create_campaign(name: ""dragon-heist"", initialSystem: ""Dnd5e"")` if new.
3. Pass `campaignName: ""dragon-heist""` explicitly on every call to `get_scene`, `commit`, etc.

Slugs are canonicalized (spaces to hyphens, lower). Shared canon (no CampaignName on entities) is visible across campaigns.

**Slug rules:** Names are canonicalized — `""Dragon Heist""` → `dragon-heist`. Same slug = same campaign singletons (time, combat, config) and campaign-tagged entities.

**Shared universe:** Entities with **no** `CampaignName` (e.g. `chars/bob-the-assassin`) are **canon** — visible in every campaign. Campaign-owned entities use prefixed IDs (`chars/dragon-heist-volo`) and are tagged with the slug on create.

**Party roster:** Tag human PCs with `isPc: true` and NPC companions with `isPartyCompanion: true` (mutually exclusive; both require a campaign slug). `get_party` returns only those flagged characters for the active campaign — not ambient `keepAlive` NPCs. Combat accepts canon entities (no `CampaignName`) plus campaign-tagged combatants; it rejects entities tagged for a different slug.

## MCP argument normalization (limited)
The server performs a *minimal* set of transparent rewrites on incoming tool arguments before binding (via McpNormalizationMiddleware + ToolCallExamples):
- Certain legacy wrapper keys for upsert_* tools (e.g. `l` → `location`).
- Stringified `changes` array → parsed array for the `commit` tool.
- A few synonym aliases for common mis-namings on secondary params.

**Do not rely on these.** Always use the exact documented parameter names, `$type` discriminators, and `characterId`/`targetId` etc. Normalization exists only as a convenience net and produces debug logs. When in doubt, copy examples directly from `get_help`, `commit` description, or the pressure `Suggested commit` blocks.

## Tool Index by Category

{{TOOL_INDEX}}

Call `list_tools` for the full machine-readable catalog (same entries, filterable by category).

**During play, strongly prefer `commit` (especially `activity` changes) over world-builder upserts.**

{{COMMIT_ENUM_VALUES}}

**KEY PHILOSOPHY (Anti-LLM-Laziness / Schrödinger's World):** 95%+ of the world is ephemeral flavor that lives ONLY in your current narration/context. Only *meaningful* interactions (that will be referenced again, combat, theft, named recurring NPCs, discovered secret doors the party will use) should be anchored via `commit`. The engine owns linking, GC of transients, visit tracking, and nags you *immediately* on the next `get_scene` or `get_world_state` with **exact, copy-paste-ready JSON** when you (or prior LLM turns) were lazy/incomplete. Treat every string in `WorldPressure` that starts with `ENGINE WARNING:` or `NARRATIVE PROMPT:` as a **mandatory high-priority directive**. Paste the example JSON into your next `commit` call. This defeats the ""silly factor"" of being forced to output perfect polymorphic arrays for every tavern bard or crate.

## Core Gameplay Loop
1. **Start of Session**: Call `get_current_campaign` + `get_world_state` (with party location) to sync time, rumors, events, char distress, **and WorldPressure**.
2. **Exploration**: Call `get_scene` on entry. **Immediately action any ENGINE WARNING / NARRATIVE PROMPT in the WorldPressure** (use the exact JSON provided).
3. **Action & Consequence**: Narrate vividly to players. At end of beat (or when something should persist), call `commit` with array of changes. Use `activity` liberally to keep sim in sync.
4. **Time Skips / Travel**: `advance_world` (triggers needs, rumor decay, schedule eval, **TransientEvictionRule** for flavor NPCs).
5. **Deep NPC**: `get_npc_context` + `get_npc_needs`.

**Golden Rule:** If you just narrated something that should ""exist"" next time the party returns or is referenced, `commit` it (via create or update). If it's pure color, use PointsOfInterest + AmbientCrowd (lightweight, no docs created until you decide to promote).

## The Commit Tool (Universal Write)
ALWAYS call at end of combat/conversation/discovery. Atomic array of `$type` mutations. Mutations are processed atomically as a single database transaction. 

- **Batch Size Guidance:** Individual commits are capped at a maximum of **50 changes** per call. Group all related mutations (e.g. travel, quest progress, HP updates, and activity updates) into a single batch to ensure consistency.
- **ID Hygiene & Campaign Isolation:** Namespace campaign-owned entity IDs with the slug (e.g. `locations/dragon-heist-trollskull-alley`, `chars/dragon-heist-volo`). Leave `CampaignName` unset only for shared canon (e.g. Bob the assassin) that should appear in every campaign.

{{COMMIT_TYPES}}

**Travel and Resting:** Use `travel` (with `destinationLocationId`) to safely move the party; it applies time and tiredness, and evaluates encounters based on distance. Use `rest` (with `intendedHours` and `securityModifier`) for camping or sleeping. The engine rolls for interruptions. If `rest` is interrupted, resolve the encounter before committing `hp` recovery!

**Crowd interrupt:** In locations with `ambientCrowd`, after a tense beat (not every dialog line), use `scene_interrupt_check` with `locationId`, `characterId`, and optional `riskModifier` (-50..+50). Omit `riskModifier` to auto-derive from `visualTags`. One interrupt per location per day; spawns a single transient from the crowd on success.

**RECOMMENDED PATTERNS (copy-paste and adapt):**

**Conversation beats (CRITICAL — every `Conversation` event needs `involved`):**
Use the canonical copy-paste batch from the `commit` tool description (RECOMMENDED PATTERNS) or:

{{CONVERSATION_EXAMPLE}}

Discovery + activity sync:
[
  { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Party found the hidden stair."", ""involved"": [""chars/pc1"", ""locations/cellar""] },
  { ""$type"": ""activity"", ""characterId"": ""chars/guard1"", ""newLocationId"": ""locations/cellar"", ""newActivity"": ""Searching crates nervously"" }
]

**Creating on the fly (the laziness countermeasure - use these instead of pure narration for anything that might matter later):**
[
  { ""$type"": ""location_create"", ""locationId"": ""locations/tavern_cellar"", ""name"": ""Dank Cellar"", ""description"": ""Smells of damp earth..."", ""type"": ""Room"", ""connectedFromLocationId"": ""locations/tavern"", ""connectionDescription"": ""A wooden trapdoor leading down"", ""pointsOfInterest"": [""Suspicious crate"", ""Rat gnawing bone""], ""ambientCrowd"": ""2-3 rats and a drunk sleeping it off"" },
  { ""$type"": ""character_create"", ""characterId"": ""chars/cloaked_figure"", ""name"": ""Cloaked Figure"", ""currentLocationId"": ""locations/tavern_cellar"", ""currentActivity"": ""Watching the party"", ""keepAlive"": false, ""notes"": ""Offered a map for coin."" }
]

Later promote a transient (so it survives GC and participates in AdvanceWorld):
[
  { ""$type"": ""schedule_change"", ""characterId"": ""chars/cloaked_figure"", ""schedule"": { ""defaultLocationId"": ""locations/market_square"", ""routines"": [ { ""condition"": ""Any"", ""locationId"": ""locations/market_square"", ""activity"": ""Haggling"", ""probability"": 0.8 } ] } }
]

**Engagements & Spatial Positions:** pairwise state (`engagement_relation`) vs. relative placement (`spatial_position`). Both now use `characterId` + `targetId` (or `targetIds`).

Categories for `engagement_relation`: `Physical`, `Social`, `Medical`, `Attention`, `Proximity`. Use a freeform `verb` (e.g. ""grappling"", ""ranting at"", ""stitching""). Omit `restrictionLevel` to use category defaults — Physical/Medical = Hard (blocks `travel` + scene pressure), Social = Soft (pressure only), Attention/Proximity = None (informational). Override with `restrictionLevel` when a beat must hard-lock travel (e.g. farewell embrace).

`distanceBand` values: `Touch`, `Close`, `Near`, `Far`, `Distant`. Optional `bearing` and `zone`.

Tavern example (drunk five paces from the party, ranting):
[
  { ""$type"": ""spatial_position"", ""characterId"": ""chars/drunk"", ""targetId"": ""chars/pc"", ""distanceBand"": ""Near"", ""zone"": ""bar"" },
  { ""$type"": ""engagement_relation"", ""characterId"": ""chars/drunk"", ""targetId"": ""chars/pc"", ""category"": ""Social"", ""verb"": ""ranting at"", ""bidirectional"": true }
]

Farewell embrace (hard-lock until resolved — override Social default):
[
  { ""$type"": ""engagement_relation"", ""characterId"": ""chars/mother"", ""targetId"": ""chars/son"", ""category"": ""Social"", ""verb"": ""embracing"", ""restrictionLevel"": ""Hard"", ""bidirectional"": true }
]

Clear when the beat ends (`verb` or `distanceBand` null):
[
  { ""$type"": ""engagement_relation"", ""characterId"": ""chars/mother"", ""targetId"": ""chars/son"", ""verb"": null, ""bidirectional"": true },
  { ""$type"": ""spatial_position"", ""characterId"": ""chars/drunk"", ""targetId"": ""chars/pc"", ""distanceBand"": null }
]

*Combat vs manual: ruleset resolvers automatically establish and clear mechanical engagements (grappling, escape) via `ruleset_action` contested checks. For unresolved non-combat beats (hugs, tending wounds, intense confrontations), commit `engagement_relation` yourself — otherwise scene pressure will nag you and Hard engagements block `travel`.*

Item + transfer patterns, status with modifiers, ruleset_action (see below), etc.

**After you see a pressure in get_scene/get_world_state, your *next* action should usually be a `commit` using the exact snippet provided (adapted with real IDs/names).** Then narrate the outcome. The engine will clear the pressure on subsequent reads.

## Schrödinger's World + Transient / Open-World Patterns (Critical for Laziness Mitigation)
- **Flavor without bloat**: When narrating a crowded tavern, a bustling market, rats in a cellar, or ""a bard playing a lute in the corner"", **do not** immediately `character_create` 20 people. Instead:
  - On initial `location_create` or via `location_update`: populate `pointsOfInterest` (light list of strings returned in get_scene) and/or `ambientCrowd` (string hint, e.g. ""8-15 rough sailors and dockworkers"").
  - The engine surfaces `NARRATIVE PROMPT` pressures when ambientCrowd expects people but few/no NPCs are anchored, or when a recent beat sounds like someone stepping out of the crowd (drunk approaches, spear-bearer, witness) without a `chars/...` id. **Promote only that individual** via `character_create` — not the whole crowd. After `advance_world`, refresh `ambientCrowd` via `location_update` if the mood should have shifted.
- **Points of Interest — add / modify details / remove**: `pointsOfInterest` are lightweight strings. The LLM decides when interaction (reading, spell effect, combat damage, deliberate act like ripping a poster or torching the board) makes one worth persisting or changing. Supported via `location_update`:
  - `addPointOfInterest` (light string; pair with `pointOfInterestDetails` map to add with details)
  - `materializePointOfInterest` + `poiDetails` (creates or **updates** current state of a PoI)
  - `pointOfInterestDetails` (map for multiple)
  - `removePointOfInterest` (e.g. board burned, poster stolen)
  Example - PC rips a poster or the board is set on fire:
  [
    { ""$type"": ""location_update"", ""locationId"": ""locations/the-tavern"", ""materializePointOfInterest"": ""A notice board with wanted posters and job postings"", ""poiDetails"": ""Board is scorched; Grim the Hook poster has been ripped off and lies crumpled on the floor."" },
    { ""$type"": ""location_update"", ""locationId"": ""..."", ""addPointOfInterest"": ""Crumpled wanted poster on the floor"" }
  ]
  Details are visible in future get_scene. You control the evolution.
- **PoI / location state decay over time**: After combat, vandalism, or other changes, record the mayhem using `addPointOfInterest`/`materializePointOfInterest` or location `featuresToAdd` + `CurrentState`. On `advance_world` + later `get_scene`, the engine surfaces suggestions to evolve or clean up the state (owners tidy up after a few days, scorch marks get painted over, temporary marks fade). Use `location_update` to reflect cleanup in progress the next day or back to normal after several days. The LLM narrates the rate of decay realistically.
- **Crowd vulnerability (visual tags + interrupt roll)**: When narration establishes how a PC looks (bloodied, wanted, disheveled, unarmed), persist it via `character_update` with `appearanceOverride` and `tagsToAdd` (e.g. `[""bloody"", ""wanted"", ""disheveled""]`). In crowded or opportunistic-faction scenes, `get_scene` scores these tags and may nudge a crowd reaction. **Optional roll**: after a tense beat (not every dialog line), commit `scene_interrupt_check` with `riskModifier` (-50..+50, like `encounterRiskModifier` on travel) — omit `riskModifier` to auto-derive from tags. On success the engine promotes ONE transient from `ambientCrowd`; cooldown is one interrupt per location per day. Protective tags (`well_armed`, `escorted`, `uniform`) reduce the score.
- **Transients auto-GC**: Any character created (or moved via activity) with `schedule: null` AND `keepAlive: false` is transient. When the party leaves the area (get_scene on another loc + `advance_world` days later) and `LastVisitedDay` on the loc is old (>1 day), the `TransientEvictionRule` emits `ActivityChange` deltas that clear `CurrentLocationId`. The doc stays (cheap) for possible later promotion by ID or narrative callback. Use `keepAlive: true` for PCs, companions, or ""favorite"" flavor you want to keep without a full schedule.
- **Auto-Linking prevents soft-locks**: Always supply `connectedFromLocationId` + `connectionDescription` on `location_create`. Engine appends forward + reverse exits (and sets parent). If you forget, next get_scene on the child will give ENGINE WARNING + exact `location_update` JSON to add the missing exit.
- **Promotion path**: Use `schedule_change` (or supply schedule at `character_create` time) to make a transient permanent (it now runs in simulation, ignored by GC).
- **Dead-ends / broken maps**: get_scene will nag with ready `location_update` + `addExit`. Use it.
- **Hallucinated locations**: get_scene never throws for bad ID. Returns stub + strong ENGINE WARNING with ready `location_create` JSON (including connectedFrom suggestion). Paste it.

**Full ""Lazy LLM Tavern"" Walkthrough Example (copy this pattern):**
You (LLM): ""You push open the door to the Rusty Nail. The common room is full of sailors and dockworkers. A one-eyed bard in the corner is singing a shanty about lost ships while plucking a battered lute. The air smells of salt, sweat, and cheap ale. A toothless barman named Bram wipes a mug...""

(You used ambient flavor + PoIs implicitly via narration. No commit yet - correct for pure color.)

Later, party talks to the bard or barman engages:
- Call `get_scene ""locations/rusty-nail""` first (authoritative state).
- Suppose it returns empty PresentNPCs but AmbientCrowd hint (or prior you set none) + NARRATIVE PROMPT pressure: it will literally give you the JSON array.
- Then: `commit` the create for the interactable ones only:
  [
    { ""$type"": ""character_create"", ""characterId"": ""chars/bram-the-barkeep"", ""name"": ""Bram Ironarm"", ""currentLocationId"": ""locations/rusty-nail"", ""currentActivity"": ""Wiping mugs and watching the door"", ""notes"": ""Toothless, one good eye, ex-sailor. Knows harbor gossip."", ""psychology"": { ""wants"": [""quiet night"", ""coin""], ""fears"": [""trouble in his bar""] } },
    { ""$type"": ""character_create"", ""characterId"": ""chars/one-eyed-bard"", ... similar ... },
    { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Party met Bram and the bard at the Rusty Nail."", ""involved"": [""chars/pc1"", ""chars/bram-the-barkeep"", ""chars/one-eyed-bard""] }
  ] ""The party enters and interacts with the locals.""

- If later the bard becomes a quest giver recurring: `schedule_change` or add Schedule at birth + `keepAlive`.
- If they just drink and leave: no commit needed for the 12 unnamed sailors. Engine will GC any you did transiently create if area goes cold.

**Full ""Travel, Faction, Quest & Rumor"" Batch Example (Cohesive World Beats):**
When the party resolves a rumor about a rebel smuggler by betraying them to the city watch, batch all the consequences:
[
  { ""$type"": ""travel"", ""characterId"": ""chars/pc1"", ""destinationLocationId"": ""locations/city-jail"", ""encounterRiskModifier"": -30 },
  { ""$type"": ""quest_progress"", ""questId"": ""quests/betray-smuggler"", ""objectiveIndex"": 0, ""newState"": ""Complete"", ""narrativeNote"": ""Handed the rebel smuggler over to the City Watch."" },
  { ""$type"": ""faction_reputation"", ""factionId"": ""factions/city-watch"", ""characterId"": ""chars/pc1"", ""delta"": 15 },
  { ""$type"": ""faction_reputation"", ""factionId"": ""factions/rebels"", ""characterId"": ""chars/pc1"", ""delta"": -20 },
  { ""$type"": ""rumor"", ""rumorId"": ""rumors/smuggling"", ""newState"": ""Resolved"", ""newText"": ""The smuggler who supplied the rebels was caught and jailed."" },
  { ""$type"": ""character_update"", ""characterId"": ""chars/smuggler-npc"", ""keepAlive"": true },
  { ""$type"": ""activity"", ""characterId"": ""chars/smuggler-npc"", ""newLocationId"": ""locations/city-jail"", ""newActivity"": ""Imprisoned behind iron bars"" },
  { ""$type"": ""event"", ""category"": ""Betrayal"", ""summary"": ""Party betrayed the rebel smuggler at the city gate; smuggler is now locked up."", ""involved"": [""chars/pc1"", ""chars/smuggler-npc"", ""factions/city-watch""] }
]
This safely moves the party (with time + fatigue), updates the quest, modifies standing with two factions, resolves the active rumor, moves the smuggler NPC into jail with a new activity, and logs a narrative event in a single atomic database operation.

**Full ""Quest + Faction + Rumor Lifecycle"" Walkthrough (how a narrative thread breathes across multiple sessions):**

A complete arc — from seeded rumor through investigation, faction reaction, and resolution — spans several commits. Here is the canonical pattern. Adapt IDs to your campaign prefix.

**Beat 1 — Seed the thread (tavern, session start):**
Bram the barkeep mentions the Nightshade gang has been raiding river barges. Commit the rumor and the quest hook, and flag Bram as the quest giver:
[
  { ""$type"": ""rumor_create"", ""rumorId"": ""rumors/nightshade-gang"", ""subject"": ""Nightshade Gang"", ""text"": ""Nightshade pirates have raided three barges on the Ashford River this month — cargo vanishing, crews turning up dead."" },
  { ""$type"": ""quest_create"", ""questId"": ""quests/stop-nightshade"", ""title"": ""Cut Out the Nightshade"", ""giverId"": ""chars/bram-the-barkeep"", ""dmNotes"": ""River merchants desperate; disrupt Nightshade operations on the Ashford."", ""objectives"": [ { ""description"": ""Locate the Nightshade hideout"" }, { ""description"": ""Destroy or scatter the gang"" }, { ""description"": ""Report back to the River Merchants' Guild"" } ], ""deadlineDay"": 14 },
  { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Bram Ironarm told the party about the Nightshade Gang's river raids. Quest: Cut Out the Nightshade accepted."", ""involved"": [""chars/pc1"", ""chars/bram-the-barkeep""] }
]

**Beat 2 — Investigation (party scouting the docks):**
Party discovers the gang uses a hidden canal warehouse. Create the location, advance the quest, record the discovery:
[
  { ""$type"": ""location_create"", ""locationId"": ""locations/nightshade-warehouse"", ""name"": ""Nightshade Canal Warehouse"", ""description"": ""A damp, low-ceilinged warehouse reachable only by flat-bottomed barge. Crates of stolen cargo line the walls."", ""type"": ""Building"", ""connectedFromLocationId"": ""locations/ashford-docks"", ""connectionDescription"": ""A concealed canal lock, invisible at high tide"" },
  { ""$type"": ""quest_progress"", ""questId"": ""quests/stop-nightshade"", ""objectiveIndex"": 0, ""newState"": ""Complete"", ""narrativeNote"": ""Party located the warehouse via the canal lock at low tide."" },
  { ""$type"": ""knowledge_update"", ""characterId"": ""chars/pc1"", ""topic"": ""Nightshade Gang"", ""details"": ""Hideout is the canal warehouse south of Ashford Docks, accessible only at low tide."" },
  { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Party found the Nightshade Gang hideout: a canal warehouse south of Ashford Docks."" }
]

**Beat 3 — Confrontation + faction ripple (the gang is broken):**
Party raids the warehouse, kills the gang leader, frees hostages. Faction standing shifts:
[
  { ""$type"": ""hp"", ""characterId"": ""chars/nightshade-boss"", ""delta"": -99 },
  { ""$type"": ""quest_progress"", ""questId"": ""quests/stop-nightshade"", ""objectiveIndex"": 1, ""newState"": ""Complete"", ""narrativeNote"": ""Gang leader slain; surviving members fled or surrendered."" },
  { ""$type"": ""faction_state"", ""factionId"": ""factions/nightshade-gang"", ""influenceDelta"": -30, ""narrative"": ""Leadership killed in the warehouse raid. Gang scattered."" },
  { ""$type"": ""faction_reputation"", ""factionId"": ""factions/river-merchants-guild"", ""characterId"": ""chars/pc1"", ""delta"": 20 },
  { ""$type"": ""faction_reputation"", ""factionId"": ""factions/city-watch"", ""characterId"": ""chars/pc1"", ""delta"": 8 },
  { ""$type"": ""rumor"", ""rumorId"": ""rumors/nightshade-gang"", ""newState"": ""Resolved"", ""newText"": ""The Nightshade pirates were smashed by a band of adventurers at their own hideout. The river may be safe again."" },
  { ""$type"": ""event"", ""category"": ""Combat"", ""summary"": ""Party raided the Nightshade warehouse. Boss killed, gang scattered. River Merchants Guild grateful."" }
]

**Beat 4 — Resolution + world state shift (report to the guild):**
Party reports back. Quest closes, territory adjusts, maybe a new rumor seeds:
[
  { ""$type"": ""quest_progress"", ""questId"": ""quests/stop-nightshade"", ""objectiveIndex"": 2, ""newState"": ""Complete"", ""narrativeNote"": ""Party reported to the River Merchants Guild. Reward collected."" },
  { ""$type"": ""faction_state"", ""factionId"": ""factions/river-merchants-guild"", ""influenceDelta"": 10, ""narrative"": ""Guild influence rising now the river route is open; trade caravans resuming."" },
  { ""$type"": ""rumor_create"", ""rumorId"": ""rumors/ashford-river-trade"", ""subject"": ""Ashford River"", ""text"": ""Merchants are saying the Ashford route is profitable again. Caravans are reforming for the first time in weeks."" },
  { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Quest complete. River Merchants Guild paid the reward. Trade caravans reforming on the Ashford."", ""involved"": [""chars/pc1"", ""factions/river-merchants-guild""] }
]

After Beat 4: `get_world_state` will show the quest as resolved, both factions at updated standing, the original rumor as Resolved (no longer nagging), and a new active rumor seeding the next hook. Faction pressure contributors will start surfacing new opportunistic moves from the now-stronger River Merchants Guild if their influence crossed the threshold. The engine does the bookkeeping; you drive the story.

**Character Combat Bootstrap — required for all combatants (KeepAlive OR maxHp > 0):**
The engine emits ENGINE WARNING until BOTH are set:
1. **HP**: `maxHp` (+ optional `currentHp`) — or omit `maxHp` and let the ruleset bootstrap pipeline derive it
2. **systemStats**: ruleset-specific combat stats via `systemStats` on `character_create` or `system_stats` patch

**Auto-bootstrap (omit maxHp for PCs):** Pipeline runs at `character_create`, `upsert_character`, `system_stats` patch, and `level_up`. PCs: omit `maxHp` — supply typed bootstrap fields. Creature stat blocks: `systemStats.statBlockHp` or `maxHp` (skips HP formula only; AC/proficiency still derive). `currentHp` alone = wounded at create.

**Typed bootstrap fields (NOT in `attributes` — numbers only there):**
- **5e**: `hitDie` (e.g. ""d12""), `level`, `constitution`, `hpMode` (`average` | `rolled`), `classLevel` fallback; **multiclass**: `classLevels` array; **casters**: `spellcastingAbility`, optional `spellSaveDc`/`spellAttackBonus`
- **PF2e**: `classHpPerLevel`, `ancestryHp`, `level`, `constitutionMod`
- **Fallout**: `endurance`, `luck`, `level`, optional `hpPerLevel` (defaults to endurance)

5e also derives `armorClass` (unarmored 10 + DEX), `attributes.proficiencyBonus`, `attributes.passivePerception`, `spellSaveDc`/`spellAttackBonus` for casters, and emits `[BOOTSTRAP HINT]` with `item_create` armor JSON when no worn armor is detected.

5e multiclass (prefer structured `classLevels` on systemStats):
" + CommitSpellHelpExamples.MulticlassBootstrap + @"

Multiclass level-up (specify which class gained a level):
" + CommitSpellHelpExamples.MulticlassLevelUp + @"

D&D 5e reference (level 1, max hit die + CON modifier):
- Fighter / Paladin / Ranger: d10 → 10 + CON mod
- Cleric / Druid / Monk / Warlock / Bard: d8 → 8 + CON mod
- Rogue / Artificer: d8 → 8 + CON mod
- Wizard / Sorcerer: d6 → 6 + CON mod
- Barbarian: d12 → 12 + CON mod

For NPCs/creatures: use the stat block value (e.g. Goblin = 7 HP, AC 15, DEX 14).
Infer from class+level for PCs. Pure flavor transients (no HP, not KeepAlive) skip this.

5e auto-bootstrap (no maxHp — engine derives HP, AC, proficiency):
{ ""$type"": ""character_create"", ""characterId"": ""chars/kergil"", ""name"": ""Kergil"", ""isPc"": true, ""keepAlive"": true, ""classLevel"": ""Human Barbarian 10"", ""systemStats"": { ""$system"": ""dnd5e"", ""hitDie"": ""d12"", ""level"": 10, ""constitution"": 16, ""hpMode"": ""average"", ""dexterity"": 14, ""skillModifiers"": { ""Athletics"": 9, ""Perception"": 5 } } }

5e creature stat block (statBlockHp — HP formula skipped, AC still bootstrapped if omitted):
{ ""$type"": ""character_create"", ""characterId"": ""chars/goblin-scout"", ""name"": ""Goblin Scout"", ""classLevel"": ""Goblin 1"", ""systemStats"": { ""$system"": ""dnd5e"", ""statBlockHp"": 7, ""dexterity"": 14, ""strength"": 8, ""skillModifiers"": { ""Stealth"": 6, ""Perception"": 2 }, ""savingThrowModifiers"": { ""Dexterity"": 2 } } }

Level up (5e HP gain; optional `healToMatch`; multiclass PCs add `classGained`):
{ ""$type"": ""level_up"", ""characterId"": ""chars/kergil"", ""levelsGained"": 1, ""hpMode"": ""rolled"", ""healToMatch"": false }

PF2e auto-bootstrap:
{ ""$type"": ""character_create"", ""characterId"": ""chars/level2-fighter"", ""name"": ""Elara"", ""keepAlive"": true, ""classLevel"": ""Human Fighter 2"", ""systemStats"": { ""$system"": ""pf2e"", ""classHpPerLevel"": 10, ""ancestryHp"": 8, ""level"": 2, ""constitutionMod"": 2, ""armorClass"": 19, ""strengthMod"": 4, ""skillModifiers"": { ""Perception"": 8, ""Athletics"": 9 } } }

Fallout auto-bootstrap:
{ ""$type"": ""character_create"", ""characterId"": ""chars/raider"", ""name"": ""Raider"", ""systemStats"": { ""$system"": ""fallout2d20"", ""endurance"": 6, ""luck"": 5, ""level"": 3, ""agility"": 7, ""perception"": 6, ""skills"": { ""SmallGuns"": 2 }, ""tagSkills"": [""SmallGuns""] } }

Patch stats on existing character:
{ ""$type"": ""system_stats"", ""characterId"": ""chars/campaign-thorin"", ""systemStats"": { ""$system"": ""dnd5e"", ""armorClass"": 16, ""strength"": 16, ""skillModifiers"": { ""Athletics"": 5 } } }

**The Visual / Physics Sandbox (Tags & Appearance) & Knowledge:**
You (the LLM) author narrative state; the engine scores a fixed set of **visualTags** for crowd-pressure and `scene_interrupt_check` (bloody, wanted, disheveled, well_armed, etc.). Ad-hoc tags like ""wet"" still rely on your judgment for physics (e.g. lightning in rain) — only the crowd-vulnerability tag vocabulary is auto-scored.
- Use `$type: ""item_create""` with `coreCategory` (e.g., ""Weapon"", ""Armor"", ""Document"") when looting or discovering items. Set `holderId` to a PC character ID (or ""party"") for inventory.
- Use `$type: ""item_update""` to add temporary `TagsToAdd` (e.g., `[""wet"", ""muddy""]`) and a narrative `NewState` (e.g., ""Covered in mud"") to items. You can also add permanent `FeaturesToAdd` (e.g., ""Leather wrapped handle"") or change `coreCategory`.
- Use `$type: ""character_update""` to do the same for characters. Give them temporary `TagsToAdd` (`[""soot_covered""]`), narrative `AppearanceOverride`, or permanent `FeaturesToAdd` (`[""Scar over left eye""]`).
- Use `$type: ""location_update""` with `newState`, `tagsToAdd`, and `featuresToAdd` to persistently change the environment (e.g., ""On fire"", `[""smoky""]`, `[""collapsed roof""]`).
- Use `$type: ""knowledge_update""` to record an important memory for a character (e.g., `""topic"": ""The Dragon"", ""details"": ""Lives in the mountain.""`). Memories naturally decay and generate prompt pressure over time to simulate epistemic drift!
- Read these fields from `SceneView` and interpret them naturally. If a goblin has the ""wet"" tag, you inherently know lightning magic should be more effective. If the PC is ""disheveled"", the noble faction should react poorly.
- Factions have dynamic `EconomicDemand`. If a faction is desperate for an item the party is carrying (e.g. ""spell scrolls""), `get_scene` will pressure you to narrate merchants offering a premium or thieves attempting to steal them. Fulfill this naturally during roleplay!

## Ruleset Actions (Combat, Spells & Skill Checks)
Use `ruleset_action` inside `commit` for attacks, spells, skills, grapples, and item use. The engine rolls and returns results — narrate from the response. **The engine auto-applies `hp` deltas from ruleset_action — do not also commit separate `hp` for the same hit.** **After any spell, commit `status` separately for concentration/charm/etc. Engine does not track spell slots.**

" + CommitRumorHelpExamples.RoutingGuide + @"

- **$type**: `""ruleset_action""`
- **characterId**: Acting character.
- **targetIds**: Targets (required for attack/save/heal; optional for non-combat `check`/`utility`).
- **actionType**: `""Attack""`, `""Spell""`, `""SkillCheck""` (non-magic skills), `""SavingThrow""`, `""ContestedCheck""`, `""OpposedCheck""` (alias), `""UseItem""`, `""Recovery""`.
- **actionName**: Freeform (`""longsword""`, `""Fireball""`, `""Detect Magic""`). Attacks: match `heldItems` name for auto weapon merge.
- **actionCategory**: `""Spell""` for magic; `""Social""`/`""Survival""` nudges utility inference when `resolution` omitted.

**SavingThrow vs Spell:** `SavingThrow` = **actor** rolls one save. `Spell` + `parameters.resolution: ""save""` = **each target** rolls in **one commit** (Fireball). Never Fireball as six separate SavingThrows.

" + CommitSpellHelpExamples.RoutingGuide + @"

**Common parameters:**
- **All**: `resolution` (Spell: attack|save|check|utility|heal), `dc`, `skill`, `save`, `halfOnSave` (5e default true), `healDice`/`healBonus`/`healAmount`
- **5e/PF2e**: `bonus`/`toHitBonus`, `damageDice`, `damageBonus`, `ac`, `mapPenalty` (PF2e), `spellAttackBonus` (override)
- **Fallout**: `difficulty` or `dc`, `attribute`, `skill`, `pool`, `bonusDice`, `useLuck` (+1 die, no auto luck spend), `rangeModifier`, `cover`, `targetPart`, `damageDice` (combat dice count), `vicious`, `piercing`, `saveAttribute`, `saveSkill`

- **advantageState**: `""Advantage""`, `""Disadvantage""`, `""None""` (5e native).

**Multi-shot into a crowd:** AmbientCrowd mercs are not combatants until `character_create`d. Spawn 2–5 hostile transients with HP/systemStats, then one `ruleset_action` with multiple `targetIds` (and optional `attackCount` from the weapon). Example spray with Schlag (attackCount 3):
```json
{
  ""$type"": ""ruleset_action"",
  ""characterId"": ""chars/valen"",
  ""targetIds"": [""chars/merc-1"", ""chars/merc-2"", ""chars/merc-3"", ""chars/harluaa-training-sergeant""],
  ""actionType"": ""Attack"",
  ""actionName"": ""Schlag"",
  ""parameters"": { ""attackCount"": ""3"" }
}
```

**Existing PCs:** `upsert_character` creates the full PC document (`isPc: true`). During play use `activity` + `travel` to move PCs — do NOT `character_create` them again (the engine merges but warns). Call `get_party` first to list `isPc` / `isPartyCompanion` roster members.

Melee attack example:
```json
[
  {
    ""$type"": ""ruleset_action"",
    ""characterId"": ""chars/fighter"",
    ""targetIds"": [""chars/goblin""],
    ""actionType"": ""Attack"",
    ""actionName"": ""longsword"",
    ""parameters"": { ""bonus"": ""7"", ""damageDice"": ""1d8"", ""damageBonus"": ""4"" }
  }
]
```

" + CommitSpellHelpExamples.HelpSection + @"

## Status Effects & Stat Modifiers
Use `status` inside `commit` to add effects, including mechanical modifiers.

## Phase 7.4 Deep Dives & Suggested Commits
If a scene has `ActiveQuests` or `RelevantFactions`, you can explore them directly via:
- `get_quest_details`: Read the full Quest structure (all objectives, deadlines, rewards).
- `get_faction_context`: Get the full Faction summary, stances, territory, and influence.
Also, if `get_scene` or `get_world_state` returns `SuggestedCommitExamples` array, copy-paste one directly into your `commit` tool (examples frequently contain real IDs from the current state; replace any remaining placeholders like `locations/actual-dest` if needed) to easily resolve stuck characters or progress quests.

## World Pressure (Your Co-DM Nag System)
Pressures appear in **every** `get_world_state`, `get_scene`, and `advance_world` response (in the ToolResult.WorldPressure array, and also embedded in some views).

- `ENGINE WARNING`: Structural/integrity problem (hallucinated loc, no exits, broken link, etc.). **Paste the JSON and fix immediately.** These are the primary defense against laziness and broken worlds.
- `NARRATIVE PROMPT`: Opportunity / flavor cue (empty but ambient expected, no PoIs on a lively spot). Use to decide whether to persist something or just narrate using the hint.
- Simulation / character / rumor pressures: Aging unresolved, dying PCs/NPCs, desperate needs, etc. Many now include mini example commit snippets.

**Never ignore them.** The next `get_scene` after you fix will usually have fewer or none. If you keep seeing the same one, you skipped the commit.

Additional pressures come from character distress contributors (HP, bad statuses, high needs) surfaced via get_world_state, plus rule narratives turned into SimulatorEvents on advance.

## Other Tools & Patterns
- `get_npc_context` / `get_npc_needs`: Use before deep roleplay. Merge descriptors happen automatically.
- `search_world`, `recall_history`: For discovery without hallucinating duplicates.
- `define_need_descriptor` + `get_need_descriptors`: For custom needs vocabulary (wanderlust, debt_pressure, etc.).
- World-builder upserts: Fine for initial seeding / major PoIs. During play, prefer `commit` + the runtime creates.
- Combat: start_combat, next_turn, end_combat + ruleset_action inside commit. Statuses applied via commit survive and modify future rolls.

## Common Laziness Traps & How the Engine Helps
- Narrating a whole new dungeon level without creates -> next get_scene on a room ID: instant hallucination pressure + exact create JSON.
- Creating a cellar via create but forgetting the back exit -> pressure on entry.
- Spawning 40 named sailors for one scene -> bloat; use ambient + 1-2 creates only for interactables; GC cleans the rest.
- Forgetting to `activity` change after a scene -> get_scene shows stale locations/activities.
- Ignoring an aging ""Unresolved"" event for 10 days -> pressure in get_world_state with resolution hint.

Call `get_help` any time you (the LLM) are unsure. Re-read the pressures section often.

Remember: the engine is strict on invariants (map connectivity, no silent deletes of important state) so *you* can be creatively lazy about flavor.
";
}
