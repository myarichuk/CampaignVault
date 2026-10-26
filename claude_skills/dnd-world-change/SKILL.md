---
name: dnd-world-change
description: Committing changes with take_turn — change types, required fields, atomic batches, auto-applied effects, persistent state, entity creation, time and refresh options
metadata:
  type: skill
---

# World Change

Every in-play change goes through `take_turn`: a `changes[]` array plus a `narrative` log entry (one or two plain sentences with the beat's concrete content: who did what, what was found or said, what changed). The scene itself is written separately, at full length. The response carries fresh summaries of every entity it touched, so no re-query is needed. New entities are created with `world_build`, never through `take_turn`.

## Which skill owns what

Each rule lives in one skill; the others name it in a few words. On a conflict, the owner wins.

| Skill | Owns |
|---|---|
| `dnd-world-change` | change syntax, required fields, atomic batches, auto-apply and auto-log, persistent state, entity creation, time, refresh options |
| `dnd-bundling` | one beat = one call, never chaining beats, which changes belong together |
| `dnd-combat` | combat flow, action types, restraint, status effects, range, feat effects (every ruleset) |
| `dnd5e-combat` / `pf2e-combat` | what D&D 5e / Pathfinder 2e add: attack flags, spells, slots, dying |
| `dnd-exploration` | location hierarchy, travel, rest, search, encounters and their cleanup |
| `dnd-npc-interaction` | reading NPCs, their voice, knowledge, needs, initiative, the two-beat rule; logging dialogue; social checks and relationship modifiers |
| `dnd-campaign-events` | ENGINE WARNINGs, quests, rumors, factions, plot-thread progress, time skips |
| `dnd-world-building` | seeding with `world_build`: order, depth, plot threads, clues, items |
| `dnd-narration` | the prose after the commit (`dnd-narration-examples`: worked passages) |

## Inspecting is read-only

Diagnose with queries: `get_entity`, `search_world`, `recall_history`, `lookup`. A `take_turn` with changes commits them and ticks the clock, even a trivial batch, so never send a change to look at something; one with no changes (a `memoriesOnlyCharacterId` or `forceFullReseed` refresh) commits nothing. `includeWorldState` and `includeParty` verify a fix you are sending; they are not status polls.

## Batches are atomic

One `take_turn` is all or nothing. If any change fails, nothing in the batch is saved, including entries that looked fine. Fix the failing entry and resend the whole batch, not only the fix.

```json
[
  { "$type": "event", "category": "Conversation", "involved": ["chars/pc", "chars/npc"], "locationId": "locations/tavern", "summary": "The smith admits the mayor paid for the blades" },
  { "$type": "relationship", "characterId": "chars/npc", "targetId": "chars/pc", "delta": 15, "reason": "The PC kept her secret from the watch" },
  { "$type": "knowledge_update", "characterId": "chars/npc", "topic": "PC_goal", "details": "The PC is hunting whoever armed the raiders", "source": "Told" }
]
```

Send each change sparse: `$type` plus the fields you mean, no nulls. The tool schema does not list change types; for the fields of one, call `lookup kind=commit_schema type=<$type>` (no `type` gives the index). Do that on a failure or an unfamiliar type, not every beat.

## Change types

| Area | `$type` | Use |
|---|---|---|
| Record | `event`, `rumor` | what happened; a rumor's lifecycle |
| Rolls and bodies | `ruleset_action`, `hp`, `grant_temp_hp`, `status`, `status_remove`, `apply_effect`, `resource`, `rest`, `death_save`, `death`, `xp_grant`, `level_up` | checks, attacks, spells, damage, temp HP, conditions, keyed buffs/debuffs, pools, rests, dying |
| A character | `character_update`, `mood`, `knowledge_update`, `need`, `attribute`, `activity`, `schedule_change`, `soil`, `piercing` | appearance and tags, mood, memories, needs, scores, what they are doing, dirt and marks on a body |
| Between people | `relationship`, `engagement_relation`, `spatial_position`, `scene_setup`, `tether` | opinion, lasting holds and states, tactical spacing, being tied to someone or something |
| NPC behaviour | `npc_initiative_nudge`, `scene_interrupt_check` | prime an NPC to act next; let the crowd break in |
| Items | `item`, `item_equip`, `item_unequip`, `item_update`, `item_use` | move, wear, change, use |
| Places | `travel`, `location_update` | journeys; a place's state, description, exits |
| Story | `quest_progress`, `plot_thread_progress`, `plot_thread_clue`, `faction_state`, `faction_reputation`, `world_event_status` | progression |
| Campaign | `campaign_update`, `mode_transition`, `archive_entity` | focus tags, play mode, retiring non-character entities |

**`relationship`, `engagement_relation`, `spatial_position` and `faction_state` are different things.** `relationship` is a numeric opinion change (−100 to 100) between two characters and needs a `reason`; a successful check never moves it by itself. `engagement_relation` is a lasting physical or social state between two characters (a verb plus `category`: grappling/Physical, escorting/Proximity, persuaded/Social). `spatial_position` is tactical spacing only. `faction_state` and `faction_reputation` are about factions, not people.

## Field rules the schema can't say

The generated Fields section below lists names, types, enums and required flags; these are the rules on top of it.

- `ruleset_action`: for a SkillCheck the `actionName` is the skill; `parameters.skill` exists only to roll under a different skill than the label. A Spell also needs `parameters.resolution` (attack, save, check, heal, utility); a SkillCheck needs `parameters.dc`. Omit `bonus`, `damageBonus` and a spell's `dc`: the sheet supplies them.
  ```json
  { "$type": "ruleset_action", "characterId": "chars/pc", "actionName": "Investigation", "actionType": "SkillCheck", "parameters": { "dc": 12 } }
  { "$type": "ruleset_action", "characterId": "chars/pc", "actionName": "Prestidigitation", "actionType": "Spell", "parameters": { "resolution": "utility" } }
  ```
- `quest_progress`: `newState` (Open, InProgress, Complete, Failed, Skipped) and `objectiveIndex` or `objectiveName`.
- `rest`: `intendedHours`.
- `event`: `locationId` goes in its own field, never inside `involved`. `category` is the closed list in Fields (there is no Exploration or Social); a wrong one fails the whole batch.
- `engagement_relation`: set `category`; left out, an unknown verb becomes Social, which never blocks travel.
- `faction_state`: `factionId` is the subject; `targetFactionId` only for a stance toward another faction.
- `knowledge_update`: `source` is Witnessed, Heard, Told, Experienced, Trauma or Conditioned; `salience` is a number from 0 to 1. Witnessed and Experienced also need `sourceEventIds`: give the paired `event` or `ruleset_action` in the same batch your own `eventId` and cite it, because the engine does not hand back ids mid-batch.

## What the engine applies for you

- `ruleset_action` applies its own damage, healing, conditions and grapple engagement. Never add a manual `hp`, `status` or `engagement_relation` for the same character in the same batch: the batch fails as a duplicate. Commit those only for something unrelated.
- Utility spells apply nothing. Commit their lasting effect as a `status` and the slot as a `resource` in the same batch.
- `status` and Physical or Medical `engagement_relation` log themselves. Everything else that matters (an HP-only `ruleset_action`, a Social, Attention or Proximity relation) needs its own `event`.

## Physical state persists only when committed

Gear worn, bonds cut, conditions ended, a new scar or outfit: anything the story changes about a body or an item reverts next scene unless it is committed. Putting on a gift is `item_equip`; cutting bonds or ending a condition is `status_remove`; a lasting look is `character_update` (`appearanceOverride`, `featuresToAdd`); gear lost or destroyed is `item_unequip`, `item_update` or `archive_entity`. Set `impliesPersistentPhysicalChange: true` on the beat's `event`; the engine then checks the batch and reminds you if the commit is missing.

Picking up, dropping or handing over an existing item is an `item` change to the new holder (a character, a location or a container item). Without it the item stays where it was.

```json
{ "$type": "item", "itemId": "items/gold-coin", "toHolderId": "chars/lyra" }
```

`location_update.description` is static prose that is never rewritten for you. When a change would make it wrong (the body is gone, the fire is out), send a new `description` in the same change.

## Entity creation and seeding before you name

Mid-play, a new person, thing or place is a change in the same `take_turn` as the beat that introduces it: `character_create` (characterId, name, currentLocationId, lifeStage, notes; `systemStats` for anyone who may fight; `keepAlive: true` if worth keeping), `item_create` (itemId, name, holderId; `definitionName` to seed from a template found with `lookup kind=items`, your fields win), `location_update` with a new locationId (name, parentLocationId, `addExit` back to where the party is), and `rumor_create`. An existing characterId is merged, not duplicated; an existing itemId fails (move it with `item`, change it with `item_update`). Session-zero seeding and anything structural (factions, quests, plot threads, creatures, spells, feats, lore, rewriting a psychology) stays on `world_build` on the /build connector; run the checklist in `dnd-world-building` before seeding a new area.

Never narrate a named person into existence. Before anyone gets a name, a voice or an action of their own, check the scene roster (`presentNPCs` lists everyone present) and the cards; if they are missing, `character_create` them (with `keepAlive: true` if worth keeping) in the same batch as the scene. Unnamed background stays unnamed. If a seeded name still fails, `search_world` for the real id before retrying.

## Needs, attributes, traits, death

- **`need`** moves a tracked need (`characterId`, `need`, `delta`). The name is free (`paranoia`, `homesickness`). Only hunger, thirst, tiredness and social_drive tick with time on their own; any other need moves only when you commit it, unless you give it `accumulationRate` (points per day) once. Pass `delta: 0` to set only the rate, and `accumulationRate: 0` to stop drift. A sudden spike (a big drink) is a `delta` in the same batch as its cause, not a rate change.
- **`attribute`** sets a persistent 0–100 score (`corruption`, `debt_pressure`) with `value` and optional `isDelta`. It never shows as a need.
- **`character_update.systemStats.traits`** holds free string facts. Sending one key merges it into the rest.
  ```json
  { "$type": "character_update", "characterId": "chars/lyra", "systemStats": { "$system": "dnd5e", "traits": { "disguise_persona": "traveling merchant" } } }
  ```
- **`death`** (`characterId`, `cause`, optional `killerId`) is the only way a character dies; 0 HP is only downed. Commit it in the batch that narrates the death. The body stays lootable at the location and the character leaves the scene, schedules and pressure. `revive: true` (with `hp` and `newLocationId`) undoes it. Never `archive_entity` a character. A dead PC is the player's call: offer a new character, revival or an ending.
- **`death_save`** rolls one 5e death save for a PC at 0 HP (omit `roll` and the engine rolls). Damage at 0 HP adds a failure and massive damage kills outright; NPCs never die on their own, so commit `death` for them.

## A roll's outcome must be written down

A pure read (Perception, Insight, a social read) changes nothing by itself, and the logged event is built from your pre-roll narrative. If the character would remember the result, fold it into a `knowledge_update` or `event` in your next batch. A roll that moved HP, items or a quest already persists.

## Time

- Set `minutesElapsed` on the request, next to `changes`, never inside a change: banter 2–5 minutes, a tense interrogation 10–30, a long talk into the night 60–180. `rest` and `travel` carry their own hours; don't add `minutesElapsed` beside them.
- Time ticks hunger, thirst and tiredness at once.
- The campaign clock and location in the latest response win over your memory and the session handoff. If it is the third hour, it is the small hours: narrate that, or commit the time forward first.
- `FormattedDate` (for example "Day 12, Month 3, Year 1492 — Morning") is ready to use when a scene needs the date.

## Refresh options on the same call

- `take_turn` returns summaries of touched entities (up to 6 NPCs and 3 scenes). On a delta turn, unchanged appearance, gear, psychology and memories are left out: a missing field means unchanged, not gone.
- `start_session` carries the opening room; after that, `fullDetailLocationId` on the travel call gives the room you enter (people, plot threads, pressure); `fullDetailCharacterId` gives one NPC's roleplay card, and `includeCombatDetail: true` adds stats and gear once a roll or fight involves them; `memoriesOnlyCharacterId` is the cheap memory-only refresh after a gap; `extraCharacterIds` and `extraLocationIds` add untouched entities.
- `includeParty` only when a PC's HP, slots, gold, needs, AC or gear changed, or before first narrating their needs this session. `partyFingerprint` already covers HP and location; echo it back as `clientPartyFingerprint` every call.
- `includeWorldState` rebuilds the whole world state: use it to verify a pressure fix, never by default. `forceFullReseed` only after a context compaction or at a fresh start.
- A standalone read of one entity is `get_entity`. A section that comes back null is explained in `warnings`; one-time guidance arrives in `guidanceHints`.

## Broad or narrow

A narrow change (tags, mood, position, activity, HP, a pool) is a `take_turn` change. Restructuring an entity (rewriting a psychology, an item's equip zones) is a one-entry `world_build`.

## Checklist

- [ ] Everything this beat changed is in one batch, with required fields set.
- [ ] Nothing the engine applies was added by hand.
- [ ] Lasting physical changes are committed, with `impliesPersistentPhysicalChange` on the event.
- [ ] Anyone newly named is seeded.
- [ ] Time is on the request, not inside a change.

<!-- fields:begin -->
## Fields

Generated from the server: `name:type [req] (enum values) {object shape}`. Unknown field names fail the batch. Field notes and every other type: `lookup kind=commit_schema type=<$type>`.

**activity** — Update what an NPC is currently doing and/or where they're…
- characterId:string [req]
- newActivity:string
- newLocationId:string
- updateLocation:bool
- reason:string

**engagement_relation** — Establish, update, or remove a pairwise engagement state…
- characterId:string [req]
- targetId:string [req]
- category:enum (Physical|Social|Medical|Attention|Proximity)
- verb:string
- clear:bool
- restrictionLevel:enum (None|Soft|Hard)
- bidirectional:bool

**event** — Record a noteworthy occurrence in the world; appears in…
- summary:string [req]
- category:enum (Unresolved|Combat|Conversation|Discovery|Arrival|Betrayal|SceneCommit|Timeskip|Simulation|Interaction|Test|Travel|SceneInterrupt|Departure)
- involved:string[]
- emotionalBeat:string
- initiatorId:string
- relatedEntityId:string
- locationId:string
- relatedLocationIds:string[]
- impliesPersistentPhysicalChange:bool
- eventId:string
- importance:enum (Trivial|Important|Core)
- recordingMode:enum (Passive|Deliberate)
- details:map<string,any>

**hp** — Adjust a character's current HP by a delta.
- characterId:string [req]
- delta:int
- critical:bool

**item** — Move/transfer an existing item to a new holder (character…
- itemId:string [req]
- toHolderId:string [req]

**mood** — Directly override an NPC's current emotional mood string…
- characterId:string [req]
- newMood:string [req]

**relationship** — Apply a numeric delta to the relationship score between two…
- characterId:string [req]
- targetId:string [req]
- reason:string [req]
- delta:int

**ruleset_action** — Perform a ruleset-mechanical action (attack, save, skill…
- characterId:string [req]
- actionName:string [req]
- actionType:enum [req] (Attack|Spell|SkillCheck|ContestedCheck|OpposedCheck|UseItem|Recovery|SavingThrow)
- targetIds:string[]
- actionCategory:enum (Melee|Ranged|Spell|Maneuver|Social|Survival)
- advantageState:enum (None|Advantage|Disadvantage)
- damageType:string
- isReaction:bool
- reactionTrigger:string
- parameters:map<string,string>
- FeatEffects:map<string,object[]>

**status** — Add a structured status effect (condition) to a character…
- characterId:string [req]
- status:string [req]
- effect:object {name:string, category:string, affectedPart:(Head|Neck|Torso|LeftArm|RightArm|LeftHand|RightHand|LeftLeg|RightLeg|LeftFoot|RightFoot), statModifiers:map<string,number>, expiresAtDay:number, expiresAtRound:int, recoveryHint:string, appliedBy:string, conditionName:string, pendingDamage:object, effectKey:string, effectTier:string, expiresAtOwnTurnStart:bool}

**travel** — Record a party or character traveling between two connected…
- characterId:string [req]
- destinationLocationId:string [req]
- narrative:string
- travelCostHoursOverride:number
- terrainOverride:string
- encounterRiskModifier:int
- hazard:string

Other types: apply_effect, archive_entity, attribute, campaign_update, character_create, character_update, death, death_save, faction_reputation, faction_state, grant_temp_hp, item_create, item_equip, item_unequip, item_update, item_use, knowledge_update, level_up, location_update, mode_transition, need, npc_initiative_nudge, piercing, plot_thread_clue, plot_thread_progress, quest_progress, resource, rest, rumor, rumor_create, scene_interrupt_check, scene_setup, schedule_change, soil, spatial_position, status_remove, tether, world_event_status, xp_grant.
<!-- fields:end -->
