using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CampaignVault.Models;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class MetaTools
{
    [ToolCategory("System")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"TOOL CATALOG: Returns the complete list of CampaignVault MCP tools (name, category, one-line description). Call this if search-based discovery only surfaced a subset. Optional category filter available.")]
    public Task<ToolResult<IReadOnlyList<ToolCatalogEntry>>> ListTools(
        [Description("Optional category filter. Omit to return all tools. Values: Session & exploration, Mutation & time, Combat & rulesets, Campaign management, Deep dives, World builder, System.")] string? category = null)
    {
        var tools = ToolCatalog.GetByCategory(category);
        var summary = string.IsNullOrWhiteSpace(category)
            ? $"Returned {tools.Count} tools across all categories. Call get_help for usage patterns."
            : $"Returned {tools.Count} tools in category '{category.Trim()}'.";
        return Task.FromResult(new ToolResult<IReadOnlyList<ToolCatalogEntry>>(true, tools, summary));
    }

    [ToolCategory("System")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"SYSTEM DISCOVERABILITY: CALL THIS FIRST. Returns the canonical DM manual with quickstart, tool index, copy-paste commit patterns, ruleset_actions, StatusEffects, and WorldPressure handling. Use list_tools for the full machine-readable catalog.")]
    public Task<ToolResult<string>> GetHelp()
    {
        var manual = @"# CampaignVault DM Manual

Welcome to the CampaignVault engine. Your role as the AI DM is to drive the narrative while letting the MCP engine handle the persistence, math, and simulation.

## Quickstart for Models
1. **Call `get_help`** (this document) and **`list_tools`** if search-based discovery only showed a subset.
2. **Call `get_current_campaign`** or **`create_campaign`** / **`select_campaign`** to establish campaign context.
3. **Call `get_world_state`** at session start to sync time, rumors, events, and **WorldPressure**.
4. **Call `get_scene`** whenever the party enters a location. Action any `ENGINE WARNING` / `NARRATIVE PROMPT` immediately.
5. **Call `commit`** at the end of every meaningful beat (combat, conversation, discovery, persistence).
6. **Call `advance_world`** for travel, rests, or downtime skips.

## Tool Index by Category

### Session & exploration
| Tool | Purpose |
|------|---------|
| `get_current_campaign` | Active campaign name, ruleset, lock-in status |
| `get_world_state` | Session kickoff: time, rumors, recent events, pressures |
| `get_scene` | Location, NPCs, items, rumors, ActiveCombat, SystemStats, pressures |
| `get_npc_context` | Deep NPC psychology, memories, initiative signals |
| `get_party` | Retrieve all PCs and major KeepAlive characters |
| `get_npc_needs` | Current needs + merged descriptors |
| `search_world` | Keyword search across lore, characters, locations |
| `recall_history` | Keyword search over past event summaries |
| `get_help` | Built-in DM manual and copy-paste patterns |
| `list_tools` | Full machine-readable tool catalog |

### Mutation & time
| Tool | Purpose |
|------|---------|
| `commit` | Universal atomic write (`WorldChange[]` with `$type` discriminators) |
| `advance_world` | Fast-forward days, run simulation rules, return pressures |

### Combat & rulesets
| Tool | Purpose |
|------|---------|
| `start_combat` / `next_turn` / `end_combat` | Initiative at start, turn tracking, round-based status expiry |

### Campaign management
| Tool | Purpose |
|------|---------|
| `create_campaign` / `list_campaigns` / `select_campaign` | Create, list, and activate campaigns |
| `get_config` / `set_active_system` | Read or set active ruleset (D&D 5e, PF2e, Fallout 2d20) |

### Deep dives
| Tool | Purpose |
|------|---------|
| `get_faction_context` | Full faction document (stances, territory, EconomicDemand) |
| `get_quest_details` | Full quest document (objectives, deadlines, progress timestamps) |

### World builder
| Tool | Purpose |
|------|---------|
| `upsert_character` / `upsert_location` / `upsert_lore` | Initial seeding and major structural work |
| `define_need_descriptor` / `get_need_descriptors` | Per-campaign shared need descriptions |

**During play, strongly prefer `commit` (especially `activity` changes) over world-builder upserts.**

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
- **ID Hygiene & Campaign Isolation:** To prevent ID collisions and cross-campaign data leakage, **always namespace your entity IDs** with a unique campaign prefix/slug (e.g., `locations/dragonheist-trollskull-alley`, `chars/dragonheist-volo` instead of `locations/starting-tavern`, `chars/bard`).

Supported `$type`s: `hp`, `item`, `item_update`, `status`, `statusremove`, `event`, `rumor`, `relationship`, `engagement_relation`, `spatial_position`, `need`, `attribute`, `mood`, `activity`, `ruleset_action`, `location_create`, `location_update`, `character_create`, `character_update`, `system_stats`, `knowledge_update`, `schedule_change`, `item_create`, `travel`, `rest`, `faction_create`, `faction_reputation`, `faction_state`, `quest_create`, `quest_progress`.

**Travel and Resting:** Use `travel` (with `destinationLocationId`) to safely move the party; it applies time and tiredness, and evaluates encounters based on distance. Use `rest` (with `intendedHours` and `securityModifier`) for camping or sleeping. The engine rolls for interruptions. If `rest` is interrupted, resolve the encounter before committing `hp` recovery!

**RECOMMENDED PATTERNS (copy-paste and adapt):**

**Conversation beats (CRITICAL — every `Conversation` event needs `involved`):**
When PCs talk with NPCs, always list every speaker in `involved` so `get_npc_context` can recall the exchange later. Field name is `involved` (NOT `participants`). If you forget `involved` but include `engagement_relation` + `activity` for the same characters in the same batch, the engine auto-infers — but explicit `involved` is strongly preferred.

[
  { ""$type"": ""event"", ""category"": ""Conversation"", ""summary"": ""Valen asked Lirael about missing caravans on the Gold Road."", ""involved"": [""chars/valen"", ""chars/lirael-goldvein""] },
  { ""$type"": ""engagement_relation"", ""actorId"": ""chars/valen"", ""targetId"": ""chars/lirael-goldvein"", ""category"": ""Social"", ""verb"": ""discussing the disappearances with"", ""bidirectional"": true },
  { ""$type"": ""activity"", ""characterId"": ""chars/valen"", ""newActivity"": ""Listening intently at the bar"" },
  { ""$type"": ""activity"", ""characterId"": ""chars/lirael-goldvein"", ""newActivity"": ""Sharing guarded information over the bar"" },
  { ""$type"": ""knowledge_update"", ""characterId"": ""chars/valen"", ""topic"": ""Caravan Disappearances on the Gold Road"", ""details"": ""Three caravans vanished without trace near Whispering Pass."", ""source"": ""Heard"", ""valence"": ""Negative"", ""urgency"": ""High"", ""importance"": ""Important"" }
]

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

**Engagements & Spatial Positions:** pairwise state (`engagement_relation`) vs. relative placement (`spatial_position`). Different field names: `actorId` vs `characterId`.

Categories for `engagement_relation`: `Physical`, `Social`, `Medical`, `Attention`, `Proximity`. Use a freeform `verb` (e.g. ""grappling"", ""ranting at"", ""stitching""). Omit `restrictionLevel` to use category defaults — Physical/Medical = Hard (blocks `travel` + scene pressure), Social = Soft (pressure only), Attention/Proximity = None (informational). Override with `restrictionLevel` when a beat must hard-lock travel (e.g. farewell embrace).

`distanceBand` values: `Touch`, `Close`, `Near`, `Far`, `Distant`. Optional `bearing` and `zone`.

Tavern example (drunk five paces from the party, ranting):
[
  { ""$type"": ""spatial_position"", ""characterId"": ""chars/drunk"", ""targetId"": ""chars/pc"", ""distanceBand"": ""Near"", ""zone"": ""bar"" },
  { ""$type"": ""engagement_relation"", ""actorId"": ""chars/drunk"", ""targetId"": ""chars/pc"", ""category"": ""Social"", ""verb"": ""ranting at"", ""bidirectional"": true }
]

Farewell embrace (hard-lock until resolved — override Social default):
[
  { ""$type"": ""engagement_relation"", ""actorId"": ""chars/mother"", ""targetId"": ""chars/son"", ""category"": ""Social"", ""verb"": ""embracing"", ""restrictionLevel"": ""Hard"", ""bidirectional"": true }
]

Clear when the beat ends (`verb` or `distanceBand` null):
[
  { ""$type"": ""engagement_relation"", ""actorId"": ""chars/mother"", ""targetId"": ""chars/son"", ""verb"": null, ""bidirectional"": true },
  { ""$type"": ""spatial_position"", ""characterId"": ""chars/drunk"", ""targetId"": ""chars/pc"", ""distanceBand"": null }
]

*Combat vs manual: ruleset resolvers automatically establish and clear mechanical engagements (grappling, escape) via `ruleset_action` contested checks. For unresolved non-combat beats (hugs, tending wounds, intense confrontations), commit `engagement_relation` yourself — otherwise scene pressure will nag you and Hard engagements block `travel`.*

Item + transfer patterns, status with modifiers, ruleset_action (see below), etc.

**After you see a pressure in get_scene/get_world_state, your *next* action should usually be a `commit` using the exact snippet provided (adapted with real IDs/names).** Then narrate the outcome. The engine will clear the pressure on subsequent reads.

## Schrödinger's World + Transient / Open-World Patterns (Critical for Laziness Mitigation)
- **Flavor without bloat**: When narrating a crowded tavern, a bustling market, rats in a cellar, or ""a bard playing a lute in the corner"", **do not** immediately `character_create` 20 people. Instead:
  - On initial `location_create` or via `location_update`: populate `pointsOfInterest` (light list of strings returned in get_scene) and/or `ambientCrowd` (string hint, e.g. ""8-15 rough sailors and dockworkers"").
  - The engine will surface a `NARRATIVE PROMPT` in get_scene when the live scene is empty but ambient is expected: this is your cue to spawn 1-3 *interactable* transients via `character_create` if players engage, or just narrate using the hint.
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
  { ""$type"": ""rumor"", ""subject"": ""smuggling"", ""newText"": ""The smuggler who supplied the rebels was caught and jailed."", ""newState"": ""Resolved"" },
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
  { ""$type"": ""rumor"", ""subject"": ""Nightshade Gang"", ""newText"": ""Nightshade pirates have raided three barges on the Ashford River this month — cargo vanishing, crews turning up dead."", ""newState"": ""Active"", ""sourceCharacterId"": ""chars/bram-the-barkeep"" },
  { ""$type"": ""quest_create"", ""questId"": ""quests/stop-nightshade"", ""title"": ""Cut Out the Nightshade"", ""description"": ""The river merchants are desperate. Find and disrupt the Nightshade Gang's operations on the Ashford."", ""objectives"": [ { ""description"": ""Locate the Nightshade hideout"", ""state"": ""Active"" }, { ""description"": ""Destroy or scatter the gang"", ""state"": ""Pending"" }, { ""description"": ""Report back to the River Merchants' Guild"", ""state"": ""Pending"" } ], ""deadlineDays"": 14 },
  { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Bram Ironarm told the party about the Nightshade Gang's river raids. Quest: Cut Out the Nightshade accepted."" }
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
  { ""$type"": ""hp"", ""characterId"": ""chars/nightshade-boss"", ""delta"": -99, ""sourceCharacterId"": ""chars/pc1"" },
  { ""$type"": ""quest_progress"", ""questId"": ""quests/stop-nightshade"", ""objectiveIndex"": 1, ""newState"": ""Complete"", ""narrativeNote"": ""Gang leader slain; surviving members fled or surrendered."" },
  { ""$type"": ""faction_state"", ""factionId"": ""factions/nightshade-gang"", ""influenceDelta"": -30, ""narrative"": ""Leadership killed in the warehouse raid. Gang scattered."" },
  { ""$type"": ""faction_reputation"", ""factionId"": ""factions/river-merchants-guild"", ""characterId"": ""chars/pc1"", ""delta"": 20 },
  { ""$type"": ""faction_reputation"", ""factionId"": ""factions/city-watch"", ""characterId"": ""chars/pc1"", ""delta"": 8 },
  { ""$type"": ""rumor"", ""subject"": ""Nightshade Gang"", ""newText"": ""The Nightshade pirates were smashed by a band of adventurers at their own hideout. The river may be safe again."", ""newState"": ""Resolved"" },
  { ""$type"": ""event"", ""category"": ""Combat"", ""summary"": ""Party raided the Nightshade warehouse. Boss killed, gang scattered. River Merchants Guild grateful."" }
]

**Beat 4 — Resolution + world state shift (report to the guild):**
Party reports back. Quest closes, territory adjusts, maybe a new rumor seeds:
[
  { ""$type"": ""quest_progress"", ""questId"": ""quests/stop-nightshade"", ""objectiveIndex"": 2, ""newState"": ""Complete"", ""narrativeNote"": ""Party reported to the River Merchants Guild. Reward collected."" },
  { ""$type"": ""faction_state"", ""factionId"": ""factions/river-merchants-guild"", ""influenceDelta"": 10, ""narrative"": ""Guild influence rising now the river route is open; trade caravans resuming."" },
  { ""$type"": ""rumor"", ""subject"": ""Ashford River"", ""newText"": ""Merchants are saying the Ashford route is profitable again. Caravans are reforming for the first time in weeks."", ""newState"": ""Active"" },
  { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Quest complete. River Merchants Guild paid the reward. Trade caravans reforming on the Ashford."", ""involved"": [""chars/pc1"", ""factions/river-merchants-guild""] }
]

After Beat 4: `get_world_state` will show the quest as resolved, both factions at updated standing, the original rumor as Resolved (no longer nagging), and a new active rumor seeding the next hook. Faction pressure contributors will start surfacing new opportunistic moves from the now-stronger River Merchants Guild if their influence crossed the threshold. The engine does the bookkeeping; you drive the story.

**Character Combat Bootstrap — required for all combatants (KeepAlive OR maxHp > 0):**
The engine emits ENGINE WARNING until BOTH are set:
1. **HP**: `maxHp` (+ optional `currentHp`)
2. **systemStats**: ruleset-specific combat stats via `systemStats` on `character_create` or `system_stats` patch

D&D 5e reference (level 1, max hit die + CON modifier):
- Fighter / Paladin / Ranger: d10 → 10 + CON mod
- Cleric / Druid / Monk / Warlock / Bard: d8 → 8 + CON mod
- Rogue / Artificer: d8 → 8 + CON mod
- Wizard / Sorcerer: d6 → 6 + CON mod
- Barbarian: d12 → 12 + CON mod

For NPCs/creatures: use the stat block value (e.g. Goblin = 7 HP, AC 15, DEX 14).
Infer from class+level for PCs. Pure flavor transients (no HP, not KeepAlive) skip this.

Full 5e bootstrap at create:
{ ""$type"": ""character_create"", ""characterId"": ""chars/goblin-scout"", ""name"": ""Goblin Scout"", ""maxHp"": 7, ""currentHp"": 7, ""classLevel"": ""Goblin 1"", ""systemStats"": { ""$system"": ""dnd5e"", ""armorClass"": 15, ""dexterity"": 14, ""strength"": 8, ""skillModifiers"": { ""Stealth"": 6, ""Perception"": 2 }, ""savingThrowModifiers"": { ""Dexterity"": 2 } } }

PF2e bootstrap:
{ ""$type"": ""character_create"", ""characterId"": ""chars/level2-fighter"", ""name"": ""Elara"", ""keepAlive"": true, ""maxHp"": 32, ""currentHp"": 32, ""classLevel"": ""Human Fighter 2"", ""systemStats"": { ""$system"": ""pf2e"", ""armorClass"": 19, ""strengthMod"": 4, ""dexterityMod"": 2, ""skillModifiers"": { ""Perception"": 8, ""Athletics"": 9 }, ""savingThrowModifiers"": { ""Fortitude"": 9, ""Reflex"": 7, ""Will"": 6 } } }

Fallout 2d20 bootstrap:
{ ""$type"": ""character_create"", ""characterId"": ""chars/raider"", ""name"": ""Raider"", ""maxHp"": 10, ""currentHp"": 10, ""systemStats"": { ""$system"": ""fallout2d20"", ""agility"": 7, ""perception"": 6, ""endurance"": 5, ""defense"": 1, ""skills"": { ""SmallGuns"": 2 }, ""tagSkills"": [""SmallGuns""] } }

Patch stats on existing character:
{ ""$type"": ""system_stats"", ""characterId"": ""chars/campaign-thorin"", ""systemStats"": { ""$system"": ""dnd5e"", ""armorClass"": 16, ""strength"": 16, ""skillModifiers"": { ""Athletics"": 5 } } }

**The Visual / Physics Sandbox (Tags & Appearance) & Knowledge:**
The engine intentionally avoids hardcoding vulnerability scores or mechanical checks for narrative states like ""wet"" or ""disheveled"". You (the LLM) are the physics engine.
- Use `$type: ""item_create""` with `coreCategory` (e.g., ""Weapon"", ""Armor"", ""Document"") when looting or discovering items. Set `holderId` to a PC character ID (or ""party"") for inventory.
- Use `$type: ""item_update""` to add temporary `TagsToAdd` (e.g., `[""wet"", ""muddy""]`) and a narrative `NewState` (e.g., ""Covered in mud"") to items. You can also add permanent `FeaturesToAdd` (e.g., ""Leather wrapped handle"") or change `coreCategory`.
- Use `$type: ""character_update""` to do the same for characters. Give them temporary `TagsToAdd` (`[""soot_covered""]`), narrative `AppearanceOverride`, or permanent `FeaturesToAdd` (`[""Scar over left eye""]`).
- Use `$type: ""location_update""` with `newState`, `tagsToAdd`, and `featuresToAdd` to persistently change the environment (e.g., ""On fire"", `[""smoky""]`, `[""collapsed roof""]`).
- Use `$type: ""knowledge_update""` to record an important memory for a character (e.g., `""topic"": ""The Dragon"", ""details"": ""Lives in the mountain.""`). Memories naturally decay and generate prompt pressure over time to simulate epistemic drift!
- Read these fields from `SceneView` and interpret them naturally. If a goblin has the ""wet"" tag, you inherently know lightning magic should be more effective. If the PC is ""disheveled"", the noble faction should react poorly.
- Factions have dynamic `EconomicDemand`. If a faction is desperate for an item the party is carrying (e.g. ""spell scrolls""), `get_scene` will pressure you to narrate merchants offering a premium or thieves attempting to steal them. Fulfill this naturally during roleplay!

## Ruleset Actions (Combat & Skill Checks)
Use `ruleset_action` inside a `commit` to roll dice and resolve attacks or skill checks via the active ruleset. The engine rolls, does the math, and returns the result (including degrees of success).
- **$type**: `""ruleset_action""`
- **actorId**: The character performing the action.
- **targetIds**: Array of target character IDs (for attacks or opposed checks).
- **actionType**: `""Strike""`, `""SkillCheck""`, `""SavingThrow""`, `""ContestedCheck""`, `""UseItem""`.
- **actionName**: Freeform text (e.g. `""longsword""`, `""Athletics""`, `""Fireball""`).
- **parameters**: Dictionary of overrides and hints for the resolver:
  - `""dc""`: Difficulty Class for skill checks or saves (5e/PF2e).
  - `""bonus""`: Attack roll bonus.
  - `""damageDice""`: Damage expression (e.g. `""1d8""` or `""3""` for Fallout).
  - `""damageBonus""`: Flat damage bonus.
  - `""ac""`: Override target AC.
  - `""mapPenalty""`: Multiple Attack Penalty for PF2e (e.g. `""5""` or `""10""`).
  - `""difficulty""`: Success count threshold for Fallout 2d20 (default 1).
  - `""attribute""`: Attribute to use for Fallout 2d20 (e.g. `""Agility""`).
  - `""skill""`: Skill to use for Fallout 2d20 (e.g. `""SmallGuns""`).
  - `""pool""`: Number of d20s to roll for Fallout 2d20 (default 2).
  - `""vicious""`: `""true""` to add effects symbols to damage bonus in Fallout.
  - `""piercing""`: Piercing rating for Fallout DR calculation.
- **advantageState**: `""Advantage""`, `""Disadvantage""`, or `""None""` (currently natively supports 5e; PF2e Fortune effects must be handled manually or by adjusting `""bonus""`).

Example:
```json
[
  {
    ""$type"": ""ruleset_action"",
    ""actorId"": ""chars/fighter"",
    ""targetIds"": [""chars/goblin""],
    ""actionType"": ""Strike"",
    ""actionName"": ""longsword"",
    ""parameters"": {
      ""bonus"": ""7"",
      ""damageDice"": ""1d8"",
      ""damageBonus"": ""4"",
      ""mapPenalty"": ""0""
    }
  }
]
```

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
        const string enumInsertAfter = "over world-builder upserts.**";
        var enumMarkerIndex = manual.IndexOf(enumInsertAfter, StringComparison.Ordinal);
        if (enumMarkerIndex >= 0)
        {
            var insertAt = enumMarkerIndex + enumInsertAfter.Length;
            manual = manual.Insert(insertAt, CommitEnumCheatSheet.Full);
        }

        return Task.FromResult(new ToolResult<string>(true, manual, "Help manual retrieved."));
    }
}
