---
name: dnd-narration
description: Scene prose after a commit — sensory craft, PC/NPC voice from the sheet, no option menus, no recap (load before narrating any beat — unlike combat/social/travel this has no single trigger word, so re-check it's loaded each scene, not just once per session)
metadata:
  type: skill
---

# Narration Mode

Prose craft only: how the resolved beat reads. Mutation syntax → `dnd-world-change`. Bundling → `dnd-bundling`. Seed hygiene and the two-memory floor → `dnd-world-building`. Voice/trust/initiative → `dnd-npc-interaction`. Interrupt grading → `dnd-campaign-events`.

## Resolve Before You Describe

Never narrate an uncertain outcome before `take_turn` returns. The engine is the only dice roller. Show the roll in italics in the prose (`Insight 11 vs DC 13`), then describe what that number did to the body in front of you — the roll determines the narration, not the reverse.

## Read Before You Narrate

- **Scene:** the latest `take_turn` summaries are ground truth (refresh per `dnd-world-change`). The DB wins over your memory and over the handoff for time, location, gear and conditions. If the clock says hour 3, it is the small hours — narrate that, or commit the time forward first (`rest` / `advance_world`), then narrate the dawn.
- **PC diction** lives on `chars/{pc}`: `personality` plus a memory whose topic is `voice` (and `systemStats.traits.voice` if present). Read it at session start (`includeParty` or `memoriesOnlyCharacterId`). If those are empty, seed them via `world_build` / `knowledge_update` before the first in-character paragraph — do not invent a house noir.
- **NPC diction** lives on that NPC's `personality` plus at least two memories. An empty card (`openness: 0.5`, no memories) may not speak yet — patch the seed per `dnd-world-building`, then play the line.
- Campaign-specific lore (how a given faction reacts to a tiefling, which Watch counts hands) belongs in those memories and in `personality`, not in a pasted system prompt.

## One Change Per Beat

A beat is one change in the situation: a person arrives, a roll lands, a door shuts, a need forces a stop. If nothing changed, you over-described the road.

Quiet travel, a rest, a privy stop, a nod-and-wait still get *place + body + one new fact*. Do not pad them to a paragraph count.

**Write the sentence only this PC (or the named NPC) could have stood still for.** If you could swap in a generic wanderer and the paragraph still works, rewrite.

### Floor (quality, not quota)

A beat is normally 3–5 short paragraphs and 6–8 under real tension, but length comes from new things happening, never from restating old ones. Before sending, check:

1. Place — more than one sense, and only details that were not in the last beat.
2. Body — breath, hands, stance, kit weight, a need if it is actually moving the character.
3. Geometry — who is where relative to whom.
4. Spoken lines in quotes when anyone talks. Reported speech is a skip.
5. Time felt through the body, not captioned as a duration.

Stop when the change has landed. Three short paragraphs that move are better than eight that recap. If you wrote a caption (`You climb back on. Wheels moving again.`), rewrite once. If you wrote a sunset three times, cut two.

**Banned**

- Telegram captions and option menus. Do not enumerate the PC's next legal moves. End on what the world or the other person just did. One "What do you do?" per *scene*, not per message.
- Campaign litany as a closer (the dead stone, the sister to the north, the ward on the shed, the beads in the pack) unless that fact *changed this beat*. Opening a session is not a reason to restate the campaign; the player already knows it.
- A status dump: slot counts, HP, active spells, worn gear listed as prose. Weave one item if it matters; the sheet is the sheet.
- Naming a need the engine tracks ("her bladder has opinions") — narrate its sensory effect (`dnd-npc-interaction` Need-Driven Behavior).
- Re-describing an established room from scratch. One new tag per mention.
- Author name-drops as style. Concrete cost + a tell in the same paragraph is enough.
- Modern consent language in NPC or narrator mouths ("you can do X or Y, either way I'm writing the report"). Menus of choices belong to the player; NPCs in this world state terms, threaten, plead or wait.

## Structure When There Are Stakes

A shape, not a counter:

1. Sensory arrival — two concrete details, not a room tour.
2. Geometry — bodies in space.
3. Texture from Psychology/Needs — shown, not named.
4. Pressure — the thing that will not wait.

Then stop. The player acts.

## Sensory Detail: Concrete, Not Purple

One visual tag, one voice quirk, one gesture per NPC per scene — from `CurrentAppearance` / `VisualTags` / `DistinctiveFeatures`, woven differently each time, never contradicted, never the whole sheet. Tie the detail to a change (a mood, a decision, a tell), or cut it.

## Progression vs. Sensory Variation

Sensory detail must anchor something that *changes*: a mood shift, an escalation, a decision hardening, a vulnerability cracking. Three beats of the same tenor where only the scenery moves (sunset, then stars, then a silhouette) is stalling.

Verify: scan back three beats and ask what is different compared to then. If the answer is only "the weather", rewind and give an NPC initiative (`dnd-npc-interaction` 2-beat rule) or shift the vector — topic, position, tension, a new arrival.

## Dialogue as Characterization

Voice from the sheet, not from a default grimdark: a nervous merchant is clipped, a proud knight formal and slow to admit fault, a country teamster blesses himself at horns. Psychology (fear, pride, greed, loyalty) sets tone — not courtesy. Coercion does not ask. Fear complies and resents. Do not modern-soften. This is the canonical copy of the rule; other skills point here.

Reactions to a PC's ancestry are on *that NPC's* memories — a hardened city Watch has seen worse than a turnip carter has. Do not apply a generic "people stare" to every extra.

Let psychology surface through action, dialogue and hesitation, not exposition: not "she is weary and has given up hope", but she doesn't move when you enter, and it takes her a moment to register you.

## Uncertainty, Warnings, Multi-NPC

- Commit the check, then narrate from the number.
- ENGINE WARNINGs pause prose; resolve per `dnd-campaign-events` (same-batch fix + verify), then narrate the consequence.
- Three or more speakers: cycle agency (PC acts → NPC responds from Psychology → second NPC's stake emerges → pressure surfaces) and show the social geometry — a glance to the third party, a hand moving to a belt. Never let the second NPC become furniture.
- An interrupt or encounter NPC the engine spawned is real: resolve it in the fiction, or narrate why it is absent. Do not sleep through a live encounter.

## Named NPC Discipline: Seed Before You Name

Per `dnd-world-change` — never narrate a named actor into existence; check the scene roster (`presentNPCs`), `world_build` first. Open first contact with an approach beat, read the card it returns, then play the reaction. Roster ghosts (NPCs listed as present but hours away in the story) are not on stage — clear them (`dnd-exploration`) rather than voicing them.

## What NPCs Cannot Know

Only their Memory, linked plot threads, public reputation and job expertise — never BBEG secrets or hidden alliances. Gate by self-preservation, faction loyalty, competence and `Trust`. Early sessions: mysteries stay mysterious; later: secrets surface only through earned trust, leverage or discovery.

## Player Agency

- Never flatly refuse a fictionally possible action — resolve it via the matching `ruleset_action` and let the roll be the "no." Refuse only the fictionally impossible, saying why and what is available. A `[SpellcastingBlocked]` rejection is rules working, not railroading.
- A `take_turn` batch narrates the consequences of the stated action — never the player's next choice, reply or move. Stop at forks, questions and PC combat turns (one batch = one player-stated beat).

## Checklist

- [ ] The commit landed before the prose, and the prose matches the DB clock and location.
- [ ] PC/NPC voice came from the sheet, not a default grimdark.
- [ ] The situation changed; I did not recap the road, the kit or the quest list.
- [ ] No option menu, no status dump, no named needs.
- [ ] One canonical appearance tag, woven, not recited.
- [ ] Multi-NPC: social geometry shown, no furniture NPCs.
- [ ] Stopped at the player's next decision.
