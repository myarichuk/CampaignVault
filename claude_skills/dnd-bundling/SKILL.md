---
name: dnd-bundling
description: Which WorldChange types to bundle in one take_turn call — cohesion rules, decision tree, and common patterns
metadata:
  type: skill
---

# Bundling & Composite Actions

**Context**: `take_turn` handles all mutations atomically with bundled auto-refresh (caps/opt-ins: `dnd-world-change`). No separate commit tool exists — `take_turn` with changes[] is the one mutation pattern. Mutation syntax, required fields, auto-apply/auto-log rules → `dnd-world-change` (canonical); this skill decides *which* types cohere in one beat.

**Tool schema (Stub mode, the default):** `take_turn`'s advertised schema deliberately does NOT list `$type` verbs or fields — these skills are the source of truth. Cache the `campaignvault___*` tool names after the first successful call; do not re-run `search_connected_tools` or re-request the schema each beat. Send sparse objects (`$type` + the fields you mean, no nulls). If a `$type` is unfamiliar or a commit fails, call `lookup kind=commit_schema` (no args = index; `type=<one $type>` = its fields) instead of guessing.

## Core Principle: Bundling Cohesion

One narrative beat = one `take_turn` call, however many change types it needs — never split a beat across calls (a failed batch rolls back atomically; a split batch can half-persist). A **bundle** is the set of `WorldChange` types describing one atomic action from the player's perspective.

✅ **Cohesive**: `ruleset_action` + `engagement_relation` (check establishes a lasting state — explicit commit; only grapple/escape-grapple auto-applies); `ruleset_action` + `character_update` + `event` (damage wounds someone); `ruleset_action` + `event` + `activity` (attack's immediate cascade — alarm, mobilization — still one beat); any pressure fix + the beat already being committed (never a dedicated fix call).

❌ **Incoherent**: `ruleset_action` (attack) + `item_update` (unrelated item) — two calls; `event` + `event` — one suffices; unclustered `character_update` + `spatial_position` + `activity` — separate beats. An intervening player decision/round always splits beats (attack now vs. ambush two rounds later = two calls); immediate same-beat consequences never split.

Auto-apply/auto-log (which pairs are redundant vs. required) → `dnd-world-change`, never re-decided here: `status` and Physical/Medical `engagement_relation` self-log (no paired `event`); HP-only `ruleset_action` and Social/Attention/Proximity relations need an explicit `event`.

## Decision Tree

**1. One narrative beat?** No (distinct events separated by a decision/round) → separate `take_turn` per beat. Yes → #2.
**2. Does the outcome change state?** No → bare `event` or `ruleset_action`. Yes → #3.
**3. How many types?** All of them, in ONE changes[] array. Worked examples: `lookup kind=help topic=patterns`.

## Common Bundling Patterns

### Social Action (Persuasion, Deception, Intimidation)

**Success case**:
```json
[
  { "$type": "ruleset_action", "characterId": "chars/valen", "actionType": "SkillCheck",
    "actionName": "Persuasion", "parameters": { "skill": "Persuasion", "dc": "14" } },
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/barkeep",
    "verb": "persuaded", "category": "Social" },
  { "$type": "event", "category": "Social", "involved": ["chars/valen", "chars/barkeep"],
    "summary": "Valen persuaded the barkeep to reveal the gang's hideout." }
]
```

**Failure case** (Social relation + optional record — never auto-logged, so pair an `event` if the attempt is worth recording):
```json
[
  { "$type": "ruleset_action", ... },
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/barkeep",
    "verb": "accused", "category": "Social" }
]
```

### Combat Action (Attack + Damage)

Use `take_turn` with ruleset_action (no separate attack tool exists):
```json
[
  { "$type": "ruleset_action", "characterId": "chars/valen", "actionType": "Attack",
    "actionName": "Longsword", "targetIds": ["chars/goblin1"],
    "parameters": { "damageDice": "1d8+3" } }
  // HP delta is auto-applied; no separate $type needed
]
```

### Character State Change (Mood, Status, Appearance)

**Single change**:
```json
[
  { "$type": "character_update", "characterId": "chars/valen",
    "newMood": "Wounded", "updateAppearance": "bloodied, breathing hard" }
]
```

**With narrative log**:
```json
[
  { "$type": "character_update", ... },
  { "$type": "event", "summary": "Valen took a critical hit and stumbled backward." }
]
```

### NPC Relationship Milestone (First Meeting — Social never auto-logs, so the event is the ONLY record; don't drop it)

```json
[
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/mysterious_stranger",
    "verb": "met", "category": "Social" },
  { "$type": "event", "category": "Narrative", "involved": ["chars/valen", "chars/mysterious_stranger"],
    "summary": "Valen encountered a mysterious stranger in the tavern." }
]
```

## Conflict Avoidance

### Narrative vs. Game State

❌ Don't update mood just to log a feeling:
```json
[
  { "$type": "character_update", "newMood": "Curious" },  // not needed if fleeting
  { "$type": "event", "summary": "Valen looked curious." } // narrate in event instead
]
```

✅ Do this if mood is persistent/mechanical:
```json
[
  { "$type": "character_update", "newMood": "Cursed", "updateAppearance": "eyes glow red" },
  { "$type": "event", "summary": "Valen was cursed!" }
]
```

### Activity vs. Spatial Position

❌ Don't double-commit:
```json
[
  { "$type": "activity", "characterId": "chars/valen", "newActivity": "Examining the painting" },
  { "$type": "spatial_position", "characterId": "chars/valen", "location": "corner" }  // redundant
]
```

✅ Activity is positional already; just use:
```json
[
  { "$type": "activity", "characterId": "chars/valen", "newActivity": "Examining the painting in the corner" }
]
```

| **Unsure about bundling** | Inspect first (`get_entity` / `lookup kind=commit_schema`) — never a speculative `take_turn` (see `dnd-world-change` No-Op Rule) |

For bundling decisions, use this decision tree, `lookup kind=help topic=patterns`, and `lookup kind=commit_schema`.
