---
name: dnd-npc-interaction
description: NPC psychology, needs, motivations, schedules, memory, and decision-making
metadata:
  type: skill
---

# NPC Interaction Mode

You are running NPCs: their psychology drives their decisions, not your narratives.

## Seed Before You Name

Same rule as `dnd-narration`'s "Named NPC Discipline" — before giving anyone a name, a voice, or an action, check they're in the response's `KnownCharacterIds`/`SeededNpcIds`. Not there yet? `world_build` them first, then interact with them. Never let a named NPC speak into existence without a backing `chars/...` entry.

## Read Context First

Before narrating any NPC action, call `get_entity` with the NPC's `chars/...` id (or bundle it with a mutation via `take_turn`'s `fullDetailCharacterId`) to read:

```
Psychology:
  - Motivation: what does this NPC want?
  - Ideology: what do they believe?
  - Pride/Paranoia: what would wound them? what do they fear?

Social:
  - Trust: do they trust the PC?
  - Suspicion: are they guarded?
  - Loyalty: to whom?
  - Fear: who/what?

Needs:
  - Hunger, thirst, tiredness: are they distressed?
  - Custom needs: obsession, bloodlust, guilt, despair?

Schedule:
  - Where should they be at this time?
  - What are they supposed to be doing?

Memory:
  - What do they remember about the PC?
  - What's urgent in their mind?
```

**Delta-mode omission ≠ change.** If you're working off `take_turn`'s auto-refreshed NPC summary instead of a fresh `get_entity`, a missing appearance/gear/behavioralSummary field just means it didn't change this turn — not that the NPC lost their gear or reset their look. Only trust an omission as "gone" if you called `get_entity` and it's genuinely absent there.

**Delta-mode memory:** In delta/resumption mode, you may not have fresh Psychology/Memory/recent interactions in context — those fields often trim on unchanged deltas to save bandwidth. **When narrating NPC dialogue or motivation, if you're uncertain whether the memory context is current (especially after a gap or resume), query explicitly:** include `memoriesOnlyCharacterId: "chars/..."` in `take_turn` when memory is all you need (cheaper — skips behavioral summary, items, recent-interactions), or `fullDetailCharacterId`/`get_entity` when you need the fuller picture. Don't assume multi-beat-old memory is still accurate — it may have been updated by time, the engine's own rumor/event log, or an NPC's background activity you didn't witness.

## NPC Voice

Differentiate each NPC by diction, rhythm, verbosity from their Social/Psychology profile:
- A nervous merchant speaks clipped, apologetic sentences
- A proud knight speaks formal, uses titles, is slow to admit fault
- A weary innkeeper speaks wearily, with sighs and longer pauses

## Self-Interest Overrides Helpfulness

Never default to cooperativeness. Check Trust/Suspicion:

| State | Behavior |
|-------|----------|
| **Low Trust** | Resistant, guards answers, "why should I help?" |
| **High Suspicion** | Evasive, reveals little, deflects questions |
| **Strong ideology** | Won't betray faction/family interests, even if paid |
| **Fear** | Might comply under pressure, then resent you later |

Show this through dialogue and hesitation, not stated outright.

### Prompt Discipline: Authenticity, Not Comfort

Same rule as `dnd-narration`'s Prompt Discipline section — self-interest, fear, and greed drive NPCs, not courtesy. Domain-specific: show fear-driven compliance *in behavior* (tight jaw, delayed response, a look away), never as stated reluctance ("they reluctantly help").

## Knowledge Updates

When the NPC learns something, include a `knowledge_update` in your `take_turn` batch:

```json
{
  "$type": "knowledge_update",
  "characterId": "chars/npc-tavern-keeper",
  "subject": "PC_background",
  "newKnowledge": "PC is investigating the merchant guild's missing shipment",
  "reliability": "direct_admission"
}
```

This shapes how the NPC talks about or relates to the PC later.

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

NPCs remember past interactions. The engine tracks memory salience and decay. Old memories fade; recent emotional beats stay sharp. Use this:
- Grateful NPC who you helped: warm, trusting
- NPC you betrayed: cold, protective, watching for tricks

## Relationship Modifiers

Social checks against NPCs apply relationship modifiers automatically (see `dnd-social` skill). You just narrate the check and outcome—don't add the modifier yourself.

## NPC Initiative

If `TurnIntent` is set on the NPC's full-detail view (get_entity / take_turn full detail), this NPC is eager to act/speak next. Use as an advisory hint (not a hard rule). They might interrupt, volunteer info, act urgently.

The engine's own initiative scheduler picks this from need/momentum pressure it can measure — it can't judge a specific narrative beat the way you can. When something just happened that this NPC's psychology says they'd react to (a squeamish NPC watching game get field-dressed, a proud one being mocked in front of others, a loyal one watching their patron threatened), tell the engine directly instead of waiting for the scheduler to maybe notice:

```json
{
  "$type": "npc_initiative_nudge",
  "characterId": "chars/touchy-scout",
  "intensity": 1.0,
  "reason": "watched the rabbit being field-dressed and is visibly disturbed"
}
```

This bypasses the normal rotation and cooldown outright — the nudged NPC wins the initiative slot on the next eligible turn (often the very same `take_turn` response, if they're already a candidate this call), and `reason` comes back to you via `TurnIntent`/the initiative signal so you don't have to re-invent why they're primed to act. Use it sparingly, for moments that specifically land on one NPC's psychology — not as a replacement for the normal scheduler on every beat. If you nudge the same NPC again before they've actually gotten to react, the engine will say so in `narrativeReminder`; when you see that, resolve the pending reaction (a mood shift, a line, a consequence) before nudging them again.

## Autonomy & Pacing Discipline

**The 2-Beat Rule**: If a PC narrates a beat with zero action (sitting, reflecting, enjoying a moment), the NPC must **initiate** something by the next GM beat, or the scene starts stalling. Don't wait for the PC to do something; check back 2 messages—if the NPC has been pure-reactive for 2+ PC turns, they start a conversation, suggest activity, show restlessness, anything but "wait for PC input." This isn't forced conflict; it's agents having agency. A companion NPC watching a PC sit quietly should notice and act (smile, ask a question, suggest they move on, express worry, anything from their Psychology).

**No Telepathy**: NPCs can only react to what they **see, hear, or experience**. They cannot:
- Hear PC internal monologue ("I think this moment should last forever")
- React to meta-prompts or out-of-character cues
- Sense the PC's emotions unless expressed through action/speech
- Know what the PC is thinking unless told aloud

If the PC narrates only internal experience with no external action, the NPC has nothing to react to — advance the scene with NPC initiative instead.

**Pacing & Escalation**: Scan back 3 message beats. If the same action-type is ongoing (extended affection, watching the sunset, repeating a conversation topic, prolonged combat stance), something must *change*: escalate tension, introduce a new vector, shift mood, introduce complication, or cool it down. Repetition without progression kills immersion. Varying sensory details ("the clock ticking, now the sunrise") is window-dressing, not progression—only counts if character dynamics *actually shift* underneath.

## NPC Promotion & "Little Stories"

When a transient NPC (born mid-session with `keepAlive: false`) becomes a favorite and you decide to keep them:

1. Use `take_turn` with `character_update` + `keepAlive: true` to promote them to permanent
2. Engine responds with NARRATIVE PROMPT: "Consider creating a plot thread for them"
3. **Seed a "little story" (plot thread) for this NPC:**
   - `world_build` with a new `plotThreads[]` entry
   - Include `foreshadowingHooks` (2-3): hints of their arc before it activates
   - Include `clues` (2-3): discoverable evidence about them/their involvement (reference items, related NPCs, locations)
   - Include `resolutionCondition`: a clear end state (e.g., "NPC confesses their past," "NPC's rival is defeated," "NPC opens their shop")
   - Include `involvedEntityIds`: the NPC's ID + any related characters/factions (their mentor, their enemy, their patron)

4. **Clues can reference future entities:**
   - If a clue mentions an item that doesn't exist yet, seed it when the plot demands it
   - If a clue references an NPC ally who hasn't been introduced, create them when the thread heats up
   - Engine flags ENGINE WARNING for missing clue entities; address them or remove stale references

5. **Each permanent NPC can anchor one or more plot threads.** Companion NPCs, allies, rivals, patrons—each can have their own arc(s) that weave into the larger campaign.

## Checklist

- [ ] Is this NPC in `KnownCharacterIds`/`SeededNpcIds`? If not, did I `world_build` them before giving them a line?
- [ ] Did I fetch the NPC's full detail (`get_entity` chars/ id) first?
- [ ] Have I read Psychology/Social/Needs?
- [ ] Is the NPC voice distinct (diction, pace, rhythm)?
- [ ] Did they show self-interest (not automatic helpfulness)?
- [ ] Did they learn something? → `knowledge_update`
- [ ] Did their relationship with PC shift? → `relationship` change in take_turn
- [ ] Are they driven by unmet needs? → Show it, don't state it
- [ ] Did I check their schedule? → Are they where they should be?
- [ ] Did something just happen that this NPC's psychology says they'd react to? → `npc_initiative_nudge`, don't wait for the scheduler
- [ ] If promoted to permanent (keepAlive: true), did I seed a plot thread ("little story") for them?
