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

### Social Rolls Change the Method, Not the Job

A social check changes *how* an NPC does the job they already have — it doesn't hand the PC control over decisions the NPC has no reason to make.

- **Fail:** they do the job the ugly way (grab, draw a weapon, call for backup).
- **Success/nat 20:** they believe a beat, hesitate, laugh, take a worse tactical angle, spend one extra sentence talking. They do **not** drop a contract, release a mark, or abandon a hunt because the line landed well.
- A king told to abdicate on a nat 20 treats it as nerve or a joke — he doesn't abdicate, and the roll doesn't obligate you to resolve the underlying problem for the party.
- A social roll can make hired eyes buy a cover story; it can't make a grab-or-silence crew decide the job is finished. If the job is "take her," they still take her — the good roll buys a minute of talk, or the target isn't cut loose in front of witnesses.

Don't invent motive to justify a roll result after the fact — decide who the NPC is and what they want *before* resolving the check (see `dnd-exploration`'s Encounter Resolution for the same rule applied to blank `crowd_interrupt`/encounter transients).

### Prompt Discipline: Authenticity, Not Comfort

Same rule as `dnd-narration`'s Prompt Discipline section. Domain-specific: failed persuasion doesn't soften into an apology; a believed lie is believed because of the roll, not because it makes moral sense; intimidation buys compliance now and resentment later. Narrate the outcome from the roll — psychology shapes how it's delivered.

## Relationship Changes

After a significant social beat, commit a `relationship` change. Payloads: `dnd-world-change`.

## Knowledge Updates

If the NPC learns something new, add a `knowledge_update` (`topic` / `details` / `sourceEventIds` when `source` is Witnessed or Experienced). Payloads: `dnd-world-change`.

Local rumors on a scene refresh follow the same rule as gear/appearance: on a `mode: delta` turn, a scene's rumor list only includes rumors that changed state/text this turn (`$type: "rumor"`, evolving an *existing* rumor — new rumors are seeded via `world_build`, not a take_turn commit). An empty or shorter list doesn't mean rumors died out — it means none changed. Check `WorldState.ActiveRumors` (via `includeWorldState: true`) or `get_entity` for the full current picture.

## Social Checklist

- [ ] Did I fetch the NPC full detail (`get_entity` chars/ id) to read Trust/Suspicion/Loyalty/Fear?
- [ ] Is there a skill check? → `ruleset_action` first, narrate outcome from result
- [ ] Did words get exchanged? → Commit `event` (category: Conversation, involved: all speakers)
- [ ] Did relationship shift? → `relationship` change in take_turn
- [ ] Did the NPC learn something? → `knowledge_update`
- [ ] Did time pass (banter, tense talk)? → `minutesElapsed` on the request
- [ ] Was this pure flavor/banter with nothing new or shifted? → `narrativeImportance: "Trivial"` on the request
- [ ] Is the NPC response authentic to their self-interest/psychology, not softened by modern courtesy? → Refusal is refusal, compliance under duress shows resentment, ideology trumps comfort
