# Witness channel, social spectacle and awareness

Groomed 2026-11-01. W0–W8, S1–S2 and A0–A6 are implemented (2026-11-02).

## The playtest that prompted it

During a night camp, goblin scouts approach. The PC casts Minor Illusion over several turns, then Fireball. The merchants and the rescued captives at the same camp never react to either. The same thing happened in an earlier session: the captives were standing right next to the goblins and could see the same illusions, but only the goblins reacted in the narration. Grok's explanation was that "the engine only advances what is explicitly committed." That is true, but it means the LLM had to remember the bystanders on its own, while busy running combat.

## Findings (what exists today)

1. **Spell casts carry no "perceptible" signal.** `RulesetActionHandler` (`src/CampaignVault/Data/ChangeHandlers/RulesetActionHandler.cs`) publishes no `EventOccurred` and no domain event for a cast. `CoreEvents` (`src/CampaignVault.PluginSdk/Events/DomainEvents.cs:125-209`) has damage, downed, died and combat start/end, but nothing like "something perceptible happened here."
   - Every `take_turn` does log a `SceneCommit` `Event` holding the narrative (`Tools/MutationTools.cs:1188-1201`). So the narration text can reach `RecentSceneEvents`, through the location in `Involved`.
   - Nothing reads it as "the bystanders noticed": only memory-similarity matching uses it.
   - The gap is the missing signal plus the missing consumer, not missing data.
2. **Mid-combat casts go through `take_turn`.** The `combat` tool only does `start` / `next` / `end` / `status` (`Tools/CombatTools.cs:62-65`). Attacks and spells in an encounter are `RulesetAction` changes in a `take_turn` batch, so a context contributor sees them.
   - Combat **start** does not go through `take_turn`, so a contributor can't see it.
   - Downed is a `CoreEvents` publish from `DamageApplication.cs:87`, not a `WorldChange`.
   - Death has a `DeathChange` `WorldChange` (`PluginSdk/Models/WorldChanges.cs:350`).
3. **No initiative driver reacts to the surroundings.** The providers in `src/CampaignVault/Data/Initiative/` (Disposition, ConversationStaleness, Memory, NeedActivityConflict, Relational, SceneMomentum) key off the NPC's own state. None of them fires on "a fireball went off 10 m away."
4. **The initiative slot holds one NPC per turn, and transient NPCs are mostly excluded.** `SceneMomentumInitiativeProvider.cs:25` returns nothing for anyone who is not a companion or keepAlive. Rescued captives are usually transient. A whole-camp reaction can't go through this slot.
5. **The right seam already exists.** `IContextContributor` (`src/CampaignVault/Data/Context/ContextOrchestrator.cs:14`) watches `ContextTurn.AppliedChanges` and emits keyed lines. Convention registration picks contributors up automatically (`AutofacModules/ConventionRegistration.cs:74`). `StealthContextContributor` (`Data/Context/CoreContextContributors.cs:180`) is nearly the template: it queries `Character` by `CurrentLocationId == turn.PartyLocationId` and prints a line about who is there.
6. **Constraints on that seam:**
   - The orchestrator has an 800-char budget (`ContextOrchestrator.CharBudget`). Lines are ranked by priority, and a key is dropped once delivered this session (`TurnCursor.DeliveredContextKeys`). So a witness key must be unique per beat, or the second fireball would be silenced.
   - The witness line also must not fire on every routine beat, or it eats that budget every combat round (see the trigger levels below).
   - `ContextTurn` has no per-commit nonce today. `TurnCursor.WorldSequence` is incremented in `CommitChangesAsync` right after a successful commit (`Tools/MutationTools.cs:1158`), which runs before the response-building block that calls `BriefAsync` (`MutationTools.cs:469`). So it is already bumped when contributors run.
7. **Perception inputs that already exist:**
   - Conditions live in `Character.StatusEffects` (`PluginSdk/Models/Character.cs:431`), each with a `ConditionName`, for example `unconscious` (`Data/HitPointState.cs:56`).
   - Passive Perception comes from `ContextTurnReads.PassivePerception`.
   - The active combat comes from `CampaignRepository.GetActiveCombatAsync` (`Data/CampaignRepository.cs:1337`).
   - Location adjacency is `Location.Exits` (`PluginSdk/Models/Location.cs:31`).
   - Sleep is **not** modelled: `RestChange` is per-character and sets no "asleep" state while the rest runs.
8. **Where spell data comes from.**
   - dnd5e YAML has `school`, `verbal`, `damageType`, `areaOfEffectType`/`Size` (e.g. `RulesetData/dnd5e/spells/fireball.yaml`). `SpellDefinition` is in `Data/Templates/SpellDefinition.cs:40`.
   - PF2e YAML does **not** emit traits or school, though the generator reads AoN `trait` (`scripts/generate_spells.py:786`).
   - **Caveat (unverified):** PF2e `auditory` / `visual` most likely mean "the effect needs the target to hear or see it," not "this effect is noisy or bright." If so, PF2e Fireball has neither. Check AoN for one spell before W3. The derivation has to work from area, damage and the illusion trait in both systems, and `auditory`/`visual` can only add to it.
   - Hand-authored additions go through overlay files in `scripts/` (pattern: `scripts/spell_damage_overlay.yaml`), never by editing `RulesetData/**`.
9. **No prompt guidance on bystanders.** A grep of `claude_skills/` and `recommended-system-prompt*.md` finds no rule about non-combatant reactions.
10. **A crowd-draw roll exists, but only the LLM triggers it.** `SceneInterruptCheck` (`PluginSdk/Models/WorldChanges.cs:171`, fields `LocationId`, `CharacterId`, `RiskModifier`, `Notes`) → `EncounterResolver.EvaluateSceneInterruptAsync` (`Data/EncounterResolver.cs:131`).
    - It rolls once, gets +3% when `Location.AmbientCrowd` is dense (`AmbientCrowdHeuristics.IsCrowdDenseEnough`), and on a hit "promotes ONE figure from the crowd" as a transient NPC with a directive.
    - It is refused during combat (`SceneInterruptChangeHandler.cs`).
    - It already takes a `contextModifier` (each point = 0.5%), which nothing passes from a "loud" beat today.
11. **There is no awareness state on characters.**
    - Stealth results are not stored. Nothing records "hidden from whom, with what total", and `StealthContextContributor` only prints passive Perception values.
    - `invisible`, `blinded`, `deafened`, `unconscious` exist as `StatusEffect` conditions (`RulesetData/dnd5e/conditions/`).
    - "Asleep" and "distracted" do not exist anywhere.
    - So every "who notices?" question is answered by the LLM: the night ambush (the PC always wakes), an NPC sneaking past, an invisible caster, a PC busy haggling while a pickpocket works.

## Design

When a committed beat is **perceptible**, the turn response carries one line naming who else perceived it and what they could perceive:

```
Witnesses (fireball by Maeve, loud+visible): Corren, Ilsa, two traders (awake); Bren (deafened: saw it only). Give each a reaction beat this turn: flinch, flee, cheer, freeze; captives may beg or bolt.
```

- **Trigger levels.** Only *spectacle* fires the line. Ordinary casting is not a spectacle: a verbal component is speech, so Cure Wounds stays quiet.
  - `RulesetAction` of type `Spell` whose spell is `loud`, `visible` or `both` (W2). A spell rated `none` never fires.
  - `RulesetAction` of type `Attack`: fires **once per encounter** (key `witness:combat:{encounterId}`), so non-combatants get "a fight broke out" once, not every round. Outside combat, once per beat.
  - `DeathChange` in the batch (visible). Downed is a `CoreEvents` publish, not a `WorldChange`, so it isn't a trigger in v1.
  - `EventOccurred` with a new optional `perceptible` field (`loud | visible | both`). This is the LLM's escape hatch for a shout, a scream or a collapsing wall.
  - Combat **start** is out: it runs through the `combat` tool, which has no briefing. The first attack or spell inside the encounter covers it.
- **Witnesses:** characters whose `CurrentLocationId` equals **the actor's** location (not `PartyLocationId`, which is wrong when the party is split), minus:
  - the actor and the action's `TargetIds`;
  - every combatant in the active encounter, who already get their own beats;
  - PCs, since the player decides what PCs do.

  Party companions stay in. Order: keepAlive/companions first, then by name. Cap at 6 names plus "+k others".
- **Perception filter:**
  - `unconscious`, `petrified` or `paralyzed`: excluded. Exception: a loud trigger wakes the unconscious-by-sleep case, but sleep isn't modelled, so unconscious stays excluded.
  - `blinded`: perceives `loud` only.
  - `deafened`: perceives `visible` only.

  The condition list lives in code (see Data note).
- **Delivery:**
  - Priority 90, above stealth (70) and slots (65).
  - Key `witness:{WorldSequence}:{location}`, so it fires every beat it applies.
  - One line per location per beat, merging every perceptible trigger in that beat.
- **What it is not:** it does not create events, memories or moods. It tells the narrator who is there and what they noticed. Writing durable memories for witnesses is the `knownBy` workstream (`KNOWN_BY_AND_CI_PLAN.md`, K1 derives an audience from "characters present at `LocationId`") and should reuse the same witness selection (W6).

**Data note (per data-oriented-rules).** Which spells are perceptible is **data**: a field emitted by the generators, with hand overrides in a `scripts/` overlay. Two things stay in **code**, with a short comment saying why:
- the mapping from condition to sense (it's a core rule shared by every creature);
- the fallback derivation used for spells generated before the field existed.

## Phases

Each phase ships on its own with a green suite. Check every box before calling it done.

- [x] **W0. Failing repro test.** Unit test in `tests/CampaignVault.UnitTests/` (model on `SpeedContextTests.cs`). Scratch campaign: PC, two non-combat NPCs and a goblin at one location; active combat containing only the PC and the goblin; a `take_turn` commit of `RulesetAction{ActionType=Spell, ActionName="fireball"}`. It asserts that `Context` contains a `Witnesses` line naming both NPCs and not the goblin. A second test covers a second fireball in the next beat, which must produce the line again.
  *Done when:* both tests fail for the right reason on master.

- [x] **W1. `WitnessContextContributor`.** Add it to `Data/Context/CoreContextContributors.cs`, or to a new `Data/Context/WitnessContextContributor.cs` if it outgrows 120 lines.
  - Add `WorldSequence` (long) to `ContextTurn`, set from `ctx.Cursor.WorldSequence` in `MutationTools.Briefing.cs:111`.
  - Triggers in W1: `Attack` (once per encounter), `DeathChange`, and `Spell` using the code fallback derivation from W2 (inline at first).
  - Witness query: same shape as `StealthContextContributor` (Raven query on `CampaignName` + `CurrentLocationId`, `Take(20)`), but keyed on the actor's `CurrentLocationId`, plus a single `GetActiveCombatAsync` call to exclude combatants.
  - Condition filter on `StatusEffects`.

  *Done when:* W0 passes, and existing context tests are unchanged.

- [x] **W2. Spell perceptibility as data.**
  - Add `public string? Perceptible { get; init; }` (`loud`, `visible`, `both`, `none`) to `SpellDefinition`, and include it in the merge at `SpellDefinition.cs:~204`.
  - Resolver: if the field is set, use it. Otherwise fall back:
    - an illusion school, or an area damage spell, is visible;
    - thunder damage, or an area damage spell, is loud;
    - anything else is `none`. The `verbal` component is deliberately **not** a trigger: it is speech, not a spectacle.
  - Comment the fallback: "kept until both spell sets are regenerated; delete after W3."
  - Caster-chosen modes (decision D1): a spell whose YAML has `perceptibleModes` (e.g. Minor Illusion: `image: visible`, `sound: loud`) reads `RulesetAction.Parameters["mode"]`. If `mode` is missing or unknown, it is `both`, so witnesses are never silently dropped. The `[Description]` on `Parameters` mentions `mode` for such spells.
  - Unit tests: fireball → both; minor illusion with `mode=image` → visible, with `mode=sound` → loud, with no mode → both; cure wounds → none.

  *Done when:* the resolver tests are green and W1 calls the resolver.

- [x] **W3. Generators emit `perceptible`.**
  - `scripts/generate_spells.py`: both systems derive the field with the same rule as the fallback (area, damage, illusion school or trait). For PF2e, `auditory` / `visual` traits only add to that, and only if the finding 8 check shows they mean what's emitted rather than what the target must perceive.
  - New overlay `scripts/spell_perception_overlay.yaml` for exceptions. Same evidentiary standard as the damage overlay: every entry quotes SRD/ORC text. Likely entries: Silent Image (visible only), Thunderwave (both: "audible out to 300 feet"), Minor Illusion.
  - Regenerate both spell sets. This **needs network access** to `www.dnd5eapi.co` and `elasticsearch.aonprd.com`. If the session is offline, stop and hand that step to Michael.
  - Delete the W2 fallback once every spell has the field.

  *Done when:* the diff shows only added `perceptible:` lines plus overlay-driven values, and a test asserts every loaded spell has a non-null `Perceptible`.

  - **As built, 2026-11-02:**
    - All 692 spells now carry `perceptible`. The regen diff was perceptible lines only, with no API drift.
      - dnd5e: 58 both, 2 visible, 259 none.
      - pf2e: 44 both, 6 loud, 9 visible, 314 none.
    - The overlays are `scripts/spell_perception_overlay{,_pf2e}.yaml`:
      - Minor Illusion and Figment take modes;
      - Silent Image and Disguise Self are visible only.
    - The finding 8 caveat is resolved. On illusions, AoN's `visual`/`auditory` traits name the illusion's form (Illusory Object is visual, Ventriloquism auditory, Figment has neither), so they are used there and ignored elsewhere.
    - Deviation: the C# derivation is **kept** rather than deleted, because homebrew and plugin spells have no generated field. Its comment says so.
    - Known gap: non-damaging light/darkness spectacles (Daylight, Faerie Fire, Darkness) derive `none`. Add them to the overlay with quotes when it matters.

- [x] **W4. `EventOccurred.perceptible`.**
  - Add the optional field in `PluginSdk/Models/WorldChanges.cs:557`, with a `[Description]` telling the LLM to set it on shouts, screams, crashes and spectacles.
  - Add it to the commit enum cheat sheet (`Tools/CommitEnumCheatSheet.cs`) and to the MCP schema (`Schema/McpSchemaInstaller.cs`).
  - W1 treats it as a trigger.

  *Done when:* a test commits `EventOccurred{perceptible: loud}` with no spell and gets a witness line. The schema snapshot tests are updated.

- **As built (W0–W2, W4), 2026-11-02:**
  - The code is in `src/CampaignVault/Data/Context/WitnessContextContributor.cs`: the `Perceptible` enum, `Perceptibility` (the spell resolver) and the contributor. The tests are in `tests/CampaignVault.UnitTests/WitnessContextTests.cs` (14 tests).
  - The line is `Witnesses (<cause>, <senses>): <names>. Give each a short reaction in this response, even mid-combat.` It has priority 90 and the key `witness:{WorldSequence}:{location}`; fight noise uses `witness:combat:{location}:{combatant ids}`, so it's said once per fight.
  - Triggers: a perceptible spell, an attack (fight noise), `DeathChange`, and `EventOccurred.perceptible`. Downed and combat start are not triggers (see finding 2).
  - Gaps that are still open:
    - Without `perceptible` data, the spell derivation makes PF2e spells mostly `none`. PF2e YAML has no damage, area or school; W3 fixes that.
    - No spell YAML carries `perceptible` / `perceptibleModes` yet (W3). Minor Illusion gets D1's behaviour from the illusion-school fallback instead.
    - The witness query is capped at 25 characters per location.

- [x] **W5. Earshot beyond the location (loud only).** For `loud` triggers, characters at locations one exit away (`Location.Exits`) get a second, shorter line: `Heard from the camp: the goblin scout at the ridge`. The second line has priority 75 and the same key scheme with a `:near` suffix.
  *Done when:* a test with an adjacent location's NPC passes, and visible-only triggers don't reach neighbours.
  *As built:* "one exit away" is capped at `WitnessSelector.EarshotMaxTravelHours` (0.25 h): an exit costing an hour leads somewhere out of earshot. Exits are kept reciprocal (`LocationConnectivity`), so only the beat location's own exits are read. Distance muffles: neighbours hear but don't see, so the line names the noise (`fireball`, `fighting`), not who made it, and a sleeper next door sleeps through it (the dispatcher's waking stays same-location). Line: `Heard from Roadside Camp (fireball): Bram at the Ridge. They hear it but can't see it; …`.

- [x] **W6. Shared witness selection for knownBy.** Pull the selection and filter out into an `IWitnessSelector` (in `Data/Witness/`), used by the contributor. Note in `KNOWN_BY_AND_CI_PLAN.md` K1 that the audience default should call it.
  *Done when:* the contributor uses the selector and its tests pass, with no behaviour change.
  *As built:* `IWitnessSelector` (`AtAsync`, `InEarshotAsync`) returns `WitnessNotice(Character, Notice)` rows; registered single-instance. The record isn't called `Witness`, which would clash with the `CampaignVault.Data.Witness` namespace. The dispatcher's waking query and A5's occupant lookup still query on their own: they need sleepers and characters tracked in the batch, which the selector's indexed query can't see.

- [x] **W7. Prompt and docs.**
  - `claude_skills/dnd-combat/SKILL.md` and `recommended-system-prompt*.md`: one rule. "A `Witnesses` line means those people perceived the beat; give each a short reaction in the same response, even mid-combat. Captives and non-combatants react to illusions as the targets do unless they have reason to know it's an illusion."
  - Mention `perceptible` on events.
  - Add a short paragraph to `Tools/DmHelpManual.cs` next to the `npc_initiative_nudge` FAQ (line ~81) explaining that the witness line is separate from the one-NPC initiative slot.

  *Done when:* the docs are updated and the prompt snapshot tests (if any) are refreshed.
  *As built:* the rule is in all three prompt variants (with `awareness_change` added to the $type vocabulary), a paragraph in `dnd-combat`, a paragraph after the `npc_initiative_nudge` FAQ in `DmHelpManual`, and a K1 note in `KNOWN_BY_AND_CI_PLAN.md`. No snapshot tests exist; the guidance-corpus tests pass.

- [x] **W8. Verification.** Run the full suite once with `/verify`, with no parallel builds. Report every failure; the two `LocalEmbeddingServiceTests` count as environmental if git-lfs is missing. Then do a scratch-campaign playtest (SFW, empty DB, per measure-on-scratch-campaigns): a camp with traders, Minor Illusion then Fireball. Check that the turn response carries the witness line both times.
  *As built:* the playtest is a test, `Playtest_the_illusion_then_the_fireball_both_reach_the_camp`, on a scratch campaign in the embedded (empty) RavenDB: traders Corren and Ilsa at the camp, lookout Bram on the ridge. Minor Illusion (sound) gives `Witnesses (minor illusion by Maeve, loud): Corren, Ilsa` and `Heard from Roadside Camp (minor illusion): Bram at the Ridge`; Fireball gives the same pair with `loud+visible`. A live LLM playtest against the MCP server is still Michael's call.

## Decisions (Michael, 2026-11-01)

- **D1. Caster-chosen modes.** Minor Illusion and similar spells take `parameters.mode`. If it's missing, the spell counts as `both` (W2).
- **D2. Witness line only, for now.** No Witness initiative driver. Revisit after the W8 playtest if reactions stay flat.
- **D3. Combat pace.** One line when the fight starts (first attack), then one per spectacle spell or death. The fight and its spectacles are only part of it, though. Social noise (performances, loud arguments, haggling) should also be perceptible and can **draw** people in, not just get noticed. That is Part S.
- **D4. Generalize perception, and model sleep.** Being asleep is one case of "who notices what". Others are a distracted PC failing a passive check, an NPC sneaking around, and an invisible caster. All of them should run through one awareness model (Part A). Sleep is modelled there, and the night-ambush fix is a consumer of that model, not a separate plan.
- **D5. The engine rolls the crowd draw** on `draw` beats (S2), and the LLM can buff or debuff that roll with a `crowdModifier` (see S2). The LLM can still call `scene_interrupt_check` on its own.

- **D6. Sleep in 5e mirrors Player Core (house rule).** A sleeper can't see and has passive Perception −5 (disadvantage). Loud signals wake them; a quiet intruder wakes them only if that passive score meets the intruder's Stealth. PF2e uses the Player Core rule as written: unconscious, −4 Perception, blinded, and Perception vs the noise DC or the quiet creatures' Stealth DCs. Engine simplification for both systems: a loud signal wakes automatically, since the engine has no noise DCs.
- **D7. Distracted is implied and explicit.** Absorbing actions (haggling or conversation, Performance, Investigation/Search and lore checks, or `parameters.absorbed: true`) make the actor distracted for that beat. The LLM can also set or clear it with `awareness_change`. Distracted means passive Perception −5.

## Part S: social spectacle and crowd draw

Some beats are not only perceived: they **pull attention**. Examples: a song in a tavern, a shouting match with a companion, loud haggling at a market stall. In a crowded place someone should come over, and the engine should make that happen, not leave it to the LLM's memory.

- [x] **S1. Attention level on perceptible beats.**
  - Add `attention: notice | draw` next to `perceptible` on `EventOccurred` (W4) and, as `Parameters["attention"]`, on any `RulesetAction`.
  - `notice` (the default) produces only the witness line. `draw` also feeds S2.
  - A `SkillCheck` on Performance defaults to `perceptible: both, attention: draw`. That mapping lives in code: check first whether skills have a data file under `RulesetData/`; if they do, put it there.
  - The `[Description]` text tells the LLM to set `loud` + `draw` on shouting matches, public scenes and loud haggling.
  - The witness line wording carries the beat: `Witnesses (raised voices, Maeve and Corren): two traders hear it`.

  *Done when:* tests cover a Performance check giving a `draw` witness line, and an `EventOccurred{perceptible: loud, attention: draw}` doing the same.

- [x] **S2. Engine-run crowd draw.**
  - On a `draw` beat, outside combat, at a location with an `AmbientCrowd`, the contributor's sibling runs `EncounterResolver.EvaluateSceneInterruptAsync` itself. Alternative: a post-commit hook staging a `SceneInterruptCheck` (pick whichever stays inside the commit, so deltas persist).
    - `contextModifier`: +20 for draw, +10 for a loud `notice` beat in a dense crowd.
    - LLM buff/debuff: an optional `crowdModifier` (−50..+50, the same 0.5%-per-point scale as `riskModifier`) on the triggering `EventOccurred` or `RulesetAction.Parameters` is added to the engine's modifier. Examples: festival night +20, a crowd hanging on the bard's every note +15, dawn in a sleepy hamlet −30, a guard-patrolled square −10. Clamp to the range and echo the applied total in the result, so the LLM sees what it changed. Leaving it out means the engine's own default only.
    - The final chance keeps `EvaluateSceneInterruptAsync`'s existing clamp (1–75%). So a debuff can't zero the draw, and a buff can't guarantee it.
    - `notes` hold the cause and outcome, e.g. `drawn by Maeve's song (Performance 18 vs DC 15: well received)`.
  - Extend the directive so the promoted figure fits the outcome: a good performance draws admirers, tippers or a rival bard; a bad one draws hecklers; a loud quarrel draws a busybody, the guard or a pickpocket using the distraction.
  - Cooldown: at most one engine crowd draw per location per N beats (config, default 3), so a long performance doesn't spawn a queue.
  - The LLM can still call `scene_interrupt_check` itself whenever it wants.
  - Decision D5: the engine rolls it automatically. The roll is a chance, not a certainty, and leaving it to the LLM is the same "remember to do it" failure this plan exists to fix. The witness line says whether a figure was drawn, so the narration matches.

  *Done when:* there are tests with a fixed RNG. A draw at a crowded location spawns a transient figure carrying the cause in its notes. No draw fires during combat or inside the cooldown, and an empty location never draws. `crowdModifier` shifts the chance and is clamped, and the applied total is echoed.

- **As built (S1–S2), 2026-11-02:**
  - The rules are in `src/CampaignVault/Data/CrowdDraw.cs`; the roll is `WorldChangeDispatcher.ApplyCrowdDrawAsync`, which runs inside the commit just before the ambient interrupt check. When it rolls, the ambient check is skipped for that beat.
  - Fields:
    - `EventOccurred.attention` / `crowdModifier`;
    - `RulesetAction.parameters.perceptible` / `attention` / `crowdModifier`;
    - `Location.LastCrowdDrawHour`;
    - `CampaignConfig.CrowdDrawCooldownMinutes` (default 60).
  - Tests: `tests/CampaignVault.UnitTests/CrowdDrawTests.cs` (7).
  - Engine messages: `CROWD DRAW at <loc> (modifier +20 engine +15 yours = +35)! ...`, `... nobody comes over this time.`, or `Crowd draw at <loc> not rolled: the last one was N min ago`.
  - Deviation 1: the cooldown is **in-game minutes**, not beats. The dispatcher has no beat counter, and game time also stops spam across many quick commits.
  - Deviation 2: the engine can't see a check's outcome (`RulesetAction` stores no result). So the directive tells the narrator to fit the figure to "this beat's check result" instead of the engine choosing admirers or hecklers.
  - Not covered by a test: the combat gate (`ActiveCombat != null` returns early, same as the ambient check).

## Part A: awareness model (who notices what)

One resolver answers "does observer O perceive signal S from source X?" Every consumer calls it: the witness line, the ambush on a rest interrupt, NPCs sneaking, pickpocketing, invisible casters. It replaces the condition filter in W1.

The concepts mirror PF2e's observed / hidden / undetected / unnoticed (Player Core, ORC) and map onto 5e's hiding, passive Perception and conditions (SRD 5.1). Every rule cites SRD or ORC text. Anything that is a house rule (sleep depth, how a distracted character's attention drops) is labelled as one and set in `CampaignConfig`, so the free-rules boundary stays visible.

- [x] **A0. Rules audit (read-only).** Write a table of the exact SRD 5.1 / Player Core text for:
  - passive checks (advantage +5 / disadvantage −5);
  - hiding (Stealth vs Perception; you can't hide from someone who sees you clearly);
  - invisible (heavily obscured for hiding; noise and tracks still give you away);
  - unconscious ("unaware of its surroundings"), blinded, deafened;
  - PF2e detection states and the unnoticed condition;
  - whatever SRD 5.1 says about distraction while travelling. If it says nothing, it's a house rule.

  Sleep has no SRD perception rule as far as I know; confirm, and if so it's a house rule.
  *Output:* a short table in this file. It decides which A-rules are cited and which are house rules.

  **A0 result (2026-11-02).** Sources: SRD 5.1 via `www.dnd5eapi.co/api/rules` and `/api/conditions`; Player Core via AoN (`category: condition`). Quotes are trimmed to the operative clause.

  | Topic | dnd5e (SRD 5.1) | pf2e (Player Core) | Engine use |
  |---|---|---|---|
  | Passive check | "If the character has advantage on the check, add 5. For disadvantage, subtract 5." (Passive Checks) | Perception DC = 10 + Perception modifier | Rule. Disadvantage on a passive score = −5. |
  | Hiding vs noticing | Stealth check "compares … with that creature's passive Wisdom (Perception) score" (Dexterity › Hiding) | Hide makes you hidden; unnoticed means "no idea you are present at all" | Rule. Concealment total vs each observer's passive Perception. |
  | Noise breaks hiding | "you give away your position if you make noise, such as shouting a warning" (Hiding) | n/a (Sneak/Hide fail on noise is GM call) | Rule. A loud action or attack by the hidden source clears concealment. |
  | Attack reveals | "If you are hidden … when you make an attack, you give away your location" (Unseen Attackers) | Hidden/undetected end when you act openly | Rule. |
  | Can't hide in plain view | "You can't hide from a creature that can see you clearly" | Observed: "Anything in plain view is observed by you." | Rule. Concealment needs cover, darkness or invisibility (the LLM's call when it commits the Stealth check). |
  | Invisible | "impossible to see … For the purpose of hiding, the creature is heavily obscured. The creature's location can be detected by any noise it makes" | Invisible: undetected by sight; noise still gives you away (hidden) | Rule. Invisible hides the visual channel only. |
  | Blinded / deafened | "can't see" / "can't hear" | same | Rule. Already in W1. |
  | Unconscious | "unaware of its surroundings" | "You're sleeping or have been knocked out … –4 status penalty to … Perception … blinded" | Rule for knocked-out characters: perceives nothing. |
  | Sleep | **No SRD rule.** (The Sleep spell applies unconscious; natural sleep is unstated.) | Sleep **is** unconscious. Wakes on damage, healing, being shaken (Interact), or "Loud noise … you automatically attempt a Perception check against the noise's DC … If creatures are attempting to stay quiet around you, this Perception check uses their Stealth DCs." | pf2e: rule as written. dnd5e: **house rule** needed (see A-decisions). |
  | Distracted | Only GM discretion: "the GM might allow you to stay hidden as you approach a creature that is distracted"; travel at a fast pace "makes characters less perceptive" (−5 passive Perception in the Travel Pace table) | No general rule; specific activities (e.g. exploration activities) | **House rule**: distracted = disadvantage on passive Perception = −5, by the SRD's own passive-check rule and its fast-pace precedent. |
  | Surprise | "compares the Dexterity (Stealth) checks of anyone hiding with the passive Wisdom (Perception) score of each creature … Any character or monster that doesn't notice a threat is surprised" | No surprise round; Stealth for initiative | Rule (dnd5e) for A5. |
  | Standing watch | Long rest allows "standing watch for no more than 2 hours" | Watches are normal during rest | Rule: a watcher is awake/alert; rotation detail is the LLM's. |

- [x] **A1. Observer awareness state.**
  - Add `Awareness { State: Alert | Distracted | Asleep, Reason, SinceTurn }` on `Character` (`PluginSdk/Models/Character.cs`). Default `Alert`, and a document missing the field reads as Alert, so no migration is needed.
  - New change `awareness_change` (characterId, state, reason). Add it to the schema and cheat sheet.
  - `RestChange` sets `Asleep` for the resting characters (all of them, minus `watcherIds`, see A5) while the rest runs. A completed rest sets `Alert`. An interrupted rest leaves sleepers asleep until A3 says a signal wakes them.
  - Distracted is set by the LLM, or implied for the turn by an absorbing activity commit (haggling, crafting, performing) through `Parameters["absorbed"]`. It is cleared the next beat.

  *Done when:* there are tests for each transition, and for an old document deserializing as Alert.

- [x] **A2. Source concealment.**
  - A `SkillCheck` on Stealth with a total stores `Concealment { StealthTotal, Mode: Hidden, SinceTurn }` on the actor.
  - The `invisible` condition counts as concealment for the visual channel only.
  - Concealment is cleared by an attack or a spell with a verbal component (5e: hiding ends when you make noise or attack), by an explicit change, or when the LLM moves the character into the open (`Parameters["revealed"]`).
  - Keep it a single value on the source, not a table of observer/source pairs. It's computed per query.

  *Done when:* there are tests for set, clear on attack, and invisibility hiding sight but not sound.

- [x] **A3. `IAwarenessResolver`** in `Data/Awareness/`.
  - Input: observer, source, signal (`visible`/`loud` plus an optional `quiet` for footsteps or whispers).
  - Output: `Perceives | PerceivesEffectNotSource | Misses | Wakes`, with a short reason.
  - The rules are the A0 table:
    - blinded → no visual; deafened → no audio;
    - unconscious/petrified/paralyzed → nothing;
    - asleep → misses visual and quiet, wakes on loud (house rule);
    - distracted → passive Perception −5 against quiet or hidden signals;
    - concealment → passive Perception against the stealth total;
    - invisible source of a visible effect → `PerceivesEffectNotSource` (everyone sees the fireball, nobody sees who cast it).
  - These are core rules shared by every creature, so they stay in code (with a comment saying so). The house-rule numbers come from `CampaignConfig`.

  *Done when:* a table-driven test covers each row of A0.

- [x] **A4. Witness line on the resolver.** W1's condition filter is replaced with A3.
  - Wording gains the outcomes: `Bren (asleep) wakes to the blast`, `the traders see the fire but not who cast it`.
  - A loud beat that wakes someone also commits `awareness_change → Alert` for them, so the next turn is consistent.

  *Done when:* the W0 tests pass unchanged, plus new asleep/invisible cases.

- [x] **A5. Night ambush noticing (the original "PC always wakes up" bug).**
  - Add `RestChange.watcherIds` (on watch, so Alert, not asleep).
  - On a rest interrupt, the resolver checks the approaching encounter (a stealth total rolled by the engine, or a quiet-approach default when the encounter has no stat block yet) against each watcher's passive Perception and each sleeper's state.
  - The interrupt result and directive list: who notices and when, who is still asleep, and suggested `surprisedIds` for the `combat start` call.
  - The directive stops implying the PC is the one who notices.
  - `EncounterResolver.EvaluateAsync` (`Data/EncounterResolver.cs:27`) gets the party or campaign sleepers. Today it only receives the one resting character.

  *Done when:* there are tests with a fixed RNG. With no watchers, sleepers are reported asleep and suggested as surprised. A watcher with high passive Perception notices, and the PC is not singled out.

- [x] **A6. Sneaking and sleight of hand against PCs.**
  - When an NPC's commit carries concealment (A2), or a Sleight of Hand check, the response gets a per-observer line, PCs included:
    `Maeve (distracted, passive Perception 13−5) does not notice; Corren (passive Perception 15) does`.
  - The LLM narrates only what the PC perceived.
  - This turns "the PC fails a passive check" into an engine fact instead of the narrator's whim.

  *Done when:* there are tests for a distracted PC missing it, an alert companion catching it, and an invisible NPC's footsteps heard but not seen.

- **As built (A1–A6), 2026-11-02**, on branch `claude/awareness-model` (stacked on PR #28):
  - **Models.** `Awareness` / `Concealment` in `PluginSdk/Models/Awareness.cs`. A null `Character.Awareness` means alert.
  - **`awareness_change`.** Implemented in `AwarenessChangeHandler`: state alert/distracted/asleep, and `revealed` ends hiding.
  - **Rules.** `AwarenessResolver` is a static class in `Data/Context/AwarenessResolver.cs`.
    - Deviation: the house-rule numbers (−5, pf2e −4) are constants there, not `CampaignConfig`. They are the SRD's disadvantage value and Player Core's unconscious penalty, so there was nothing to configure.
  - **Shared beat detection.** `PerceptibleBeats` (`Data/Context/PerceptibleBeats.cs`) is used by both the witness line and the dispatcher.
  - **A1, sleep from rest.** An interrupted long rest (not short, intended ≥ 6 h) sets asleep for the rester, the party companions here and `sleeperIds`, minus `watcherIds`. A completed rest clears it.
    - Deviation: sleep isn't modelled *during* a rest that completes, since it completes inside one commit.
  - **A2, hiding.** A Stealth check stores `Concealment.StealthTotal`. It is cleared by an attack, a spell with a verbal component, a loud or attention-drawing action, or `revealed`.
    - Not done: automatically revealing someone who walks into the open.
  - **A4, waking.** `WorldChangeDispatcher.ApplyWakingAsync` wakes sleepers who can hear a loud beat, inside the commit, and reports `WOKEN by …`.
  - **A5, noticing an approach.** On a rest interrupt the approach's Stealth is d20 + 2 (`EncounterResolver.RollApproachStealth`), and every occupant is checked against it.
    - The result lists who noticed, who woke, and who didn't (5e: suggested `surprisedIds`; pf2e: a no-surprise note).
    - The occupant lookup is advisory: a failure falls back to the commit's characters.
    - Known gap: hostiles who were already at the location are listed as observers too.
  - **A6, who notices a sneak.** `NoticingContextContributor` replaced `StealthContextContributor`. It handles Stealth (via concealment) and Sleight of Hand; the total is recorded on the action as `parameters.engineRollTotal` by `RulesetActionHandler`. The line lists who notices and who doesn't, PCs included, with the D7 absorption (haggling, searching) applied.
  - **Tests:**
    - `AwarenessResolverTests` (13), one row per line of the A0 table;
    - `WitnessContextTests` gained the waking, silent image, rest, hiding, `awareness_change`, A5 and A6 cases;
    - `TakeTurnNeedDrivenTests`' stealth test now asserts the new line.

## Suggested order

1. **W0–W2, W4:** the witness line ships with a simple condition filter. This fixes the reported playtest.
2. **S1–S2:** social spectacle and crowd draw, reusing the same triggers.
3. **A0 → A1–A3 → A4:** the awareness model replaces the filter.
4. **A5:** the night ambush fix.
5. **A6:** sneaking and sleight of hand.
6. **W3:** generator regeneration, which needs network access; it can run any time after W2.
7. **W5–W7:** earshot beyond the location, the shared selector, prompts.
8. **W8:** verification, and again at the end of each part.

Each step is a green-suite gate. W6's `IWitnessSelector` should be built on A3 if A lands first.

## Still open

1. **House-rule numbers** (A0 decides which are needed): does a sleeper wake only on loud, or also on a failed passive check against a quiet approach? What is the default crowd-draw cooldown?
2. **A1, distracted:** should it be implied by absorbing activities (my lean), or always set explicitly by the LLM?
