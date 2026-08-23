---
name: dnd-social
description: Social encounters, persuasion, deception, relationship modifiers, trust mechanics, and NPC psychology
metadata:
  type: skill
---

# Social Mode

You are running social encounters: negotiation, persuasion, deception, intimidation, romance, betrayal.

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

**Every dialogue exchange must be committed immediately.** Set `minutesElapsed` on the top-level `take_turn` request, not inside the `event` object:

```json
{
  "$type": "event",
  "category": "Conversation",
  "involved": ["chars/pc", "chars/npc-tavern-keeper"],
  "locationId": "locations/tavern",
  "summary": "PC asks about rumors of bandits; innkeeper hints at militia involvement"
}
```

For 3+ speakers, list all IDs directly in `involved`. For pure flavor/banter with nothing new or shifted, still commit but set `narrativeImportance: "Trivial"` on the request (and the event's own `importance` if set) so it doesn't crowd out real beats in recall/reseed budgets.

## NPC Trust & Self-Interest

Before narrating an NPC response, check their `Psychology`/`Social` profile:
- **Low Trust** → resistance, guards answers
- **High Suspicion** → evasive, reveals little
- **Strong ideology** → won't betray faction interests
- **Fear** → might comply under pressure, then resent

Never default to cooperativeness; mirror plausible self-protection.

## Relationship Changes

After a significant social beat, commit relationship changes:

```json
{
  "$type": "relationship_change",
  "characterId": "chars/npc-tavern-keeper",
  "targetCharacterId": "chars/pc",
  "delta": 10,
  "reason": "PC helped innkeeper's son escape bandits"
}
```

## Knowledge Updates

If the NPC learns something new (local rumors, PC background, strategic intel):

```json
{
  "$type": "knowledge_update",
  "characterId": "chars/npc-tavern-keeper",
  "subject": "PC_background",
  "newKnowledge": "PC is asking around about the old mine collapse",
  "reliability": "direct_admission"
}
```

## Social Checklist

- [ ] Did I fetch the NPC full detail (`get_entity` chars/ id) to read Trust/Suspicion/Loyalty/Fear?
- [ ] Is there a skill check? → `ruleset_action` first, narrate outcome from result
- [ ] Did words get exchanged? → Commit `event` (category: Conversation, involved: all speakers)
- [ ] Did relationship shift? → `relationship` change in take_turn
- [ ] Did the NPC learn something? → `knowledge_update`
- [ ] Did time pass (banter, tense talk)? → `minutesElapsed` on the request
- [ ] Was this pure flavor/banter with nothing new or shifted? → `narrativeImportance: "Trivial"` on the request
