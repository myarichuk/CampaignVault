namespace CampaignVault.Tools;

/// <summary>
/// Canonical DM manual, split into focused sections for get_help topic parameter.
/// </summary>
internal static class DmHelpManual
{
    internal const string QuickstartSection = @"# CampaignVault DM Manual — Quickstart

Welcome to the CampaignVault engine. Your role as the AI DM is to drive the narrative while letting the MCP engine handle the persistence, math, and simulation.

## Quickstart for Models
1. **Call `get_help`** (this document) or **`list_tools`** for the full tool catalog.
2. **Establish campaign context**: `list_campaigns` → `create_campaign` (if needed), then pass `campaignName` on every tool call.
3. **Call `get_current_campaign(campaignName)`** to confirm slug, ruleset, and lock-in.
4. **Call `get_world_state`** at session start to sync time, rumors, events, and **WorldPressure**.
5. **Call `get_scene`** whenever the party enters a location. Action any `ENGINE WARNING` / `NARRATIVE PROMPT` immediately.
6. **Call `commit`** at the end of every meaningful beat (combat, conversation, discovery, persistence).
7. **Call `advance_world`** for travel, rests, or downtime skips.
8. **Never roll dice yourself** (mentally, or via an external script/tool) for anything that should be recorded — not just attacks. Ambient Perception on arrival, Investigation, Stealth, a passive save: all of it goes through `commit`'s `ruleset_action` with `actionType: ""SkillCheck""` (or `SavingThrow`/`ContestedCheck` as appropriate), the same $type used for combat. It is the engine's only dice roller, in or out of combat. Example: `{""$type"":""ruleset_action"",""characterId"":""chars/lyra"",""actionName"":""Perception"",""actionType"":""SkillCheck"",""actionCategory"":""Survival"",""parameters"":{""dc"":""12""}}`.

## Campaign slug scoping

`campaignName` (e.g. ""dragon-heist"") is **required** on every campaign-scoped tool call. There is no per-session selection or ""current campaign"" magic.

**Workflow:**
1. `list_campaigns` to discover existing slugs.
2. `create_campaign(name: ""dragon-heist"", initialSystem: ""Dnd5e"")` if new.
3. Pass `campaignName: ""dragon-heist""` explicitly on every call to `get_scene`, `commit`, etc.

Slugs are canonicalized (spaces to hyphens, lower). Shared canon (no CampaignName on entities) is visible across campaigns.

**Shared universe:** Entities with **no** `CampaignName` (e.g. `chars/bob-the-assassin`) are **canon** — visible in every campaign. Campaign-owned entities use prefixed IDs (`chars/dragon-heist-volo`) and are tagged with the slug on create.

**Party roster:** Tag human PCs with `isPc: true` and NPC companions with `isPartyCompanion: true` (mutually exclusive; both require a campaign slug). `get_party` returns only those flagged characters for the active campaign — not ambient `keepAlive` NPCs. Combat accepts canon entities (no `CampaignName`) plus campaign-tagged combatants; it rejects entities tagged for a different slug.

## The Golden Rule: The One-Door Principle

A **new entity or full replace** always goes through `world_build` (batch: characters, locations, items, factions, quests, rumors, plotThreads, creatures, spells, feats, lore) — there is no `_create` `$type` in `commit` for these. See `get_help topic=world-building` for the full guide and a copy-paste example.

A **change to something that already exists** (numeric/state deltas, tag/exit add-remove, progress) goes through `commit`. During play, strongly prefer `commit` (especially `activity` changes) for everything except creating a brand-new entity.

## Narrative Focus & Event Importance

`NarrativeFocus` is a free-text tag list on the campaign describing the kind(s) of story it tells (e.g. `[""political intrigue""]`, `[""dungeon crawl""]`, `[""horror investigation""]`). There is no server-side genre→importance matrix — these tags exist purely to steer **your** judgment as DM when you set `importance` on an `event` commit. Campaigns evolve; update the tags whenever the story's center of gravity shifts.

**Setting it:** pass `narrativeFocus` to `create_campaign`, or call `set_narrative_focus(campaignName, tags)` at any point afterward (this replaces the full tag list, not just adds to it). `get_current_campaign` echoes the current tags back to you.

**Choosing `importance` on `event` commits:** every event has an `importance` of `Trivial`, `Important`, or `Core`. If omitted, the engine defaults it by category (`Betrayal`/`Discovery`/`Combat`/`Arrival` → `Important`; engine bookkeeping categories like `Departure`/`Timeskip`/`Simulation` → `Trivial`). But the *same raw happening* should often get a different importance depending on what this campaign is actually about — you should set `importance` explicitly whenever the default would misjudge it:

- **Dungeon-crawl campaign:** the party learns a troll is vulnerable to fire → `Core` (directly actionable, campaign-defining knowledge for this focus). A tavern conversation about local gossip → `Trivial`.
- **Political-thriller campaign:** the same troll-fire fact → `Trivial` (flavor, not load-bearing). A duke's aide hinting at an assassination plot → `Core`.

`Core` events always survive retrieval budgets (ambient context, NPC context, recall); `Trivial` events are the first to drop off once a budget fills up. When in doubt, check `NarrativeFocus` before deciding.

## Core Gameplay Loop
1. **Start of Session**: Call `get_current_campaign` + `get_world_state` (with party location) to sync time, rumors, events, char distress, **and WorldPressure**.
2. **Exploration**: Call `get_scene` on entry. **Immediately action any ENGINE WARNING / NARRATIVE PROMPT in the WorldPressure** (use the exact JSON provided).
3. **Action & Consequence**: Narrate vividly to players. At end of beat (or when something should persist), call `commit` with array of changes. Use `activity` liberally to keep sim in sync.
4. **Time Skips / Travel**: `advance_world` (triggers needs, rumor decay, schedule eval, **TransientEvictionRule** for flavor NPCs).
5. **Deep NPC**: `get_npc_context` + `get_npc_needs`.

**Golden Rule:** If you just narrated something that should ""exist"" next time the party returns or is referenced, `commit` it (via create or update). If it's pure color, use PointsOfInterest + AmbientCrowd (lightweight, no docs created until you decide to promote).

## Time During a Scene (not just travel/rest)

Ordinary scenes take in-game time too — a long interrogation or a late-night talk isn't free just because nobody called `advance_world`. Any `commit` change (except `rest`/`travel`, which have their own hour fields) accepts an optional `minutesElapsed`: summed across the batch, it nudges hunger/thirst/tiredness immediately, no day-boundary or `advance_world` required. Set it whenever a beat represents more than a few seconds — a quick exchange ≈2-5 min, a tense negotiation or a multi-hour talk ≈60-180 min.

For tense or crowded scenes (interrogation, stakeout, a heated negotiation — not combat-only), also commit `scene_interrupt_check` after the beat (not every line) to let the engine roll whether someone from the ambient crowd interrupts. Cooldown: once per location per day. See `get_help topic=combat` or `get_commit_schema` for the full field list.

## For Deep Dives

- **Initial world-building / session 0**: call `get_help topic=world-building`
- **Commit patterns & examples**: call `get_help topic=patterns`
- **Combat & ruleset actions**: call `get_help topic=combat`
- **World pressure handling**: call `get_help topic=world-pressure`
- **Tags, items, equip, climate & knowledge**: call `get_help topic=visual-sandbox`
- **Commit type reference**: call `get_help topic=commit-enum`
- **FAQ & laziness traps**: call `get_help topic=faq`

## Tools by Category

Call `list_tools` for the full machine-readable catalog (filterable by category).

**Session & Exploration**: `get_world_state`, `get_scene`, `get_party`, `get_npc_context`, `get_npc_needs`, `recall_history`, `search_world`

**Mutation & Time**: `commit` (universal write), `advance_world` (travel/rests)

**Combat & Rulesets**: `start_combat`, `attack`, `next_turn`, `trigger_opportunity_attack`, `end_combat`

**Campaign Management**: `create_campaign`, `get_current_campaign`, `list_campaigns`, `set_active_system`, `set_narrative_focus`, `get_config`, `get_spells`, `get_system_handbook`, `query_creatures`

**Deep Dives**: `get_faction_context`, `get_quest_details`, `get_plot_thread`, `list_plot_threads`

**World Builder**: `world_build` (batch create/update for all entity kinds), `define_need_descriptor`, `get_need_descriptors`

**System**: `get_help` (this), `list_tools`, `get_commit_schema`

## Key Philosophy (Anti-LLM-Laziness / Schrödinger's World)

95%+ of the world is ephemeral flavor that lives ONLY in your current narration/context. Only *meaningful* interactions (that will be referenced again, combat, theft, named recurring NPCs, discovered secret doors the party will use) should be anchored via `commit`. The engine owns linking, GC of transients, visit tracking, and nags you *immediately* on the next `get_scene` or `get_world_state` with **exact, copy-paste-ready JSON** when you (or prior LLM turns) were lazy/incomplete.

Treat every string in `WorldPressure` that starts with `ENGINE WARNING:` or `NARRATIVE PROMPT:` as a **mandatory high-priority directive**. Paste the example JSON into your next `commit` call. This defeats the ""silly factor"" of being forced to output perfect polymorphic arrays for every tavern bard or crate.
";

    internal const string PatternsSection = @"# Commit Patterns & Narrative Examples

## The Commit Tool (Universal Write)
ALWAYS call at end of combat/conversation/discovery. Atomic array of `$type` mutations. Mutations are processed atomically as a single database transaction.

- **Batch Size Guidance:** Individual commits are capped at a maximum of **50 changes** per call. Group all related mutations (e.g. travel, quest progress, HP updates, and activity updates) into a single batch to ensure consistency.
- **ID Hygiene & Campaign Isolation:** Namespace campaign-owned entity IDs with the slug (e.g. `locations/dragon-heist-trollskull-alley`, `chars/dragon-heist-volo`). Leave `CampaignName` unset only for shared canon (e.g. Bob the assassin) that should appear in every campaign.

## Travel and Resting

Use `travel` (with `destinationLocationId`) to move a character any meaningful distance — it applies time and tiredness AND rolls an encounter check via `encounterRiskModifier` (based on distance/danger). Use `rest` (with `intendedHours` and `securityModifier`) for camping or sleeping — the engine rolls for interruptions there too. If either is interrupted, resolve the encounter before committing `hp` recovery! Resource pools (spell slots, focus points, etc.) and tiredness recover immediately when `rest` completes — no separate `advance_world` call needed for that.

**`activity` is NOT a substitute for either of these.** It's a direct, no-side-effect position/state write — fine for local repositioning already established as safe (settling into a spot the party already occupies, an NPC crossing a room), but it does not roll for encounters. Moving a PC/NPC any real distance, especially with plausible risk (alone, at night, unescorted, hostile or unknown territory), should go through `travel`, not `activity` — otherwise the possibility of an ambush or interruption silently never gets checked.

**Overnight/partial-day spans without full-on resting** (a long watch, waiting out a storm) that aren't a `rest` commit and aren't a `travel`: use `advance_world`'s `hours` parameter (e.g. `hours: 8`) instead of computing `days`/`timeOfDay` by hand — the engine derives the resulting time from the current clock. But `advance_world` itself has no encounter mechanic at all; if the span carries real risk, commit a `rest` (camping) or `travel` (moving) instead so the risk actually gets rolled.

**Ambient interrupts happen automatically, no separate commit needed.** Any ordinary commit carrying `minutesElapsed` (a long search, an interrogation, a stakeout) is itself automatically eligible for an interrupt roll — you don't need to also commit `scene_interrupt_check` for this to work. It only fires where `location.DangerModifier > 0` or the location's `ambientCrowd` reads as dense; a location with neither never rolls (a locked, empty safe-house stays quiet). It's skipped entirely if the batch already contains an explicit `rest`/`travel`/`scene_interrupt_check`, or during active combat. If it fires, the commit response's summary includes an `AMBIENT INTERRUPT` line with a transient NPC/situation to resolve — same non-forced-combat design as the other three mechanisms. Set `dangerModifier` deliberately on your locations (see seeding order above) — it's the only lever that controls whether this happens at all.

## Conversation Beats (CRITICAL)

Every `Conversation` event needs `involved` (all participants). Use this canonical pattern:

{{CONVERSATION_EXAMPLE}}

## Discovery + Activity Sync

[
  { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Party found the hidden stair."", ""involved"": [""chars/pc1""], ""locationId"": ""locations/cellar"" },
  { ""$type"": ""activity"", ""characterId"": ""chars/guard1"", ""newLocationId"": ""locations/cellar"", ""newActivity"": ""Searching crates nervously"" }
]

## Spatial Anchoring

`locationId` is the primary place an event happened; `relatedLocationIds` covers a beat that spills across more than one place. Prefer these over stuffing a location ID into `involved` — `locationId`/`relatedLocationIds` are indexed for `recall_history`'s `locationId` filter.

Bar fight spilling into the alley (one event, two locations):
[
  { ""$type"": ""event"", ""category"": ""Combat"", ""summary"": ""A bar fight breaks out and spills from the tavern into the alley outside, where a PC drags a bully to interrogate him."", ""involved"": [""chars/pc1"", ""chars/bully""], ""locationId"": ""locations/rusty-nail"", ""relatedLocationIds"": [""locations/rusty-nail-alley""] }
]

## Lazy Tavern Walkthrough (Copy This Pattern)

You (LLM): ""You push open the door to the Rusty Nail. The common room is full of sailors and dockworkers. A one-eyed bard in the corner is singing a shanty about lost ships while plucking a battered lute. The air smells of salt, sweat, and cheap ale. A toothless barman named Bram wipes a mug...""

(You used ambient flavor + PoIs implicitly via narration. No commit yet - correct for pure color.)

Later, party talks to the bard or barman engages:
- Call `get_scene ""locations/rusty-nail""` first (authoritative state).
- Suppose it returns empty PresentNPCs but AmbientCrowd hint (or prior you set none) + NARRATIVE PROMPT pressure: it will literally give you the JSON array.
- Then: `world_build` the interactable ones only:
  { ""character"": { ""id"": ""chars/bram-the-barkeep"", ""name"": ""Bram Ironarm"", ""currentLocationId"": ""locations/rusty-nail"", ""currentActivity"": ""Wiping mugs and watching the door"", ""notes"": ""Toothless, one good eye, ex-sailor. Knows harbor gossip."", ""psychology"": { ""wants"": [""quiet night"", ""coin""], ""fears"": [""trouble in his bar""] } } }
  { ""character"": { ""id"": ""chars/one-eyed-bard"", ... similar ... } }
- Then `commit` the beat:
  [
    { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Party met Bram and the bard at the Rusty Nail."", ""involved"": [""chars/pc1"", ""chars/bram-the-barkeep"", ""chars/one-eyed-bard""] }
  ]

- If later the bard becomes a quest giver recurring: `schedule_change` or add Schedule at birth + `keepAlive`.
- If they just drink and leave: no commit needed for the 12 unnamed sailors. Engine will GC any you did transiently create if area goes cold.

## Travel, Faction, Quest & Rumor Batch

When the party resolves a rumor about a rebel smuggler by betraying them to the city watch, batch all the consequences:
[
  { ""$type"": ""travel"", ""characterId"": ""chars/pc1"", ""destinationLocationId"": ""locations/city-jail"", ""encounterRiskModifier"": -30 },
  { ""$type"": ""quest_progress"", ""questId"": ""quests/betray-smuggler"", ""objectiveIndex"": 0, ""newState"": ""Complete"", ""narrativeNote"": ""Handed the rebel smuggler over to the City Watch."" },
  { ""$type"": ""faction_reputation"", ""factionId"": ""factions/city-watch"", ""characterId"": ""chars/pc1"", ""delta"": 15 },
  { ""$type"": ""faction_reputation"", ""factionId"": ""factions/rebels"", ""characterId"": ""chars/pc1"", ""delta"": -20 },
  { ""$type"": ""rumor"", ""rumorId"": ""rumors/smuggling"", ""newState"": ""Resolved"", ""newText"": ""The smuggler who supplied the rebels was caught and jailed."" },
  { ""$type"": ""character_update"", ""characterId"": ""chars/smuggler-npc"", ""keepAlive"": true },
  { ""$type"": ""activity"", ""characterId"": ""chars/smuggler-npc"", ""newLocationId"": ""locations/city-jail"", ""newActivity"": ""Imprisoned behind iron bars"" },
  { ""$type"": ""event"", ""category"": ""Betrayal"", ""summary"": ""Party betrayed the rebel smuggler at the city gate; smuggler is now locked up."", ""involved"": [""chars/pc1"", ""chars/smuggler-npc"", ""factions/city-watch""], ""importance"": ""Core"" }
]

This safely moves the party (with time + fatigue), updates the quest, modifies standing with two factions, resolves the active rumor, moves the smuggler NPC into jail with a new activity, and logs a narrative event in a single atomic database operation.

## Quest + Faction + Rumor Lifecycle (Full Arc)

A complete arc — from seeded rumor through investigation, faction reaction, and resolution — spans several commits.

**Beat 1 — Seed the thread (tavern, session start):**
Bram the barkeep mentions the Nightshade gang has been raiding river barges. Create the rumor and the quest hook via their upsert tools, and flag Bram as the quest giver:
{ ""rumor"": { ""id"": ""rumors/nightshade-gang"", ""regionLocationId"": ""locations/ashford-docks"", ""subject"": ""Nightshade Gang"", ""currentText"": ""Nightshade pirates have raided three barges on the Ashford River this month — cargo vanishing, crews turning up dead."" } } (world_build)
{ ""quest"": { ""id"": ""quests/stop-nightshade"", ""title"": ""Cut Out the Nightshade"", ""giverId"": ""chars/bram-the-barkeep"", ""dmNotes"": ""River merchants desperate; disrupt Nightshade operations on the Ashford."", ""objectives"": [ { ""description"": ""Locate the Nightshade hideout"" }, { ""description"": ""Destroy or scatter the gang"" }, { ""description"": ""Report back to the River Merchants' Guild"" } ], ""deadlineDay"": 14 } } (world_build)
Then `commit` the beat:
[
  { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Bram Ironarm told the party about the Nightshade Gang's river raids. Quest: Cut Out the Nightshade accepted."", ""involved"": [""chars/pc1"", ""chars/bram-the-barkeep""] }
]

**Beat 2 — Investigation (party scouting the docks):**
Party discovers the gang uses a hidden canal warehouse. Create the location via `world_build`:
{ ""location"": { ""id"": ""locations/nightshade-warehouse"", ""name"": ""Nightshade Canal Warehouse"", ""description"": ""A damp, low-ceilinged warehouse reachable only by flat-bottomed barge. Crates of stolen cargo line the walls."", ""type"": ""Building"", ""connectedFromLocationId"": ""locations/ashford-docks"", ""connectionDescription"": ""A concealed canal lock, invisible at high tide"" } }
Then advance the quest and record the discovery via `commit`:
[
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
Party reports back. Quest closes, territory adjusts. Commit the quest/faction/event beat:
[
  { ""$type"": ""quest_progress"", ""questId"": ""quests/stop-nightshade"", ""objectiveIndex"": 2, ""newState"": ""Complete"", ""narrativeNote"": ""Party reported to the River Merchants Guild. Reward collected."" },
  { ""$type"": ""faction_state"", ""factionId"": ""factions/river-merchants-guild"", ""influenceDelta"": 10, ""narrative"": ""Guild influence rising now the river route is open; trade caravans resuming."" },
  { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Quest complete. River Merchants Guild paid the reward. Trade caravans reforming on the Ashford."", ""involved"": [""chars/pc1"", ""factions/river-merchants-guild""] }
]
A new rumor seeds separately via `world_build`:
{ ""rumor"": { ""id"": ""rumors/ashford-river-trade"", ""regionLocationId"": ""locations/ashford-docks"", ""subject"": ""Ashford River"", ""currentText"": ""Merchants are saying the Ashford route is profitable again. Caravans are reforming for the first time in weeks."" } }

After Beat 4: `get_world_state` will show the quest as resolved, both factions at updated standing, the original rumor as Resolved (no longer nagging), and a new active rumor seeding the next hook. Faction pressure contributors will start surfacing new opportunistic moves from the now-stronger River Merchants Guild if their influence crossed the threshold. The engine does the bookkeeping; you drive the story.

## Wilderness Landmark Promotion

When the party explicitly marks a wilderness location (""We carve our names into this stone and mark it on the map""), promote it from transient narration to a real `Location`. Do this in order (so later steps can reference the prior ones' IDs):
1. **commit an event** — category `Discovery`, recordingMode `Deliberate`, importance `Core` (this is a deliberate, load-bearing act).
2. **world_build** — type `Wilderness`, connectedFromLocationId set to the location they came from (auto-links with two-way exits).
3. **commit a knowledge_update** — on the recording PC, recordingMode `Deliberate`, importance `Core`, relatedEntityIds = [new location id], sourceEventIds = [event id from step 1].

Example (party finds and marks a distinctive ridge):
Commit the event:
[
  { ""$type"": ""event"", ""summary"": ""Party discovered and marked the Raven's Ridge on their map."", ""category"": ""Discovery"", ""involved"": [""chars/pc1"", ""chars/pc2""], ""locationId"": ""locations/wilderness-foothills"", ""eventId"": ""events/ravens-ridge-marked"", ""recordingMode"": ""Deliberate"", ""importance"": ""Core"" }
]
Call `world_build`:
{ ""location"": { ""id"": ""locations/ravens-ridge"", ""name"": ""Raven's Ridge"", ""description"": ""A distinctive sandstone outcrop overlooking the foothills. Deep claw marks scar the rocks, as if something massive has climbed here often. A weathered bone is wedged in a crevice."", ""type"": ""Wilderness"", ""connectedFromLocationId"": ""locations/wilderness-foothills"", ""connectionDescription"": ""A winding trail up the ridge"", ""pointsOfInterest"": [""Claw marks"", ""Weathered bone""], ""dangerModifier"": 15 } }
Commit the knowledge_update:
[
  { ""$type"": ""knowledge_update"", ""characterId"": ""chars/pc1"", ""topic"": ""Raven's Ridge"", ""details"": ""A distinctive ridge we found and marked on our map. Distinctive sandstone formations and old claw marks. Seemed like a creature's haunt."", ""recordingMode"": ""Deliberate"", ""importance"": ""Core"", ""relatedEntityIds"": [""locations/ravens-ridge""], ""sourceEventIds"": [""events/ravens-ridge-marked""] }
]

On a failed Survival/Nature check before marking (optional flavor: represent the party's uncertainty), fire the same sequence but narrate the location's details as partially inaccurate (distance off, misidentified feature, etc.). The Location is still created and persistent; the inaccuracy is in your narration, not a schema field.

## Ad-Hoc Waypoint Detail (arriving somewhere specific, not deliberately marked)

Different from Wilderness Landmark Promotion above: the party isn't naming/marking the spot, they're just narratively arriving somewhere specific inside a broad, already-existing location (a region, a stretch of foothills, ""the woods"") — fleeing to make camp, taking cover, stashing something. Don't reach for a full `world_build` sub-location for this (that's for deliberate, repeat-visit landmarks). Instead, attach the specific detail as a materialized PoI on the broad location — preferably inline on the SAME `activity` change that moves the character there, via its own `poiName`/`poiDetails` fields, so the move and its detail can't be split into two commits and have the second one forgotten:
[
  { ""$type"": ""activity"", ""characterId"": ""chars/pc1"", ""newActivity"": ""making a cold camp for the night"", ""newLocationId"": ""locations/mere-of-dead-men"", ""updateLocation"": true, ""reason"": ""fleeing danger, seeking a defensible spot"", ""minutesElapsed"": 45, ""poiName"": ""Sheltered camp saddle"", ""poiDetails"": ""A low rocky saddle behind a tumbled boulder and a stand of pine; wind-blocked, water audible nearby, no tracks crossing the clearing. Cold-camped, no fire."" }
]
(A separate `location_update` with `materializePointOfInterest`/`poiDetails` targeting the same location does the same thing and still works — the inline fields on `activity` are just the one-commit version of the exact same mechanism.)
Ask yourself the same ""will this be referenced again"" test as any other commit: a hidden killer on the loose, a stash, an ambush risk, a tracking check DC — all of that hinges on these details existing somewhere queryable. Skipping the PoI here isn't ""Schrödinger's World flavor,"" it's losing state the story is about to depend on. If the party never returns and nothing hinges on it, a plain `activity` with no PoI is fine — judge by stakes, not habit. If you do skip it and the batch also contains an Important/Core event referencing that location, `commit`'s response will remind you.

## Schrödinger's World + Transient / Open-World Patterns

**Flavor without bloat**: When narrating a crowded tavern, a bustling market, rats in a cellar, or ""a bard playing a lute in the corner"", **do not** immediately `world_build` 20 people. Instead:
  - On initial `world_build` or via `location_update`: populate `pointsOfInterest` (light list of strings returned in get_scene) and/or `ambientCrowd` (string hint, e.g. ""8-15 rough sailors and dockworkers"").
  - The engine surfaces `NARRATIVE PROMPT` pressures when ambientCrowd expects people but few/no NPCs are anchored, or when a recent beat sounds like someone stepping out of the crowd (drunk approaches, spear-bearer, witness) without a `chars/...` id. **Promote only that individual** via `world_build` — not the whole crowd. After `advance_world`, refresh `ambientCrowd` via `location_update` if the mood should have shifted.

**Points of Interest — add / modify details / remove**: `pointsOfInterest` are lightweight strings. The LLM decides when interaction (reading, spell effect, combat damage, deliberate act like ripping a poster or torching the board) makes one worth persisting or changing. Supported via `location_update`:
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

**PoI / location state decay over time**: After combat, vandalism, or other changes, record the mayhem using `addPointOfInterest`/`materializePointOfInterest` or location `featuresToAdd` + `CurrentState`. On `advance_world` + later `get_scene`, the engine surfaces suggestions to evolve or clean up the state (owners tidy up after a few days, scorch marks get painted over, temporary marks fade). Use `location_update` to reflect cleanup in progress the next day or back to normal after several days. The LLM narrates the rate of decay realistically.

**Transients auto-GC**: Any character created (or moved via activity) with `schedule: null` AND `keepAlive: false` is transient. When the party leaves the area (get_scene on another loc + `advance_world` days later) and `LastVisitedDay` on the loc is old (> grace period), `TransientEvictionRule` clears `CurrentLocationId`, logs a `Departure` event, records the NPC on `location.recentlyDeparted`, sets `departedAtDay`/`departedFromLocationId` on the character doc, and transfers held items to the location (not deleted). `advance_world` returns `evictedNpcs` with names + source locations. Re-promote via `activity` + `keepAlive: true` / `schedule_change` for favorites (the character already exists; no need to re-`world_build`).

**Auto-Linking prevents soft-locks**: Always supply `connectedFromLocationId` + `connectionDescription` on `world_build` when creating a new location. Engine appends forward + reverse exits (and sets parent). If you forget, next get_scene on the child will give ENGINE WARNING + exact `location_update` JSON to add the missing exit.

**Promotion path**: Use `schedule_change` (or supply schedule at `world_build` time) to make a transient permanent (it now runs in simulation, ignored by GC).

**Dead-ends / broken maps**: get_scene will nag with ready `location_update` + `addExit`. Use it.

**Hallucinated locations**: get_scene never throws for bad ID. Returns stub + strong ENGINE WARNING with ready `world_build` JSON (including connectedFrom suggestion). Paste it.

## Engagements & Spatial Positions

`relationship` vs `engagement_relation` — not the same thing: `relationship` (`characterId` + `targetId` + a numeric `delta`, roughly ±20 per significant event) is a **durable** opinion/trust score change — how much this character likes/trusts the target, persisted and read back by the relationship bands below. `engagement_relation` is a **momentary, categorical** in-scene state — what the two are physically/socially doing to each other right now, not a trust delta. Use `relationship` when a beat should change how an NPC feels about someone long-term (a favor, a betrayal); use `engagement_relation` when a beat should reflect who's currently grappling, tending wounds, or embracing whom.

Pairwise state (`engagement_relation`) vs. relative placement (`spatial_position`). Both now use `characterId` + `targetId` (or `targetIds`).

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

**Multi-party conversations (PC + companion + NPC):** `engagement_relation` is pairwise — use one row per speaker↔anchor pair, then log the beat. Either set `involved` explicitly on the `event`, or let the engine merge participants from `engagement_relation`, `spatial_position`, `activity`, and `ruleset_action` in the same commit batch.
[
  { ""$type"": ""engagement_relation"", ""characterId"": ""chars/pc"", ""targetId"": ""chars/barkeep"", ""category"": ""Social"", ""verb"": ""ordering drinks from"", ""bidirectional"": true },
  { ""$type"": ""engagement_relation"", ""characterId"": ""chars/companion"", ""targetId"": ""chars/barkeep"", ""category"": ""Social"", ""verb"": ""listening in on"", ""bidirectional"": true },
  { ""$type"": ""event"", ""category"": ""Conversation"", ""summary"": ""The party and the barkeep trade rumors over ale."", ""involved"": [""chars/pc"", ""chars/companion"", ""chars/barkeep""] }
]
All participants in `involved` are recalled by `get_npc_context` for each speaker.

**N-way NPC debate (no PC present):** `involved` isn't limited to PC-anchored beats — any number of NPCs arguing among themselves works the same way. List every speaker in `involved`; `engagement_relation` rows are optional here since there's no travel-lock or pressure need, just useful if you want to show who's directly sparring with whom.
[
  { ""$type"": ""event"", ""category"": ""Conversation"", ""summary"": ""Archivist Wren, Magister Dol, and two visiting scholars argue over the ruin's true age, voices rising over each other in the reading room."", ""involved"": [""chars/archivist-wren"", ""chars/magister-dol"", ""chars/scholar-1"", ""chars/scholar-2""] },
  { ""$type"": ""activity"", ""characterId"": ""chars/archivist-wren"", ""newActivity"": ""Jabbing a finger at a spread map, insisting on the older dating"" },
  { ""$type"": ""activity"", ""characterId"": ""chars/magister-dol"", ""newActivity"": ""Arms crossed, unconvinced, muttering counterpoints"" },
  { ""$type"": ""activity"", ""characterId"": ""chars/scholar-1"", ""newActivity"": ""Frantically flipping through a referenced text to back Wren up"" },
  { ""$type"": ""activity"", ""characterId"": ""chars/scholar-2"", ""newActivity"": ""Sipping tea, quietly amused by the chaos"" }
]
`get_npc_context` for any of the four will recall the debate and who was in it — no PC involvement required.

**Commit hints:** after a `Conversation`/`Discovery`/`Betrayal` event, the engine may return advisory `Hint:` lines in the commit summary — e.g. flagging that no `activity` commit covered a participant's body language, that no `knowledge_update` captured what was learned, that a co-committed `knowledge_update` doesn't set `sourceEventIds`, or that the beat reads as unusually novel/repetitive versus recent events. These never fail the commit; treat them as a second-pass checklist, not an error.

**Ground truth vs. subjective memory — two different questions:** ""what does this NPC remember"" and ""did this actually happen"" are NOT the same query, and conflating them will make an NPC's (possibly distorted) belief look like fact.
- **What an NPC believes** (subjective, may have drifted or be flat wrong): `get_npc_context` → `Psychology.Memories`.
- **What actually happened** (ground truth, indexed): `recall_history` with `involvedCharacterId` (was this character actually present/involved?) and/or `locationId` (what happened at this place?).

Example — ""was Bob a witness to the party robbing the noble three sessions ago"": call `recall_history(involvedCharacterId: ""chars/bob"", locationId: ""locations/noble-manor"")` and check whether the robbery event is in the results — do NOT infer this from `chars/bob`'s `Psychology.Memories`, since a memory there only tells you what Bob *thinks* happened (or that he has no memory of it at all, which isn't the same as ""wasn't there"").

When a `knowledge_update` is derived from a specific logged event, set `sourceEventIds` (referencing either a prior event's ID or a same-batch `eventId` you set on the `event` change) so later `knowledge_update`s can be checked against the ground truth instead of drifting unmoored.

**After you see a pressure in get_scene/get_world_state, your *next* action should usually be a `commit` using the exact snippet provided (adapted with real IDs/names).** Then narrate the outcome. The engine will clear the pressure on subsequent reads.
";

    internal const string CombatSection = @"# Combat & Ruleset Actions

## Character Combat Bootstrap

**Required for all combatants** (KeepAlive OR maxHp > 0):
1. **HP**: `maxHp` (+ optional `currentHp`) — or omit `maxHp` and let the ruleset bootstrap pipeline derive it
2. **systemStats**: ruleset-specific combat stats via `systemStats` on `world_build` or `system_stats` patch

**Auto-bootstrap (omit maxHp for PCs):** Pipeline runs at `world_build`, `system_stats` patch, and `level_up`. PCs: omit `maxHp` — supply typed bootstrap fields. Creature stat blocks: `systemStats.statBlockHp` or `maxHp` (skips HP formula only; AC/proficiency still derive). `currentHp` alone = wounded at create.

**Typed bootstrap fields (NOT in `attributes` — numbers only there):**
- **5e**: `hitDie` (e.g. ""d12""), `level`, `constitution`, `hpMode` (`average` | `rolled`), `classLevel` fallback; **multiclass**: `classLevels` array; **casters**: `spellcastingAbility`, optional `spellSaveDc`/`spellAttackBonus`
- **PF2e**: `classHpPerLevel`, `ancestryHp`, `level`, `constitutionMod`
- **Fallout**: `endurance`, `luck`, `level`, optional `hpPerLevel` (defaults to endurance)

5e also derives `armorClass` (unarmored 10 + DEX), `attributes.proficiencyBonus`, `attributes.passivePerception`, `spellSaveDc`/`spellAttackBonus` for casters, and emits `[BOOTSTRAP HINT]` with `world_build` armor JSON when no worn armor is detected.

D&D 5e reference (level 1, max hit die + CON modifier):
- Fighter / Paladin / Ranger: d10 → 10 + CON mod
- Cleric / Druid / Monk / Warlock / Bard: d8 → 8 + CON mod
- Rogue: d8 → 8 + CON mod
- Wizard / Sorcerer: d6 → 6 + CON mod
- Barbarian: d12 → 12 + CON mod

For NPCs/creatures: use the stat block value (e.g. Goblin = 7 HP, AC 15, DEX 14).
Infer from class+level for PCs. Pure flavor transients (no HP, not KeepAlive) skip this.

5e auto-bootstrap (no maxHp — engine derives HP, AC, proficiency), via `world_build`:
{ ""character"": { ""id"": ""chars/kergil"", ""name"": ""Kergil"", ""isPc"": true, ""keepAlive"": true, ""classLevel"": ""Human Barbarian 10"", ""systemStats"": { ""$system"": ""dnd5e"", ""hitDie"": ""d12"", ""level"": 10, ""constitution"": 16, ""hpMode"": ""average"", ""dexterity"": 14, ""skillModifiers"": { ""Athletics"": 9, ""Perception"": 5 } } } }

5e creature stat block (statBlockHp — HP formula skipped, AC still bootstrapped if omitted), via `world_build`:
{ ""character"": { ""id"": ""chars/goblin-scout"", ""name"": ""Goblin Scout"", ""classLevel"": ""Goblin 1"", ""systemStats"": { ""$system"": ""dnd5e"", ""statBlockHp"": 7, ""dexterity"": 14, ""strength"": 8, ""skillModifiers"": { ""Stealth"": 6, ""Perception"": 2 }, ""savingThrowModifiers"": { ""Dexterity"": 2 } } } }

**Level up:** The engine does not track XP. When a milestone is earned in narration, commit `level_up` for the PC or party companion (`isPartyCompanion: true`). Applies HP gains, re-syncs spell/resource pools, and runs bootstrap. Optional `reason` is logged in the commit summary.
{ ""$type"": ""level_up"", ""characterId"": ""chars/kergil"", ""levelsGained"": 1, ""hpMode"": ""rolled"", ""healToMatch"": false, ""reason"": ""cleared the goblin warrens"" }
Party companion:
{ ""$type"": ""level_up"", ""characterId"": ""chars/wolf-companion"", ""levelsGained"": 1, ""reason"": ""bonded after the siege"" }

PF2e auto-bootstrap, via `world_build`:
{ ""character"": { ""id"": ""chars/level2-fighter"", ""name"": ""Elara"", ""keepAlive"": true, ""classLevel"": ""Human Fighter 2"", ""systemStats"": { ""$system"": ""pf2e"", ""classHpPerLevel"": 10, ""ancestryHp"": 8, ""level"": 2, ""constitutionMod"": 2, ""armorClass"": 19, ""strengthMod"": 4, ""skillModifiers"": { ""Perception"": 8, ""Athletics"": 9 } } } }

Fallout auto-bootstrap, via `world_build`:
{ ""character"": { ""id"": ""chars/raider"", ""name"": ""Raider"", ""systemStats"": { ""$system"": ""fallout2d20"", ""endurance"": 6, ""luck"": 5, ""level"": 3, ""agility"": 7, ""perception"": 6, ""skills"": { ""SmallGuns"": 2 }, ""tagSkills"": [""SmallGuns""] } } }

Patch stats on existing character:
{ ""$type"": ""system_stats"", ""characterId"": ""chars/campaign-thorin"", ""systemStats"": { ""$system"": ""dnd5e"", ""armorClass"": 16, ""strength"": 16, ""skillModifiers"": { ""Athletics"": 5 } } }

## Ruleset Actions (Combat, Spells & Skill Checks)

Use `ruleset_action` inside `commit` for attacks, spells, skills, grapples, and item use. The engine rolls and returns results — narrate from the response. **The engine auto-applies `hp` deltas from ruleset_action — do not also commit separate `hp` for the same hit.**

**After any spell, commit `status` separately for concentration/charm/etc.** Spend spell slots via `$type: ""resource""` (poolName: spell_slots_N, delta: -1, spellName required for validation); the engine validates spell level vs. slot level, and spending below 0 HARD-FAILS the commit (e.g. ""Insufficient spell_slots_3 for Valen: has 0, needs 1."") — check remaining pool via get_scene/get_npc_context before spending, or catch the error and adjust the spell/slot choice. Grants above max still clamp silently.

{{SPELL_ROUTING}}

- **$type**: `""ruleset_action""`
- **characterId**: Acting character.
- **targetIds**: Targets (required for attack/save/heal; optional for non-combat `check`/`utility`).
- **actionType**: `""Attack""`, `""Spell""`, `""SkillCheck""` (non-magic skills), `""SavingThrow""`, `""ContestedCheck""`, `""OpposedCheck""` (alias), `""UseItem""`, `""Recovery""`.
- **actionName**: Freeform (`""longsword""`, `""Fireball""`, `""Detect Magic""`). Attacks: match `heldItems` name for auto weapon merge.
- **actionCategory**: `""Spell""` for magic; `""Social""` / `""Survival""` nudges utility inference when `resolution` omitted.

**SavingThrow vs Spell:** `SavingThrow` = **actor** rolls one save. `Spell` + `parameters.resolution: ""save""` = **each target** rolls in **one commit** (Fireball). Never Fireball as six separate SavingThrows.

{{SPELL_EXAMPLES}}

**Common parameters:**
- **All**: `resolution` (Spell: attack|save|check|utility|heal), `dc`, `skill`, `save`, `halfOnSave` (5e default true), `healDice`/`healBonus`/`healAmount`
- **5e/PF2e**: `bonus`/`toHitBonus`, `damageDice`, `damageBonus`, `ac`, `mapPenalty` (PF2e), `spellAttackBonus` (override)
- **Fallout**: `difficulty` or `dc`, `attribute`, `skill`, `pool`, `bonusDice`, `useLuck` (+1 die, no auto luck spend), `rangeModifier`, `cover`, `targetPart`, `damageDice` (combat dice count), `vicious`, `piercing`, `saveAttribute`, `saveSkill`

- **advantageState**: `""Advantage""`, `""Disadvantage""`, `""None""` (5e native).

**Relationship-based social roll bonuses:** Social skill checks (Persuasion, Deception, Intimidation, Insight, Performance, or `ActionCategory: Social`) automatically apply modifiers based on the target NPC's relationship opinion of the actor. Relationship bands: ≥80 trusted friend (+5), 60–79 friendly (+3), 40–59 acquainted (+1), −39..39 neutral (0), −59..−40 distrustful (−1), −79..−60 hostile (−3), ≤−80 hated enemy (−5). The engine includes the relationship label (e.g., ""(trusted friend)"") in the roll narrative. Non-social skills (Athletics, Perception, etc.) are unaffected by relationships. **Note: NarrativeRulesetResolver (pure oracle mode) does not apply relationship modifiers.**

**Multi-shot into a crowd:** AmbientCrowd mercs are not combatants until created via `world_build`. Spawn 2–5 hostile transients with HP/systemStats, then one `ruleset_action` with multiple `targetIds` (and optional `attackCount` from the weapon). Example spray with Schlag (attackCount 3):
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

**Existing PCs:** `world_build` creates the full PC document (`isPc: true`). During play use `activity` + `travel` to move PCs — do NOT re-`world_build` them to move them (the engine merges but it's the wrong tool for a routine move). Call `get_party` first to list `isPc` / `isPartyCompanion` roster members.

**Melee attack example:**
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

## Status Effects & Stat Modifiers

Use `status` inside `commit` to add effects, including mechanical modifiers.

## Combat Lifecycle

Use `start_combat`, `next_turn`, `end_combat` + `ruleset_action` inside `commit`. Statuses applied via commit survive and modify future rolls.
";

    internal const string WorldPressureSection = @"# World Pressure & Simulation

## WorldPressure System

Pressures appear in **every** `get_world_state`, `get_scene`, and `advance_world` response (in the ToolResult.WorldPressure array, and also embedded in some views).

- **`ENGINE WARNING`**: Structural/integrity problem (hallucinated loc, no exits, broken link, etc.). **Paste the JSON and fix immediately.** These are the primary defense against laziness and broken worlds.
- **`NARRATIVE PROMPT`**: Opportunity / flavor cue (empty but ambient expected, no PoIs on a lively spot). Use to decide whether to persist something or just narrate using the hint.
- **Simulation / character / rumor pressures**: Aging unresolved, dying PCs/NPCs, desperate needs, etc. Many now include mini example commit snippets.

**Never ignore them.** The next `get_scene` after you fix will usually have fewer or none. If you keep seeing the same one, you skipped the commit.

Additional pressures come from character distress contributors (HP, bad statuses, high needs) surfaced via get_world_state, plus rule narratives turned into SimulatorEvents on advance.

## Pressure Contributors

The engine tracks world state across these dimensions and surfaces pressures when:
- **Aging Unresolved Events**: Discoveries, betrayals, or combats that haven't been resolved after several days.
- **Dangling Items**: Items created but never picked up or used meaningfully.
- **Faction Economy**: Factions desperate to acquire or sell specific items.
- **Faction Recent Events**: Factions reacting to power shifts or opportunities.
- **Faction Territory**: Territory changes triggering movement or consolidation pressures.
- **Never Visited Locations**: New locations the party created but never visited.
- **Quest Deadlines**: Quests approaching or past their deadline.
- **Scene Quest Staleness**: Active quests not progressing in the current scene.
- **Urgent Initiative**: High-stakes NPCs wanting immediate action.
- **Engagement Relations**: Hard-locked characters blocking travel or pressuring resolution.
- **Incomplete System Stats**: Combatants missing HP, AC, or other required stats.
- **Stuck Travel**: Party stuck in transit (vehicle broken, path blocked).

Each pressure surfaces with a brief explanation and often includes **suggested commit JSON** that you can copy-paste and adapt. **Use these immediately** — they're your co-DM's way of flagging world state that needs action.

## After Seeing a Pressure

Your **next** action should usually be a `commit` using the exact snippet provided (adapted with real IDs/names). Then narrate the outcome. The engine will clear the pressure on subsequent reads.

Example workflow:
1. `get_scene` returns `NARRATIVE PROMPT: ""Tavern has ambient crowd but no anchored NPCs. Promote an interactable character.""` with suggested JSON.
2. You `world_build` for Bram the barkeep (optional step to flesh out flavor).
3. You `commit` the suggested JSON (or adapt it with a conversation event).
4. Next `get_scene`: pressure cleared.

If you keep seeing the same pressure, you likely skipped the commit or the commit doesn't match the pressure's expectation.
";

    internal const string VisualSandboxSection = @"# The Visual / Physics Sandbox

You (the LLM) author narrative state; the engine scores a fixed set of **visualTags** for crowd-pressure and `scene_interrupt_check` (bloody, wanted, disheveled, well_armed, etc.). Ad-hoc tags like ""wet"" still rely on your judgment for physics (e.g. lightning in rain) — only the crowd-vulnerability tag vocabulary is auto-scored.

## Items & Inventory

- Use `world_build` with `coreCategory` (e.g., ""Weapon"", ""Armor"", ""Document"") when looting or discovering items. Set `holderId` to a PC character ID (or ""party"") for inventory. Use `quantity` for multiples (e.g., 5 potions = 1 item with quantity: 5, not 5 separate items).
- Use `$type: ""item_update""` to add temporary `TagsToAdd` (e.g., `[""wet"", ""muddy""]`) and a narrative `NewState` (e.g., ""Covered in mud"") to items. You can also add permanent `FeaturesToAdd` (e.g., ""Leather wrapped handle"") or change `coreCategory`.
- Use `$type: ""item""` (`itemId` + `toHolderId`) to move an *existing* item to a new holder (a character, location, or container item) — e.g. a PC hands off a torch, or loot gets dropped in a location. Not for creating or editing item properties; use `world_build`/`item_update` for that. **Worn/carried gear that should visibly hang off another equipped item** (a sword in a back sheath, throwing knives on a bandolier, a coin pouch on a belt) uses this same holder chain: set the sword/knife/pouch's `toHolderId` to the sheath/bandolier/belt *item's* id (not the character's), while that container item is itself `item_equip`'d onto the character. Scene/inventory views resolve the contained item through its container's holder.
- **Open-carry vs. concealed:** the holder chain above only tells you *what's carried by what* — it says nothing about whether it's visible. Tag the CONTAINER item (not the contents) with `""open-carry""` (a sword in a back sheath, knives looped on a bandolier — visible at a glance) or `""concealed""` (a dagger in a boot, coins in a belt pouch — hidden) via `tagsToAdd`. This is narrative-only bookkeeping: nothing derives `well_armed`/`unarmed` from it automatically, so also tag the *character* directly when what's visibly on display should affect a crowd's reaction (see World Pressure). For a concealed weapon someone might find, put the DC on an `upsertItemDetail`'s `intent` (below) — e.g. ""DC 15 Perception to notice the boot sheath.""
- Use `item_update`'s `upsertItemDetail` to track persistent, granular details on an item — scratches, stains, secret compartments, custom pockets, and active/ongoing conditions (corrosive ooze eating through leather, a puncture that leaks, a lock glued shut, a tether/rope) — that should survive across sessions (unlike `TagsToAdd`, which is for temporary labels). Pass `id` if you already know it (from a prior commit response or `get_item`) to update that exact detail; omit it and the engine resolves by semantic similarity, falling back to creating a new one. `intent` is DM-only narration/mechanics guidance (never shown to players) — e.g. a suggested DC, discovery condition, or ongoing-effect rate (""1 dmg/round until wiped off""). `tetheredToId` optionally references whatever the item is currently physically anchored to (a location, another item, or a character) — e.g. a rope's other end lashed to `locations/ruins-column`, or a horse leashed to a stake `items/tether-stake` — purely descriptive; the DM-LLM reads it back and adjudicates movement/range consequences, the engine doesn't enforce them. Pass `""""` (empty string) to clear it once cut/freed. List `participants` (with `role: ""Caused""` or `""Witnessed""`) to push a memory into those characters' psychology automatically. Use `retireItemDetailId` to soft-retire a detail once it's no longer true (compartment emptied, stain cleaned, tether cut) — it keeps the record (so any memory referencing it stays resolvable) rather than deleting it. Set `reviewIntervalDays` to how often (in days) the engine should nudge you to reconsider this specific detail — details change at wildly different rates, so pick one that fits: a puncture that's actively leaking might warrant 1-3 days, while a scorch mark or crater might warrant 60-90+. Omit to use the engine's 60-day default (you'll get a one-time NOTE in the commit summary if you create/update a Hazard or Environmental detail without setting it, since those are the ones most likely to need a non-default value). This is separate from actual quantity tracking (a water gourd's fill level) — use `maxCharges`/`item_use` for that; `reviewIntervalDays` only governs when the engine nags you to revisit the detail's narrative description.

## Equip, Outfits & Layers

- Define equippable items via `world_build`, specifying `equipZones` (e.g., `[""Torso""]`, `[""MainHand"", ""OffHand""]`) and `equipLayer` (Base, Armor, Outer, Held) once. These define the item's equipment slots; they do not change on each equip.
- Use `$type: ""item_equip""` to equip an existing item on a character (takes `characterId`, `itemId`, `replaceConflicts` only). AC and WarmthRating recompute immediately.
- Set `replaceConflicts: true` to silently unequip conflicting items (same zone+layer+StackGroup). Omit for non-destructive conflicts (error on the call, with a structured ENGINE WARNING breakdown of exactly which zone/layer is contested and by what).
- **To swap an entire outfit:** commit multiple `item_equip`/`item_unequip` changes in a single `commit` call (one array, one atomic write — a failed batch never persists anything). If a hard-fail names an item this same batch also unequips *later* in the array, the error includes a reorder nudge: put the `item_unequip` before the `item_equip`.
- Use `$type: ""item_unequip""` to remove an item from a character. AC/warmth recompute.
- Use `$type: ""item_use""` to consume a charge or quantity from an item (`delta: -1`). Fires ambient-decay nag if the item has an expiry.
- **Layering:** Items on the same zone+layer conflict (two breastplates). Different layers coexist (robe over chainmail). Only Armor-layer and Held items + special ""stacksWithArmor"" items contribute to AC; warmth sums across all layers.
- **Modular/independent stacking — `stackGroup`:** Set a `stackGroup` string on an item via `world_build` to carve out an independent sub-slot within its zone+layer. Items with *different* stackGroups on the same zone+layer coexist (e.g. `""pauldron-left""` + `""pauldron-right""`, both Torso/Armor); items sharing the *same* stackGroup still conflict at capacity 1 (can't wear two `""pauldron-left""`s); a stackGroup-tagged item never competes with an ungrouped item on the same zone+layer. Omit for today's flat behavior.
- **Dex-cap source with multiple Torso/Armor pieces:** once `stackGroup` lets more than one Torso/Armor item be equipped at once, set `Properties[""dexCapSource""]=""true""` on whichever piece should govern the dex-cap. With zero or one Torso/Armor item equipped, nothing changes. With multiple equipped and none marked, the engine picks one and emits a NARRATIVE PROMPT asking you to mark it explicitly; multiple marked emits an ENGINE WARNING.
- **Prerequisites & incompatibilities — `requiresEquippedTags` / `incompatibleWithEquippedTags`:** set on an item via `world_build`, checked against the `tags` of already-equipped items, independent of zone/layer. `requiresEquippedTags: [""chest-armor""]` hard-fails equipping until something tagged `""chest-armor""` is already worn (e.g. pauldrons needing a breastplate for their straps). `incompatibleWithEquippedTags: [""legwear-outer""]` hard-fails equipping while anything tagged `""legwear-outer""` is worn (e.g. a loincloth vs. travel trousers), even cross-zone. Neither is auto-resolved by `replaceConflicts` — these are declared design statements the DM must resolve explicitly by equipping/unequipping the right items.
- **Cosmetic-only fields — `visualTags` / `appearanceNote`:** set on an item via `world_build` for purely narrative appearance detail (a low-cut dress, a form-fitting outfit) that never affects AC/warmth/movement but is surfaced alongside equipped items in scene/party/NPC views for you to narrate from.
- Set `acBonus` (float) on an Armor-layer or Held item's `Properties` via `world_build` if it contributes to ArmorClass (per the layering rule above: Armor-layer, shields, or `stacksWithArmor`-tagged items only). Cached into `ArmorClass` and recomputed on every equip/unequip, same as warmth/speedModifier.
- Set `speedModifier` (float, signed) on any item via `world_build` if it affects movement: negative values slow (heavy armor, uncomfortable sandals, waterlogged gear), positive values speed up (enchanted boots, lightweight gear). Cached on `MovementModifier` and recomputed on every equip/unequip, same as warmth. Not enforced by travel — narrate the movement effects. The LLM decides which items carry modifiers based on narrative context.

## Climate & Weather

- Set `climateZone` on `world_build` (Arctic, Tundra, Temperate, Desert, Tropical, Alpine, Subterranean). Zones inherit from parent locations; `get_scene` reports current ambient temperature and time of day.
- Characters automatically track warmth vs. ambient temperature (SystemStats.Temperature = ambient + WarmthRating). Equipped items with `warmth` property sum to a WarmthRating: it raises felt temperature, which helps in the cold but hurts in the heat (furs are protective in the Arctic, dangerous in the Desert). Sustained extremes (felt temp <= -20 or >= 50) surface as narrative pressure prompts — there is no automatic mechanical penalty; the consequence call stays with the DM-LLM.
- Ambient items (provisions, scrolls) with `ambientExpiresAtDay` set decay over time. `get_world_state` nags when expiry passes; resolve via `item_update` with a fresh expiry, `archive_entity`, or `item_transfer` to a character.

## Character Appearance & Tags

- Use `$type: ""character_update""` to do the same for characters. Give them temporary `TagsToAdd` (`[""soot_covered""]`), narrative `AppearanceOverride`, or permanent `FeaturesToAdd` (`[""Scar over left eye""]`).
- When narration establishes how a PC looks (bloodied, wanted, disheveled, unarmed), persist it via `character_update` with `appearanceOverride` and `tagsToAdd` (e.g., `[""bloody"", ""wanted"", ""disheveled""]`). In crowded or opportunistic-faction scenes, `get_scene` scores these tags and may nudge a crowd reaction.

## Location State & Points of Interest

- Use `$type: ""location_update""` with `newState`, `tagsToAdd`, and `featuresToAdd` to persistently change the environment (e.g., ""On fire"", `[""smoky""]`, `[""collapsed roof""]`).

## Knowledge & Memories

- Use `$type: ""knowledge_update""` to record an important memory for a character (e.g., `""topic"": ""The Dragon"", ""details"": ""Lives in the mountain.""`). Memories naturally decay and generate prompt pressure over time to simulate epistemic drift!
- When the memory stems from a logged event, also set `sourceEventIds` (e.g. `[""events/valen-lirael-caravans""]`) — this lets you later compare the NPC's retelling against what actually happened (via `recall_history`), which is exactly what makes a good rumor: a memory that has quietly drifted from its source.

## Resources & Currency

- Award/spend party currency the same way as any other pool: `$type: ""resource""`, `poolName: ""gold""` (dnd5e/pf2e) or `""caps""` (fallout2d20), `delta: ±N`.

## Crowd Interrupts & Vulnerability

**Optional roll:** after a tense beat (not every dialog line), commit `scene_interrupt_check` with `riskModifier` (-50..+50, like `encounterRiskModifier` on travel) — omit `riskModifier` to auto-derive from tags. On success the engine promotes ONE transient from `ambientCrowd`; cooldown is one interrupt per location per day. Protective tags (`well_armed`, `escorted`, `uniform`) reduce the score.

## Faction Economic Demand

Factions have dynamic `EconomicDemand`. If a faction is desperate for an item the party is carrying (e.g. ""spell scrolls""), `get_scene` will pressure you to narrate merchants offering a premium or thieves attempting to steal them. Fulfill this naturally during roleplay!

## Interpretation in Play

Read these fields from `SceneView` and interpret them naturally. If a goblin has the ""wet"" tag, you inherently know lightning magic should be more effective. If the PC is ""disheveled"", the noble faction should react poorly.
";

    internal const string CommitEnumSection = @"# Commit Type Enum Reference

When calling `commit`, each change in the array must specify a `$type` discriminator. Here is the complete cheat sheet of valid types and their canonical usage:

{{COMMIT_ENUM_VALUES}}

## Key Rules

- All `$type` values are strings (exact case-sensitive match).
- `characterId`, `locationId`, `questId`, etc. are required where indicated — omitting them will hard-fail the commit.
- Omitting a field that is optional means the engine preserves its current value (no blank-out).
- Some `$type`s have automatic side effects (marked in `get_commit_schema`) — do not duplicate them in a single commit (e.g., do not commit both `rest` and a separate `hp` for HP recovery; `rest` auto-applies).

For more details, call `get_commit_schema` (optional category filter: Combat, Narrative, World, PlotThread).
";

    internal const string FaqSection = @"# FAQ & Laziness Traps

## Common Mistakes

**Narrating a whole new dungeon level without creates**
→ Next `get_scene` on a room ID: instant hallucination pressure + exact create JSON. Paste it.

**Creating a cellar via create but forgetting the back exit**
→ Pressure on entry: ENGINE WARNING with `location_update` JSON to add the missing exit.

**Spawning 40 named sailors for one scene**
→ Bloat; use ambient + 1-2 creates only for interactables; GC cleans the rest.

**Forgetting to `activity` change after a scene**
→ `get_scene` shows stale locations/activities. Update it.

**Ignoring an aging ""Unresolved"" event for 10 days**
→ Pressure in `get_world_state` with resolution hint. Fix it.

**Not committing `knowledge_update` after a discovery**
→ NPC will forget or confabulate the facts. Set `sourceEventIds` so you can verify truth later via `recall_history`.

**Confusing `relationship` (durable opinion) with `engagement_relation` (momentary state)**
→ Relationship is ±20 per significant event; engagement is ""grappling right now"". Use the right one.

## Tips & Tricks

**Leverage `ambientCrowd`:** Don't create 15 NPCs for a tavern. Use ambientCrowd: ""8-12 rough sailors"" and promote only the one the party talks to.

**Use `schedule_change` to make transients permanent:** A transient spawned via crowd interrupt can be `schedule_change`'d to persist across `advance_world` (it now has a routine and won't auto-GC).

**Batch related mutations:** Travel + quest progress + faction shifts + activity changes in one `commit` ensures consistency.

**Read `Suggested Commit Examples` in pressures:** The engine gives you copy-paste JSON. Use it.

**Call `recall_history` to verify ground truth:** If you're unsure whether an NPC was present for an event, search for the event, not for the NPC's memory of it.

**Check remaining pools before spending:** Before a spell or resource-heavy action, call `get_scene` or `get_npc_context` to see available slots/pools. Spending below 0 HARD-FAILS.

**Use `recordingMode: Deliberate` + `importance: Core` for player-initiated acts:** When the party *deliberately* does something they mark as important (marking the map, making a vow, burning a bridge), set these flags so the event survives all retrieval budgets.

**Transients created with `schedule: null` + `keepAlive: false` auto-GC:** If you create an NPC for a single scene and don't want to keep them around, leave schedule unset and keepAlive false. Engine cleans up when the area goes cold.

**Location state persists:** After combat, vandalism, or major events, use `location_update` to record the state. `pointsOfInterest` evolve over time; narrator the decay realistically.

**Engage visual tags early:** Persist visual state (bloodied, disheveled, wanted) via `character_update` early so crowd interrupt and faction pressure can react naturally.

**Watch your WorldPressure — it's your co-DM:** Never ignore ENGINE WARNING or NARRATIVE PROMPT. If you see the same pressure twice, you didn't commit the fix.
";

    internal const string WorldBuildingSection = @"# Initial World-Building (Session 0)

Seeding a fresh campaign — the starting region, key NPCs, opening quest — is a one-time batch job. Use `world_build` instead of many individual entity calls: it accepts arrays for every entity kind in one atomic call (all-or-nothing — a bad entry rolls back the whole batch and tells you which one failed).

## Before you seed

1. `create_campaign` (or confirm it already exists via `get_current_campaign`).
2. `set_active_system` if the ruleset isn't already locked in — bootstrap (HP/AC derivation) for `world_build`'s `characters[]` depends on it.
3. `set_narrative_focus` — steers `importance` defaults on later `event` commits.

## Recommended seeding order (matches world_build's own dispatch order)

1. **locations** — the starting hub/region first, then anywhere it links to (set `connectedFromLocationId` for auto-linked exits, or `exits` directly). Set `dangerModifier` (-50 to +50) on each one based on plausible in-fiction threat — it has no automatic/inferred value, defaults to 0 (perfectly safe) if omitted, and directly feeds the probability of `rest`/`travel`/`scene_interrupt_check` encounter rolls there. A guarded inn room might be -20; an unpatrolled wilderness saddle at night, +15 to +25.
2. **factions** — any powers already active in the region.
3. **creatures / spells / feats** — only if this campaign has homebrew content; skip otherwise.
4. **characters** — PCs first (`isPc: true`), then the handful of named NPCs the opening scene actually needs. Don't pre-create a whole cast — most NPCs should stay ambient (`ambientCrowd` on the location) until the party interacts with them.
5. **items** — starting gear, set `holderId` to the owning character. **Characters have no inline equipment fields** — a guard, soldier, crime boss, or any combat-capable NPC you seed in step 4 is unarmed/unarmored until you ALSO give them a matching `items[]` entry here in the SAME batch. It's easy to seed a rich cast of NPCs and forget this step entirely since nothing about the character record itself hints at it — `world_build` emits a non-blocking warning per newly-seeded character with no items[] entry (in this batch or already on file) specifically to catch that.
6. **quests** — the opening hook, if you have one ready.
7. **plotThreads** — DM-only scaffolding for arcs you're seeding in advance.
8. **lore** — background/history entries worth being searchable.
9. **rumors** — seed sparingly; most rumors should emerge from play, not from a pre-written list.

Forward references are fine — a quest's `giverId` pointing at a character earlier in the same batch resolves normally since `characters` dispatches before `quests`; a reference to something NOT in this batch at all just produces a non-blocking warning (create it later).

## Copy-paste example

```json
{
  ""batch"": {
    ""locations"": [
      { ""id"": ""locations/dragon-heist-yawning-portal"", ""name"": ""The Yawning Portal"", ""description"": ""A famous tavern built around a deep well leading to Undermountain."", ""type"": ""Building"", ""climateZone"": ""Temperate"", ""ambientCrowd"": ""a dozen adventurers and regulars"" }
    ],
    ""characters"": [
      { ""id"": ""chars/valen"", ""name"": ""Valen"", ""isPc"": true, ""currentLocationId"": ""locations/dragon-heist-yawning-portal"", ""systemStats"": { ""$system"": ""dnd5e"", ""hitDie"": ""d10"", ""level"": 1, ""constitution"": 14 } },
      { ""id"": ""chars/durnan"", ""name"": ""Durnan"", ""currentLocationId"": ""locations/dragon-heist-yawning-portal"", ""currentActivity"": ""Tending the bar"", ""notes"": ""Owner of the Yawning Portal, retired adventurer."" }
    ],
    ""quests"": [
      { ""id"": ""quests/find-floon"", ""title"": ""Where's Floon?"", ""giverId"": ""chars/durnan"", ""objectives"": [ { ""description"": ""Track down Floon Blagmaar's last known whereabouts"" } ] }
    ]
  },
  ""campaignName"": ""dragon-heist""
}
```

## After seeding

Call `get_world_state` — its `seedCoverage` block reports counts (locations, PC characters, factions, open quests, active plot threads) plus a short `gaps` hint list (e.g. ""no PC characters yet"", ""starting location has no climateZone""). Use it to spot what's still missing before you start the session; the gaps shrink as you seed more.

For the full field-level schema of each entity kind, see `get_help topic=commit-enum` for enum values, or inspect the `world_build` tool's own input schema (every field mirrors what `character_update`/`location_update`/etc. accept during play).
";
}
