---
name: dnd-conversation
description: Committing dialogue beats — Conversation events, involved lists, Trivial flavor marking (load when words were exchanged and must be logged)
metadata:
  type: skill
---

# Conversation Mode

You are logging dialogue: who spoke, where, what was said. Checks/DCs/modifiers → `dnd-social`. Voice/psychology → `dnd-npc-interaction`. Only committed `event`/`knowledge_update` text feeds NPC memory resurfacing — narrate-only banter is invisible to it.

**Memory rule:** an `event` with `category: Conversation` and `involved: [npc1, npc2]` registers NPC-NPC conversation — no PC required.

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

**Engagement relations** track lasting states (restraining, escorting, grappling — see `dnd-world-change` for the `relationship` vs `engagement_relation` split).
**Conversation events** track *what* was said. Don't use engagement to mark dialogue participants.

## Narration Format

Show, don't tell. Weave in ONE visual/voice detail per NPC mention — see `dnd-narration` for the full compression rules.

### Dialogue Authenticity

Same rule as `dnd-narration`'s Prompt Discipline section — psychology is the dialogue, not courtesy.

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

## Checklist (per-beat commit mechanics — checks/psychology live in `dnd-social` / `dnd-npc-interaction`)

- [ ] Did someone speak? → Commit `event` with Conversation category
- [ ] Are 3+ speakers present? → List all in `involved`
- [ ] Did an NPC learn something? → Add `knowledge_update` to same batch (fields: `dnd-world-change`)
- [ ] Did time actually pass? → Include `minutesElapsed` on the request (not every line gets its own event — batch related lines, but each exchange must be committed before the player's next action)
- [ ] Was this beat pure flavor/banter with nothing new or shifted? → Mark `narrativeImportance: "Trivial"` on the request (and the event's own `importance` if you added one)
