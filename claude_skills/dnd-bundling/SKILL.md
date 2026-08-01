---
name: dnd-bundling
description: Which WorldChange types to bundle in one take_turn call — cohesion rules, decision tree, and common patterns
metadata:
  type: skill
---

# Bundling & Composite Actions

**Context**: Campaign Vault's `take_turn` tool handles all mutations atomically with bundled auto-refresh. This skill guides which WorldChange types to bundle in one `take_turn` call for cohesion and narrative clarity. There is no separate commit tool and no wrapper tools — `take_turn` with your chosen changes[] is the one mutation pattern.

## Core Principle: Bundling Cohesion

A **bundle** is a set of `WorldChange` types that logically belong together — they describe one atomic action from the player's perspective.

✅ **Cohesive bundles**:
- `ruleset_action` + `engagement_relation` (skill check shifts trust/suspicion)
- `ruleset_action` + `character_update` + `event` (combat damage wounds someone)
- `engagement_relation` + `event` (establish a new relationship, log it)
- `ruleset_action` + `event` + `activity` (an attack triggers an immediate cascading consequence — alarm bells, guards mobilizing — still one beat, one call)
- Any ENGINE WARNING/pressure fix + the beat you were already about to commit — never a dedicated call just for the fix

❌ **Incoherent bundles**:
- `ruleset_action` (attack) + `item_update` (unrelated item state) — use two separate take_turn calls
- `event` + `event` — typically redundant; one event should suffice
- `character_update` (mood) + `spatial_position` + `activity` (unclustered changes) — break into separate beats

## Decision Tree

### 1. **Is this one narrative beat?**

**Yes** → Go to #2
**No** (multiple distinct events) → Call `take_turn` separately for each beat

**Example: Yes**
```
Valen tries to seduce the guard.
→ One beat, one skill check, one relationship outcome
→ Single take_turn bundle
```

**Example: No**
```
Valen attacks the guard this turn. Two rounds later, after regrouping, the guard's allies set an ambush down the corridor.
→ Two turns separated by an intervening player decision/round
→ Two separate take_turn calls, one per turn

(Contrast: the attack + the alarm it triggers + guards mobilizing in response are
all IMMEDIATE, same-beat consequences of one action — that's a cohesive bundle,
ONE take_turn call: ruleset_action + event + activity. Don't split cascading
same-beat consequences just because they touch different WorldChange types.)
```

### 2. **Does the outcome change character state?**

**Yes** → Go to #3
**No** → Just an `event` or `ruleset_action` (read-only check)

**Example: Yes**
```
Valen persuades the barkeep → Barkeep's trust increases → character state changed
```

**Example: No**
```
Valen asks the barkeep "Have you heard of the Shadow Guild?"
→ DM responds with roleplay → no game state change (unless barkeep gives an item)
```

### 3. **How many WorldChange types does this action need?**

However many the beat genuinely needs — one, two, or five — they all go in ONE `take_turn` changes[] array. Never split a single beat's changes across calls (a failed batch rolls back atomically; a split batch can half-persist). Reference `get_help topic=patterns` for worked examples.

## Common Bundling Patterns

### Social Action (Persuasion, Deception, Intimidation)

**Success case**:
```json
[
  { "$type": "ruleset_action", "characterId": "chars/valen", "actionType": "SkillCheck",
    "actionName": "Persuasion", "parameters": { "skill": "Persuasion", "dc": "14" } },
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/barkeep",
    "engagement": { "verb": "persuaded", "distanceBand": "close" } },
  { "$type": "event", "category": "Social", "involved": ["chars/valen", "chars/barkeep"],
    "summary": "Valen persuaded the barkeep to reveal the gang's hideout." }
]
```

**Failure case** (suspicion increases, no reward):
```json
[
  { "$type": "ruleset_action", ... },  // skill check failed
  { "$type": "engagement_relation", "engagement": { "verb": "accused", "distanceBand": null } }
  // No event unless DM wants to log the failed attempt
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

### NPC Relationship Milestone (First Meeting)

```json
[
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/mysterious_stranger",
    "engagement": { "verb": "met", "distanceBand": "close" } },
  { "$type": "event", "category": "Narrative", "involved": ["chars/valen", "chars/mysterious_stranger"],
    "summary": "Valen encountered a mysterious stranger in the tavern." }
  // Relationship auto-logs; event captures narrative significance
]
```

## Conflict Avoidance

### Duplicate Events

❌ Don't do this:
```json
[
  { "$type": "engagement_relation", "engagement": { "verb": "met" } },  // auto-logs event
  { "$type": "event", "summary": "Valen met someone" }                 // redundant event
]
```

✅ Do this instead:
```json
[
  { "$type": "engagement_relation", "engagement": { "verb": "met" } }
  // Auto-logged event is sufficient; add explicit event only if narrative warrants unique framing
]
```

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

| **Unsure about bundling** | Use `take_turn` with explicit changes[] batch — auto-refresh handles follow-up reads |

## Refresh Opt-Ins (avoid extra round-trips)

`take_turn` echoes touched-entity summaries automatically. When the next beat needs more, opt in on the SAME call instead of a follow-up query:
- `includeParty: true` — full party roster refresh
- `includeWorldState: true` (+ `partyLocationId`) — rumors/quests/factions/time/pressures
- `fullDetailCharacterId` / `fullDetailLocationId` — one full NPC/scene view bundled in
- `extraCharacterIds` / `extraLocationIds` — refresh entities the batch didn't touch

Standalone reads with no mutation use `get_entity` instead.

For bundling decisions, use:
- This decision tree
- `get_help topic=patterns` (worked examples)
- `recommended-system-prompt.md` (SACRED RULES section)
- `get_commit_schema` (per-$type required fields and co-commit hints)
