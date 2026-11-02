# Impressions: how a beat lands on each observer

## Problem

Playtest (night road, the caravan splitting off, Maeve's Minor Illusion against a lone goblin scout, four scenes in a row):

1. **Too many witnesses.** `WitnessSelector` counts every non-PC whose `CurrentLocationId` equals the actor's. It ignores the
   recorded `SpatialPositions` bands, the light, and whether anyone is leaving. Lys, Sera and Corren, half a mile up a dark road
   with the caravan, were told to "give each a short reaction" to a five-foot illusion.
2. **No way to say "they're leaving".** Location is a yes/no bucket. An NPC who waves goodbye and walks off is fully present
   until something clears their location, and then fully gone. Nothing sits between those two states.
3. **The illusion wasn't Minor Illusion.** SRD 5.1: "a sound **or** an image of an object", no larger than a 5-ft cube; the
   image "can't create sound, light, smell"; physical interaction reveals it; a creature that uses its action to examine it
   makes an Intelligence (Investigation) check against the spell DC. With `mode` missing, the engine took both and said
   "utility effect, nothing rolled", so a gurgling, red-eyed, advancing ghost passed.
4. **No per-observer reading, no reaction, no memory.** The scout never got a check, never decided anything and kept nothing
   from beat to beat, so four scenes replayed "scrambles backward". The same gap covers every beat meant to move someone:
   butterflies for children, a bouquet, flattery (sincere or not), a lie, a threat, a performance.

## Model

One pipeline for every beat meant to make someone feel something. The beat carries an **appeal**; each observer goes through it:

```
appeal: tone (delight, awe, amusement, affection, gratitude, comfort, pride, trust, sorrow,
              fear, menace, disgust, anger, insult)
        intensity (mild | strong | overwhelming), deceptive (bool), reception (per-observer tone override)

per observer
  1. Perceive   AwarenessResolver + sight (I1): band to the source, light, darkvision, departing
  2. Read       sincere: lands. Deceptive: does the observer believe it?
                  - an illusion: believed (RAW: only examining it, an action, earns a check: I6's disbelieve)
                  - a check (Deception, Persuasion, Intimidation, Performance): the contest decides for its target;
                    everyone else, passive Insight (5e) / Perception DC (pf2e) against the roll total
                  - an event with no roll: passive Insight against the actor's passive Deception
                Seen through inverts the appeal: flattery reads as manipulation, a fright as a cheap trick.
  3. Appraise   intensity x who they are: LifeStage (a child loves butterflies more), Openness (wonder), Resilience
                (fear), relationship to the actor, Traits/Fears/Wants keywords, and habituation (the same tone from the
                same actor in the last day counts less each time)
  4. Respond    a tendency for the narrator, not prose: approach, linger, thank, warm, comply, withdraw, flee, confront,
                scoff, unmoved
  5. Persist    an Impression record on the observer (habituation memory, capped), a relationship change toward the
                actor when the observer knows who did it, and a mood on a strong impression
```

The engine owns the bookkeeping and the defaults. The LLM owns taste: `reception` overrides the tone for named observers
(the miller's daughter hates insects), and the engine runs the rest of the pipeline on that.

The result is one summary line in the commit, replacing the bare witness line for that beat:

```
IMPRESSIONS (minor illusion by Maeve, delight): Pip — enchanted (strong): approaches; Tam — amused: lingers;
Old Wenna — uneasy (distrusts magic): withdraws. Narrate each; already recorded.
```

## Decisions

- **D1. No invented rolls.** A sincere beat never rolls for quality on its own. When the beat already is a check (Performance,
  Persuasion), its result sets the intensity: failing the DC drops a tier, beating it by 10 raises one.
- **D2. Appeal is explicit, with three inferences.** An Intimidation check is `menace`, a Deception check is deceptive
  (`trust` unless an appeal is given), a Performance check is `delight`. Everything else needs `appeal`. An unmarked spell
  stays a plain witness beat.
- **D3. Audience.** A loud or visible beat reaches everyone who perceives it. A quiet one (flattery, a bouquet handed over)
  reaches its targets (`targetIds`, or the event's `involved` minus the actor). PCs are never appraised; the player decides.
  Combatants are appraised: a fight is exactly where the scout's fear matters.
- **D4. Relationship needs a known author.** Fear of a "ghost" the scout believes real doesn't move his opinion of Maeve.
  Seeing through her trick does (−).
- **D5. No auto-conditions.** A frightened tendency is not the frightened condition. That has mechanics (disadvantage, can't
  approach) the SRD grants only to specific effects. The tendency is advice; the narrator commits a status when the rules say so.
- **D6. "Alone" needs evidence.** Outside combat there is no allegiance data, so the flee tendency reads morale and HP only.
  In combat, allies are the observer's side of the encounter.
- **D7. Distances are bands, not feet.** Touch 5 ft, Close 10, Near 30, Far 120, Distant 500 (house mapping) only to compare
  against darkvision. With no band recorded, an observer is Near, as today.
- **D8. Light.** Explicit `Location.Lighting` wins. Otherwise indoors (Building, Room) is dim; outdoors follows the hour:
  bright 07–17, dim at 06 and 18–19, dark otherwise; a settlement or district at night is dim (lamps, windows). In the dark
  only Touch/Close are seen, or up to darkvision; in dim light, up to Far. A luminous beat (fire or radiant damage, Daylight,
  `luminous: true`) is seen at any light. The illusion's own image is not luminous (RAW: it can't create light).
- **D9. Departing.** `activity` with `departing: true` (destination optional) marks an NPC as leaving: Distant from everyone
  there right away, and moved (to the destination, or out of the location) once a quarter hour of campaign time has passed.

## Phases

- [x] **I0. Plan.** This document.
- [x] **I1. Sight.** `SightResolver`: the band between observer and source from either side's `SpatialPositions`, the light
  (D8), darkvision parsed from the race trait or `Attributes["darkvision"]`; `PerceptibleBeat.Luminous`. `WitnessSelector`
  strips Visible from an observer who can't see the source (note: "too dark/far to see it"); a beat left with no senses is
  missed. `Location.Lighting` (bright/dim/dark) on the model and on `location_update`. Tests: dark road, Far observer misses
  a visible illusion; hears a loud one; darkvision sees it at Near; a fireball is seen in the dark; dim light reaches Far.
  *As built:* `Data/Witness/SightResolver.cs`; `AwarenessResolver.Perceive` takes what the position blocks (`blocked`, `blockedNote`),
  so a far trader on a dark road reads `Corren (far, too dark to see it; heard it only)` for a loud+visible beat and misses an
  image-only one. Location type defaults to Building (dim) when a caller never set it, so an outdoor place created without a
  type stays dim at night: set `type` or `lighting`. Not applied to A6 noticing (stealth/sleight), which keeps passive Perception only.
- [x] **I2. Departing and encounter stubs.** `ActivityChange.Departing`, `Character.Departing { ToLocationId, FromLocationId,
  SinceHour }`; the dispatcher completes a departure after 0.25 h. Sight treats a departing observer as Distant. The departure
  reminder names `departing: true`. An unnamed encounter stub ("Unknown Encounter Entity") is listed once as "N unnamed
  encounter stubs (name them with character_update)", not as a witness each. Tests.
  *As built:* a departure completes after two more committed beats or a quarter hour, whichever is first: beats under an hour
  don't move the clock (`ApplyMicroTimeNudgeAsync`), so time alone would leave them leaving forever. `departing:false` turns them
  back; any other move clears it. Off-scene completion sets `DepartedFromLocationId`/`DepartedAtDay` like a transient eviction.
- [x] **I3. Illusion legality.** A spell whose `perceptibleModes` are a choice fails without a valid `mode` (fix hint lists the
  modes). `SpellDefinition.IllusionLimits` (quoted SRD/ORC text) from `scripts/spell_perception_overlay.yaml` through the
  generators; a utility cast of such a spell echoes the limits and the disbelief check (5e: Investigation vs DC n; pf2e:
  Perception vs DC n). Tests.
  *As built:* `Data/Impressions/IllusionRules.cs`. Regenerated with `scripts/generate_spells.py` (both APIs reachable): exactly the
  six overlay-flagged files changed, one `illusionLimits` line each (Minor Illusion, Silent Image, Major Image; Figment, Illusory
  Object, Illusory Creature). `Perceptibility.OfSpell` still reads a missing mode as both, defensively; the handler refuses it first.
- [x] **I4. Appeal and Read.** `Appeal` parsing (`RulesetAction.Parameters`: `appeal`, `intensity`, `deceptive`, `reception`;
  `EventOccurred`: typed `appeal`, `appealIntensity`, `deceptive`, `reception`), the D2 inferences, the contest's outcome on
  `ResolverResult` (both systems), `ImpressionResolver.Read`. Tests: sincere lands; a believed illusion; passive Insight
  against a Deception total, both ways; the contested target follows the contest.
  *As built:* `Data/Impressions/Appeal.cs` (tone table, parsing, inference), `ImpressionResolver.Read`. Contested checks now return
  `RollTotal`, `Skill` and `ActorWon` (both systems); `RulesetActionHandler` stashes the total (`engineRollTotal`) and the outcome
  (`engineActorWon`) on the applied action. Side effect handled: a contested Stealth check now has a total, so the concealment step
  treats a lost contest as a failed hide instead of hiding the loser. A bad tone fails the commit in the handler, before the roll.
- [ ] **I5. Appraise, Respond, Persist.** `ImpressionResolver.Appraise/Respond`, `Character.Impressions` (capped 10, 24 h
  habituation window), the dispatcher step after waking, the summary line, the witness line skipping a beat that carries an
  appeal (earshot line kept). Tests: butterflies for a child vs an adult vs a superstitious elder; a bouquet raises the
  relationship; flattery seen through lowers it; the scout alone with low morale flees, and the fourth fright is weaker than
  the first; a believed illusion leaves the relationship alone.
- [ ] **I6. Overrides, disbelief, docs.** `reception` applied; a SkillCheck with `disbelieve` (the illusion's caster id) that
  meets the caster's spell DC turns that observer's impression into seen-through. `DmHelpManual`, `CommitEnumCheatSheet`,
  the skills in `claude_skills/` and the `recommended-system-prompt*.md` files: one rule each. `WITNESS_CHANNEL_PLAN.md` line 155
  ("react as the targets do") corrected.
- [ ] **I7. Verification.** `/verify` once, no parallel builds; every failure reported. A scratch-campaign playtest test: the
  night road, the caravan Far and departing, butterflies for a child, then the ghost against the scout.
