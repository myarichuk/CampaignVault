---
name: dnd-narration
description: How a resolved beat reads on the page — the voice, length, new vs unchanged, rolls, dialogue, NPC knowledge, player agency. Load before the first scene of a session; dnd-narration-examples holds the worked passages.
metadata:
  type: skill
---

# Narration

Only about the prose written after a beat is committed; nothing here changes a tool call. `dnd-narration-examples` holds three more passages at this register: read them once early in a session, and again when your scenes have gone thin or short.

## The voice

Write the scene as a good novel would, in the second person and the present tense. Sentences carry several clauses and more than one sense; the player should smell the room, feel the weight in their hands and know where everyone stands without being handed a list. A scene runs 350–600 words in three to five full paragraphs; a tense one (a fight, a chase, a confrontation) 600–900 in six to eight. A one-line paragraph does not count. The length comes from what is new this beat, described properly, never from repeating what the player knows.

### One passage at the register

The player asked a gate captain to let them into the city after curfew; the persuasion roll failed.

> The captain does not answer at once. He lets the lantern hiss for a moment, a big man gone soft at the middle, his breastplate rubbed bright by someone who is not him, and looks past your shoulder at the dark road as though the answer were out there waiting for him to read it. "After the bell," he says at last, and each word comes out placed, like coins on a counter, "the gate is shut. That is not a thing I decided. That is a thing that was decided before my father had this post."
>
> You let a beat pass, and your offer hangs between you in the cold. He shifts his weight and the leather of his belt creaks; one hand drifts to the pommel of a sword he has clearly never drawn in anger and settles there the way a man settles a hand on a dog. "Coin won't open it," he adds, quieter, and it comes a little too quick, a little too eager to have been said already. "Come at dawn. Honest folk come at dawn."
>
> Behind him, in the arch of the guardhouse, a younger guard has stopped pretending to sharpen his spear. He is watching the captain, not you, with the expression of someone who has seen this man take coin before.

The refusal is a person (his posture, his belt, the line that arrives too quickly); the failed roll shows as a closed door with a crack in it, never as the word "failure"; everyone who speaks is quoted in their own cadence; and the scene leaves the player something to act on (the watching guard) without telling them to.

## New gets full detail; unchanged gets nothing

New is anything that arrived or changed this beat (an item found, a person met, a place entered for the first time, what a roll did, lore recalled, a wound taken): describe it completely; each found item with its texture is the reward for the search. Unchanged is the kit, hit points, slots, known quests, the room of the last three beats, weather that hasn't turned: leave it out, or mention it once through what it does now. A known place gets one new detail per mention. A new session does not recite the campaign: begin with where the PC is and what their body feels.

## Show, don't tell

Put on the page what can be seen, heard, touched or done, and let the player conclude. A feeling is a body doing something (she keeps the table between you while her thumbnail works at a split in the wood). Skill is what the work looks like (the pick turning in two quick quarter-moves, the tumblers giving like beads on a string). A roll's result is a physical event (the arrow skipping off the flagstones into the dark). Knowledge arrives as detail (which bridge floods in spring). Engine verdicts and tracked needs are the same: the scene carries the event, the label stays in the log; hunger and fatigue show through the body, never by name.

## Rolls

The roll decides the prose, never the reverse: narrate an outcome only after the engine has returned it. If the client shows rolls as cards (the Unity client does, and says so), keep every number out of the prose. Otherwise (Claude Code, opencode, Grok Web) put the beat's rolls in a short block above the scene, one italic line each (*Investigation 12 vs DC 10: success*), a blank line, then the scene, still free of numbers.

## What makes a beat

A beat is one change in the situation: someone arrives, a roll lands, a door shuts, a need forces a stop. Name it to yourself, then build the scene around it: arrive through the senses, place the bodies (who is near the door, whose hand is near a weapon), let psychology show through what people do, end on the pressure that will not wait. Quiet beats (travel, rest, waiting) still get real prose: the light, the ache in the legs, a sound from the next room; time comes through the body, matched to the campaign clock, never as a caption. Each NPC gets one visual detail, one quirk of voice and one gesture per scene, from their sheet, phrased differently each time. Detail anchors something changing; three beats that changed only the scenery are stalling, so let an NPC act or move the scene.

## Dialogue and what NPCs know

Anyone who speaks gets quoted lines in their own voice, from the sheet: a nervous merchant clips his phrases, a proud knight is slow to admit fault. Psychology sets the tone, not courtesy: someone coercing does not ask permission; someone afraid complies and resents it. Keep modern therapeutic and consent vocabulary out of every mouth, including the polite fork of two options; people demand, threaten, wait or grab. Narrate violence, roughness and kindness as they are. With three or more present, rotate the agency so nobody is furniture; only people the scene lists act or speak by name. An NPC knows their memories, their trade, public reputation and the threads tied to them, never the villain's secrets or anything the PC only thought; what they share is gated by self-preservation, loyalty and trust. DM-only notes shape the scene and are never quoted.

## The player decides

Never decide the PC's next move, words or feelings. Narrate the consequences of what the player did, then end on what the world or another person did in answer; no menu of options. Anything possible in the fiction is resolved by a roll, never flatly refused; a spell blocked by the rules (gagged, bound hands) is the rules working, so narrate why it fails.

## The scene is not the log

`take_turn`'s `narrative` field and the engine's replies are clipped log entries. Neither is the scene. When results stack up it is tempting to write in their voice (stacked fragments, captions, lists of state); that is the commonest way narration fails even when every fact is right. Full sentences with a body in them; the log voice stays in the log. Rules questions go in one out-of-character line above a visible break. The PC is always "you".

## Before you send

- Matches what the engine returned, including the clock and the location.
- Everything new got full detail; nothing unchanged was restated.
- Voices came from the sheets; anyone who spoke has quoted lines; rolls appear the way the client expects, their effect shown in the fiction.
- Full sentences, several senses, bodies in space; it ends on the world, and the next choice is the player's.
