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

**Picking up / dropping / giving an item (`$type: "item"`):** moves an *existing* item to a new holder — character, location, or container item. Never narrate a pickup without it, or the item stays owned by its old holder and `get_entity` on the location will still list it as `visibleItems`.

```json
{ "$type": "item", "itemId": "items/gold-coin", "toHolderId": "chars/lyra" }
```

For a brand-new item (loot that didn't exist yet), create it via `world_build`'s `items[]` first, then transfer if needed.
| Combat/Mechanics | `ruleset_action`, `status`, `hp`, `resource` | Dice rolls, HP, spell slots |
| Activities | `activity`, `travel`, `rest` | Movement, waiting, recovery |
| Quests/World | `quest_progress`, `rumor`, `faction_state`, `plot_thread_progress` | Story progression |
| Locations | `location_update` | State, PoI materialization, danger modifier |

**`location_update.description` is static prose** — independent of `currentState`/`pointOfInterestDetails` and never auto-rewritten. If a state change would make the old description contradict canon (e.g. a body removed from a scene, a fire put out), explicitly resend a new `description` in the same `location_update`, or `get_entity` will keep surfacing the stale text.
| Campaign | `campaign_update` | Narrative focus tags (full replacement list) |

Call `get_commit_schema` for the machine-readable field list per $type.

## Entity Creation

Never create entities through take_turn changes — there are no `_create` $types. Use `world_build` (batch: characters, locations, items, factions, quests, rumors, plotThreads, creatures, spells, feats, lore, needDescriptors), even for a single new entity (a one-item batch is fine). It reports a merge (not a duplicate) if the id already exists.

**Before calling world_build**, run the world-building seeding checklist in `dnd-exploration` — especially the 6-step location depth + plot thread enrichment check. A missed district, missing PoIs, or unfilled clues are gaps that surface as a broken `get_entity` or a flat narration later.

**Plot thread clues must materialize as real items or NPCs:** If a clue references a physical object, seed it as an `items[]` entry. The clue's `involvedEntityIds` must include the item ID so `get_entity` on the item surfaces clue context. Tag the item: `tags: ["clue:plot-threads/..."]`. Without this, the party searches the world and finds nothing.

New locations follow the same rule — see `dnd-exploration` for the Region→Settlement→District→Building→Room hierarchy and when a spot needs a full Location vs. just a PoI.

## Batch Changes (one take_turn, multiple changes)

Atomic all-or-nothing; if any change fails, the entire batch rolls back — **nothing is saved, including changes earlier in the same batch that individually "succeeded."** Fix the failing entry and resend the FULL corrected batch, not just the fix.

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
- `quest_progress` — must also include `objectiveIndex` or `objectiveName`; there's no default, and omitting both hard-fails the change (and the whole batch with it).
- `rest.intendedHours` — always set explicitly (positive number)
- `event.locationId` — never put location ID inside `involved`
- `knowledge_update.sourceEventIds` — required when `source` is `Witnessed` or `Experienced` (the character was directly there). Pass a client-chosen `eventId` on the paired `event`/`ruleset_action` change in the *same* batch and reference it here — the engine won't hand back a mid-batch ID for reuse, so you must pre-choose one. `Heard`/`Told` (secondhand/rumor) don't need this.

## Time Tracking

Most changes accept `minutesElapsed`:
- Banter: 2–5 minutes
- Tense interrogation: 60–180 minutes
- Long rest: 480 minutes (8 hours)

Time accumulates and immediately nudges hunger/thirst/tiredness.

**Calendar date:** `WorldStateView.Time.FormattedDate` (e.g. "Day 12, Month 3, Year 1492 (Current Era) — Morning") is a ready-to-narrate sentence — use it directly rather than assembling one from the raw `Year`/`Month`/`Day`/`Epoch` fields. Reference it when a scene calls for grounding the party in time (a new day, a festival, "how long have we been here"), not on every beat.

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
