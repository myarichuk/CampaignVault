---
name: dnd-world-change
description: Atomic take_turn batches, entity creation/updates, and world state persistence
metadata:
  type: skill
---

# World-Change Mode

You are persisting changes to the world: events, character state, items, relationships, quests, locations, and factions. All in-play mutations go through **`take_turn`** — pass `changes[]` + `narrative`, and the response echoes fresh summaries of every touched entity (no re-query needed).

## Atomic Turn Discipline

**Every narrative beat ends with a same-turn `take_turn` before the player responds.** The take_turn is the period at the end of every sentence.

```json
{
  "$type": "event",
  "category": "Conversation",
  "involved": ["chars/pc", "chars/npc"],
  "locationId": "locations/tavern",
  "summary": "NPC reveals secret about the mayor"
}
```

## Change Types ($type discriminators)

| Category | $type | Purpose |
|----------|-------|---------|
| Narrative | `event`, `rumor` | Record dialogue, actions, discoveries |
| Character State | `character_update`, `mood`, `knowledge_update` | Appearance, mood, memory |
| Relationships | `relationship`, `engagement_relation`, `spatial_position` | Social bonds, proximity, restraint |
| Inventory | `item`, `item_update`, `item_equip`, `item_unequip` | Carry/drop/equip items |
| Combat/Mechanics | `ruleset_action`, `status`, `hp`, `resource` | Dice rolls, HP, spell slots |
| Activities | `activity`, `travel`, `rest` | Movement, waiting, recovery |
| Quests/World | `quest_progress`, `rumor`, `faction_state`, `plot_thread_progress` | Story progression |
| Locations | `location_update` | State, PoI materialization, danger modifier |
| Campaign | `campaign_update` | Narrative focus tags (full replacement list) |

Call `get_commit_schema` for the machine-readable field list per $type.

## Entity Creation

Never create entities through take_turn changes — there are no `_create` $types. Use `world_build` (batch: characters, locations, items, factions, quests, rumors, plotThreads, creatures, spells, feats, lore, needDescriptors), even for a single new entity (a one-item batch is fine). It reports a merge (not a duplicate) if the id already exists.

New locations follow the same rule — see `dnd-exploration` for the Region→Settlement→District→Building→Room hierarchy and when a spot needs a full Location vs. just a PoI.

## Batch Changes (one take_turn, multiple changes)

Atomic all-or-nothing; if any change fails, the entire batch rolls back — fix the failing entry and resend the FULL batch:

```json
[
  { "$type": "event", "involved": ["chars/pc", "chars/npc"], "category": "Conversation", "summary": "..." },
  { "$type": "relationship", "characterId": "chars/npc", "targetId": "chars/pc", "delta": 15 },
  { "$type": "knowledge_update", "characterId": "chars/npc", "topic": "PC_goal", "details": "..." }
]
```

## Required Fields (never rely on defaults)

- `ruleset_action.actionType` — "Attack", "Spell", "SkillCheck", etc.
- `quest_progress.newState` — "Open", "Active", "Complete", "Failed", etc.
- `rest.intendedHours` — always set explicitly (positive number)
- `event.locationId` — never put location ID inside `involved`

## Time Tracking

Most changes accept `minutesElapsed`:
- Banter: 2–5 minutes
- Tense interrogation: 60–180 minutes
- Long rest: 480 minutes (8 hours)

Time accumulates and immediately nudges hunger/thirst/tiredness.

## Narrow vs. Broad Mutations

- **Broad scope** (structural changes: field rewrites, entity creation) → `world_build`
- **Narrow scope** (tags, state, position, activity, HP, resource tweaks) → `take_turn` with `character_update`, `item_update`, `status`, `resource`, etc.
- **Ambiguous** (item's equipZones, character's Psychology/Needs profile) → `world_build` (single-item batch)

Example: Don't use take_turn changes to rewrite a character's entire Psychology. Use `character_update` for narrow tags/mood, or `world_build` if you're restructuring Psychology deeply.

## Bundled Refresh (why you never re-query)

`take_turn`'s response includes fresh summaries for touched NPCs (cap 6) and scenes (cap 3), plus opt-ins: `includeParty`, `includeWorldState`, `fullDetailCharacterId`, `fullDetailLocationId`, `extraCharacterIds`/`extraLocationIds`. If an expected section comes back null, check the response's `warnings` array.

## Checklist

- [ ] Did I narrate a beat? → `take_turn` before player responds
- [ ] Is this dialogue? → `event` with Conversation category + all speakers in `involved`
- [ ] Did something change (mood, position, item)? → Include in the batch
- [ ] Is time passing (banter, rest, travel)? → `minutesElapsed`
- [ ] Did a character level up or cast a spell? → Include `ruleset_action` or `resource` spend
- [ ] Are multiple changes happening at once? → Batch them in one `take_turn` changes array
- [ ] Did I send all required fields? → Check actionType, newState, intendedHours, locationId
