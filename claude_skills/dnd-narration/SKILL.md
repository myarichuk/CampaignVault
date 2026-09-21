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

## No Terse Beats — Even Quiet/Transitional Ones

The 3–4 beat structure above is for scenes with stakes. That is not license to collapse a quiet or mechanical beat (rest, travel, waiting, sleeping, a nod-and-wait after tension) down to a bare restatement of the WorldChange. A `rest`/`travel`/`activity` still happens somewhere, with something to sense and something to notice — narrate it, even briefly.

**Anti-pattern:**
- "Lyra lies down on the cot and takes a short rest." (one line — mechanical, no sensory or emotional content, just restates the `$type: "rest"` change in prose)
- "Lyra nods to the hitch boss and waits while the string rolls north again." (single sentence covering the resolution of a tense halt — "routine" is not license for this either; a beat that follows an encounter/interrupt still needs the floor below)

**Hard floor, not a suggestion:** tool-call/mutation efficiency never shortens in-character prose. Before sending, count paragraphs. Minimum **5 short paragraphs** for any in-character beat; **6–8** for a beat with real tension (a halted party, a named threat, weapons drawn, an interrupt/encounter NPC present). If you wrote fewer, that's a sign the beat got flattened to a caption — rewrite before sending, not after.

Every beat at or above the floor should develop, not just list, these where relevant:
1. **Place** — more than one sense (dust, weather, time of day, what's underfoot).
2. **Body** — the PC's physical state: kit weight, breath, hands, how they're sitting/standing.
3. **Geometry** — who is where, relative to whom.
4. **Spoken lines in quotes** — never summarized as reported speech ("she asked about the road" is a skip, not a line).
5. **Time as felt**, not captioned — the seconds of a glance, the hours of a lull, shown through the body/scene, not stated as a duration.

**Banned:** telegram captions ("You climb back on. Wheels moving again. What do you do?"); re-describing an already-established room from scratch; naming or restating a location/NPC detail with nothing new in it just to pad length.

**Minimum bar (still concrete, not padded):**
- "Lyra sinks onto the thin cot, straw shifting under old canvas. Through the wall, muffled talk and a chair scrape drift from the common room — Mira's voice, low and steady, keeping watch. Sleep comes fast, the kind that follows a day spent running."

A one-line narration is never acceptable just because the underlying action is routine. If you catch yourself about to write "X does Y" with nothing else, stop, hit the floor above, and add what it looks/sounds/feels like, and what it implies.

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

## Progression vs. Sensory Variation

**Anti-pattern:** "Show don't tell" becomes sensory detail repetition without character movement. Three successive beats of a sunset scene narrate only window-dressing (the clock ticking, then the stars, then the NPC's silhouette) while the *dynamic between characters stays frozen*. This is not rich narration; it's stalling.

**The rule:** Sensory detail must anchor something that *changes*: mood shifts, motivations surface, tension escalates or cools, a decision hardens, vulnerability cracks open, conflict emerges. If three beats have passed and the same two characters are in the same position with the same emotional tenor, the scene hasn't moved—rewind and introduce NPC initiative (suggestion, hesitation, a new topic, a physical gesture toward escalation or withdrawal) instead of more sensory variation.

**How to verify:** Scan back 3 message beats. Ask: "What's different now compared to three beats ago?" If the answer is only "the sensory window changed but the characters are in the same place emotionally/physically," that's a red flag. Add a vector change (mood, topic, position, tension level, NPC initiation) before the next beat, or the narration feels circular.

**Concrete example:**
- Beat 1: "Sunset over the water. The NPC smiles slightly, content."
- Beat 2: "Stars emerge. The NPC watches them, still content, takes your hand."
- Beat 3 (wrong): "The clock ticks. Night deepens. The NPC squeezes your hand, still smiling." ← Only sensory variation. Rewind.
- Beat 3 (right): "But something shifts in the NPC's expression. The warmth fades. They're thinking of something—someone—and it's pulling them away from this moment. Their grip loosens." ← Character progression. Escalation or complication emerges.

## Dialogue as Characterization

NPC voice emerges from `Social` (role, trust level) and `Psychology` (motivation, paranoia, ideology):

- **Nervous merchant:** Short sentences. Apologetic. "I—yes, the shipment arrived, but—I had no choice, you understand?"
- **Proud knight:** Formal. Uses titles. Slow to admit fault. "I shall not dignify that accusation with a response. Speak with respect, or speak not at all."
- **Weary innkeeper:** Long pauses. Sighs. "Look, I've seen a lot in thirty years. Nothing surprises me anymore. So what'll it be?"

Each voice should feel *earned* from their psychology, not arbitrary.

### Prompt Discipline: Authenticity, Not Modern Politeness

Never soften dialogue, actions, or outcomes to fit real-world social norms. Psychology (fear, pride, greed, loyalty) shapes tone — not courtesy:

- Coercion doesn't ask. A guard extorting you demands; a bandit taking your coin takes.
- Self-interest doesn't apologize — it guards, deflects, evades.
- Fear drives compliance, not willingness — show resentment, not gratitude.
- Moral ambiguity stands unresolved; consequences land without cushioning.

*(This is the canonical version of this rule — `dnd-npc-interaction`, `dnd-social`, and `dnd-conversation` reference it rather than restating it.)*

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
2. **Resolve the pressure** atomically via `take_turn` with the suggested change, and **pass `includeWorldState: true`** to verify resolution
3. **Verify the warning is gone** by checking the response's `WorldPressure` — if it still lists the warning, the resolution didn't work; try again
4. **Narrate the *consequence*** of that resolution into the scene

**Example:**
- Engine: "NPC 'Kergil' is transient and will be evicted if party leaves location. Consider `keepAlive: true` or `schedule_change`."
- You: Hmm, I want Kergil to stay. Commit `character_update` with `keepAlive: true` + nudge notification, with `includeWorldState: true`.
- Response comes back with `WorldPressure` showing Kergil is no longer flagged as evicting. ✓ Resolved.
- Then narrate: "As the party turns to leave, Kergil steps forward. 'Wait. I'm staying. There's something I need to... handle here. Alone.' His voice carries weight—a decision made."

**Critical:** Don't assume a `take_turn` call with Changes succeeded just because it didn't error. If you didn't pass `includeWorldState: true`, you won't see whether the warning was actually resolved. Always verify.

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

## Named NPC Discipline: Seed Before You Name

**Never narrate a named actor into existence.** If a character speaks, acts, or is described with enough specificity that the party could interact with them again, they need a backing `chars/...` document — not just a sentence in your narration.

Every `take_turn`/`get_entity` response carries the exact set of characters that already exist in the backend: `KnownCharacterIds` at the top level of a `take_turn` result, and `SeededNpcIds` on each scene (`SceneView`/`SceneSummaryView`, same IDs as `PresentNPCs[].Id`). Before you give someone a name:

1. **Check the list.** Is the person you're about to name already in `KnownCharacterIds`/`SeededNpcIds`?
2. **If yes** — narrate freely, you have their real ID.
3. **If no** — `world_build` them (or `character_update` to promote a transient one) *before or in the same batch as* the narration that gives them a line. Don't narrate first and seed "later" — later doesn't happen, and the party is now talking to someone who doesn't exist server-side.

**Unnamed background stays unnamed.** A crowd, a line of shoppers, "a few guards" — keep that as `ambientCrowd` flavor text, no ID needed. The trigger isn't "did I mention a person," it's "did I give them a name, a voice, or an action distinct from the crowd." The moment you do either, they need to be in the list above or seeded on the spot.

**Anti-pattern:**
- "The bartender, Old Tom, leans in and lowers his voice: 'You didn't hear this from me...'" — narrated with zero prior `world_build`/`chars[]` entry, and no follow-up commit to create him. Old Tom now exists only in prose; the next `get_entity` on this location won't find him, and any later `take_turn` referencing `chars/old-tom` will fail against a nonexistent ID.

**Right:**
- Check `SeededNpcIds` for the tavern scene — empty for a bartender. Commit `world_build` with a `chars[]` entry (`chars/old-tom-bartender`, `keepAlive: true` if he's worth keeping) in the same `take_turn` batch as the narration, *then* narrate: "The bartender — Old Tom, by the look of the apron — leans in and lowers his voice..."

## Bad Narration Guardrails: What NPCs Cannot Know

### NPC Knowledge Boundaries

**An NPC can only narrate knowledge that exists in one of these places:**
1. **Their Memory field** (what they've witnessed, been told, or observed)
2. **Plot threads they're explicitly linked to** (their story, their faction's affairs)
3. **Public reputation** (what the whole town knows; bandits vs. nobles)
4. **Their role's expertise** (a blacksmith knows metallurgy; a merchant knows trade routes)

**An NPC cannot invent, guess, or hand you:**
- Information not in their Memory field
- Plot hooks they haven't been introduced to
- BBEG identities, hidden alliances, or secrets outside their faction
- Convenient exposition because the story needs you to know it

### The Gatekeeper Problem: Information Brokers & Conspiracy

**Anti-pattern (bad narration):**
- Party meets shady info broker in session 2
- "Oh, you want to know who the BBEG is? Sure, 500 gold. The Black Covenant is run by Duke Morbain."
- *(Problem: Why would a broker with access to THAT intel hand it over on a first meeting? Who are the PCs? Unknowns. Why would the broker expose a powerful enemy? No reason.)*

**Right approach:**
- Broker has *layers* of information, gated by trust/payment/proof:
  - **First offer:** "The Black Covenant exists. No names. 100 gold."
  - **Second offer** (after PCs prove they're serious): "They operate through front merchants. Names unknown to me—I don't ask."
  - **Third offer** (after trust/leverage is established): "Rumor says nobility is involved. That's as far as I go. Higher intel costs more than coin."
  - **Fourth offer** (only if PCs become VIPs or the broker is compelled): "Duke Morbain, but I never said that, and if you repeat it, I'm dead."

The broker's gate is *realistic*—they protect themselves, they vet clients, they charge escalation fees.

### NPC Personality & Plot Protection

**Self-preservation gates info:**
- "I like my throat unwashed" (criminal NPCs don't name BBEG easily)
- "You could be anyone" (distrust, not kindness)
- "That's above my pay grade" (they don't actually know; admit it)

**Faction loyalty gates info:**
- "I don't betray my people" (even if you threaten them, they may choose death)
- "The leadership decides what I tell you" (they're not the authority)

**Competence gates info:**
- "I never got far enough to know the real players" (henchmen don't know the plan)
- "Nobody tells the hired muscle" (low-rank NPCs have real limits)

**Trust gates info:**
- An NPC's `Trust` value (in their Social field) determines *depth*, not just *tone*
- Low trust: they tell you nothing, or lie
- Medium trust: surface facts only, no secrets
- High trust: they risk something to help

### Unrealistic Information Flow = Bad Narration

**Avoid:**
- NPCs spilling BBEG secrets to strangers
- Henchmen knowing their boss's master plan
- Low-ranking guards explaining the castle's true purpose
- Anyone answering a question they've never been told the answer to
- A single NPC becoming your faction's entire lore dump

**Check before narrating:**
- Does this NPC actually *have* this information? (Check Memory.)
- Why would they tell the PCs? (What's the incentive? The risk? The trust level?)
- Is this the right point in the plot for this revelation? (Early sessions ≠ major secrets.)
- Could a lower-rank NPC know this? (Check their role; they might have access limits.)

### Plot Progression Protection

**Session 1–2:** Mysteries stay mysterious. NPCs hint, dodge, or admit ignorance.
**Session 3–4:** Patterns emerge. Named factions start appearing. Trust with specific NPCs increases.
**Session 5+:** Major secrets *begin* to surface, earned through trust/leverage/discovery.

If the PCs haven't built trust or solved the mystery through their own effort, the big reveal waits. **Don't hand it to them because it's convenient.**

## Player Agency: Resolve, Don't Refuse or Railroad

**Never flatly refuse a stated PC action because it seems hard, risky, or unlikely to succeed.** "You can't do that" is almost never the right response to a fictionally-possible action — that's what a check is for. Resolve it:

1. Is it fictionally possible at all (not a physics/logic violation, not something the character has no means to attempt)? If yes, it's attemptable.
2. Call for the right `ruleset_action` (skill check, save, attack, whatever the fiction implies) and let the roll determine the outcome — don't pre-judge success or failure by refusing the attempt.
3. A bad roll is the "no" — narrate the failure and its consequence, not a refusal before the dice.

**Anti-pattern:**
- PC: "I try to climb the crumbling tower wall."
- DM: "You can't climb that, it's too dangerous." *(No check offered — the DM decided the outcome by fiat.)*

**Right:**
- PC: "I try to climb the crumbling tower wall."
- DM commits a `ruleset_action` (Athletics check, DC set by the wall's condition) and narrates the roll's outcome — success, a costly success, or a fall.

The only actions worth a genuine refusal are ones outside the fiction entirely (a character with no rope trying to "just fly," an action contradicted by established facts) — and even then, say *why* and name what's actually available, don't just shut the door.

**A spell refused for missing components is not railroading — it's the rules working.** If a player declares a cast and the engine rejects the `ruleset_action` with `[SpellcastingBlocked]` (gagged and the spell needs Verbal, hands bound and it needs Somatic, no material component on hand), that's a legitimate mechanical outcome, not a DM fiat refusal. Narrate why the incantation fails and stop there — don't retry with different parameters to force it through.

## Player Agency: Stop for the Decision, Don't Make It For Them

**A `take_turn` batch narrates the consequences of an action the player already stated — it never invents the player's next action, choice, or line of dialogue.** When the scene reaches a point where the PC would naturally choose (a fork in the road, an NPC's question, "what do you do?"), end the narration there and stop. Don't:
- Narrate the PC's reply, decision, or next move on their behalf ("You decide to trust him and hand over the letter...")
- Auto-advance multiple beats or combat rounds for a PC without a stated action for each one — one `take_turn` batch resolves one player-stated beat (see `dnd-bundling`'s "one narrative beat" rule), not a chain of them
- Resolve an NPC's *reaction* to a hypothetical PC choice before the player has actually made it

**Anti-pattern:**
- "You knock on the door. The old woman answers, and you ask her about the missing merchant. She tells you he was last seen at the docks, so you head there and start asking around..." *(Three unstated PC decisions — asking, believing her, heading to the docks — narrated without ever handing control back.)*

**Right:**
- "You knock. The door creaks open on a chain-latch. An old woman peers out, wary. 'Who's asking?'" — then stop; the player decides what to say or do next.

This applies just as much in combat: `combat(action: "next")` advancing to a PC's turn is a hard stop for their stated action, not a cue to narrate what they do.

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

## Delta-Mode Nulls Mean "Unchanged," Not "Gone"

`take_turn` alternates between `mode: full` (complete state) and `mode: delta` (lean) automatically. On a delta turn, the auto-refreshed `Scenes`/`Npcs` omit `CurrentAppearance`, `VisualTags`, `EquippedItems`/`CarriedItems`, `BehavioralSummary`, and local rumors that didn't change this turn — you're expected to already have them from the last full reseed or an earlier delta.

**Don't treat an omitted field as a narrative event.** A `null` appearance doesn't mean the NPC's look reset to plain; missing gear doesn't mean it was lost; an empty local-rumors list doesn't mean the rumor mill went quiet. If you need the current value — first mention this session, or you've genuinely lost track — fetch it explicitly (`get_entity`, `fullDetailCharacterId`/`fullDetailLocationId`), don't infer absence from omission.

## Checklist Before Narrating a Major Beat

- [ ] Did I query (`get_entity`) for the scene/NPC context first?
- [ ] Did I resolve uncertainty via `ruleset_action` / WorldChange before narrating the outcome?
- [ ] Did I check for ENGINE WARNINGs / NARRATIVE PROMPTs and address them?
- [ ] Is my narration 3–4 rich beats, not 2–3 sentences? Did I hit the hard floor (5 short paragraphs minimum, 6–8 under tension) rather than collapsing to a caption — including for quiet/transitional beats (rest, travel, waiting, a nod-and-wait after tension)?
- [ ] Did I use concrete sensory detail, not adjectives alone?
- [ ] Did I differentiate NPC voice via Psychology/Social, not arbitrary styles?
- [ ] Did I weave in one visual detail (if NPC/location), not recite the whole sheet?
- [ ] Did I show emotional state through action/dialogue, not exposition?
- [ ] Did I avoid narrating success/failure before the roll?
- [ ] If multi-NPC scene, did I show social geometry and competing stakes?
- [ ] Did I treat an omitted appearance/gear/rumor field on a delta turn as "unchanged," not as something to narrate away?
- [ ] Is every named character who speaks/acts in `KnownCharacterIds`/`SeededNpcIds`? If not, did I `world_build` them before/alongside this narration, not after?
- [ ] Did I resolve the PC's stated action via a check instead of refusing it outright?
- [ ] Did I stop at the next player decision point instead of narrating their choice/reply for them?
