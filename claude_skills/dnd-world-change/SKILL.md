---
name: dnd-world-change
description: Atomic commits, batch mutations, entity creation/updates, and world state persistence
metadata:
  type: skill
---

# World-Change Mode

You are persisting changes to the world: events, character state, items, relationships, quests, locations, and factions.

## Atomic Commit Discipline

**Every narrative beat ends with a same-turn `commit` before the player responds.** The commit is the period at the end of every sentence.

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
| Relationships | `relationship_change`, `engagement_relation`, `spatial_position` | Social bonds, proximity, restraint |
| Inventory | `item_transfer`, `item_update`, `item_equip`, `item_unequip` | Carry/drop/equip items |
| Combat/Mechanics | `ruleset_action`, `status`, `hp_change`, `resource` | Dice rolls, HP, spell slots |
| Activities | `activity`, `travel`, `rest` | Movement, waiting, recovery |
| Quests/World | `quest_progress`, `rumor`, `faction_state`, `plot_thread_progress` | Story progression |
| Locations | `location_update` | State, PoI materialization, danger modifier |

## Single Entity Creation

Don't use `commit` for entity creation. Use `world_build` (batch) or never (rely on agent to seed via `world_build`). Exception: `character_create` (collision safety):

```json
{
  "$type": "character_create",
  "id": "chars/new-npc",
  "name": "Grask",
  "systemStats": { "hitDie": "d8" }
}
```

This surfaces a collision error if the NPC already exists.

New locations follow the same rule — see `dnd-exploration` for the Region→Settlement→District→Building→Room hierarchy and when a spot needs a full Location vs. just a PoI.

## Batch Commits (one commit, multiple changes)

Atomic all-or-nothing; if any change fails, the entire batch rolls back:

```json
[
  { "$type": "event", "involved": ["chars/pc", "chars/npc"], "category": "Conversation", "summary": "..." },
  { "$type": "relationship_change", "characterId": "chars/npc", "targetCharacterId": "chars/pc", "delta": 15 },
  { "$type": "knowledge_update", "characterId": "chars/npc", "subject": "PC_goal", "newKnowledge": "..." }
]
```

## Required Fields (never rely on defaults)

- `ruleset_action.actionType` — "Attack", "Spell", "SkillCheck", etc.
- `quest_progress.newState` — "Open", "Active", "Complete", "Failed", etc.
- `rest.intendedHours` — always set explicitly (positive number)
- `event.locationId` — never put location ID inside `involved`

## Time Tracking

Most commits accept `minutesElapsed`:
- Banter: 2–5 minutes
- Tense interrogation: 60–180 minutes
- Long rest: 480 minutes (8 hours)

Time accumulates and immediately nudges hunger/thirst/tiredness.

## Narrow vs. Broad Mutations

- **Broad scope** (structural changes: field rewrites, entity creation) → `world_build` or `upsert_*` methods (internal only, not `commit`)
- **Narrow scope** (tags, state, position, activity, HP, resource tweaks) → `commit` with `character_update`, `item_update`, `status`, `resource`, etc.
- **Ambiguous** (item's equipZones, character's Psychology/Needs profile) → `world_build`

Example: Don't `commit` to rewrite a character's entire Psychology. Use `character_update` for narrow tags/mood, or `world_build` if you're restructuring Psychology deeply.

## Checklist

- [ ] Did I narrate a beat? → `commit` before player responds
- [ ] Is this dialogue? → `event` with Conversation category + all speakers in `involved`
- [ ] Did something change (mood, position, item)? → Include in commit
- [ ] Is time passing (banter, rest, travel)? → `minutesElapsed`
- [ ] Did a character level up or cast a spell? → Include `ruleset_action` or `resource` spend
- [ ] Are multiple changes happening at once? → Batch them in one `commit` array
- [ ] Did I send all required fields? → Check actionType, newState, intendedHours, locationId
