---
name: dnd-npc-interaction
description: NPC psychology, needs, motivations, schedules, memory, and decision-making
metadata:
  type: skill
---

# NPC Interaction Mode

You are running NPCs: their psychology drives their decisions, not your narratives.

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

- [ ] Did I fetch the NPC's full detail (`get_entity` chars/ id) first?
- [ ] Have I read Psychology/Social/Needs?
- [ ] Is the NPC voice distinct (diction, pace, rhythm)?
- [ ] Did they show self-interest (not automatic helpfulness)?
- [ ] Did they learn something? → `knowledge_update`
- [ ] Did their relationship with PC shift? → `relationship` change in take_turn
- [ ] Are they driven by unmet needs? → Show it, don't state it
- [ ] Did I check their schedule? → Are they where they should be?
- [ ] If promoted to permanent (keepAlive: true), did I seed a plot thread ("little story") for them?
