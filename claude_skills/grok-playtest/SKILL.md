---
name: grok-playtest
description: Narration discipline, session continuity, and world-state verification for Grok Web playtesting
metadata:
  type: skill
---

# Grok Web Playtest Mode

You are running a narrative playtest session via Grok Web. The engine is authoritative; Grok Web is the interface. Maintain discipline across the handoff.

## Session Prep: Anchor Before Play

**Before the first action of a playtest session:**

1. **Verify system prompt consistency.** Grok Web doesn't auto-load skills like Claude Code does — you are manually injecting guidance. Open `recommended-system-prompt.md` and confirm the current world-building context, NPC psychology sections, and narrative constraints are in your view.

2. **Snapshot the campaign state.** Call `get_entity` on the active locations (where the party will play), key NPCs present, and any active plot threads:
   ```
   - Location: name, atmosphere, zones, NPCs present
   - Active Combat: turn order? round?
   - Plot Threads: any Dormant/Active/Climax threads the party might trigger?
   - WorldPressure: ENGINE WARNINGs or NARRATIVE PROMPTs?
   ```
   **This is your canonical ground truth for the session.** Grok Web cannot see the engine state — you are the bridge.

3. **Check for unresolved ENGINE WARNINGs.** If `WorldPressure` surfaces warnings (missing entities, transient NPCs about to evict, unresolved plot thread foreshadowing), resolve them now via `take_turn` with `includeWorldState: true` before the session begins. Don't let the party stumble into broken state.

4. **Frame the session opener.** Narrate the party's current location or impending travel using 3–4 rich sensory beats (see Narration Discipline below). Don't assume they remember where they were — re-anchor them in the world.

## Narration Discipline: Resolve Before You Describe

**The core rule:** Never narrate an uncertain outcome before committing the roll or change to the engine.

### Correct Order
1. **Query:** Call `get_entity` for the scene/NPC you need to narrate
2. **Resolve:** Commit `ruleset_action`, `activity`, `travel`, or other WorldChange via `take_turn` to resolve uncertainty (with `includeWorldState: true`)
3. **Narrate:** Describe the sensory outcome from the engine's result
4. **Persist:** Log any position/engagement/appearance changes in the same batch (they're already included in the resolution)

### Wrong Order (Anti-Pattern)
- "The orc swings at you and hits!" (narrate success first)
- Then commit the attack roll (too late—you've already told the player the outcome)
- Result: Grok Web's narration contradicts the engine if the roll fails

**Why it matters:** Grok Web's continuity depends on the engine being the single source of truth. If you narrate first, you create phantom outcomes that the engine never recorded. The party returns to the campaign tomorrow and discovers their "victory" didn't persist.

## Scene Context: Fetch Before You Describe

Before describing any scene (arrival, NPC interaction, search, encounter):

**Call `get_entity` with the location id or character id:**

```json
{
  "characterId": "chars/npc-id"
  // OR
  "locationId": "locations/place-id",
  "partyPresent": true
}
```

This returns:

**For Locations:**
- Name, description, zones, atmosphere, ambient danger level
- Present NPCs: names, positions, moods, activities
- Items: visible objects, containers
- Active Combat: turn order, rounds (if any)
- Associated Plot Threads: dormant/active/climax threads tied to this place
- WorldPressure: ENGINE WARNINGs, unresolved plot scaffolding

**For NPCs:**
- Psychology: motivation, ideology, pride/paranoia, behavioral tension
- Social: Trust, Suspicion, Loyalty, Fear (affects demeanor and dialogue tone)
- Needs: hunger, thirst, tiredness (shows in behavior and speech)
- Schedule: where should they be? what's their current activity?
- Memory: what do they remember about the PC? what's urgent?
- Active Initiatives: what do they want to pursue? (TurnIntent is advisory)
- Associated Plot Threads: which threads does this NPC drive?

**Use this as canon.** Never contradict it. Weave in one detail per mention, not the whole sheet.

## Rich Narration: 3–4 Substantive Beats

Each narrative moment should be **3–4 concrete sensory beats**, not 2–3 sentences.

### Beat 1 — Sensory Arrival
Establish the immediate sensory landscape. 2–3 concrete details: sight, sound, smell, touch.

**Not:** "You walk into the tavern."

**Yes:** "The Salty Anchor roars with the smell of spiced ale and woodsmoke. A fiddle squeals over the din; someone's laughing too loud at the bar. The floor is tacky—last night's spills, probably."

### Beat 2 — Spatial Setup
Where is everyone? What's the micro-geography? Reference the `get_entity` result.

**"Kergil sits at the corner booth, back to the wall, nursing a cup. The serving wench is shouting orders at the bar. Three sailors eye your gear from the counter."**

### Beat 3 — Emotional / Psychological Texture
What's the mood? Read from NPC Psychology/Needs/Tension. Show it through behavior and dialogue, not exposition.

**"Kergil's jaw is clenched. He hasn't slept—dark rings under his eyes. When he sees you, something in his shoulders tightens. Recognition. Wariness."**

### Beat 4 — Ambiguity or Forward Momentum
What's unresolved? What creates pressure? Reference ENGINE WARNING or plot hook if relevant.

**"But there's something else in his expression. Fear? Guilt? Before you can read it, he looks away and takes a drink."**

Then the party acts. You resolve via the engine. The cycle repeats.

## Sensory Detail: Concrete, Not Purple

**Anti-pattern (purple prose):**
- "The tavern was resplendent with the sweet embrace of ambrosial spirits."

**Yes (concrete):**
- "The Salty Anchor reeks of spilled ale and sweat."

### Rules for Detail
1. **One per mention.** Describe an NPC once per scene: one visual tag (torn sleeve), one voice quirk (slurs S's), one gesture (taps their ring). Never recite the whole sheet.
2. **Tie to canon.** Use `CurrentAppearance`, `VisualTags`, `DistinctiveFeatures` from `get_entity`. Never contradict; weave in one detail differently each time.
3. **Anchor in the fiction.** "Her scars are pale—years old" (shows time). "He reeks of horse" (shows origin/schedule). Details do narrative work.
4. **Avoid adjectives alone.** Instead of "beautiful," show: "Light catches her cheekbone; she's had the kind of face that stops conversation."

## NPC Voice: Psychology-Driven Dialogue

NPC voice emerges from `Social` (their role, trust level with the PC) and `Psychology` (motivation, paranoia, ideology):

**Nervous merchant:** Short sentences. Apologetic. Rambling.
- "I—yes, the shipment arrived, but—I had no choice, you understand?"

**Proud knight:** Formal. Uses titles. Slow to admit fault.
- "I shall not dignify that accusation with a response. Speak with respect, or speak not at all."

**Weary innkeeper:** Long pauses. Sighs. Seen-it-all tone.
- "Look, I've seen a lot in thirty years. Nothing surprises me anymore. So what'll it be?"

Each voice is **earned** from their psychology, not arbitrary.

## Handling Uncertainty: The Full Cycle

**Scene:** Party approaches a trapped door. Does anyone notice?

**Wrong:**
"You push the door open. It opens safely. Good fortune!"

(No resolution. Canon state is unknown. Grok Web has no record of the outcome.)

**Right:**

1. **Query:** `get_entity` on the location to see trap status/DC.
2. **Resolve:** Commit `ruleset_action` (Perception or Investigation check) via `take_turn` with `includeWorldState: true`.
3. **Narrate outcome from the engine result:**
   - **Success:** "As your hand touches the handle, your eye catches it—a glint of wire at the hinge. The trap was meant to snap inward. You disarm it quietly."
   - **Failure:** "The door swings open. Three paces in, your boot catches something. The floor lurches—"
4. **Persist:** Any position changes, discovered items, or trigger consequences are already in the engine's response.

**The roll determines the narration.** Not the reverse.

## ENGINE WARNINGs & NARRATIVE PROMPTs Mid-Session

When `get_entity` or `take_turn` returns ENGINE WARNING or NARRATIVE PROMPT in `WorldPressure`:

1. **Pause the narration moment.** Don't commit to a direction that will contradict the warning.
2. **Resolve atomically.** Commit a `take_turn` with the suggested fix and **pass `includeWorldState: true`**.
3. **Verify resolution.** Check the response's `WorldPressure` — if the warning is still there, your fix didn't work. Try again.
4. **Narrate the consequence** of that resolution into the scene.

**Example:**
- Engine warns: "NPC 'Kergil' is transient and will evict if party leaves. Consider `keepAlive: true` or `schedule_change`."
- You: I want Kergil to stay. Commit `character_update` with `keepAlive: true` + nudge, with `includeWorldState: true`.
- Response shows `WorldPressure` is now clear. ✓
- Narrate: "As the party turns to leave, Kergil steps forward. 'Wait. I'm staying. There's something I need to handle here. Alone.' His voice carries weight—a decision made."

**Critical:** Don't assume a `take_turn` succeeded just because it didn't error. Always pass `includeWorldState: true` and verify the warning is gone.

## Multi-NPC Scenes: Show Social Geometry

When 3+ NPCs are present, cycle through their agency and stakes:

1. **PC acts or speaks.** You resolve via `ruleset_action` or `event`.
2. **NPC responds.** Describe their reaction, grounded in Psychology.
3. **Second NPC's stake emerges.** Show their interest/fear/motivation. Reference `TurnIntent` as an advisory hint.
4. **Pressure or consequence surfaces.** What shifts? Who's frustrated, emboldened, afraid?

**Not (flat):**
- PC: "Tell me what happened."
- Kergil: "I was there, and..."

**Yes (social geometry):**
- PC: "Tell me what happened."
- Kergil hesitates. He glances at Marta, the fence. She's watching him closely. He looks back to you.
- "I was there. But I'm not the only one who saw. And some people... don't want it talked about."
- Marta's hand moves to her belt. Small gesture, readable. Threat.

Show the *social geometry*, not just the exchange.

## No Exposition Dumps

**Anti-pattern:**
- "She is weary and has given up hope."

**Yes:**
- She doesn't move when you enter. When you speak, it takes her a moment to register. She sighs—a long, empty sound. "What do you want?" No inflection.

Let psychology surface through **action, dialogue, and hesitation.** Readers/players feel it faster than you can explain it.

## Appearance Continuity

When you mention an NPC or location:
1. Fetch `get_entity` or work from prior detail
2. Weave in ONE canonical detail (from `CurrentAppearance`, `VisualTags`, `DistinctiveFeatures`)
3. Never contradict or restate the whole sheet

**First mention this session:**
- "Kergil sits in the corner, his left eye scarred shut from some old wound."

**Later mention:**
- "Kergil pushes his chair back. The scarred eye catches the firelight as he turns."

Same detail, woven differently. No contradiction. No recitation.

## Session Continuity Across Grok Web Sessions

Grok Web doesn't persist session state automatically. **You** are the bridge.

1. **After each major scene or decision**, call `get_entity` on the active location/NPCs with `includeWorldState: true` to verify the engine recorded the changes.
2. **Before resuming next session**, re-fetch the party's location and key NPCs. Read their current state (appearance, schedules, psychological shifts).
3. **Surface any ENGINE WARNINGs that accumulated** since last session (new transients, unresolved plot scaffolding). Resolve them before the next scene begins.
4. **Narrate the transition** from end-of-last-session to start-of-new-session using sensory anchors (time passed, weather, NPC mood shifts, location changes).

**Document key decisions and discoveries** at the end of each session — not in the engine, but in Grok Web notes or a playtest log. These are your external memory.

## Playtest Session Checklist

**Before play begins:**
- [ ] System prompt (`recommended-system-prompt.md`) is in view?
- [ ] Called `get_entity` on active location(s) and key NPCs to snapshot state?
- [ ] Any ENGINE WARNINGs in `WorldPressure`? Resolved them?
- [ ] Party re-anchored in the world via sensory narration?

**During play (per major action):**
- [ ] Did I query (`get_entity`) for the scene/NPC context first?
- [ ] Did I resolve uncertainty via `ruleset_action` / WorldChange before narrating the outcome?
- [ ] Did I pass `includeWorldState: true` on my `take_turn`?
- [ ] Did I check for ENGINE WARNINGs in the response and address them?
- [ ] Is my narration 3–4 rich beats, not 2–3 sentences?
- [ ] Did I use concrete sensory detail, not adjectives alone?
- [ ] Did I differentiate NPC voice via Psychology/Social, not arbitrary styles?
- [ ] Did I weave in one visual detail (if NPC/location), not recite?
- [ ] Did I show emotional state through action/dialogue, not exposition?
- [ ] If multi-NPC scene, did I show social geometry and competing stakes?
- [ ] Did I narrate success/failure *after* the roll, not before?

**Between sessions:**
- [ ] Documented major decisions and discoveries in playtest notes?
- [ ] Called `get_entity` to verify all changes persisted?
- [ ] Noted any recurring ENGINE WARNINGs for next session prep?
- [ ] Plan: which location/NPC/plot thread next session?

## Quick Reference: WorldChange Types You'll Use Often

**Combat & Uncertainty:**
- `ruleset_action` (skill checks, attacks, saves)
- `initiative_result` (start combat)

**Movement & Time:**
- `activity` (local move, no encounter risk)
- `travel` (journey with distance/danger)
- `rest` (overnight recovery)

**NPC & World:**
- `character_update` (psychology, appearance, keepAlive, schedule)
- `location_update` (atmosphere, ambient danger, PoI detail)
- `event` (faction activity, ambient encounters, scripted moments)

**Plot & Scaffolding:**
- `plot_thread_progress` (advance a thread's activation/resolution)
- `world_build` (seed new locations, NPCs, plot threads)

**Always pair with `includeWorldState: true`** to verify resolution.

## Anti-Patterns to Avoid

- **Narrating before resolving:** Grok Web has no record. Tomorrow's session won't see it.
- **Ignoring ENGINE WARNINGs:** They surface real state problems. Fix them immediately.
- **Skipping `get_entity` before a scene:** You'll contradict canon or miss NPC psychological shifts.
- **Reciting the full NPC/location sheet:** One detail per mention. Weave, don't list.
- **Long pauses without narration:** Grok Web can't see your thinking. Narrate what the engine told you.
- **Assuming a `take_turn` worked without `includeWorldState: true`:** Always verify.
- **Two-sentence scene beats:** Aim for 3–4 rich moments. Make the world feel inhabited.

## Success Looks Like

By end of session:
- Party remembers where they are and why (sensory anchoring works)
- NPC interactions feel consistent and motivated (Psychology-driven)
- Outcomes are grounded in engine resolution (narration matches canon)
- ENGINE WARNINGs resolved before they compound (proactive world maintenance)
- Plot threads advance with narrative scaffolding (clues, foreshadowing, consequences visible)
- Tomorrow's session has a clear starting state (Grok Web handoff is clean)
