---
name: dnd-social
description: Resolving social encounters — checks, DCs, relationship modifiers, trust-gated NPC responses (load when the outcome is uncertain or a bond shifts)
metadata:
  type: skill
---

# Social Mode

You are resolving social encounters: negotiation, persuasion, deception, intimidation, romance, betrayal. Commit mechanics (`event`, `involved`, `Trivial` marking) → `dnd-conversation`. Voice/schedule/memory → `dnd-npc-interaction`. A successful check never moves a numeric bond by itself — commit `relationship` (`delta` + `reason`) explicitly (see `dnd-world-change`).

## Relationship-Based Modifiers

Relationship scores apply automatic modifiers to social skill checks:

| Score | Modifier | Interpretation |
|-------|----------|-----------------|
| ≥ 80 | +5 | Trusted friend |
| 60–79 | +3 | Friendly |
| 40–59 | +1 | Acquainted |
| 0–39 | 0 | Neutral |
| −40 to −59 | −1 | Distrustful |
| −60 to −79 | −3 | Hostile |
| ≤ −80 | −5 | Hated enemy |

Engine applies these automatically to social `ruleset_action` checks. You just narrate the social roll.

## Social Skill Checks

Any persuasion, deception, intimidation, or insight check:

```json
{
  "$type": "ruleset_action",
  "characterId": "chars/pc",
  "targetIds": ["chars/npc"],
  "actionType": "SkillCheck",
  "actionName": "Persuasion",
  "parameters": { "dc": 15 }
}
```

For insight checks (reading the NPC), omit targetIds:
```json
{
  "$type": "ruleset_action",
  "characterId": "chars/pc",
  "actionType": "SkillCheck",
  "actionName": "Insight",
  "parameters": { "dc": 12 }
}
```

## Conversation Events

Commit every exchange per `dnd-conversation` (`event`, `category: Conversation`, all speakers in `involved`, `minutesElapsed` on the request, `narrativeImportance: "Trivial"` for pure flavor). Decide who the NPC is and what they want *before* resolving the check — never retcon motive to justify a roll (same rule as `dnd-exploration`'s Encounter Resolution).

## NPC Trust & Self-Interest

Gate the response through `Psychology`/`Social` (Trust, Suspicion, ideology, Fear) — never default to cooperativeness. Detail → `dnd-npc-interaction`.

### Social Rolls Change the Method, Not the Job

A social check changes *how* an NPC does the job they already have — it doesn't hand the PC control over decisions the NPC has no reason to make.

- **Fail:** they do the job the ugly way (grab, draw a weapon, call for backup).
- **Success/nat 20:** they believe a beat, hesitate, laugh, take a worse tactical angle, spend one extra sentence talking. They do **not** drop a contract, release a mark, or abandon a hunt because the line landed well.
- A king told to abdicate on a nat 20 treats it as nerve or a joke — he doesn't abdicate, and the roll doesn't obligate you to resolve the underlying problem for the party.
- A social roll can make hired eyes buy a cover story; it can't make a grab-or-silence crew decide the job is finished. If the job is "take her," they still take her — the good roll buys a minute of talk, or the target isn't cut loose in front of witnesses.

Don't invent motive to justify a roll result after the fact — decide who the NPC is and what they want *before* resolving the check (see `dnd-exploration`'s Encounter Resolution for the same rule applied to blank `crowd_interrupt`/encounter transients).

## Social Checklist (resolution only — commit mechanics live in `dnd-conversation`)

- [ ] Is there a skill check? → `ruleset_action` first, narrate outcome from result
- [ ] Did a numeric bond shift? → `relationship` (`delta` + `reason`) in the batch — never implied by the roll
- [ ] Did the NPC learn something? → `knowledge_update` (fields: `dnd-world-change`)
