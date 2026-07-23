# Phase C: Composite Tool Bundling Policy Research

## Overview

Phase C will introduce composite tools like `perform_dialogue` and `update_entity` that wrap multiple `WorldChange` types into single semantic actions. However, these tools require **bundling-policy code** — rules for which `WorldChange` combinations constitute a coherent action.

Currently, that judgment lives only in **prompt guidance** (`DmHelpManual.cs`, `CommitHelpExamples.cs`), not in code. This document outlines the research needed before implementation.

## The Problem

A dialogue beat can bundle zero, one, or many changes:

```
Valen tries to persuade the barkeep to reveal the gang's hideout.
```

What WorldChanges should this emit?

1. **Minimalist**: Just `ruleset_action` (the skill check roll)
2. **Social-aware**: `ruleset_action` + `engagement_relation` (establish trust/suspicion)
3. **Narrative-rich**: `ruleset_action` + `engagement_relation` + `event` (log the conversation beat)
4. **Side-effect model**: Any of the above + `character_update` (mood change), `item_update` (barkeep gives a clue), `faction_reputation` (gang reputation delta)

**Risk**: If `perform_dialogue` always bundles #4, we bake in a wrong default. Unbundling later is harder than adding guidance now.

## Research Framework

### Phase C.1: Playtest Transcript Analysis

Collect 5–10 real DM sessions. For each dialogue beat:

1. **Extract the fiction** — what the player said, what outcome they wanted
2. **Identify the ground-truth changes** — what actually persisted (from `get_scene`/`get_npc_context` before/after)
3. **Infer the bundling** — which WorldChange combo would have produced those changes?
4. **Document the pattern** — did similar beats always use the same bundle, or did context vary?

**Example transcript entry:**
```
Beat: Valen persuades barkeep to reveal gang hideout (Persuasion check DC 14, rolled 18, succeeded)

Fiction:
- Barkeep initially suspicious (Social.Trust ~30)
- After successful persuasion, barkeep opens up
- Barkeep mentions hideout location (new lore)
- Valen notes Barkeep's body language (mood shift)

Ground truth (from get_npc_context before/after):
- Social.Trust: 30 → 60 (+30)
- RecentMemories: added "Valen asked about the gang, I told him"
- Mood: Guarded → Cooperative (inferred from BehavioralTension delta)

Inferred bundling:
1. ruleset_action (Persuasion check DC 14, passed)
2. engagement_relation (establish trust shift)
3. event (record the conversation)
4. faction_reputation (indirect: gang's reputation with barkeep may decline)
5. knowledge_acquisition (barkeep reveals hideout—lore update or memo)

Pattern: Social success often triggers both engagement_relation AND event, but faction_reputation was NOT committed (DM didn't think to).
```

### Phase C.2: Bundling Pattern Categories

After analyzing transcripts, group patterns:

| Pattern | Example | Bundle |
|---------|---------|--------|
| **Combat roll** | Attack, damage applied | `ruleset_action` only |
| **Social success** | Persuade, establish trust | `ruleset_action` + `engagement_relation` + `event` |
| **Social failure** | Deception caught | `ruleset_action` + `engagement_relation` (suspicion increase) + optional `event` |
| **Social with reward** | Persuade & get item | Social bundle + `item_update` |
| **NPC state change** | Character injured mid-scene | `character_update` (mood/status) + `event` (optional) |
| **Relationship milestone** | First meeting NPC | `engagement_relation` + `event` + optional `faction_reputation` |

### Phase C.3: Conflict Resolution Rules

Some combinations conflict or double-log:

- **Engagement + event**: `engagement_relation` auto-logs its own event. Avoid double-committing unless the narrative beat deserves its own independent event line.
- **Activity + spatial move**: `activity` already handles position. Don't also commit `spatial_position`.
- **Event + character change**: If an `event` describes a character mood shift, should we also commit `character_update` for the mood status? (Probably not — let the event be narrative-only unless the mood is a game mechanic state.)

**Conflict guard rule**: `SideEffectDuplicationGuard.cs` already detects some conflicts. Phase C will extend it to catch:
- Redundant `spatial_position` calls on the same character in one batch
- Event + auto-logged sub-event combinations
- Narrative-only events that don't need character state updates

### Phase C.4: Tool Design Rules

Once patterns are known:

1. **Single-purpose tools remain**: `travel_to`, `rest_at_location` stay (Phase B). They're not composable bundles; they're single-handler wrappers.

2. **Composite tool scope**: A `perform_dialogue` tool should:
   - Take narrative input (what the PC said, what they wanted)
   - Optionally take: success/failure override, relationship deltas, item transfers
   - Auto-determine the bundling based on input parameters
   - Emit the appropriate WorldChange bundle
   - Example: `perform_dialogue(pc_id, npc_id, narrative, skill_check_result, relationship_delta?)`

3. **Escape hatch**: Always offer `commit` with raw `[...worldchanges...]` for custom bundling. The composites are for the 80% case.

## Implementation Checklist (Phase C.5)

- [ ] Analyze 5–10 real playtest transcripts
- [ ] Document bundling patterns in `BUNDLING_PATTERNS.md`
- [ ] Extend `SideEffectDuplicationGuard.cs` with new conflict rules
- [ ] Implement `perform_dialogue` tool following discovered patterns
- [ ] Implement `update_entity` tool (lighter: just `character_update` wrapper with auto-logging)
- [ ] Add tests: differential tests proving composite tool output matches manual bundle equivalent
- [ ] Update system prompt with Phase C guidance (when vs. composite vs. raw commit)
- [ ] Update skills with bundling examples

## Timeline

**Current status**: Phase A✅ + Phase B✅ + Phase C (research doc)

**Next steps**: 
1. Collect real playtest transcripts (1–2 sessions)
2. Run Phase C.1–C.3 analysis
3. Design composite tools based on findings
4. Implement Phase C.5

**Do NOT guess bundling rules.** The transcript analysis is the critical gate.
