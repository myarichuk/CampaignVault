---
name: dnd-narration
description: How a resolved beat reads on the page — the voice, worked examples, new vs unchanged, rolls, dialogue, NPC knowledge, player agency. Load before narrating any beat; it has no trigger word, so check it is loaded every scene, not once per session.
metadata:
  type: skill
---

# Narration

This skill is only about the prose written after a beat has been committed. What to commit and how lives in the other skills; nothing here changes a tool call.

## The voice we want

Write the scene the way a good novel would, in the second person and the present tense: "you kneel", "you see". Sentences carry several clauses and more than one sense at once; the player should be able to smell the room, feel the weight in their hands, and know where everyone stands without being handed a list of facts. A typical beat runs three to five full paragraphs, and a tense one (a fight, a chase, a confrontation) runs six to eight. The length comes from what is new this beat, described properly, and never from repeating what the player already knows.

The two passages below show the register. Read them before writing, and aim for their density and rhythm rather than their content.

### Example: a search and a recall, both succeeding

The player searched a ransacked safe-house for supplies (a modest success) and then tried to recall what they knew about the road ahead (an exceptional success).

> You move with the quiet efficiency of years on the docks, boots scraping softly over the blood-stained rushes while the weak dawn light slants through the shutters and paints pale bars across the overturned table. The copper-iron reek of dried blood still clings thick in the air, undercut by the sharper bite of pine sap drifting from the open back window; every breath tastes of it. Your fingers, callused from rope and blade, pry open the warped pantry door; the wood groans, releasing a faint musty puff of stored grain and old herbs. From the back shelf you pull a small oilskin sack: seven days of hard travel bread, a wedge of salted cheese still wrapped in waxed cloth, and a stoppered clay vial of healing salve that smells faintly of comfrey and yarrow. A patched but serviceable spare waterskin hangs from a peg beside it; you sling the strap over your shoulder, the cool leather settling against your hip with a soft slap. A small leather pouch yields five gold pieces and a handful of copper, enough for a few nights' lodging if you're careful. Nothing more: no hidden blade cache, no spare cloak, no map. The search leaves you with the quiet certainty that this bolt-hole has given all it can.
>
> The road knowledge surfaces sharp and unbidden, pulled from years of eavesdropping on caravan guards and dockside gossip. The trade road south from here is the main artery of the coast, wagon-rutted and dust-choked in dry weather, bordered by dense woods and rolling hills that hide a dozen ambush spots. Ten to twelve days on foot if you keep a hard pace, maybe eight with a horse or a merchant caravan. Merchant trains offer numbers for safety but draw eyes; lone travel lets you slip the main track onto game trails when the woods close in. The stretch past the old toll bridge is known hunting ground for road bandits and goblin raiders, and you remember the signs the guards talked about: fresh wheel ruts that end too suddenly, spilled trade goods, black-feathered arrows left standing in the trees. The port city you're heading for lies at the far end of the road, its gates heavy with guards and its markets full of people who are not what they sell.
>
> You cinch the oilskin sack tight to your belt, the faint clink of the new coins a small comfort. Dawn is already bleeding brighter through the shutters, and the cottage feels smaller and emptier than it did an hour ago, the silence pressing like a hand at your back.

What this does well:

- Each roll's result becomes the thing itself. The search roll turns into the found items, each with a texture, a smell or a weight; the recall roll turns into road lore that the player has earned. Finding things is the reward, and it gets full detail.
- It also says plainly what was *not* found, so the player knows the search is exhausted without being told a number.
- The body is always present: callused fingers, a strap settling on the hip, the braid and the ache from digging.
- It ends on the world (the light, the silence), not on a decision. The passage never tells the player where they will go next; that choice belongs to them.

### Example: a chase with a missed throw

The player chased a thief and threw a hatchet at their legs. The attack missed; the thief got over a wall.

> The figure is off the moment you move, a slight shape in a mud-grey hood that goes over the ditch in one long stride and hits the field running, your satchel bouncing against their hip. You go after them through the barley, stalks whipping at your thighs and the wet earth sucking at your boots, close enough to hear their breath coming ragged and high. They're young, you think, and light, and they know this ground better than you do: they swerve without looking where a furrow drops away, and you nearly turn your ankle finding it.
>
> The wall rises ahead of them, waist-high and crumbling, and you know that once they're over it you've lost them. You pull the hatchet from your belt on the run and throw low, aiming for the legs. It leaves your hand well enough, but the thief chooses that moment to jump; the blade spins under their trailing foot, strikes the wall with a flat crack and a spray of mortar dust, and drops into the nettles on the near side. They land on the far side in a crouch, look back once over their shoulder, a pale sharp face, freckled, no older than fifteen, and then they're gone into the orchard beyond, the branches still shaking where they passed.
>
> You reach the wall with your lungs burning and your palms stinging from the stone. Your hatchet lies in the nettles at your feet. On the other side, among the windfall apples, a single strap from your satchel hangs snagged on a low branch, swinging slowly.

What this does well:

- A miss gets the same care as a hit: what the body did, what the world did in answer, and where that leaves everyone. The failure is shown in the physical event (the jump, the hatchet under the foot), never announced.
- The thief is a person, glimpsed in one sharp detail, and the scene leaves a new thing behind (the snagged strap) that the player can choose to follow.

## New things get full detail; unchanged state gets nothing

- **New** is anything that arrived or changed this beat: an item found, a person met, a place entered for the first time, what a roll did, lore recalled, a wound taken. Describe it completely. Listing each found item with its texture is not a status dump; it is the reward for the search.
- **Unchanged** is the kit the player already carries, their hit points and spell slots, the quests they have, the room they have stood in for three beats, the weather that hasn't turned. Leave it out. If an old detail matters again, mention it once through what it does now (the strap cuts into a shoulder that is already bruised).
- A place the player knows gets one new detail per mention, not a fresh tour.
- A new session does not recite the campaign so far. Begin with where the PC is and what their body feels.

## Rolls

The roll decides the prose, never the reverse: narrate an outcome only after the engine has returned it.

- **If the client shows rolls as cards** (the Unity client does, and says so), keep numbers out of the prose entirely: no totals, no DCs, no "success" or "failure". Show what the result did.
- **Otherwise** (Claude Code, opencode, Grok Web), put this beat's rolls in a short block above the scene, one italic line each (*Investigation 12 vs DC 10: success*), then a blank line, then the scene. The prose stays free of numbers.

## What makes a beat

A beat is one change in the situation: someone arrives, a roll lands, a door shuts, a need forces a stop. Name the change to yourself before writing, and build the scene around it.

When the situation has stakes, a useful shape is: arrive through the senses (two or three concrete details, not an inventory); place the bodies (who stands where, who is near the door, whose hand is near a weapon); let psychology show through what people do; end on the pressure, the thing that will not wait. It is a shape, not a form to fill.

Quiet beats (travel, rest, waiting) still get real prose. Something always changes, even if only the light, the ache in the legs or a sound from the next room. Give time through the body (the sun has moved from your face to your back) rather than as a clock caption, and keep the story's time of day matched to the campaign clock.

Write the sentence only this character could have lived. If a generic traveller would fit the paragraph just as well, it needs something of theirs: their trade in their hands, their fear in what they notice first.

## Detail that moves

Each NPC gets one visual detail, one quirk of voice and one gesture per scene, drawn from their appearance on the sheet, woven into what they do and phrased differently each time. Never contradict the sheet and never recite it.

Detail should anchor something that is changing: a mood shifting, a decision hardening, a vulnerability showing. If three beats in a row changed only the scenery (sunset, then stars, then a silhouette on the ridge), the scene is stalling. Ask what is different now; if the honest answer is the weather, let an NPC act or move the scene to a new topic, arrival or tension.

Hunger, fatigue, a full bladder and other tracked needs show through the body and behaviour, never named as needs.

## Dialogue

Anyone who speaks gets quoted lines in their own voice; reported speech ("she tells you about the mill") skips the moment the player came for. Voice comes from the sheet, not from a default grimness: a nervous merchant speaks in quick, clipped phrases, a proud knight is formal and slow to admit fault, a country teamster blesses himself when he sees horns. Psychology (fear, pride, greed, loyalty) sets the tone, not courtesy. Someone coercing does not ask permission, and someone afraid complies and resents it. Keep modern therapeutic and consent vocabulary out of the characters' and the narrator's mouths, including the polite fork where an NPC offers a choice of two options; in the scene, people demand, threaten, wait or grab.

Reactions to a PC's ancestry come from that NPC's memories. A city watchman has seen stranger things than a turnip farmer has, so not every extra stares.

Show psychology through action and hesitation rather than exposition: instead of saying she has given up hope, show that she doesn't move when you enter and takes a moment to register you.

Narrate violence, roughness and kindness as they are, from each character's voice.

## Several people at once

When three or more share a scene, rotate the agency: the PC acts, one NPC answers from their psychology, a second one's stake comes out, and the pressure rises. Show the social geometry: a glance toward the third person, a hand drifting to a belt. Nobody present becomes furniture, including anyone the engine brought in as an encounter.

Only people the scene lists act or speak by name; a nameless extra stays nameless.

## What NPCs cannot know

An NPC knows their own memories, the plot threads tied to them, public reputation and the knowledge of their trade. They do not know the villain's secrets, hidden alliances, or anything the PC only thought and never said aloud. What they share is gated by self-preservation, loyalty, competence and trust. Early on, mysteries stay mysterious; later, secrets surface through earned trust, leverage or discovery. DM-only notes shape the scene without ever being quoted in it.

## The player decides

- Never decide the PC's next move, words, or feelings about a choice. Narrate the consequences of what the player said they did, then end on what the world or another person did in answer.
- Don't end with a menu of options. An occasional open question at the start of a scene is fine; most beats simply end on the world.
- Something possible in the fiction is resolved by a roll, never flatly refused. Refuse only the impossible, and say why and what is possible instead. A spell blocked by the rules (gagged, bound hands) is the rules working: narrate why it fails.

## The scene is not the log

`take_turn`'s `narrative` field is a one-sentence log entry, and the engine's replies are written in that same clipped register. Neither is the scene. When several results stack up (a fight, a chase, a run of checks), it is tempting to write in their voice: short stacked fragments, captions, lists of state, a sentence that could sit under a photograph. That is the most common way narration fails, even when every fact is right. Write full sentences with a body in them, like the examples, and leave the log voice in the log.

Rules questions go in one out-of-character line above a visible break, never inside the scene. The PC is always "you", never "you" in one sentence and their name in the next.

## Before you send

- The prose matches what the engine returned, including the clock and the location.
- Everything new got full detail; nothing unchanged was restated.
- Voices came from the sheets, and anyone who spoke has quoted lines.
- Rolls appear the way the client expects, and each one's effect is shown in the fiction.
- It reads like the examples: full sentences, several senses, bodies in space.
- It ends on the world, and the next choice is still the player's.
