# Bundling & Composite Actions (Phase C Guidance)

**Context**: Campaign Vault supports two approaches to multi-change actions: raw `commit` with explicit `WorldChange` arrays, or (future) composite tools that auto-bundle. This skill guides when to use which.

## Core Principle: Bundling Cohesion

A **bundle** is a set of `WorldChange` types that logically belong together — they describe one atomic action from the player's perspective.

✅ **Cohesive bundles**:
- `ruleset_action` + `engagement_relation` (skill check shifts trust/suspicion)
- `ruleset_action` + `character_update` + `event` (combat damage wounds someone)
- `engagement_relation` + `event` (establish a new relationship, log it)

❌ **Incoherent bundles**:
- `ruleset_action` (attack) + `item_update` (unrelated item state) — use two separate commits
- `event` + `event` — typically redundant; one event should suffice
- `character_update` (mood) + `spatial_position` + `activity` (unclustered changes) — break into separate beats

## Decision Tree

### 1. **Is this one narrative beat?**

**Yes** → Go to #2  
**No** (multiple distinct events) → Commit separately for each beat

**Example: Yes**
```
Valen tries to seduce the guard.
→ One beat, one skill check, one relationship outcome
→ Single bundle or commit
```

**Example: No**
```
Valen attacks the guard, the guard takes damage, and alarm bells ring across the fort.
→ Three beats: attack, alarm (event), and fort reaction (NPC activities)
→ Three separate commits
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

**One type** (e.g., just `character_update` for a mood shift)
→ Use raw `commit` with that single type, or wait for lightweight wrapper tools (Phase B+)

**Two types** (e.g., `ruleset_action` + `engagement_relation`)
→ Use raw `commit` with the pair; pattern is stable and clear

**Three+ types** (e.g., social success bundles `ruleset_action` + `engagement_relation` + `event` + `item_transfer`)
→ **Future Phase C composite tools** (when available) — use `perform_dialogue`, `update_entity`  
→ **Now**: Use raw `commit` with explicit list (reference `DmHelpManual` for bundling examples)

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

**Already handled by `attack` tool (Phase B-adjacent)**, but raw example:
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

## When to Use Each Tool

| Scenario | Tool/Approach |
|----------|---------------|
| **Simple movement (same location)** | `activity` |
| **Journey (distance, encounters)** | `travel_to` (Phase B) or `commit` with `travel` |
| **Long rest (pools recover)** | `rest_at_location` (Phase B) or `commit` with `rest` |
| **Single skill check (no side effects)** | `ruleset_action` or `get_help` for examples |
| **Combat attack** | `attack` tool (Phase B-adjacent) or `ruleset_action` + `engage_relation` |
| **Social action (future)** | `perform_dialogue` (Phase C, TBD) or manual `commit` |
| **Generic entity update** | `character_update` / `item_update` or `update_entity` (Phase C, TBD) |
| **Complex multi-beat sequence** | Multiple `commit` calls, one per beat |
| **Unsure about bundling** | Use raw `commit` with explicit types; tools fill in later |

## Phase C Roadmap

**Phase C.1**: Collect real playtest transcripts → extract bundling patterns  
**Phase C.2**: Codify patterns into tool specifications  
**Phase C.3**: Implement `perform_dialogue` and `update_entity`  
**Phase C.4**: Update this skill with concrete tool examples  

Until Phase C tools ship, rely on:
- This decision tree
- `DmHelpManual.cs` (get_help topic=patterns)
- `CommitHelpExamples.cs` (sample JSON)
- Raw `commit` with explicit WorldChange arrays
