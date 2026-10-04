# Mechanics remediation plan

Source: a read-only code review of the rules engine (2026-10-04). Nothing was built or run for it. Every ticket cites
`file:line` as read at the time; re-check lines before editing, because they drift. Citations from the Serena symbol
tools were shifted from 0-based to 1-based; a few cite a method with no line number on purpose.

Read this first:

- **Scope.** The core combat, effect, rest, level-up, status and PF2e paths were read in depth. The areas marked
  "skimmed" in §9 were not.
- **Labels.** Each item is a **BUG**, a **GAP** (undocumented, absent) or a **DEFERRED** item that `RULES_GAPS.md`
  already lists. `RULES_GAPS.md` gets updated in M-00 and M-91.
- **Constraints from `CLAUDE.md` and the project memory.**
  - One build at a time, never in the background. Run the full suite before any ticket is called done and report
    failures, including pre-existing ones. The two `LocalEmbeddingServiceTests` failures are the known git-lfs gap.
  - `dotnet test` exits 5 here: build once, then run the xunit v3 executables directly.
  - Free rules only. Ship SRD 5.1 and Player Core content; War Caster and Resilient are PHB feats, so core exposes a
    hook and does not ship them. Evasion (SRD, rogue and monk level 7) is allowed.
  - Data-oriented rules. Rules go in YAML or generator data where reasonable, and each ticket says why anything stays
    code.
  - `RulesetData/**` content that comes from `scripts/` is regenerated, not hand-edited. Spell and item tweaks go
    through `scripts/spell_damage_overlay.yaml` or `scripts/item_overlay.yaml`.
  - Condition YAMLs are hand-authored, because no generator covers them (checked against `scripts/`). Edit them
    directly.
  - Prefer query-layer filtering over `[JsonIgnore]` for DTO changes. Don't use C# keywords as identifiers, and
    watch nullable annotations and shadowing.
- **Ticket template.** Problem, evidence, fix (with the data-vs-code reason), tests (file and cases), compatibility
  and migration, acceptance.

---

## 1. Phase list (check each off before declaring the plan done)

| # | Phase | Why this order |
|---|---|---|
| 0 | Safety net and decision log | Pin current behavior, record gaps, get Michael's calls before building on guesses. |
| 1 | Independent quick fixes | Small, no shared design, immediate wins. |
| 2 | One damage pipeline | Collapses six separate bugs (save mitigation, stacked resistance, tick bypass, rider and crit failures, roll-once, evasion). |
| 3 | Condition mechanics as data | Collapses the "text-only conditions" family. Phases 4, 6 and 8 consume it. |
| 4 | Turn flow, dying, concentration | Needs phases 2 and 3. |
| 5 | Level-up and multiclass | Independent of 2 and 3, apart from AC recompute. |
| 6 | PF2e correctness | Partly needs phases 2 and 3. |
| 7 | Spellcasting bookkeeping | Warn first, enforce later. |
| 8 | Rest and survival | Needs the rest decision (D1) and the condition model. |
| 9 | Range and small rules | Lowest value. |
| 10 | Client, prompts, docs | Last, so it describes what exists. |
| 11 | Verification gate | Full suite, report, confirm. |

Dependency sketch: `0 → 1`, `0 → 2 → 4`, `0 → 3 → {4, 6, 8}`, `5` is independent apart from M-44 (needs M-05),
`7` needs M-60 before M-61 and M-62, `10` and `11` come last.

---

## 2. Findings registry (what this plan fixes)

| ID | Label | Finding | Ticket |
|---|---|---|---|
| F1 | BUG | Dying PCs are skipped by `NextTurn`, so the "roll death saves each turn" prompt can't fire from turn flow | M-30 |
| F2 | BUG | Delayed tick damage writes HP directly, bypassing dying rules, massive damage and resistance | M-11, M-13 |
| F3 | BUG | `level_up` with `levelsGained > 1` grants one level of HP | M-40 |
| F4 | BUG | `level_up` with a class not on the sheet is resolved three different ways: the planner uses the first class, the class-level sync uses the last class, and the HP step uses the named class's hit die | M-41 |
| F5 | BUG | Docs say "`rest` already heals", but rest never restores HP and there are no hit dice | M-01, M-70 |
| F6 | GAP | Conditions are text only: no advantage, disadvantage, auto-fail, speed or crits | M-20 to M-23 |
| F7 | GAP | `Immunities` and `Suppresses` on conditions are parsed and merged but never read (so, for example, PF2e `stunned` suppressing a `slowed` condition that doesn't exist does nothing) | M-24 |
| F8 | GAP | Exhaustion exists in two disconnected forms (status "Exhaustion N" vs attribute `exhaustion_level`) with no mechanics, and no death at level 6 | M-25 |
| F9 | BUG | Save-based damage (5e single and area, 5e pools, PF2e basic saves) ignores resistances, immunities and feat resistance | M-10, M-11 |
| F10 | BUG | Stat resistance and feat resistance both halve the same hit | M-10 |
| F11 | BUG | Area save damage is rolled per target instead of once per cast | M-12 |
| F12 | BUG | Crit re-rolls `damageDice` including an inline `+N`, doubling the flat bonus | M-02 |
| F13 | BUG | Rider damage is a second `HpChange`: an extra death-save failure and a second concentration check at 0 HP; a crit at 0 HP doesn't count double | M-13 |
| F14 | BUG | PF2e spell attacks use Str/Dex plus weapon proficiency; there is no spell attack stat | M-50 |
| F15 | BUG | PF2e skill, grapple, escape, contested and spell-save rolls ignore advantage and disadvantage from the pipeline | M-03 |
| F16 | BUG | PF2e `actionCost: 0` is forced to 1; agile MAP and spell-attack MAP aren't handled | M-04, M-51 |
| F17 | BUG | Equip, unequip and transfer recompute AC as `10 + dex + armor`, dropping Unarmored Defense and stat-block AC | M-05 |
| F18 | GAP | Temporary hit points don't exist | M-15 (D3) |
| F19 | GAP | A spell action neither spends a slot nor checks known or prepared spells; concentration is never applied by a cast; nothing flags an unpaired spend | M-60 to M-62 |
| F20 | BUG | Concentration isn't broken automatically at 0 HP, and its name match is a loose `Contains("Concentration")` | M-08, M-32 |
| F21 | GAP | PF2e has no dying or wounded handling, and the 5e concentration-on-damage save also applies to PF2e | M-52 (D2) |
| F22 | BUG | Long-rest exhaustion decrement ignores food and drink; deprivation and rest interact badly | M-71 |
| F23 | BUG | No level cap in the level-up handler (the UI status reader and recipe validation cap at 20) | M-07 |
| F24 | BUG | 5e and PF2e initiative discard resolved advantage (`.Bonus` only) | M-06 |
| F25 | GAP | Grapple applies only an engagement relation: no Grappled condition, and it never ends when the grappler is incapacitated or moves away | M-27 |
| F26 | GAP | Speed is floored at 5 ft, so "speed 0" conditions can't be expressed | M-26 |
| F27 | BUG | Contested checks ignore a supplied `bonus` | M-06 |
| F28 | GAP | Range checks only a maximum band: no ranged-in-melee disadvantage or long-range penalty; `aoeRadius` replaces `range` | M-80 |
| F29 | GAP | Evasion isn't modelled | M-14 |
| F30 | GAP | Unity client shows no death saves or conditions | M-90 |
| F31 | BUG | Reactions reset at round start instead of at the combatant's own turn start | M-81 |

Reviewed and found fine, with no ticket: the batch is atomic (a failed action rolls back, so slots aren't burned);
disadvantage crit detection uses the kept die; actor incapacitation is gated for attacks, spells and item use in
`WorldChangeDispatcher` (about line 958); minion lapse and revive paths; rest pool recovery and its idempotence;
travel time uses `SpeedRules.TravelStretch` (`TravelChangeHandler.cs:571`); PF2e HP gain on level-up multiplies by
`LevelsGained` correctly (`Pf2eDeriveHitPointsStep.cs`, `ApplyLevelGainAsync`), so F3 is 5e only.

Low confidence, grep-level only (not "found fine"): pools preserved across level-up re-sync
(`ResourcePoolInitializer`) and multiclass caster level (`Dnd5eCasterLevelHelper`, which has tests).

Already documented in `RULES_GAPS.md` (DEFERRED, not new): PF2e takes numeric effect kinds only; subclass features
that are rule text only (Rage, Cutting Words); expertise, favored enemy and the Land druid cantrip.

---

## 3. Phase 0: safety net and decisions

### M-00 Baseline, characterization and registry
- **Problem.** Several fixes change existing behavior. Some existing tests may assert the buggy behavior. For example,
  `ApplyAllModifiersCharacterizationTests.cs` pins the numeric fold, and `CombatToolsTests.cs` likely pins the
  skip-downed rule.
- **Do.**
  1. Build once, then run the full suite via the xunit v3 executables. Record the baseline (expected: only the two
     git-lfs failures).
  2. For each ticket below, find tests that assert the old behavior before changing it. Change them with the fix and
     say so in the commit.
  3. Add the undocumented gaps (F6, F7, F8, F18, F19, F25, F26, F28, F29) to `RULES_GAPS.md` now, labelled
     "planned: see MECHANICS_REMEDIATION_PLAN.md", so the doc stops hiding them.
- **Acceptance.** Baseline recorded in the PR description; `RULES_GAPS.md` lists every GAP above.
- **Status (2026-10-04).** Steps 1 and 3 done. Baseline: unit suite 2291 total, 0 failed, 2 skipped (the two skipped
  rate-limit tests); integration suite not run (needs Docker). No git-lfs failures on this machine. `RULES_GAPS.md`
  has a "Planned" entry for F6, F7, F8, F18, F19, F25, F26, F28, F29. Step 2 (tests that pin old behavior) is done
  per ticket, when each fix lands.

### Decisions needed before the dependent tickets (all resolved, see §8)
D1 rest HP and hit dice, D2 PF2e dying and the concentration house rule, D3 temp HP scope, D4 spell bookkeeping
enforcement, D5 multiclass prerequisites, D6 how a dying PC takes its turn.

---

## 4. Phase 1: independent quick fixes

### M-01 Fix the rest/doc contradiction (blocked on D1)
- **Problem.** `DmHelpManual.cs:104` tells the model "`rest` already heals: no separate `hp`". The code doesn't:
  `RestChangeHandler` recovers resource pools only (`RestRecoveryLogic.BuildRecoveryDeltas`), and the `RestChange`
  description (`WorldChanges.cs:104`) says only pools recover. `RestChange` has no `[CommitSideEffects]`
  (`RulesetAction` has them, at line 942). A model that trusts the manual never heals anyone.
- **Fix.** Immediately, make the doc truthful. Whichever way D1 lands, M-70 implements it. If D1 chooses
  delegated healing, the manual must say "rest does not heal; commit `hp`".
- **Tests.** A doc-drift test, in the style of `CommitSchemaDriftTests.cs`, asserting that anything the manual calls an
  automatic side effect is declared with `[CommitSideEffects]`.
- **Acceptance.** No sentence in `DmHelpManual.cs`, the system prompts, or `claude_skills/` claims behavior the code
  lacks.

### M-02 Crit must not double an inline flat modifier
- **Evidence.** `Dnd5eRulesetResolver.cs:313-318` rolls the crit extra with `Expression = damageDice`, and
  `DefaultRollService.ParseExpression` accepts a flat part, so `"1d8+3"` re-adds `+3`. `HasInlineModifier` is the
  existing detector (`AttackDerivation.cs:32`).
- **Fix.** Strip the trailing flat modifier before the crit roll (code: it is arithmetic on a string, not rules
  data). Apply the same to the sneak-attack, rider and extra-damage crit rolls if they ever carry a flat part.
- **Tests.** `Dnd5eRulesetResolverTests.cs`: crit with `damageDice: "1d8+3"` and a seeded RNG, where the extra roll
  has no `+3`; crit with `damageBonus` param; crit with an extended crit range.
- **Compat.** None.
- **Acceptance.** Crit damage equals normal damage plus dice only.

### M-03 PF2e must use the folded roll mechanic everywhere
- **Evidence.** `Pf2eRulesetResolver.cs:394` (skill), `:446` (grapple), `:477` (escape), `:513` (contested),
  `:586` (spell save) pass `GetMechanicFromAction(action)` instead of the fold's `Mechanic`. Attack and basic save
  already use the fold. Status, willpower and plugin advantage are therefore dropped on those rolls.
- **Fix.** Use `checkFold.Mechanic` (and the target's fold in contested and spell-save rolls) as 5e does at
  `Dnd5eRulesetResolver.cs:1279`. Pure code (it is plumbing).
- **Tests.** `Pf2eRulesetResolverTests.cs`: a provider-granted disadvantage changes the dice mechanic on a skill
  check, grapple and spell save.
- **Acceptance.** Every PF2e d20 roll reads the fold's mechanic.

### M-04 PF2e action cost 0 and reactions
- **Evidence.** `Pf2eRulesetResolver.cs:702` `cost = Math.Max(1, cost)`. A free action (`actionCost: 0`) costs 1.
- **Fix.** Allow 0 (reject negatives). Leave reactions as they are; they use `ReactionAvailable`.
- **Tests.** `Pf2eRulesetResolverTests.cs`: free action leaves the budget at 3; cost 3 empties it.
- **Acceptance.** Free actions are free.

### M-05 AC recompute must keep Unarmored Defense and stat-block AC
- **Evidence.**
  - `ArmorParameterResolver.cs:53` (5e) and `:62` (PF2e) always set AC to `10 + dex + armor`.
  - It runs on every equip, unequip, transfer and item update: `ItemEquipHandler.cs:115,282`,
    `ItemTransferHandler.cs:134,141`, `ItemChangeHandlers.cs:146`, `WorldBuilderTools.cs:276`.
  - Only the bootstrap defense step knows Unarmored Defense (`Dnd5eDeriveDefenseStep.cs:15-45`), and it runs only when
    AC is still 10. A monk who unequips armor, or equips a shield, loses it. A creature with a stat-block AC loses it
    on any equip.
- **Fix.**
  1. Make the recompute share the defense step's logic: no body armor means the best of `10 + dex` and the class
     formula (`CharacterClassFeatures.UnarmoredArmorClass`, which is already data-driven from progression YAML).
  2. Record a base AC (new `SystemExtension` field, default null) when a stat block states one explicitly, and use it
     as the floor when nothing armor-like is equipped. Code, because it is a precedence rule.
- **Tests.** `ArmorParameterResolverTests.cs`: barbarian and monk unarmored with and without a shield; creature with
  stat-block AC 15 equips a ring; unequip returns to the base, not 10 + dex.
- **Compat.** Existing characters have no base-AC field: treat null as today's behavior. Do not backfill.
- **Acceptance.** No equip path lowers AC below the character's unarmored best.

### M-06 Small roll-plumbing fixes
- Contested check ignores `bonus` (`Dnd5eRulesetResolver.cs:1329-1356`): honor it like skill checks do, including the
  "sheet not added" semantics.
- Initiative discards advantage (`Dnd5eRulesetResolver.cs:1618-1631`, `Pf2eRulesetResolver.cs:662-676`): use the
  resolution's advantage, and pass `Mechanic` to the roll.
- **Tests.** `Dnd5eRulesetResolverTests.cs`, `Pf2eRulesetResolverTests.cs`.
- **Acceptance.** A plugin provider can grant initiative advantage and it takes effect.

### M-07 Enforce the level cap in the handler
- **Evidence.** The cap lives in `LevelUpStatusReader.cs:9,24` (UI flag) and `RecipeCharacterCreation.cs:79`
  (creation); `XpThresholdCalculator.cs:126,142` clamps the XP table. `LevelUpChangeHandler` (`CharacterChangeHandlers.cs:376-495`)
  has none.
- **Fix.** Reject a level-up that would exceed 20, and make the handler consult `LevelUpStatusReader.MaxLevel`.
  If a campaign wants epic levels, make it a `CampaignConfig` option (decision: ask only if it comes up).
- **Tests.** `CharacterLevelUpToolsTests.cs`: level 20 refuses; 19 to 21 with `levelsGained: 2` refuses.
- **Acceptance.** No path produces a level above the cap.

### M-08 Concentration identification
- **Evidence.** `Contains("Concentration")` at `HpChangeHandler.cs:97`, `StatusChangeHandler.cs:128-131`,
  `DelayedTickProcessor.cs:109`. A status like "Concentration Broken" or a homebrew "Lost Concentration" matches.
- **Fix.** Add a `Concentrating` marker: a `StatusEffect` category or a `ConditionName` of `concentration`, with the
  current name match kept as a fallback for stored statuses. One shared helper (`ConcentrationRules.Find(stats)`)
  replaces the three call sites.
- **Tests.** `ConcentrationBreakTests.cs`: "Lost Concentration" doesn't count; legacy "Concentration: Bless" still does.
- **Compat.** Stored statuses keep working through the fallback; document that the name match is deprecated.
- **Acceptance.** One definition of "is concentrating".


**Phase 1 status (2026-10-04): M-01 to M-08 done, uncommitted.** Unit suite 2319 total, 0 failed, 2 skipped
(`MechanicsPhase1Tests.cs`, plus new cases in `ArmorParameterResolverTests.cs`). Notes:
- M-01 is the interim doc fix: the manual now says `rest` restores pools, not HP. M-70 flips it when rest heals.
- M-05 added `SystemExtension.BaseArmorClass` (SDK additive, for 0.17.0) and `UnarmoredArmorClass.AllowsShield`
  (monk: false). The item handlers and `WorldBuilderTools` now take an optional `ProgressionDefinitionProvider`.
- M-08: `ConcentrationRules` (conditionName `concentration`, category `Concentration`, or the legacy name prefix).
- Found on the way: `PressureManagerIntegrationTests.GetWorldState_DoesNotSurfaceNeedsPressure_ForNonPartyBackgroundNpc`
  failed once in the full run and passed alone and on rerun (flaky, not touched).
- Found on the way: this machine's culture is `he_IL`, where `int.TryParse("-1")` fails; PF2e `actionCost` now parses
  invariant. Other culture-sensitive parses probably exist (not audited).

---

## 5. Phase 2: one damage pipeline

Design: today each path computes its own mitigation, then emits `HpChange { Delta }`. `HpChangeHandler` then applies
dying rules and the concentration save, but the other writers (`DelayedTickProcessor`) skip them. Make one component
the only place that turns "N damage of type T" into an HP change, and make `HpChange` carry what the dying rules need.

### M-10 `DamageResolver` (mitigation, once)
- **Problem.** `ApplyTargetDamageReduction` (`Dnd5eRulesetResolver.cs:713-726`) and `ApplyFeatResistance` (`:705-711`)
  are separate, so a stat-block resistance multiplier plus a feat resistance (such as Rage) halve twice (`:330-331`).
  Save paths apply neither (`:1577-1616`, `:950-1001`; PF2e `:617-660`).
- **Fix.** New `Rulesets/DamageResolver` (code: it is ordered arithmetic): take packets `(amount, type, flags)`, apply
  in the SRD order (flat adjustments, then resistance once however many sources, then vulnerability), return the
  total per target and notes. Input from both the stat block (`DamageResistances`, `DamageModifiers`) and feat effects
  (`FeatEffectRules.ResistsDamage`, `DamageReduction`). Make the case-sensitivity of `DamageModifiers` lookups
  match the case-insensitive resistance lookup. PF2e gets resistance, weakness and immunity from the same component
  with PF2e ordering.
- **Tests.** New `DamageResolverTests.cs`; extend `Dnd5eRulesetResolverTests.cs` and `Pf2eRulesetResolverTests.cs`
  with fire-resistant target taking Fireball; resistance from two sources halves once.
- **Compat.** Behavior changes only where two sources overlapped or a save path ignored a trait.
- **Acceptance.** All damage mitigation lives in one class; no resolver implements its own.

### M-11 Route every damage path through it
- **Paths.** Attack hit, splash on miss, extra damage dice, rider (`:355-367`), sneak attack, 5e single save and area
  save, 5e pool save, PF2e basic save, delayed tick (`DelayedTickProcessor.cs:59`).
- **Delayed tick.** It is `internal static` over a bare `Character`, called from four `CombatTools` sites
  (`:136` start-overwrite, `:356` and `:415` next-turn, `:500` end-combat). Extract the HP-and-dying part of
  `HpChangeHandler` into a context-free `DamageApplication.Apply(character, packet, sink)` where the sink collects
  messages and events, and call it from both. Do not fold it into the handler only, because the processor has no
  `ChangeContext`.
- **Tests.** `DelayedTickProcessorTests.cs`: a PC dropping to 0 gets a death-save tally; massive damage kills;
  resistance applies; concentration still checks. `DeathConsumerTests.cs` for the dying rules via the shared path.
- **Other HP writers.** Several paths set `CurrentHp` directly and skip every rule: `DeathSaveChangeHandler.cs:74`
  (natural 20), `DeathChangeHandler.Revive` (`:134`), `CharacterChangeHandlers.cs:113` (`character_update` setting
  `currentHp`) and `:476` (`HealToMatch`), bootstrap (`BootstrapHpResolver.cs:51-55`,
  `CharacterBootstrapOrchestrator.cs:30`, the HP steps) and `DelayedTickProcessor.cs:59`. Today they also leave a stale
  death-save tally and a stale Unconscious state. Add one helper (`HitPointState.Set(character, newHp, reason)`)
  that every writer calls; it owns the zero-crossing side effects (clear or start the tally, add or remove
  Unconscious from M-31, break concentration from M-32, publish downed or recovered events).
- **Acceptance.** `grep -rn "CurrentHp *[+-]\?=" src` finds writes only inside the helper and `DamageApplication`.

### M-12 Roll area damage once per cast
- **Evidence.** `TryApplySaveDamageAsync` is called per target (`Dnd5eRulesetResolver.cs:1490`);
  `ApplyPoolSaveDamageAsync` is called per target too (`:1485-1486`) and rolls every pool each time; PF2e the same
  (`Pf2eRulesetResolver.cs:590`).
- **Fix.** Roll the damage before the target loop, keep each target's save separate, apply half or none per target.
  Note it in the narrative so the DM sees one number.
- **Tests.** `Dnd5eRulesetResolverTests.cs`: three targets, seeded RNG, damage is identical for those who failed.
- **Acceptance.** One damage roll per spell cast.

### M-13 Hit instances in the dying rules
- **Evidence.** `HpChangeHandler.cs:129-184` counts one failure per `HpChange`; the rider is a separate `HpChange`
  (`Dnd5eRulesetResolver.cs:363`), and a critical hit at 0 HP isn't double.
- **Fix.**
  1. Merge rider damage into the target's single packet per action (M-11), so one hit is one `HpChange`.
  2. Add optional `HpChange.Critical` (default false, additive to the SDK model) so a hit at 0 HP from a crit records
     two failures. `HpChange` is a plugin SDK type: follow §7a.
  3. One concentration check per damage instance, not per `HpChange` row.
- **Tests.** `DeathConsumerTests.cs`: rider hit at 0 HP is one failure; crit at 0 HP is two; two ordinary hits are two.
- **Compat.** `Critical` is optional; old payloads behave as before.
- **Acceptance.** Failures match the SRD for single, rider and crit hits.

### M-14 Evasion and save-damage hooks
- **Fix.** Evasion (SRD rogue 7, monk 7) as a feat-effect kind `evasion` on Dexterity saves, authored in the 5e class
  progression YAML (`RulesetData/dnd5e/progressions/rogue.yaml`, `monk.yaml`). Those are hand-authored: `CLAUDE.md`
  and `scripts/README.md` name only a PF2e classes generator (`generate_pf2e_classes.py`), so there is no 5e
  progression generator to change. (Data.) The save-damage path asks `FeatEffectRules` for it: success means no damage, failure means
  half. Vocabulary addition: add `evasion` to `FeatEffectKinds` and `Validate`.
- **Tests.** `FeatEffectRulesTests.cs` (validation of the new kind), `Dnd5eRulesetResolverTests.cs`.
- **Acceptance.** A level 7 rogue takes no damage on a successful Dexterity save.

### M-15 Temporary hit points (blocked on D3)
- **Problem.** None exist: no field, no handler. 5e (False Life, Heroism) and PF2e both use them.
- **Fix if D3 says yes.** `Character.TempHp` (nullable, default null). `DamageApplication` spends temp first and does
  not stack them (the higher value wins). Heal does not restore temp. Rests clear it. Data-driven grant through a
  feat-effect kind `tempHp` or a `StatusChange` hook.
- **Tests.** New `TempHpTests.cs`.
- **Compat.** Additive field.

**Phase 2 status (2026-10-04): M-10 to M-15 implemented.** What was built, and where it differs from the tickets:
- **M-10.** `Rulesets/DamageResolver` (internal static). 5e order is flat reductions, then resistance once (a stat-block
  multiplier below 1 and a feat's `resistance` are the same step: the smaller factor wins, no double halving), then
  vulnerability (a multiplier above 1), with immunity (a multiplier of 0) winning. PF2e order is immunity, then weakness
  (a negative `DamageResistances` value), then resistance. Type lookups are case-insensitive. **Behavior changes beyond the
  ticket's Compat line:** flat reduction now comes before the multiplier (it came after), and a resistance plus a
  vulnerability both apply (halve, then double).
- **M-11, resolver paths.** 5e hit, crit, sneak, extra dice, rider, splash, single save, pool save, self save and PF2e hit and
  basic save all call it. The multi-instance attacks (Magic Missile darts) stay one `HpChange` per dart, on purpose:
  each dart is its own damage instance for death saves and concentration.
- **M-11, other writers.** `Data/DamageApplication` (temp HP, downed, dying rules, concentration) is the one place damage
  lands. `HpChangeHandler` and `DelayedTickProcessor` both use it through an `IDamageSink`. The tick now gets the
  dying rules and massive damage it lacked, and `CombatTools` passes a `currentDay` for the death record. **Limit:** a tick's
  mitigation reads the stat block only (the processor has no feat providers), so a conditional feat resistance such as
  Rage doesn't apply to a tick. `Data/HitPointState.Set` is the helper for DM-set and level-up HP (clears or starts the
  tally); `character_update` and `HealToMatch` use it. Death, revive and a natural 20 already handled the tally and stay
  as they are. Bootstrap writes stay (they initialise a new character). M-31 and M-32 hooks are not built (later phases).
- **M-12.** The cast's damage is rolled once, on the first target that needs it, and shared (so existing single-target roll
  order is unchanged). A pool spell's save still halves the sum: each pool is halved so a target's resistance to one type
  touches only that pool, and the rounding remainder goes to the first pool, so the total is `floor(sum / 2)` as before.
  The "one number" note for the DM isn't added to the narrative yet.
- **M-13.** The rider merges into the hit's one `HpChange`; `HpChange.Critical` (optional, omitted from JSON when false) is
  set on a crit, and a crit at 0 HP records two failures.
- **M-14.** `evasion` feat-effect kind (valueless, Dexterity saves that allow half), authored on the rogue and monk level 7
  features and the ranger's Superior Hunter's Defense option.
- **M-15.** `Character.TempHp` and `TempHpSource` (additive), the `grant_temp_hp` commit (`TempHpGrant`: `characterId`,
  `amount`, `source`; the larger value wins), spent first by `DamageApplication`, cleared by death and by a long rest. Heal
  doesn't restore them. Concentration counts the full damage; massive damage and death saves count only what reached HP.
- **SDK (for 0.17.0, not yet bumped):** `HpChange.Critical`, `Character.TempHp`, `Character.TempHpSource`, `TempHpGrant`,
  `FeatEffectKinds.Evasion`.
- **Not run:** the integration suite (needs Docker).

---

## 6. Phase 3: condition mechanics as data

Today `ConditionDefinition` has only a description, duration, mood hint, `Immunities`, `Suppresses` and stacking flags
(`ConditionDefinition.cs:5-54`). The YAMLs under `src/CampaignVault/RulesetData/{dnd5e,pf2e}/conditions/` carry
`mechanicalSummary` prose. Core produces advantage or disadvantage only from willpower and feat effects. The
`IRollModifierProvider` seam is the right home: it is synchronous and pure, and its `RollQuery` carries `Actor`,
`Other` and the roll kind (`IRollModifierProvider.cs:34-41`). The attack roll passes the target as `Other`, so a core
provider can express "attackers have advantage" by reading the target's conditions. A plugin could not do this
cleanly for every condition, so it belongs in core.

### M-20 Data model
- **Fix.** Extend `ConditionDefinition` with an `effects:` list reusing the existing feat-effect vocabulary
  (`FeatEffectKinds`) plus a small number of condition-specific kinds, each with a `scope: self | againstSelf`:
  `advantage` and `disadvantage` (`on: attack|check|save`, optional `subject`), `autoFailSave` (abilities),
  `autoCrit` (`within: Touch`), `speed` (`set: 0` or a multiplier), `blocksActions`. For stacking conditions add a
  `levels:` table so exhaustion's effects accumulate by level. Data, because the effects are the rules.
- **Why code remains.** The provider logic, adjacency (`SpatialDistanceBand.Touch`) and stack-level parsing
  (`ConditionExpiryEvaluator.TryParseStackLevel`) are interpretation, not content.
- **Tests.** `ConditionDefinitionTests.cs`: parse and merge (the `Merge` method at `ConditionDefinition.cs:43-53` must
  carry the new fields), schema validation of unknown kinds.
- **Acceptance.** A condition YAML can express every SRD condition's mechanics without code.

### M-21 `ConditionMechanicsProvider`
- **Fix.** A core `IRollModifierProvider` registered next to `StatusEffectModifierProvider` in `RollModifierPipeline`
  (`:23-27`), resolving each active status to its definition and emitting `RollModifier`s. Both directions:
  the actor's own conditions, and for an attack the `Other`'s `againstSelf` conditions within Touch (melee) or not
  (ranged). Auto-fail saves become a sentinel result the resolvers honor: add `RollResolution.AutoFail`, or fold
  it as a very large penalty only if the pipeline cannot carry a flag (prefer the flag, because a large number
  pollutes the roll summary). `RollModifier` and `RollResolution` are positional records in the plugin SDK
  (`IRollModifierProvider.cs:44-47`): add `AutoFail` as an init-only property, not a constructor parameter (see §7a).
- **Tests.** New `ConditionMechanicsTests.cs`: prone melee vs ranged, paralyzed auto-fail Str and Dex, restrained Dex
  save disadvantage, poisoned attack and check disadvantage, frightened with source in sight (assert flag),
  advantage and disadvantage cancel.
- **Acceptance.** The effects in each SRD condition's `mechanicalSummary` are applied and appear in the roll notes.

### M-22 Resolving a status to its definition (migration)
- **Problem.** Plain-string statuses have no `ConditionName` (the legacy branch of `StatusChangeHandler.HandleAdd`),
  and legacy statuses are stored by name.
- **Fix.** Resolve by `ConditionName`, else by normalized `Name` (the same normalizer as
  `StatusEffectModifierProvider.Normalize`), and parse a trailing level for stacking names. Don't rewrite stored data.
- **Tests.** "Poisoned" with no `ConditionName` resolves; "Exhaustion 3" resolves to level 3.
- **Compat.** No data migration; behavior appears for existing characters as soon as the provider ships. That
  is a visible change in existing campaigns. Mention it in the release notes.

### M-23 Author the condition data
- **5e (SRD 5.1).** Fill `effects` for blinded, charmed, deafened, frightened, grappled, incapacitated, invisible,
  paralyzed, petrified, poisoned, prone, restrained, stunned, unconscious, and the exhaustion level table (1 check
  disadvantage; 2 speed halved; 3 attack and save disadvantage; 4 max HP halved; 5 speed 0; 6 death).
- **PF2e (Player Core).** Fifteen PF2e condition files exist (`RulesetData/pf2e/conditions/`) with prose only. This is
  an **audit and fill**, not authoring from scratch:
  - **Stale or missing names.** `unconscious.yaml` suppresses `flat-footed` (the pre-remaster name) and `stunned.yaml`
    suppresses `slowed`; neither condition exists as a file. **Off-guard**, **slowed**, **dying**, **wounded**,
    **drained**, **enfeebled**, **clumsy** and **stupefied** are absent.
  - **Text that may not match Player Core.** For example `prone.yaml` ("melee attackers gain +1 to hit") and
    `paralyzed.yaml` ("critical hits ... succeed on a lower degree of success"). Check each against the ORC source
    (Archives of Nethys, the same source `generate_pf2e_*.py` uses) before encoding effects. Do not encode text that
    was never verified.
  - Numeric conditions become penalties: frightened N, sickened N, and off-guard as -2 AC. PF2e Sneak Attack
    (`Pf2eRulesetResolver.cs:324-332`) currently relies on the model passing `sneakAttack: true`; off-guard as a real
    condition lets the engine check it (the caller's flag stays as the DM's override).
  - Hand-authored: no generator covers conditions.
- **Tests.** One data test per condition that loads the YAML and asserts the effect list matches the summary text.
- **Acceptance.** No shipped condition is prose only.

### M-24 Enforce `Immunities` and `Suppresses`
- **Evidence.** Referenced only in `ConditionDefinition.cs:49-50` (the `Merge` method).
- **Fix.** `StatusChangeHandler.HandleAdd` (`:50-147`) refuses a condition the target is immune to (with a clear note)
  and the provider ignores a suppressed condition (unconscious suppresses incapacitated and prone).
- **Tests.** `ConditionDefinitionTests.cs`, `ApplyEffectTests.cs`.
- **Acceptance.** The fields do something or are removed.

### M-25 Unify exhaustion
- **Evidence.** The deprivation rule and long rest use the status "Exhaustion N"
  (`SurvivalDeprivationRule.cs` `TryEscalate`, `RestChangeHandler.ClearUntilLongRestConditionsAsync`), while
  `Dnd5eExhaustionPressureContributor.cs` reads `Attributes["exhaustion_level"]`. Nothing connects them, the
  contributor's text promises effects that aren't enforced, and the rule has no cap, so level 7 and higher exist and
  nothing kills at 6.
- **Fix.** The status is canonical. Rewrite the contributor to read the status level (and keep reading the attribute
  as a fallback for a release). Apply the M-23 level table. Death at level 6 emits a `DeathChange` with cause
  "exhaustion". Max HP halving at level 4 is a derived value in `DamageApplication`/`HpChange` clamping: decide with
  M-15 whether it is a stored `MaxHp` change or an effective cap (prefer an effective cap, so removing the condition
  restores it).
- **Tests.** `SurvivalDeprivationRuleTests.cs`, a new `ConditionExpiryEvaluatorTests.cs` (none exists for
  `ConditionExpiryEvaluator`), a new test for `Dnd5eExhaustionPressureContributor`.
- **Compat.** Characters with only the attribute still produce pressure for one release.
- **Acceptance.** There is one exhaustion, and it has teeth.

### M-26 Speed zero
- **Evidence.** `SpeedRules.cs:Effective` floors at `Floor = 5`; `RollModifierPipeline.Speed:74-84` floors at 5.
- **Fix.** Let a condition set speed to 0 (restrained, grappled, exhaustion 5), while keeping the floor for
  *travel* so a captive can still be dragged (that comment is the reason for the floor). Add a combat-movement
  accessor that returns the true 0, and keep `TravelStretch` capped.
- **Tests.** `SpeedContextTests.cs` and `TravelSpeedTests.cs`; travel still completes with a restrained captive.
- **Acceptance.** Combat movement can be 0; travel never divides by 0.

### M-27 Grapple
- **Evidence.** `EngagementMutationHelper.cs:13-35` only adds a `Grappling` relation. The detectors match any action
  name containing "grapple" or "escape" (`:7-11`).
- **Fix.** Applying the grapple also applies a `Grappled` status (speed 0 from M-23) to the target. The
  relation ends when the grappler is incapacitated (ActionBlock) or the two are no longer within Touch. Tighten the
  detectors to require `ActionType: ContestedCheck`/`OpposedCheck` or an explicit `maneuver` parameter.
- **Tests.** `GrappleRulesetTests.cs`, `CombatE2ETests.cs`.
- **Acceptance.** Grapple has a cause, an effect and an end.

**Phase 3 status (2026-10-04): M-20 to M-27 implemented.** What was built, and where it differs from the tickets:
- **M-20.** `ConditionEffect` and `ConditionEffectKinds` in `Data/Templates` (not the SDK), `ConditionDefinition.Effects`,
  `Levels` (cumulative), `EffectsAt`, `MaxLevel`, and `Merge` carries them. Kinds: advantage, disadvantage, autoFailSave,
  autoCrit, speed, blocksActions, maxHpMultiplier, death, and two the tickets did not list, `bonus` (PF2e numbers, typed,
  `perLevel`) and `implies` (prone implies off-guard). Documented in PLUGINS.md.
- **M-21.** `ConditionMechanicsProvider`, a core `IRollModifierProvider` exempt from the plugin system filter. Both
  directions (own conditions; the target's `againstSelf` ones on an attack). `RollModifier.AutoFail` and
  `RollResolution.AutoFail` are SDK additions (0.17.0, now bumped); `RollFold` carries it and both resolvers' save paths
  honor it. Melee or ranged is a roll tag (`melee`/`ranged`) the attack fold adds; the DM's `assert` flags are tags too
  (`sourceInSight`). `autoCrit` is a hook in the 5e hit path (`ConditionsAutoCrit`), not a roll modifier. PF2e typed
  stacking (worst penalty and best bonus per type) is applied among the conditions' own numbers only, not against feat effects.
- **M-22.** `ConditionMechanics.TryResolve`: `ConditionName`, else the normalized name, else the name without a trailing
  level. No data migration; existing characters pick the behavior up immediately (release-note it).
- **M-23.** Every 5e SRD condition has effects except charmed and deafened (a relation, a fact about the check). PF2e was
  audited against Player Core text from Archives of Nethys: prone and paralyzed prose was wrong and is fixed; new files for
  off-guard, slowed, dying, wounded, drained, enfeebled, clumsy, stupefied; frightened, sickened, fatigued, clumsy, enfeebled
  (Athletics only), drained (Fortitude), stupefied (Will and skills) are penalties. `charmed` is not a Player Core condition
  and is left as it was. Prose-only by design, pinned by a test: blinded, charmed, deafened, invisible, stunned, slowed,
  wounded. `flat-footed` suppression is now `off-guard`. PF2e Sneak Attack also fires when the target has off-guard.
- **M-24.** `StatusChangeHandler` refuses a condition the target is immune to (a failure with a message); suppression is
  applied wherever conditions are read. No shipped condition uses `immunities`; it is for homebrew and plugins.
- **M-25.** The status is canonical. The contributor reads the status level (the attribute is a fallback). The level is
  capped at the table's top (6); a `death` effect kills from the status handler, whichever way the level arrives. Level 4
  is an effective cap (`ConditionMechanics.EffectiveMaxHp`) applied by `HpChangeHandler` healing and when the condition
  lands; **not yet applied** to character_update, level-up top-up and revive clamps (DM overrides).
- **M-26.** `RollModifierPipeline.CombatSpeed` can be 0 and applies conditions; `Speed` stays floored at 5 for travel.
  The chase context line uses `CombatSpeed`. Cards and `SpeedRules.Effective` are context-free and still show base speed.
- **M-27.** A grapple also applies the Grappled (PF2e: Grabbed) condition with `AppliedBy` set; escape removes it; a
  grappler who gains a `blocksActions` condition releases everyone it holds. The escape detector now needs the grapple
  named. **Not done:** ending the grapple when the two move apart (no distance model to check). The old grapple test that
  expected one mutation now expects two.

---

## 7. Phases 4 to 10

### Phase 4: turn flow, dying, concentration (needs phases 2 and 3; D6)

**M-30 Downed PCs take a turn.**
- **Evidence.** `CombatTools.cs:320-328` and `:353` filter on `CurrentHp > 0`. `HpChangeHandler.cs:165` tells the
  model to roll "each turn".
- **Fix.** Include a PC at 0 HP who is not dead and not stable in the turn order. Give that turn a restricted action
  budget (no actions, only the death save). Per D6 (resolved), the turn start surfaces a "death save due" notice and
  the model calls `death_save`; nothing auto-rolls, and the DM may adjudicate otherwise through the normal action path. NPCs at 0 HP stay skipped (the existing rule).
  **Budget trap.** `RulesetResolverBase.TryConsumeActionSlot` returns true when `ActionBudget.Count == 0`
  (`RulesetResolverBase.cs:~938`), so an empty budget means unlimited actions. Give the dying turn explicit zero keys:
  `action: 0, bonus: 0` for 5e, `actions: 0` for PF2e (the keys each ruleset's `GetTurnActionBudget` uses), plus a
  `deathSaveDue: 1` marker. A reaction still bypasses the budget, so also refuse reactions from a dying PC via
  `ActionBlock` (M-31).
- **Tests.** `CombatToolsTests.cs`, `CombatE2ETests.cs`: a dying PC gets a turn; stable and dead are skipped; a round
  with only dying PCs and no enemies ends the combat rather than looping.
- **Compat.** `AnyoneAlive` semantics change; keep the end-combat rule for "no one can act".
- **Acceptance.** The prompt in `HpChangeHandler` is true.

**M-31 Unconscious at 0 HP.**
- **Fix.** Reaching 0 HP applies the `unconscious` condition (and prone), healing above 0 removes it. This
  needs M-23 and makes `ActionBlock` stop a downed PC from acting through its existing condition-name list
  (`ActionBlock.cs:23-26`). No change to `ActionBlock` itself, which is the point. The apply and remove must live in
  the `HitPointState` helper from M-11, because healing and zero-crossing do not all go through `HpChange` (see the
  list of writers there). Natural-20 death saves, revive and `character_update` are the cases that would otherwise
  leave a conscious character flagged Unconscious.
- **Tests.** `DeathConsumerTests.cs`: down, then heal removes it; death keeps it off (dead is separate).
- **Acceptance.** A PC at 0 HP can't take actions out of combat either.

**M-32 Concentration at 0 HP; hook for save advantage.**
- **Fix.** `DamageApplication` breaks concentration outright when the target reaches 0 HP or is incapacitated, and
  rolls the save otherwise. The save goes through the roll pipeline as a new kind `RollKinds.ConcentrationSave` (subject
  Constitution), so a plugin can grant advantage (War Caster, Resilient are PHB and ship as plugin content only).
  The pipeline hook is the only core change. Do not ship those feats.
- **Tests.** `ConcentrationBreakTests.cs`: breaks at 0 HP without a roll; a test provider grants advantage.
- **Acceptance.** The hook exists; no PHB content shipped.

**M-33 Death saves through the pipeline.**
- **Fix.** `DeathSaveChangeHandler.cs:69-70` rolls a bare d20. Route it through `FoldAsync`-equivalent so Bless and
  similar effects count, with a `RollKinds.DeathSave`.
- **Tests.** `DeathConsumerTests.cs`.

**Phase 4 status (2026-10-04): M-30 to M-33 implemented.** What was built, and where it differs from the tickets:
- **M-30.** A dying 5e player character (`HitPointState.IsDying`: 0 HP, not dead, not stable) is picked by `next_turn`.
  Its action budget is every key the ruleset uses at 0 plus `deathSaveDue: 1` (`CombatTools.DeathSaveDueKey`), and the
  turn summary carries a `DEATH SAVE DUE` line with the `death_save` commit. Nothing rolls on its own (D6). The first
  `death_save` on that turn spends the marker and a second is refused; outside the PC's own turn it is not limited.
  The turn-start peek uses the same "can take a turn" test as the pick. Stable, dead
  and NPC characters at 0 HP are still skipped. `AnyoneAlive` stays "someone with HP above 0", so a round with only
  dying PCs gives each its turn and then ends the combat. **Not done:** PF2e dying PCs (no dying/wounded flow until
  M-52), and `start_combat` still drops combatants who are already at 0 HP.
- **M-31.** `HitPointState.SyncDowned` adds an Unconscious status (key `engine:downed`) to a living player character at
  0 HP and removes it when HP rises, from `HitPointState.Set`, damage, the death-save natural 20, death and revive.
  Only the engine's own status is ever removed, so a sleep spell's Unconscious stays. NPCs and the dead are not flagged,
  and the existing `ActionBlock` list does the blocking, unchanged. A plain Prone status is added with it and stays when
  they wake. A PC downed before the engine flagged them is backfilled when their dying turn starts or they roll a death
  save; `character_update`, the repository upsert and `CharacterManager` re-sync after they write HP. **Not done:**
  incapacitation by a status (stunned) ends concentration only when damage next lands, not at once. Because
  `unconscious.yaml` suppresses prone, a ranged attack on an unconscious target gets advantage where the rules give a
  straight roll (Phase 3 data, left alone).
- **M-32.** `RollKinds.ConcentrationSave` (and `DeathSave`) in the SDK. Core's own layers read both as saves, so statuses
  and conditions count; a plugin provider asks for the specific kind to grant advantage. Concentration breaks with no
  roll at 0 HP or when `ActionBlock` says the caster can't act. `IDamageSink.ResolveRollAsync` carries the pipeline;
  combat turn advancement (delayed ticks) has no pipeline, so a tick-damage concentration check still uses the raw save.
- **M-33.** The death save resolves through the pipeline: advantage from a provider, a bonus added to the total (the
  natural die still decides a 1 or a 20), the message shows `(d20 n+bonus)`.

### Phase 5: level-up and multiclass (independent; M-44 needs M-05)

**M-40 HP for every level gained.**
- **Evidence.** `Dnd5eDeriveHitPointsStep.cs:91` computes one gain; `:98` adds `LevelsGained` to the level.
- **Fix.** Loop `LevelsGained` times (rolled mode rolls each), and add the feature and feat HP deltas once for the
  whole jump (they are already a level-range difference at `:92-95`). PF2e needs no change here:
  `Pf2eDeriveHitPointsStep.ApplyLevelGainAsync` already multiplies by `LevelsGained`.
- **Tests.** `CharacterLevelUpToolsTests.cs` and `BootstrapHpResolverTests.cs`: 3 to 5 gives two gains; rolled mode
  consumes two rolls.
- **Acceptance.** HP matches creating the character at the new level in average mode.

**M-41 Add a new class on level-up (D5).**
- **Evidence.** Three components resolve an unknown `ClassGained` three ways, and nothing ever adds a class entry
  (no `ClassLevels.Add` anywhere):
  - `LevelUpPlanner.Plan` (`LevelUpPlanner.cs:26-51`) falls back to `entries[0]`, the first class, and plans that
    class's next level's choice slots.
  - `BootstrapClassLevelHelper.cs:27-31` falls back to `ClassLevels[^1]`, the last class, for the level record.
  - `Dnd5eDeriveHitPointsStep.cs:78-83` uses the named class's hit die.
  For Fighter 5 taking `classGained: Wizard`, the HP uses a d6, the picks come from Fighter 6 and the level goes to
  Fighter 6.
- **Fix.** One `ClassGainedResolver` used by all three. If `ClassGained` names a class not in `ClassLevels`, add an
  entry at level 1, and make the planner plan that class's level 1 (`classLevel = 0 + 1`, its progression). Validate
  that the class exists in the progression data; otherwise fail loudly instead of leveling a different class. Prerequisites
  (SRD: 13 in the class's key ability) per D5 (resolved: block, with a recorded `overridePrerequisites` DM override). Multiclass proficiency grants are a data table on
  the class YAML (the SRD gives them); check what `LevelUpPlanner` and the class provider already carry before adding
  fields.
- **Tests.** `Dnd5eMulticlassTests.cs`, `CharacterLevelUpToolsTests.cs`, `LevelUpPlannerTests.cs`, and a new unit test
  for `BootstrapClassLevelHelper.SyncClassLevelFromStats`, which has no test today.
- **Compat.** Stored characters whose `ClassLevels` is empty (single-class from the string) must still work: the
  helper's string path (`:39-43`) stays. A character mid-multiclass in the buggy state (last class over-leveled) can't
  be repaired automatically; offer a `character_update` recipe in the notes.
- **Acceptance.** Fighter 5 taking `classGained: Wizard` becomes `Fighter 5 / Wizard 1`.

**M-42 Current HP on level-up.**
- **Evidence.** `CharacterChangeHandlers.cs:474-477`: current HP rises only with `HealToMatch`, which defaults false
  (`WorldChanges.cs:1171`).
- **Fix.** Default to true for PCs, since the SRD gives the new hit points as current as well, keep the field as an opt
  out, and note it in the commit schema.
- **Tests.** `CharacterLevelUpToolsTests.cs`.

**M-43 Retroactive Constitution.**
- **Fix.** When Constitution changes (a level-up pick or `AttributeChange`), adjust max HP by the modifier delta times
  the character's level **before** the level being gained. Level-up picks apply before the HP step
  (`CharacterChangeHandlers.cs:412-441`), so the new level's gain already uses the new modifier; multiplying by the new
  total would count that level twice. For an `AttributeChange` outside a level-up, use the current total level. PF2e
  has the same shape (`level * (classHp + conMod)`). Put it in the shared HP step so both paths use it, and report it
  in the commit message. Skip when `StatBlockHp` is set (the existing guard).
- **Tests.** `BootstrapHpResolverTests.cs`.

**M-44 Recompute AC and pools on level-up.**
- **Fix.** The level-gain step list (`Dnd5eRulesetResolver.cs:42-44`) omits the defense step, so a Constitution or
  Dexterity change from a pick leaves AC stale. After M-05, add the recompute to level-up.

**Phase 5 status (2026-10-04): M-40 to M-44 implemented.** What was built, and where it differs from the tickets:
- **M-40.** `Dnd5eDeriveHitPointsStep.ApplyLevelGainAsync` takes one hit die per level gained (rolled mode rolls each, and
  the message lists them); the feature and feat deltas are added once. 3 to 5 equals creating at level 5.
- **M-42.** `LevelUpChange.HealToMatch` defaults to `true` (a property initializer, so an explicit `false` still opts out and
  the SDK shape is unchanged). The level-up menu tool no longer forces `false`. Cheat sheet and field description updated.
- **M-41.** `ClassGainedResolver` is the one answer for the planner, the hit point step, the class-level sync and the
  `lookup kind=level_up` tool (`className`). Names compare as whole class names ("War" is not "Warlock"). A class the
  character lacks is added at level 1 in the sync (structured entries are built from a string like "Fighter 5" first, so
  the trailing number isn't bumped), and the sync now runs right after the hit point step so proficiencies and
  spellcasting see the new class. A multiclass character must name `classGained`; a single-class one need not. Unknown
  class names and, in PF2e, any class the character lacks are refused. The requirements are class data
  (`multiclassRequires` in the 12 SRD class files, plus `ClassDefinition.MulticlassRequires` for plugins), checked for the
  new class and every current class, only when the class is new, before anything changes. `overridePrerequisites` (an
  SDK field) lets the DM through and the override is recorded. A class taken for the first time ignores the other classes'
  recorded picks. The level-up menu tool takes an optional `classGained` too.
- **M-43.** `LevelUpDerivedStats.ApplyRetroactiveConstitution`: the modifier change times the levels already taken (the
  new level's gain already uses the new modifier). Legacy `abilityScoreIncreases` now go in with the picks, before the
  hit point step, so they and the picks behave the same. Skipped under `StatBlockHp`; a drop clamps current HP through
  `HitPointState.Set`. Not done: a Constitution change made outside a level-up (`character_update` on `systemStats`):
  `AttributeChange` doesn't carry the six abilities, so the plan's third path has no home today.
- **M-44.** Differs from the ticket: it isn't a pipeline step. A step runs after the picks and can't tell a derived armor
  class from a stated one, so the handler measures the formula before and after (`ArmorParameterResolver.Apply`, equipped
  gear included) and moves the stored armor class by the difference. A stat block's own AC rises by the same amount; an
  unarmored PF2e character's AC follows the level. Pools: `InitializePools` already ran after the sync, so a new class's
  pools are initialised; nothing added.
- **Callers.** The XP-threshold nudge now suggests the class as the sheet names it (it used to send "Human" for "Human
  Fighter 5") and tells a multiclass character's model to pick one. The level-up menu and `lookup kind=level_up` return
  the resolver's error (naming the classes) for a multiclass character who doesn't say which, and show the gaining
  class's own level features. A level-up no longer heals a dead PC or one at 0 HP.
- **Behavior changes to know.** `HealToMatch` defaults to true; legacy `abilityScoreIncreases` now count for the new
  level's hit points; a multiclass `level_up` must name `classGained`; the hit die lookup now matches a decorated class
  name ("Fighter (Champion)") loosely everywhere, so an initial bootstrap of such an entry now counts its dice; a string
  class like "Human Fighter 5" loses "Human" when a second class is added and it is rewritten from the entries. The Unity
  menu client doesn't send `classGained` yet: it needs a class picker for multiclass characters.
- **Tests.** `MechanicsPhase5Tests` (29). Two existing tests changed on purpose: the ASI hit point totals (37 to 40, the
  retroactive Constitution) in `CharacterLevelUpToolsTests` and `CharacterBootstrapTests`.

### Phase 6: PF2e correctness

**M-50 Spell attack stat (F14).** Add `SpellAttack` to `Pf2eExtension` (nullable), derived by
`Pf2eDeriveSpellcastingStep` alongside `SpellDc` (`:62-64`). `Pf2eRulesetResolver.ResolveAttackAgainstTargetAsync`
branches on `ActionType.Spell` and skips `DerivedAttack.Pf2e`. Read `spellAttackBonus` like 5e does. Tests:
`Pf2eRulesetResolverTests.cs`. Migration: existing casters get it on the next bootstrap re-run; until then fall back
to the DC minus 10, with a note (a PF2e spell DC is attack + 10).

**M-51 MAP (F16).** Count spell attacks toward MAP (`:713`). Agile weapons use -4 and -8: a data flag via
`weaponTags` (`agile`), read in `WeaponProfile` (`AttackDerivation.cs:6-21`). The data is already there: generated PF2e weapons carry `agile` in their item `tags`
(for example `RulesetData/pf2e/items/shortsword.yaml`), so no generator or overlay change is needed, provided
`WeaponParameterResolver` copies item tags into the `weaponTags` parameter (confirm when implementing). Tests in
`Pf2eRulesetResolverTests.cs`.

**M-52 Dying, wounded, concentration house rule (blocked on D2).** If D2 says implement: add `dying N` and
`wounded N` as condition data (M-23) and a PF2e branch in `DamageApplication` (reaching 0 applies dying 1 plus
wounded; a crit adds 2). Remove the 5e concentration save for PF2e (`HpChangeHandler.cs:230-240`), unless D2 keeps it
as a documented house rule. Tests: `Pf2eRulesetResolverTests.cs`.

**M-53 PF2e mitigation.** Covered by M-10; add weakness, and verify the PF2e save path doubles damage on a critical
failure after mitigation order is correct.

**Phase 6 status (2026-10-04): M-50 to M-53 implemented.** What was built, and where it differs from the tickets:

- **M-50.** `Pf2eExtension.SpellAttackBonus` (the plan said `SpellAttack`; the 5e name and the action parameter are `spellAttackBonus`, so
  models use one key). Derived by `Pf2eDeriveSpellcastingStep` as the stored (or derived) DC minus 10. A spell action rolls it through
  `DerivedAttack.Pf2eSpell`: explicit `bonus`, then the `spellAttackBonus` parameter, then the sheet, then DC minus 10 with a note. No
  weapon proficiency, no STR on damage, no Sneak Attack. The auditor's "caster unwired" finding checks it too. Found on the way: the
  bootstrap fills a spell DC once and never refreshed it, so it froze at the level first derived; the level-up handler now moves a
  stored DC and attack by the formula's change (`LevelUpDerivedStats`, the M-44 pattern).
- **M-51.** A spell that rolls an attack counts toward MAP (`TryConsumeActionSlot`; a save spell does not). `WeaponProfile.Agile`:
  -4/-8 instead of -5/-10; an explicit `mapPenalty` still wins. The tags reach the resolver through
  `WeaponParameterResolver.ApplyWeaponItemProperties`, as the plan expected.
- **M-52.** `Pf2eDying` holds the counters as statuses ("Dying 2", the exhaustion convention) and the recovery rules.
  `HitPointState.IsDying` is system-aware (so combat gives a dying PF2e PC its turn and the prompt reads RECOVERY CHECK);
  `UsesDeathSaves` stays 5e-only. `DamageApplication`: 0 HP gives dying 1 (2 on a critical) plus wounded, damage while dying adds
  1 (2), no second wounded, dying 4 minus doomed is death, (no instant-death rule: PF2e's massive-damage threshold could not be verified against Player Core here, so none is guessed).
  `HitPointState.SyncDowned` also clears a stale dying on anyone above 0 HP (a revival or upsert that sets HP directly must not leave an action-blocking status). `HitPointState.Set` ends dying and adds one wounded when HP rises above 0 (every heal and top-up passes through it).
  `DeathSaveChangeHandler` runs the recovery check for a PF2e PC: flat d20 vs DC 10 + dying, no modifiers, nat 20/1 move a step,
  dying that reaches 0 ends and adds wounded. Concentration: the 5e damage save is gone for PF2e; 0 HP or incapacitation still
  ends concentration (a house rule, kept). New `doomed.yaml`. A long rest ends wounded for a PC already at max HP (doomed uses the
  generic stacking long-rest decrement). PCs only, like 5e.
- **M-53.** Weakness was already in `DamageResolver` (Phase 2: a negative flat value). Added: the save-spell path now sets
  `Critical` on a critical failure (dying 2 needs it), and tests pin the order (double, then weakness or resistance; half, then
  resistance; a Strike crit doubles first).
- **Tests (38 new in `MechanicsPhase6Tests`).** Changed on purpose: `HpChange_Pf2e_FailedFortitudeSave_BreaksConcentration` now says PF2e
  damage calls for no save (the three PF2e Fortitude-save tests became that one), and
  `Pf2e_OnlyTheDocumentedConditionsAreProseOnly` lists `doomed`.
- **Not built.** Hero Points, Treat Wounds (wounded ends on a long rest at full HP only, and rest does not heal until M-70), the Unity
  client's PF2e dying display, PF2e instant death. Weakness applies per damage packet, not once per instance of a type.
  `SpellProficiency` is filled once by `Pf2eDeriveProficiencyStep`, so a stored DC does not pick up the Expert jump at level 7 (the
  level-up delta covers level and ability only).
- **Also fixed.** `SpellResolutionHelper.InferMode` treated `save` without `dc` as an attack, so the derived-DC fallback was
  unreachable and MAP would now have counted it; `save` alone is a save (5e derives its DC too).

### Phase 7: spellcasting bookkeeping (D4)

**M-60 Reject an unpaired slot spend (D4: block by default, `Warn` via config).** The engine knows the spell (`SpellDefinitionProvider`) and its level, and the
batch is visible (`context.Batch`, as `ItemEquipHandler.BuildBatchReorderNudge` already uses). When a leveled
`RulesetAction` Spell has no `ResourceChange` for a `spell_slots_*` pool in the same batch, fail the batch (or emit a message when the flag is `Warn`). Tests: a new `SpellSlotPairingTests.cs` (nothing covers this today). Acceptance: forgetting the spend is visible.

**M-61 Known and prepared validation.** Compare the cast against the character's recorded spells (the creation
recipes already track known and prepared groups: `RecipeCharacterCreation.cs:745-754`). Block by default per D4; a
`CampaignConfig` option relaxes it to a warning. Ritual casting, innate racial spells and homebrew spells are exempt: never block spells
the engine does not know (the gate already treats unknown spells as pass-through, `RulesetActionHandler.cs:~330`).

**M-62 Auto concentration.** A cast of a concentration spell (`spell.Concentration == true`) adds the M-08 marker
status through the existing `StatusChangeHandler`, which already breaks the prior one (the concentration block in
`HandleAdd`). Tests:
`ConcentrationBreakTests.cs`.

**Phase 7 status (2026-10-04): M-60, M-61 and M-62 built.** Tests: `SpellBookkeepingTests` (26).
- The gate sits next to the component gate in `RulesetActionHandler` (`SpellBookkeeping.cs`), so a refusal rolls nothing.
  Setting: the campaign option `spellBookkeeping` in `SystemOptions` (`warn` turns refusals into messages; default blocks). This
  replaces the plan's `SpellBookkeeping:Enforcement` host-config key: it is per campaign and needs no SDK change.
- M-60: a leveled cast needs a `ResourceChange` spend (`spell_slots_N`, `pact_magic` or `focus_points`, same caster) anywhere in the
  batch. Passes when there is no batch, the caster has no `spell_slots_*` pools, the spell has no definition (homebrew), or the
  action carries `ritual`, `innate`, `focus` or `item` = true. A slot spent in an earlier commit is not seen: the model must pair them.
- M-61: cantrips against `Cantrips`; leveled spells against `Prepared` when that list is non-empty, else `Known`. Skipped when all
  three lists are empty. Names compare as slugs.
- M-62: 5e only. PF2e spell data sets `concentration: true` on 357 of 373 spells (the *concentrate* trait, not 5e concentration),
  so applying it there would be wrong. Dispatched through `StatusChangeHandler`, so the earlier concentration breaks. Skipped when the
  batch already adds a concentration status for the caster. Homebrew (`CustomSpell`) spells get no auto concentration.
- Not done: the prompts and skills still tell the model to commit the status by hand (Phase 10, M-91).

### Phase 8: rest and survival

**M-70 Rest HP and hit dice (blocked on D1).** If the engine owns it: long rest restores HP to max and half of the
total hit dice (a `hit_dice` pool, from the class data, recovering on long rest like other pools); short rest lets the
caller spend hit dice (`resource` spend plus a roll). If delegated, M-01's doc fix is enough, and the rest result
should say "no HP restored" explicitly. Tests: `RestRecoveryLogicTests.cs`, `RestThenAdvanceWorldIntegrationTests.cs`.

**M-71 Long rest and deprivation.** The SRD decrement needs food and drink. `ClearUntilLongRestConditionsAsync`
decrements unconditionally, so a party that starves while resting nightly nets no exhaustion. Check the character's
hunger and thirst needs against the same thresholds `SurvivalDeprivationRule` uses
(`SurvivalThresholds.RecoveryHunger/RecoveryThirst`) before decrementing, and say why when it doesn't. Also make
`CloneStatusEffect` (`RestChangeHandler.cs`, end of file) copy `ExpiresAtOwnTurnStart` and `PendingDamage`, which it
silently drops today. Tests: `RestThenAdvanceWorldIntegrationTests.cs`.

**Phase 8 status (2026-10-04): M-70 and M-71 built.** Tests: `MechanicsPhase8Tests` (16).
- **M-70.** A long rest (`RestChangeHandler`) heals: 5e to the effective maximum (exhaustion 4 lowers it), PF2e by Constitution
  modifier (at least 1) x level, as Player Core's Rest says. A dying character isn't healed (a message says so); a stable one at 0 wakes.
  New hand-authored pool `RulesetData/dnd5e/pools/hit_dice.yaml` (one per level, `recovery: Never`): the rest returns half
  (at least one) itself. `RepairMissingResourcePools` adds it to existing characters. A short rest heals nothing; its result says to
  spend `hit_dice` (resource, delta -1) and commit the rolled HP. The engine doesn't roll hit dice. Not done: a hit die's size isn't
  tracked per class (a multiclass character reads `hitDie` or the class), and PF2e has no short-rest healing (Treat Wounds is still open).
- **M-71.** The 5e exhaustion decrement is skipped, with a reason, when the character is starving or parched. **Deviation:** the gate
  is `SevereHunger`/`SevereThirst` (90), not the plan's `RecoveryHunger`/`RecoveryThirst` (20): a default character sits at hunger 25 and
  thirst 20, so the plan's line would block nearly every rest. PF2e doomed isn't gated. `CloneStatusEffect` now copies
  `ExpiresAtOwnTurnStart`, `PendingDamage`, `EffectKey` and `EffectTier`. Needs are read before the rest's own hours accrue.

### Phase 9: range and small rules

**M-80 Range.** Add a minimum-range and "ranged attack with a hostile within Touch" disadvantage, and a long-range
band, as provider logic over the existing spatial bands. Keep it permissive when positions are absent (the current
design). Make `aoeRadius` check the *origin point* range separately from the radius (`RangeValidationHelper.cs:36-100`).
Tests: new `RangeValidationTests.cs` (no test file for `RangeValidationHelper` was found by name).

**M-81 Reaction reset.** Move the `ReactionAvailable = true` reset from the round wrap to each combatant's turn start
(`CombatTools.cs:376-380`). Tests: `CombatToolsTests.cs`.

### Phase 10: client, prompts, docs

**M-90 Unity client.** The grep of `UnityClient/Assets/CampaignVault/Scripts` found HP fractions and the level-up
overlay but no death-save or condition handling. Add the death-save tally to the party frame and condition chips from
the status list. This was only skimmed; confirm the model classes before estimating. Unity tests: run the editor
binary directly (`-batchmode -runTests`), per the project notes.

**M-91 Prompts and docs.** Update `DmHelpManual.cs`, `recommended-system-prompt*.md`, `claude_skills/` (dnd-combat),
and `ToolCallExamples.cs` so nothing tells the model to hand-apply what the engine now applies (conditions, dying,
slots). Update `RULES_GAPS.md`: move what shipped to "Done", keep the rest.

---

## 7a. Plugin SDK compatibility (cross-cutting)

Types these tickets touch live in the plugin SDK (`src/CampaignVault.PluginSdk`, version 0.16.0, packed by
`scripts/pack-pluginsdk.sh` into `nupkgs/`), and the plugins under `plugins/` (CraftingMode, MedievalWeapons,
NecromancersGrimoire, ShadowAndSteel, TrailAndHearth) plus the sample plugins that build into the host's `Plugins`
folder compile against it:

| Ticket | SDK type touched |
|---|---|
| M-05 | `SystemExtension` (base AC field) |
| M-13 | `HpChange` (`Critical`) |
| M-15 | `Character` (`TempHp`) |
| M-21 | `RollResolution`, `RollModifier` (`AutoFail`) |
| M-50 | `Pf2eExtension` (`SpellAttack`) |
| M-32, M-33 | `RollKinds` (new kinds) |
| M-20 | condition data types, if `ConditionDefinition` lives in the SDK (check; it is in `CampaignVault.Data.Templates`) |

Rules:
1. **Additive only.** New members are nullable or have defaults. Do not add constructor parameters to the positional
   records `RollModifier(Source, Bonus, Advantage, Reason)` and `RollResolution(Bonus, Advantage, Notes)`
   (`IRollModifierProvider.cs:44-47`); add init-only properties instead, or every shipped plugin that constructs one
   stops compiling.
2. **Stored documents.** `Character` and the extensions are persisted (RavenDB) through
   `SystemExtensionNewtonsoftConverter`; new members must deserialize from older documents without migration.
3. **Commit schema.** `HpChange.Critical` changes the generated commit schema; update the snapshot that
   `CommitSchemaDriftTests.cs` and `CommitEnumCheatSheetTests.cs` guard, deliberately.
4. **Version.** One minor bump for the whole set (0.16.0 to 0.17.0) in `CampaignVault.PluginSdk.csproj`, then
   `scripts/pack-pluginsdk.sh` (it removes and recreates `nupkgs/`), and a note in the changelog or `PLUGINS.md` about
   the new members. Check `minEngineVersion` handling for plugins that want the new members.
5. **Build the plugins.** Phase 11 builds every plugin project against the changed SDK, one build at a time.

---

## 8. Decisions (resolved by Michael)

| ID | Decision | Consequence for the tickets |
|---|---|---|
| D1 | **Engine owns rest healing.** | M-70 and M-01 go ahead as written: long rest restores HP, hit dice and exhaustion; short-rest hit dice follow. |
| D2 | **Yes.** PF2e gets dying and wounded; the 5e concentration save is removed for PF2e. | M-52 goes ahead. |
| D3 | **Yes.** Temp HP in scope, shipped after M-10. Grants are generic (spell, feat, class feature, item), so M-15 exposes one `grant_temp_hp` path, not a spell special case. Rule: temp HP does not stack, the larger value wins unless the source says otherwise. | M-15 goes ahead. Add a source tag so the sheet shows where it came from. |
| D4 | **Block**, behind a config flag. The engine is the source of truth. Homebrew and innate spells stay exempt. | M-60..M-6x: the error path replaces the warning path. The flag (`SpellBookkeeping:Enforcement` = `Block` default, `Warn` available) is for tables that want leniency. |
| D5 | **Block**, with an explicit DM override parameter (`overridePrerequisites: true`) that is recorded in the change log. | M-41: enforce the 13-in-key-ability rule for both the new class and the current one, and give the model a clear error naming the unmet ability. |
| D6 | **Prompt the model** for `death_save` on a dying PC's turn. The AI DM may instead adjudicate through the normal action path (a stabilising effect, a revive, divine intervention), because the prompt is not a forced roll. | M-40: turn-start surfaces a "dying, death save due" notice; nothing auto-rolls. The auto-roll option is dropped. |

---

## 9. What was and was not reviewed

Read in depth: `Dnd5eRulesetResolver`, `Pf2eRulesetResolver`, `RulesetResolverBase`, `RollModifierPipeline`,
`StatusEffectModifierProvider`, `FeatEffectRules`, `CombatFeatureRules`, `AttackDerivation`, `ArmorParameterResolver`,
`SpeedRules`, `RangeValidationHelper`, `RulesetActionHandler`, `HpChangeHandler`, `DeathSaveChangeHandler`,
`DeathChangeHandler`, `RestChangeHandler`, `RestRecoveryLogic`, `StatusChangeHandler.HandleAdd`, `ItemEquipHandler`,
`ResourceChangeHandler`, `XpGrantChangeHandler`, `XpThresholdReachedPressureContributor`, `DelayedTickProcessor`,
`DefaultRollService`, `CombatTools.NextTurn`, `Dnd5eDeriveHitPointsStep`, `Pf2eDeriveHitPointsStep`,
`BootstrapClassLevelHelper`, `LevelUpPlanner.Plan`, the level-up handler, `ConditionDefinition`, all 5e and PF2e
condition YAMLs, `SurvivalDeprivationRule`, `Dnd5eExhaustionPressureContributor`, `EngagementMutationHelper`,
`ActionBlock`, `PluginSystems`, `IRollModifierProvider`.

Skimmed or sampled only (findings here are lower confidence):

- `ResourcePoolInitializer`, `Dnd5eCasterLevelHelper` and the multiclass slot tables, `Dnd5eDeriveProficiencyStep`:
  grep-level only, no defects found, not checked against the SRD tables.
- `ClimateExposureRule`, `NeedsAccumulationRule`, `TravelChangeHandler` (only the speed hook), `EncounterResolver`
  (only its signature and chance inputs): structure only.
- The 33 pressure contributors: only the exhaustion, XP-threshold and concentration-adjacent ones were read.
- `TetherChangeHandler`, `TetherState`, `ModeTransitionChangeHandler`, `EngagementRelationChangeHandler` beyond its
  first 80 lines, the sample plugins' content, and the Unity client (grep for HP, AC, death-save and level-up only):
  structure or grep only.
- `WeaponParameterResolver` (whether it copies item tags into `weaponTags`), the 5e class progression YAML contents,
  `LevelUpPlanner` beyond `Plan`, and PF2e spell and item data beyond one `agile` grep: not read.

Tests were located by file name and `grep`, not run. Names used in this plan were checked against
`tests/CampaignVault.UnitTests/`; files marked "new" do not exist. Existing and checked: `CombatToolsTests`,
`CombatE2ETests`, `DelayedTickProcessorTests`, `ConcentrationBreakTests`, `DeathConsumerTests`, `DeathTests`,
`Dnd5eRulesetResolverTests`, `Pf2eRulesetResolverTests`, `ArmorParameterResolverTests`, `RestChangeHandlerTests`,
`RestRecoveryLogicTests`, `RestThenAdvanceWorldIntegrationTests`, `CharacterLevelUpToolsTests`,
`LevelUpChoiceApplicationTests`, `LevelUpPlannerTests`, `Dnd5eMulticlassTests`, `ConditionDefinitionTests`,
`FeatEffectRulesTests`, `GrappleRulesetTests`, `SpeedContextTests`, `TravelSpeedTests`, `SurvivalDeprivationRuleTests`,
`BootstrapHpResolverTests`, `CommitSchemaDriftTests`, `CommitEnumCheatSheetTests`. Check whether an existing test pins
the old behavior before changing it (M-00).

## 10. Phase 11: verification gate

1. One build at a time, no overlap, no background builds, no wakeup polling. If the SDK changed (§7a): build the host,
   then each plugin project under `plugins/`, sequentially, then `scripts/pack-pluginsdk.sh`. If a build hangs, stop and
   ask.
2. Run every xunit v3 test executable directly (not `dotnet test`); run Unity tests only if M-90 was touched.
3. Report the full result, including pre-existing failures, and wait for Michael's confirmation before calling any
   phase complete.
4. Re-run the checklist in §1: every phase is checked off or explicitly deferred with a reason.
