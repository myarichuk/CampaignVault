---
name: dnd-conversation
description: Logging dialogue — Conversation events, who goes in involved, time for talk, and Trivial flavor beats (load when words were exchanged and must be recorded)
metadata:
  type: skill
---

# Conversation

How a spoken exchange is recorded. Social checks are `dnd-social`; the NPC's psychology and voice are `dnd-npc-interaction`; how the dialogue reads is `dnd-narration`.

## Every exchange is committed

Each exchange (one player message and the answers it gets) is committed as a Conversation `event` before the player responds. Several lines in one exchange share one event. Only committed events and `knowledge_update`s feed NPC memory; talk that was only narrated is invisible to it.

```json
{ "$type": "event", "category": "Conversation", "involved": ["chars/pc", "chars/npc-guard"], "locationId": "locations/castle-gate", "summary": "The guard demands the PC's business; the PC claims to be a merchant" }
```

- `involved` lists everyone who spoke, including NPCs talking only to each other (no PC needed). It marks who took part; `engagement_relation` is for physical and lasting states, never for conversation.
- `locationId` is its own field.
- Put `minutesElapsed` on the request: a greeting 1–2 minutes, banter 2–5, a tense interrogation 10–30, a long talk into the night 60–180.
- Pure flavor with nothing new and no shift (a toast, small talk, a gesture) is still committed, with `narrativeImportance: "Trivial"` on the request and `"importance": "Trivial"` on the event, so it doesn't crowd real beats out of recall. Leave both out for anything that reveals information, moves a relationship or will matter later.

A heated debate among four people, one exchange:

```json
{ "$type": "event", "category": "Conversation", "involved": ["chars/pc", "chars/companion-bard", "chars/npc-mayor", "chars/npc-militia-captain"], "locationId": "locations/town-hall", "summary": "The mayor and the militia captain clash over who leads the recruitment; the bard sides with the captain" }
```

## Checklist

- [ ] The exchange is one Conversation event with every speaker in `involved`.
- [ ] What an NPC learned is a `knowledge_update` in the same batch.
- [ ] Time is on the request; pure flavor is marked Trivial.
