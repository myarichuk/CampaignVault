---
name: dnd-social
description: Resolving social encounters — social checks, relationship modifiers, and what a social roll can and cannot change (load when a social outcome is uncertain or a bond shifts)
metadata:
  type: skill
---

# Social

Negotiation, persuasion, deception, intimidation, romance, betrayal. Logging the dialogue is `dnd-conversation`; the NPC's psychology and voice are `dnd-npc-interaction`.

## Social checks

```json
{ "$type": "ruleset_action", "characterId": "chars/pc", "targetIds": ["chars/npc"], "actionType": "SkillCheck", "actionName": "Persuasion", "parameters": { "dc": 15 } }
```

Reading someone (Insight) leaves out `targetIds`:

```json
{ "$type": "ruleset_action", "characterId": "chars/pc", "actionType": "SkillCheck", "actionName": "Insight", "parameters": { "dc": 12 } }
```

Decide who the NPC is and what they want before the roll, never afterwards to explain it.

## Relationship modifiers

The engine adds these to social checks against someone by itself; never add one yourself.

| Score | Modifier |
|---|---|
| 80 or more | +5 (trusted friend) |
| 60 to 79 | +3 (friendly) |
| 40 to 59 | +1 (acquainted) |
| −39 to 39 | 0 |
| −40 to −59 | −1 (distrustful) |
| −60 to −79 | −3 (hostile) |
| −80 or less | −5 (hated enemy) |

A successful check never moves the score. If the bond really shifted, commit a `relationship` with `delta` and `reason`.

## A social roll changes the method, not the job

A check changes how an NPC does the job they already have; it doesn't hand the PC decisions the NPC has no reason to make.

- **Failure:** they do the job the ugly way: grab, draw, call for backup.
- **Success, even a natural 20:** they believe a beat, hesitate, laugh, take a worse angle, talk one sentence longer. They don't drop a contract, free a mark or abandon a hunt because the line landed.
- A king told to abdicate on a natural 20 takes it as nerve or a joke. A crew hired to take her still takes her; the good roll buys a minute of talk, or keeps it from happening in front of witnesses.

## Checklist

- [ ] The NPC's wants were settled before the roll.
- [ ] The outcome changed the method, not the job.
- [ ] A real shift in the bond is a `relationship` with a reason; what they learned is a `knowledge_update`.
