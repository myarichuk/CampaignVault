---
name: dnd-bundling
description: Shaping take_turn calls — one beat per call, never chaining beats, the one approved two-call split, and which changes belong together (load before any take_turn)
metadata:
  type: skill
---

# Bundling

This skill decides how many `take_turn` calls a moment takes and what goes into each. Field syntax, required fields and what the engine applies by itself are in `dnd-world-change`.

## One beat, one call

A beat is one atomic action from the player's point of view, with its immediate consequences. Everything it changes goes into one `changes[]` array, however many types that takes. Never split a beat across calls: a batch rolls back as a whole, but a split beat can half-persist.

A decision in between always makes two beats. An attack now and an ambush two rounds later are two calls; the alarm the attack raises this instant is part of the same one.

## Never chain beats

One player message gets at most one committing `take_turn`. If the result holds something the player hasn't seen (an interrupt, an encounter, combat starting, a roll's outcome), stop and narrate it; the player's next message decides what follows. Don't commit a rest, a fight, a chase or a journey on your own to keep the story moving. Resending a rolled-back batch and read-only refreshes don't count. A `narrativeReminder` saying "N commits in a row" means this rule was already broken.

## The one approved split

When the rest of the beat depends on a roll you haven't seen, use two calls: call A makes the roll; call B commits what it revealed (a `knowledge_update` citing call A's `eventId`, a mood, a relationship). Nothing else is split.

## What belongs together

Together in one batch:
- a check and the lasting state it creates (a persuaded NPC as a Social `engagement_relation`);
- an attack and its immediate cascade (the guard shouts, the room stands up);
- the fix for an ENGINE WARNING and the beat you are already committing (never a separate fix call).

Apart, in separate beats or not at all:
- two unrelated things (an attack and an item update elsewhere in the room);
- two events for one moment (one suffices);
- a mood committed for a passing feeling (let the prose carry it; commit `mood` only when it lasts or matters mechanically);
- `spatial_position` next to an `activity` that already says where someone is.

Multiple actors share one batch only when they act in the same instant, such as one area spell hitting several targets.

## Examples

A persuasion that lands. Social relations don't log themselves, so the event is the record:

```json
[
  { "$type": "ruleset_action", "characterId": "chars/valen", "actionType": "SkillCheck", "actionName": "Persuasion", "targetIds": ["chars/barkeep"], "parameters": { "dc": 14 } },
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/barkeep", "verb": "persuaded", "category": "Social" },
  { "$type": "event", "category": "Conversation", "involved": ["chars/valen", "chars/barkeep"], "locationId": "locations/golden-tavern", "summary": "Valen talks the barkeep into naming the gang's hideout" }
]
```

An attack. Send the weapon's dice without a modifier (the sheet adds it); damage applies itself, so no `hp` change:

```json
[
  { "$type": "ruleset_action", "characterId": "chars/valen", "actionType": "Attack", "actionName": "Longsword", "targetIds": ["chars/goblin-1"], "parameters": { "damageDice": "1d8" } }
]
```

A curse that should last: the look and the mood are committed, and the event carries the flag:

```json
[
  { "$type": "character_update", "characterId": "chars/valen", "featuresToAdd": ["eyes glow a dull red"] },
  { "$type": "mood", "characterId": "chars/valen", "newMood": "Cursed" },
  { "$type": "event", "category": "Discovery", "involved": ["chars/valen"], "locationId": "locations/crypt", "summary": "Valen touches the idol and is cursed", "impliesPersistentPhysicalChange": true }
]
```

A first meeting worth remembering:

```json
[
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/stranger", "verb": "met", "category": "Social" },
  { "$type": "event", "category": "Conversation", "involved": ["chars/valen", "chars/stranger"], "locationId": "locations/golden-tavern", "summary": "A hooded stranger sits down across from Valen uninvited" }
]
```

Moving within a room is an `activity` that says where:

```json
[
  { "$type": "activity", "characterId": "chars/valen", "newActivity": "Examining the painting in the corner" }
]
```

Unsure what a type needs? `lookup kind=commit_schema type=<$type>` or `get_entity`, never a trial `take_turn`.
