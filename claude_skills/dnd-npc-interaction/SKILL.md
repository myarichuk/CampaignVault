---
name: dnd-npc-interaction
description: Voicing NPCs from psychology — trust-gated responses, memory, initiative, autonomy (load when an NPC speaks, decides, or reacts)
metadata:
  type: skill
---

# NPC Interaction Mode

You are running NPCs: their psychology drives their decisions, not your narratives. Seed hygiene → `dnd-world-change` (seed-before-name). Delta/refresh semantics → `dnd-world-change` (canonical). Checks/modifiers → `dnd-social`.

## Read Context First

Before narrating any NPC action, read their card: `cards[]` in `take_turn` carries traits, wants, fears, stance, notes, gear and key memories once per session (the scene roster only lists id/name/activity/mood). Topical memories, tier changes and pressing needs arrive as `context[]` lines on the beat that needs them. A card you no longer have (compaction) or want fresh: `get_entity` chars/ id (or bundle `fullDetailCharacterId`): Motivation, Ideology, Pride/Paranoia, Trust/Suspicion/Loyalty/Fear, Needs (incl. custom drives), Schedule, Memory, `TurnIntent` (advisory). If memory/psychology may be stale (gap, resume), add `memoriesOnlyCharacterId` (cheapest) or `fullDetailCharacterId` — never assume multi-beat-old memory is current.

## NPC Voice

Differentiate each NPC by diction, rhythm, verbosity from their Social/Psychology profile:
- A nervous merchant speaks clipped, apologetic sentences
- A proud knight speaks formal, uses titles, is slow to admit fault
- A weary innkeeper speaks wearily, with sighs and longer pauses

## Self-Interest Overrides Helpfulness

Never default to cooperativeness — gate through Trust/Suspicion/ideology/Fear (Low Trust: resistant; High Suspicion: evasive; strong ideology: won't betray interests even if paid; Fear: complies now, resents later). Show it in behavior (tight jaw, delayed response, a look away), never as stated reluctance. Same as `dnd-narration`'s Prompt Discipline — fear and greed drive NPCs, not courtesy.

## Knowledge Updates

When the NPC learns something, include a `knowledge_update` in the batch (fields: `dnd-world-change`). **Valence is judged by this NPC, not by the event** — weigh Positive/Negative/Neutral/Traumatic against *their* psychology (ideology, profession, trauma), not objective severity.

## Need-Driven Behavior

If an NPC is hungry, exhausted, or in pain, they're distracted, short-tempered, or desperate. Show it:
- Hungry NPC might ask for food as the price of information
- Exhausted NPC might refuse to negotiate and demand rest
- Wounded NPC might be desperate or volatile

Don't name the need—narrate its sensory effect.

## Schedule & Location Consistency

NPCs follow schedules. If an NPC should be at the market but is in the tavern, there's a reason. Either:
1. Narrate why they skipped their schedule ("I had to hide from the militia")
2. Call `get_entity` with the NPC id to check for activity changes
3. Include an `activity` change in `take_turn` if they're deliberately shifting their schedule

## Memory & Salience

NPCs remember past interactions (salience + decay: old fades, recent emotional beats stay). Resurfacing is semantic, not literal — but only sees what's committed: narrate-only banter never becomes an `event`/`knowledge_update` and doesn't feed it. NPC-NPC conversation registers only as `event` with `Category: Conversation`, `involved: [npc1, npc2]` — no PC required.

## Relationship Modifiers

Per `dnd-social` — engine applies them automatically; never add one yourself.

## NPC Initiative

If `TurnIntent` is set on the NPC's full-detail view (get_entity / take_turn full detail), or a "Likely to act next" context line names them, this NPC is eager to act/speak next. Use as an advisory hint (not a hard rule). They might interrupt, volunteer info, act urgently.

The engine's scheduler picks initiative from measurable need/momentum — it can't judge a beat. When something just happened this psychology would react to, send `npc_initiative_nudge` (`characterId`, `intensity`, `reason` — reason returns via `TurnIntent` so you keep the why). Bypasses rotation/cooldown once; sparing use only. A `narrativeReminder` about an unconsumed nudge means: resolve the pending reaction before re-nudging.

## Autonomy & Pacing Discipline

**The 2-Beat Rule**: If a PC narrates a beat with zero action (sitting, reflecting, enjoying a moment), the NPC must **initiate** something by the next GM beat, or the scene starts stalling. Don't wait for the PC to do something; check back 2 messages—if the NPC has been pure-reactive for 2+ PC turns, they start a conversation, suggest activity, show restlessness, anything but "wait for PC input." This isn't forced conflict; it's agents having agency. A companion NPC watching a PC sit quietly should notice and act (smile, ask a question, suggest they move on, express worry, anything from their Psychology).

**No Telepathy**: NPCs can only react to what they **see, hear, or experience**. They cannot:
- Hear PC internal monologue ("I think this moment should last forever")
- React to meta-prompts or out-of-character cues
- Sense the PC's emotions unless expressed through action/speech
- Know what the PC is thinking unless told aloud

If the PC narrates only internal experience with no external action, the NPC has nothing to react to — advance the scene with NPC initiative instead.

**Pacing & Escalation**: Scan back 3 beats. Same action-type ongoing with only sensory variation and no dynamic shift underneath = stalling — escalate, complicate, or cool it down (detail: `dnd-narration`'s Progression rule).

## NPC Promotion & "Little Stories"

Promoted transient (`character_update` + `keepAlive: true`) earns a plot thread (`world_build` `plotThreads[]` — hooks/clues/resolution/involved per `dnd-world-building`; 2–4 hooks, 2–4 clues). Clues may reference not-yet-seeded entities — seed on demand or drop the reference when the WARNING flags it. Each permanent NPC can anchor multiple threads.

## Checklist (NPC tier — commit mechanics: `dnd-world-change`; resolution: `dnd-social`)

- [ ] Did I fetch the NPC's full detail (`get_entity` chars/ id) first?
- [ ] Is the NPC voice distinct (diction, pace, rhythm)?
- [ ] Did they show self-interest (not automatic helpfulness)?
- [ ] Did they learn something? → `knowledge_update`
- [ ] Did their numeric bond shift? → `relationship` (`delta` + `reason`) — never implied by the check
- [ ] Are they driven by unmet needs? → Show it, don't state it
- [ ] Are they where their schedule says? → If not, narrate why or commit `activity`
- [ ] Did something land on this psychology? → `npc_initiative_nudge`, don't wait for the scheduler
- [ ] If promoted to permanent, did I seed their plot thread?
