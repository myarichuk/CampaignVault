# Bundling Patterns Reference (Phase C Candidate Examples)

This document catalogs observed and candidate bundling patterns for Phase C composite tools. Each pattern shows the narrative action, the logical bundle, and the rationale.

**Status**: Research phase — these patterns are based on system design, not yet validated against real playtest transcripts. Phase C.1 will confirm or revise.

## Legend

- **Narrative** — What the player said/did in English
- **Bundle** — Array of `WorldChange` types that should be committed atomically
- **Rationale** — Why this bundle is cohesive
- **Conflicts** — Known conflicts or edge cases to avoid
- **Confidence** — Design confidence: High (validated in code), Medium (designed but untested), Low (speculative)

---

## Social Actions

### Pattern: Persuasion Attempt (Success)

**Narrative**: "Valen tries to persuade the barkeep to reveal the gang's hideout."

**Bundle**:
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

**Rationale**:
- Skill check determines if persuasion works
- Engagement captures trust/relationship shift
- Event logs narrative significance

**Conflicts**:
- Don't double-commit the event if `engagement_relation` already auto-logs

**Confidence**: Medium (design sound, needs transcript validation)

**Phase C Tool**: `perform_dialogue(actor_id, target_id, narrative, skill_check_result, relationship_delta?)`

---

### Pattern: Persuasion Attempt (Failure)

**Narrative**: "Valen tries to deceive the guard about his disguise. The guard sees through it."

**Bundle**:
```json
[
  { "$type": "ruleset_action", "characterId": "chars/valen", "actionType": "SkillCheck",
    "actionName": "Deception", "parameters": { "skill": "Deception", "dc": "16" } },
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/guard",
    "engagement": { "verb": "accused", "distanceBand": "close" } }
]
```

**Rationale**:
- Skill check shows failure
- Engagement captures suspicion increase
- No separate event needed (suspicion change is the narrative outcome)

**Confidence**: Medium

---

### Pattern: Social Action with Item Reward

**Narrative**: "Valen successfully persuades the merchant to give him a discount on the healing potion."

**Bundle**:
```json
[
  { "$type": "ruleset_action", "characterId": "chars/valen", "actionType": "SkillCheck",
    "actionName": "Persuasion", "parameters": { "skill": "Persuasion", "dc": "12" } },
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/merchant",
    "engagement": { "verb": "charmed", "distanceBand": "close" } },
  { "$type": "item_update", "itemId": "items/healing-potion-1", 
    "updatePrice": "25_gp" },  // Discount applied
  { "$type": "event", "category": "Social", "involved": ["chars/valen", "chars/merchant"],
    "summary": "The merchant gave Valen a 25% discount on a healing potion." }
]
```

**Rationale**:
- Persuasion check + engagement shift + item state change (price) + narrative log
- Cohesive because all parts result from one social success

**Conflicts**:
- Don't also commit `character_update` for "happy" mood unless it's persistent/mechanical

**Confidence**: Low (needs transcript validation for item bundling patterns)

---

## Combat Actions

### Pattern: Attack Roll (Already Handled by `attack` Tool)

**Note**: The `attack` tool (Phase B-adjacent) wraps this. Raw example for reference:

**Narrative**: "Valen attacks the orc with his longsword."

**Bundle**:
```json
[
  { "$type": "ruleset_action", "characterId": "chars/valen", "actionType": "Attack",
    "actionName": "Longsword", "targetIds": ["chars/orc-1"],
    "parameters": { "damageDice": "1d8+3" } }
]
```

**Rationale**:
- Attack roll + damage is one atomic action
- HP delta auto-applied; no separate `character_update` needed

**Confidence**: High (implemented in `attack` tool)

---

### Pattern: Attack + Conversation (Interleaved Combat)

**Narrative**: "Valen attacks the orc, and if it lands, he shouts 'Surrender or die!'"

**Bundle**: Two separate commits, not one!

```json
// Commit 1: Attack
[
  { "$type": "ruleset_action", "characterId": "chars/valen", "actionType": "Attack", ... }
]

// Commit 2: Taunt/intimidation (separate beat)
[
  { "$type": "ruleset_action", "characterId": "chars/valen", "actionType": "SkillCheck",
    "actionName": "Intimidation", "parameters": { "skill": "Intimidation", "dc": "13" } },
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/orc-1",
    "engagement": { "verb": "intimidated", "distanceBand": "close" } }
]
```

**Rationale**:
- Attack is one beat
- Taunt/intimidation is a separate beat (happens after attack resolves)

**Confidence**: High

---

## Character State Changes

### Pattern: Character Takes Wound (Status Effect)

**Narrative**: "The orc's sword slash wounds Valen's shoulder."

**Bundle**:
```json
[
  { "$type": "character_update", "characterId": "chars/valen",
    "updateAppearance": "shoulder wound, bleeding",
    "newStateModifiers": ["Wounded"] }
]
```

**Rationale**:
- Damage is already applied by `attack` tool's `ruleset_action`
- This commit captures the visible wound + status effect

**Conflicts**:
- Don't double-commit HP (already done by attack's `ruleset_action`)

**Confidence**: Medium

---

### Pattern: NPC First Meeting

**Narrative**: "Valen enters the tavern and meets the mysterious elf at the bar."

**Bundle**:
```json
[
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/mysterious_elf",
    "engagement": { "verb": "met", "distanceBand": "close" } },
  { "$type": "event", "category": "Narrative", "involved": ["chars/valen", "chars/mysterious_elf"],
    "summary": "Valen encountered a mysterious elf at the bar." }
]
```

**Rationale**:
- Engagement establishes the relationship
- Event captures the narrative moment

**Conflicts**:
- Engagement auto-logs; explicit event adds narrative framing, not redundancy

**Confidence**: High

---

### Pattern: Mood/Appearance Change (Non-Combat)

**Narrative**: "Valen realizes he's been betrayed and sits down, looking devastated."

**Bundle**:
```json
[
  { "$type": "character_update", "characterId": "chars/valen",
    "updateAppearance": "slumped in chair, face in hands",
    "newMood": "Devastated" },
  { "$type": "event", "category": "Narrative", "summary": "Valen learned of the betrayal." }
]
```

**Rationale**:
- Mood/appearance change is game state
- Event captures the triggering moment

**Conflicts**:
- Mood should only be `character_update` if it's persistent/mechanical (affects skill checks, perception)

**Confidence**: Medium

---

## Relationship & Faction Changes

### Pattern: Relationship Milestone (Trust Established)

**Narrative**: "After hours of conversation, the rogue finally trusts Valen with her real name."

**Bundle**:
```json
[
  { "$type": "engagement_relation", "characterId": "chars/valen", "targetId": "chars/rogue",
    "engagement": { "verb": "trusted", "distanceBand": "close" } },
  { "$type": "event", "category": "Narrative", "summary": "The rogue revealed her true identity to Valen." }
]
```

**Rationale**:
- Engagement captures the trust shift
- Event logs the narrative significance

**Confidence**: Medium

---

### Pattern: Faction Reputation Shift (Indirect)

**Narrative**: "Valen bribed the city guards to look the other way while the thieves' guild escaped."

**Bundle**:
```json
[
  { "$type": "faction_reputation", "characterId": "chars/valen", "factionId": "factions/city_guard",
    "delta": -20, "reason": "Accepted bribe from thieves' guild" },
  { "$type": "faction_reputation", "characterId": "chars/valen", "factionId": "factions/thieves_guild",
    "delta": 30, "reason": "Helped guild escape guards" },
  { "$type": "event", "category": "Narrative", "involved": ["chars/valen"],
    "summary": "Valen secretly aided the thieves' guild." }
]
```

**Rationale**:
- Reputation deltas reflect Valen's alignment with factions
- Event logs the betrayal

**Confidence**: Low (faction bundling patterns need transcript validation)

---

## Edge Cases & Anti-Patterns

### ❌ Anti-Pattern: Over-Bundling

**Bad**:
```json
[
  { "$type": "ruleset_action", ... },
  { "$type": "engagement_relation", ... },
  { "$type": "event", ... },
  { "$type": "character_update", "newMood": "Happy" },  // ← Unrelated
  { "$type": "item_update", "itemId": "...", "updatePrice": "..." }  // ← Unrelated
]
```

**Why**: Not all changes resulting from one beat belong in one commit. Mood update is fleeting; item price is unrelated.

**Better**: Split into two commits:
1. Persuasion + trust shift + narrative event
2. Separate commit for item price change (if mechanically justified)

---

### ❌ Anti-Pattern: Double Events

**Bad**:
```json
[
  { "$type": "engagement_relation", "engagement": { "verb": "met" } },  // Auto-logs event
  { "$type": "event", "summary": "They met." }  // ← Redundant
]
```

**Why**: `engagement_relation` already auto-logs. Explicit event duplicates.

**Better**:
```json
[
  { "$type": "engagement_relation", "engagement": { "verb": "met" } }
]
```

If you need a separate narrative frame:
```json
[
  { "$type": "engagement_relation", "engagement": { "verb": "met" } },
  { "$type": "event", "category": "Narrative", "summary": "An unexpected alliance formed." }  // ← Adds narrative value
]
```

---

## Phase C Composite Tool Candidates

Based on these patterns, Phase C will propose:

1. **`perform_dialogue(actor_id, target_id, narrative, skill_check_result?, relationship_delta?)`**
   - Auto-bundles: `ruleset_action` (skill check) + `engagement_relation` (trust shift) + `event` (narrative)
   - Used for: social actions, persuasion, deception, intimidation
   - Escape hatch: raw `commit` for unusual bundling

2. **`update_entity(entity_id, entity_type, updates)`**
   - Auto-bundles: `character_update` OR `item_update` + optional `event`
   - Used for: mood, appearance, status changes
   - Escape hatch: raw `commit` for multi-entity changes

3. **`faction_action(actor_id, faction_id, delta, reason, involved_npcs?)`**
   - Auto-bundles: `faction_reputation` + `event`
   - Used for: reputation shifts, betrayals, alliances
   - Escape hatch: raw `commit` for complex multi-faction scenarios

---

## Phase C Implementation Checklist

- [ ] Validate patterns against 5–10 playtest transcripts
- [ ] Identify missing patterns or conflicts
- [ ] Refine bundling rules in `SideEffectDuplicationGuard.cs`
- [ ] Design tool parameter signatures
- [ ] Implement `perform_dialogue`, `update_entity`, `faction_action`
- [ ] Add differential tests (composite output = manual bundle)
- [ ] Update this document with confirmed patterns
- [ ] Publish skill examples in `dnd-bundling` skill

---

## References

- **BUNDLING_POLICY_RESEARCH.md** — Research framework and Phase C roadmap
- **claude_skills/dnd-bundling/SKILL.md** — User-facing decision tree and examples
- **DmHelpManual.cs** — Prompt guidance (current source of bundling rules)
- **CommitHelpExamples.cs** — JSON examples for manual commits
- **SideEffectDuplicationGuard.cs** — Conflict detection (to be extended)
