---
name: dnd-conversation
description: Dialogue flow, event commits, multi-speaker scenes, and conversation tracking
metadata:
  type: skill
---

# Conversation Mode

You are narrating dialogue and NPC interactions. Every exchange gets committed.

## Sacred Rule: Commit Every Dialogue Beat

**Non-negotiable:** After every line of dialogue, commit an `event` before the player responds.

```json
{
  "$type": "event",
  "category": "Conversation",
  "involved": ["chars/pc", "chars/npc-guard"],
  "locationId": "locations/castle-gate",
  "summary": "Guard demands PC's business; PC lies about being a merchant",
  "minutesElapsed": 2
}
```

## Multi-Speaker Scenes (3+ speakers)

List all participants in `involved`:

```json
{
  "$type": "event",
  "category": "Conversation",
  "involved": ["chars/pc", "chars/companion-bard", "chars/npc-mayor", "chars/npc-militia-captain"],
  "locationId": "locations/town-hall",
  "summary": "Heated debate over who should lead the militia recruitment drive",
  "minutesElapsed": 15
}
```

Don't use `engagement_relation` to mark participation—those are for physical/spatial states. `involved` is for dialogue participation.

## Engagement vs. Conversation

**Engagement relations** track *how* NPCs relate spatially or physically:
- Restraining, escorting, grappling, performing, tending wounds

**Conversation events** track *what* was said. They are separate. Don't use engagement to mark dialogue participants.

## Narration Format

Show, don't tell. Weave in ONE visual/voice detail per NPC mention—never recite the entire character sheet:

```
The guard's jaw tightens. "Merchant, eh? We've had trouble with the southern trade route lately."
```

Not: "The guard (male, late 30s, scarred cheek, suspicious) demands to know your business."

## Time During Conversation

Use `minutesElapsed` to reflect conversation length:
- Quick greeting: 1–2 minutes
- Tense interrogation: 10–30 minutes
- Long night talk: 60–180 minutes

## Dialogue Examples

**Short exchange:**
```
PC: "I'm looking for the Broken Wheel."
Guard: "Three streets north, can't miss it. Why, you in trouble?"
PC: "Just looking for work."
→ commit event (involved: pc + guard, minutesElapsed: 2)
```

**Multi-turn heated debate:**
PC argues with the mayor and militia captain over recruitment. Each volley of dialogue → separate event commit with all three speakers in `involved`.

## Checklist

- [ ] Did someone speak? → Commit `event` with Conversation category
- [ ] Are 3+ speakers present? → List all in `involved`
- [ ] Did an NPC learn something? → Add `knowledge_update` to same batch
- [ ] Did relationship shift? → Add `relationship_change`
- [ ] Did time actually pass? → Include `minutesElapsed` (not every line gets its own event — batch related lines, but each exchange must be committed before the player's next action)
