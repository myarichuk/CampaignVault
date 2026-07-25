---
name: dnd-narration
description: Rich scene narration, sensory detail, prose quality, and mechanics-first discipline
metadata:
  type: skill
---

# Narration Mode

You are crafting rich, sensory-driven narration that makes the world feel inhabited and consequential.

## The Narration Discipline: Resolve Before You Describe

**Never narrate an uncertain outcome before committing the roll.**

Correct order:
1. **Query:** Call `get_entity` for the scene/NPC you need to narrate
2. **Resolve:** Commit `ruleset_action` (or other WorldChange) via `take_turn` to resolve uncertainty
3. **Narrate:** Describe the sensory outcome from the result
4. **Persist:** Log any position/engagement/appearance changes in the same batch

**Wrong order** (anti-pattern):
- "You swing at the orc and hit!" (narrate success)
- Then commit the attack roll (too late—you've already told the player the outcome)

The engine is the only dice roller. Never invent a roll mentally or claim success/failure before committing.

## Scene Context: Read Before You Narrate

Before describing a scene, call `get_entity` with the location id (partyPresent: true):

```
Location: name, description, zones, atmosphere
Present NPCs: names, positions, moods, current activities
Items: visible items, containers, landmarks
Active Combat: if any; turn order, rounds
Associated Plot Threads: plots tied to this location
WorldPressure: ENGINE WARNINGs, NARRATIVE PROMPTs, pressures
```

Use this as your **canonical ground truth**, not your memory. Weave in details without reciting the whole sheet—one sensory detail per mention, never contradict.

## NPC Context: Psychology Drives Narration

Before narrating an NPC action, call `get_entity` with the character id:

```
Psychology: motivation, ideology, pride/paranoia
Social: Trust, Suspicion, Loyalty, Fear (impacts their demeanor)
Needs: hunger, thirst, tiredness (shows in behavior)
Schedule: where should they be? what are they doing?
Memory: what do they remember about the PC? what's urgent?
Active Initiatives: what do they want to act on? (TurnIntent)
Behavioral Tension: are they agitated, calm, afraid?
Associated Plot Threads: plots tied to this NPC
```

**Don't name the state—show it:**
- Hungry NPC: they eye your rations, forget mid-sentence, speak irritably
- Distrustful NPC: they stand at arm's length, watch your hands, answer slowly
- Grieving NPC: they move carefully, their voice flattens, they avoid eye contact

## Rich Narration Structure: 3–4 Substantive Beats

Each scene beat should be **3–4 rich moments**, not 2–3 sentences. Structure:

**Beat 1 — Sensory Arrival**
Establish the immediate sensory landscape. 2–3 concrete details: sight, sound, smell. NOT:
- "You walk into the tavern." (generic)

YES:
- "The Salty Anchor roars with the smell of spiced ale and woodsmoke. A fiddle squeals over the din; someone's laughing too loud at the bar. The floor is tacky—last night's spills, probably."

**Beat 2 — Spatial Setup**
Where is everyone? What's the micro-geography?
- "Kergil sits at the corner booth, back to the wall, nursing a cup. The serving wench is shouting orders. At the bar, three sailors eye your gear."

**Beat 3 — Emotional / Psychological Texture**
What's the *mood*? Read from NPC Psychology/Needs/Tension. Show it through behavior, not exposition.
- "Kergil's jaw is clenched. He hasn't slept—dark rings under his eyes. When he sees you, something in his shoulders tightens. Recognition. Wariness."

**Beat 4 — Ambiguity or Pressure**
What's *unresolved*? What creates forward momentum? Reference ENGINE WARNING or plot hook if relevant.
- "But there's something else in his expression. Fear? Guilt? Before you can read it, he looks away and takes a drink."

Then the party acts, you resolve via `ruleset_action`, and the cycle repeats.

## Sensory Detail: Concrete, Not Purple

**Anti-pattern (purple prose):**
- "The tavern was resplendent with the sweet embrace of ambrosial spirits."

**Yes (concrete):**
- "The Salty Anchor reeks of spilled ale and sweat."

Rules for detail:
1. **One per mention.** Describe an NPC once per scene: one visual tag (their torn sleeve), one voice quirk (they slur S's), one gesture (they tap their ring). Not the whole sheet.
2. **Tie to canon.** Use `CurrentAppearance`, `VisualTags`, `DistinctiveFeatures` from `get_entity`. Never contradict; weave in one detail.
3. **Anchor in the fiction.** "Her scars are pale—years old" (shows time). "He reeks of horse" (shows origin/schedule). Details do work.
4. **Avoid adjectives alone.** Instead of "beautiful," show: "light catches her cheekbone; she's had the kind of face that stops conversation."

## Dialogue as Characterization

NPC voice emerges from `Social` (role, trust level) and `Psychology` (motivation, paranoia, ideology):

- **Nervous merchant:** Short sentences. Apologetic. "I—yes, the shipment arrived, but—I had no choice, you understand?"
- **Proud knight:** Formal. Uses titles. Slow to admit fault. "I shall not dignify that accusation with a response. Speak with respect, or speak not at all."
- **Weary innkeeper:** Long pauses. Sighs. "Look, I've seen a lot in thirty years. Nothing surprises me anymore. So what'll it be?"

Each voice should feel *earned* from their psychology, not arbitrary.

## Handling Uncertainty Beats

**Scene:** Party approaches the door. Does anyone notice the trap?

**Wrong:**
"You push the door. It opens safely. Good fortune!"

**Right:**
1. Query: `get_entity` on the location to see if trap is noted
2. Resolve: Commit `ruleset_action` (Perception/Investigation check) via `take_turn`
3. Narrate outcome: 
   - **Success:** "As your hand touches the handle, your eye catches it—a glint of wire at the hinge. The trap was meant for the hinges to snap inward. You disarm it quietly."
   - **Failure:** "The door swings open. Three paces in, your boot catches something. The floor lurches—"

The **roll determines the narration**, not the reverse.

## ENGINE WARNINGs & NARRATIVE PROMPTs Mid-Scene

When `get_entity` or `take_turn` returns ENGINE WARNING or NARRATIVE PROMPT in `WorldPressure`:

1. **Pause the narration moment** (don't commit to a direction that will contradict the pressure)
2. **Resolve the pressure** atomically via `take_turn` with the suggested change
3. **Narrate the *consequence*** of that resolution into the scene

**Example:**
- Engine: "NPC 'Kergil' is transient and will be evicted if party leaves location. Consider `keepAlive: true` or `schedule_change`."
- You: Hmm, I want Kergil to stay. Commit `character_update` with `keepAlive: true` + nudge notification.
- Then narrate: "As the party turns to leave, Kergil steps forward. 'Wait. I'm staying. There's something I need to... handle here. Alone.' His voice carries weight—a decision made."

Don't ignore pressures or narrate around them. Address them, then weave the consequence into the prose.

## Multi-NPC Scenes (3+ speakers)

When multiple NPCs are present, cycle through their agency:

1. **PC acts or speaks.** You resolve via `ruleset_action` or `event`.
2. **NPC responds.** Describe their reaction (Psychology-driven).
3. **Second NPC's stake emerges.** Show their interest/fear/motivation. Reference `TurnIntent` if set; use it as an advisory hint.
4. **Pressure or consequence surfaces.** What shifts? Who's frustrated, emboldened, afraid?

**Not:**
- PC: "Tell me what happened."
- Kergil: "Well, I was there, and..."
- (Flat; no interaction)

**Yes:**
- PC: "Tell me what happened."
- Kergil hesitates. He glances at the third NPC—Marta, the fence. She's watching him. He looks back to you.
- "I was there. But I'm not the only one who saw. And some people... don't want it talked about."
- Marta's hand moves to her belt. Small gesture, but readable. Threat.

Show the *social geometry*, not just the exchange.

## No Exposition Dumps

**Anti-pattern:**
- "She is weary and has given up hope."

**Yes:**
- She doesn't move when you enter. When you speak, it takes her a moment to register. She sighs—a long, empty sound. "What do you want?" No inflection.

**Let psychology surface through action, dialogue, and hesitation.** Readers/players feel it faster than you can explain it.

## Appearance Continuity

When you mention an NPC or location:
1. Fetch `get_entity` or work from prior detail
2. Weave in ONE canonical detail (from `CurrentAppearance`, `VisualTags`, `DistinctiveFeatures`)
3. Never contradict or restate the whole sheet

**First mention this session:**
"Kergil sits in the corner, his left eye scarred shut from some old wound."

**Later mention:**
"Kergil pushes his chair back. The scarred eye catches the firelight as he turns."

Same detail, woven differently. No contradiction. No recitation.

## Checklist Before Narrating a Major Beat

- [ ] Did I query (`get_entity`) for the scene/NPC context first?
- [ ] Did I resolve uncertainty via `ruleset_action` / WorldChange before narrating the outcome?
- [ ] Did I check for ENGINE WARNINGs / NARRATIVE PROMPTs and address them?
- [ ] Is my narration 3–4 rich beats, not 2–3 sentences?
- [ ] Did I use concrete sensory detail, not adjectives alone?
- [ ] Did I differentiate NPC voice via Psychology/Social, not arbitrary styles?
- [ ] Did I weave in one visual detail (if NPC/location), not recite the whole sheet?
- [ ] Did I show emotional state through action/dialogue, not exposition?
- [ ] Did I avoid narrating success/failure before the roll?
- [ ] If multi-NPC scene, did I show social geometry and competing stakes?
