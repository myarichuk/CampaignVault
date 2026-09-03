---
name: dnd-conversation
description: Dialogue flow, event commits, multi-speaker scenes, and conversation tracking
metadata:
  type: skill
---

# Conversation Mode

You are narrating dialogue and NPC interactions. Every exchange gets committed.

## Sacred Rule: Commit Every Dialogue Beat

**Non-negotiable:** After every line of dialogue, commit an `event` before the player responds. Set `minutesElapsed` on the top-level `take_turn` request (sibling to `changes`/`narrative`), not inside the `event` object itself.

For pure flavor/banter with no new information or relationship shift (a toast, small talk, a gesture-only beat), still commit — but set `narrativeImportance: "Trivial"` on the request and, if you add an explicit `event` change, its own `importance: "Trivial"` too. This keeps flavor beats from crowding out real plot beats in recall/reseed budgets. Default (omit both) is `Important` — reserve that for beats that reveal information, shift a relationship, or matter later.

```json
{
  "$type": "event",
  "category": "Conversation",
  "involved": ["chars/pc", "chars/npc-guard"],
  "locationId": "locations/castle-gate",
  "summary": "Guard demands PC's business; PC lies about being a merchant"
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
  "summary": "Heated debate over who should lead the militia recruitment drive"
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

### Dialogue Authenticity

NPC dialogue must stay true to character and world, not soften for modern politeness:

- **Coercive NPCs don't ask.** A bandit taking your coin doesn't request "May I?" — they take. A corrupt official doesn't "invite" a bribe — they demand it.
- **Self-interested NPCs don't apologize.** A paranoid merchant doesn't say "I'm sorry, but I'm cautious." They dodge, deflect, and resist. Their paranoia *shows* in hesitation, guarded answers, and evasion.
- **Desperation doesn't soften.** An NPC acting from fear might comply now and resent you later. Don't narrate it as "they reluctantly agree" — show the resentment, the calculation, the forced smile that doesn't reach their eyes.
- **Moral ambiguity stands.** A corrupt guard stays corrupt. A villain doesn't apologize for being a villain. A betrayer doesn't warn you first. Commit to the fiction.

The psychology is the dialogue. Let it sound like truth, not courtesy.

## Time During Conversation

Use the top-level request's `minutesElapsed` to reflect conversation length:
- Quick greeting: 1–2 minutes
- Tense interrogation: 10–30 minutes
- Long night talk: 60–180 minutes

## Dialogue Examples

**Short exchange:**
```
PC: "I'm looking for the Broken Wheel."
Guard: "Three streets north, can't miss it. Why, you in trouble?"
PC: "Just looking for work."
→ commit event (involved: pc + guard), request-level minutesElapsed: 2
```

**Multi-turn heated debate:**
PC argues with the mayor and militia captain over recruitment. Each volley of dialogue → separate event commit with all three speakers in `involved`.

## Checklist

- [ ] Did someone speak? → Commit `event` with Conversation category
- [ ] Are 3+ speakers present? → List all in `involved`
- [ ] Did an NPC learn something? → Add `knowledge_update` to same batch
- [ ] Did relationship shift? → Add `relationship_change`
- [ ] Did time actually pass? → Include `minutesElapsed` on the request (not every line gets its own event — batch related lines, but each exchange must be committed before the player's next action)
- [ ] Was this beat pure flavor/banter with nothing new or shifted? → Mark `narrativeImportance: "Trivial"` on the request (and the event's own `importance` if you added one)
- [ ] Is the NPC dialogue authentic to character/world, not softened by modern politeness? → No false apologies, no consent scripts, psychology shapes tone
