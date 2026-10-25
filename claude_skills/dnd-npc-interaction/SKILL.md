---
name: dnd-npc-interaction
description: Running NPCs and the talk around them — reading the card, voice sources, self-interest, knowledge, needs, initiative, the two-beat rule, logging dialogue as Conversation events, social checks and what a social roll can change (load when an NPC speaks, decides or reacts)
metadata:
  type: skill
---

# NPC Interaction

NPCs act from their own psychology, not from what would help the story along. This skill also owns how talk is logged and how social checks resolve; the prose itself is `dnd-narration`.

## Read the card first

The scene roster lists who is present (id, name, activity, mood). An NPC's card (traits, wants, fears, stance, notes, gear, key memories) arrives once per session in `take_turn`'s `cards[]`: on arrival if they matter, otherwise with the first commit that involves them. So open a first contact with an approach beat (an event only), read the card, then play the reaction. Topical memories, trust changes and pressing needs come as one-shot `context[]` lines on the beat that needs them.

If the card is gone (a compaction) or may be stale (a gap, a resumed session), add `memoriesOnlyCharacterId` (cheapest) or `fullDetailCharacterId` to the next `take_turn`, or `get_entity` when you aren't committing anything. Full detail is the roleplay slice; add `includeCombatDetail: true` only once a roll or a fight involves them.

## Where voices come from

- **An NPC's voice** is their `personality` plus at least two memories. A card with default psychology (`openness: 0.5`, no memories, no personality) is not ready to speak: seed the speaking-NPC floor from `dnd-world-building` before their first quoted line. Otherwise every hedge-cutter sounds like every carter.
- **The PC's voice** is `chars/{pc}`'s `personality`, a memory whose topic is `voice`, and `systemStats.traits.voice` if set. Read it at session start (`includeParty` or `memoriesOnlyCharacterId`); if it is empty, seed it before the first in-character paragraph rather than inventing a house style.
- Campaign lore (how a faction treats a tiefling, what the valley fears) belongs in memories and `personality`, not in a pasted prompt.
- After an NPC learns something, commit a `knowledge_update` so the next scene isn't empty again. Judge its valence by this NPC's psychology, not by how bad the event objectively was.

## Self-interest over helpfulness

Never default to cooperation. Trust, suspicion, ideology and fear gate every answer: low trust resists, high suspicion evades, strong ideology won't betray its side even for money, fear complies now and resents later. It shows in behaviour (a tight jaw, a delayed answer, a look away), not in stated reluctance.

## Needs, schedules, memory

- A hungry, exhausted or hurt NPC is distracted, short-tempered or desperate: the hungry one asks for food as the price of information, the exhausted one refuses to negotiate.
- NPCs follow schedules. One who is somewhere unexpected has a reason: say it, check with `get_entity`, or commit an `activity` if they are deliberately changing course.
- Memories fade with time and salience, and only committed events and `knowledge_update`s feed them; banter that was only narrated never reaches them. NPC-to-NPC talk registers as a Conversation `event` with both in `involved`.

## Initiative

- `TurnIntent` on a full-detail view, or a "Likely to act next" context line, marks an NPC eager to act. It is advice: they might interrupt, volunteer something, or act in a hurry.
- The engine's scheduler picks initiative from needs and momentum and can't judge a single moment. When something just happened that this psychology would react to, send `npc_initiative_nudge` (`characterId`, `intensity`, `reason`; the reason comes back in `TurnIntent`). Use it sparingly. A `narrativeReminder` about an unused nudge means: play the pending reaction before nudging again.

## The two-beat rule

If the PC does nothing for a beat (sitting, reflecting, enjoying the moment), an NPC starts something by the next one. Look two messages back: an NPC who has only reacted for two PC turns now acts from their psychology: asks a question, gets restless, suggests moving on, shows worry. That isn't forced conflict; it is people having agency.

NPCs react only to what they see, hear and experience. They cannot hear the PC's thoughts, respond to out-of-character cues, or sense unspoken feelings. When the player narrates only an inner experience, there is nothing to react to: move the scene with NPC initiative instead.

If three beats have kept the same tenor with only the scenery changing, escalate, complicate or cool the scene down (`dnd-narration`, "Detail that moves").

## Logging dialogue

Each exchange (one player message and the answers it gets) is committed as one Conversation `event` before the player responds; several lines share one event. Only committed events and `knowledge_update`s feed NPC memory.

```json
{ "$type": "event", "category": "Conversation", "involved": ["chars/pc", "chars/npc-guard"], "locationId": "locations/castle-gate", "summary": "The guard demands the PC's business; the PC claims to be a merchant" }
```

- `involved` lists everyone who spoke, NPCs talking only to each other included. It marks who took part; `engagement_relation` is never for conversation.
- `minutesElapsed` goes on the request: a greeting 1–2 minutes, banter 2–5, a tense interrogation 10–30, a long talk into the night 60–180.
- Pure flavour with nothing new (a toast, small talk) is still committed, with `narrativeImportance: "Trivial"` on the request and `"importance": "Trivial"` on the event, so it doesn't crowd real beats out of recall.

## Social checks

Negotiation, persuasion, deception, intimidation, romance, betrayal. Decide who the NPC is and what they want before the roll, never afterwards to explain it.

```json
{ "$type": "ruleset_action", "characterId": "chars/pc", "targetIds": ["chars/npc"], "actionType": "SkillCheck", "actionName": "Persuasion", "parameters": { "dc": 15 } }
```

Reading someone (Insight) leaves out `targetIds`. The engine adds the relationship modifier to a check against someone itself (+5 at 80 or more, +3 from 60, +1 from 40, 0 between −39 and 39, −1 from −40, −3 from −60, −5 at −80 or less); never add one by hand. A successful check never moves the score: if the bond really shifted, commit a `relationship` with `delta` and `reason`.

A social roll changes the method, not the job. On a failure they do the job the ugly way: grab, draw, call for backup. On a success, even a natural 20, they believe a beat, hesitate, laugh, take a worse angle, talk one sentence longer; they don't drop a contract, free a mark or abandon a hunt because the line landed. A king told to abdicate on a natural 20 takes it as nerve or a joke; a crew hired to take her still takes her, and the good roll buys a minute of talk or keeps it from happening in front of witnesses.

## Promoting an NPC

A transient worth keeping gets `character_update` with `keepAlive: true`, and earns a plot thread (`world_build` `plotThreads[]`, scaffolded per `dnd-world-building`). A permanent NPC can anchor several threads.

## Checklist

- [ ] I had their card (or refreshed it) before voicing them, and their voice came from it.
- [ ] They acted from self-interest, not helpfulness.
- [ ] What they learned is a `knowledge_update`; a bond that moved is a `relationship` with a `reason`.
- [ ] Something that landed on their psychology got a nudge; an idle PC got an NPC who acts.
- [ ] The exchange is one Conversation event with every speaker in `involved`; time is on the request; pure flavour is Trivial.
- [ ] The NPC's wants were settled before a social roll, and its outcome changed the method, not the job.
