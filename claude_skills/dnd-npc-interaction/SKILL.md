---
name: dnd-npc-interaction
description: Running NPCs from their psychology — reading their card, voice sources, self-interest, knowledge, needs, schedules, initiative and the two-beat rule (load when an NPC speaks, decides or reacts)
metadata:
  type: skill
---

# NPC Interaction

NPCs act from their own psychology, not from what would help the story along. Social checks are in `dnd-social`, logging dialogue in `dnd-conversation`, and the prose itself in `dnd-narration`.

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

## Promoting an NPC

A transient worth keeping gets `character_update` with `keepAlive: true`, and earns a plot thread (`world_build` `plotThreads[]`, scaffolded per `dnd-world-building`). A permanent NPC can anchor several threads.

## Checklist

- [ ] I had their card (or refreshed it) before voicing them, and their voice came from it.
- [ ] They acted from self-interest, not helpfulness.
- [ ] What they learned is a `knowledge_update`; a bond that moved is a `relationship` with a `reason`.
- [ ] Something that landed on their psychology got a nudge; an idle PC got an NPC who acts.
