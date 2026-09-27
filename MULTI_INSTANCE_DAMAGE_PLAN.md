# Multi-instance damage: implementation plan

Written after an audit of the combat resolvers against actual dnd5e/pf2e rules (see chat log —
not reproduced here). Verdict that motivates this plan: `Dnd5eRulesetResolver` and
`Pf2eRulesetResolver` both model exactly one shape — **one roll expression, one flat bonus, one
hit/miss check against one target's AC/DC, one immediate HP delta**. That shape is correct for the
majority of SRD content (cantrips, most save-for-half spells, ordinary weapon swings) but wrong,
silently, for a small and specific set of iconic spells that don't fit it:

- **Magic Missile** — no attack roll (auto-hit), 3+ discrete darts that can be split across up to 3
  targets. dnd5eapi.co's `damage_at_slot_level` gives the *pre-summed all-darts total*
  (`"1": "3d4 + 3"`), not the per-dart `1d4+1` the rule describes.
- **Scorching Ray** — `damage_at_slot_level["2"]` is the damage of *one ray*; the ray count (3 at
  2nd level, +1/slot above) isn't in the API data anywhere.
- **Acid Arrow** — deals **half damage even on a miss**, plus a **second damage tick at the end of
  the target's next turn**. Neither is in the API's `damage` entry; the engine currently returns
  flat 0 on any miss and has no concept of delayed damage at all.

Confirmed against live dnd5eapi.co queries (Fireball, Magic Missile, Acid Arrow, Scorching Ray) —
see the chat transcript for the raw JSON.

## Design principle: generic machinery, small curated data overlay

Do **not** try to auto-detect "this spell is multi-instance" from the API's prose (`desc`/
`higher_level` text) — that's the exact unstructured-text problem already ruled out for pf2e spell
damage, for the same reasons (inconsistent phrasing, branches, multiple pools in one sentence).
dnd5e SRD 5.1 damage spells are a **closed, small set** (~150 spells), so instead: build the engine
primitives generically (instance loops, delayed-tick scheduling, miss-behavior), and drive them
from a small hand-verified overlay naming the dozen or so spells that actually need them. Every
other spell keeps behaving exactly as today.

## Order of execution

Each phase ends in a buildable, fully-green state — stop after any phase if needed.

### Phase 0 — Spell audit, no code

Read every SRD 5.1 spell that has a `damage` entry (`www.dnd5eapi.co/api/2014/spells`, filter
`damage != null`, ~150 entries) and classify each by `desc`/`higher_level` text into:

- **Simple** (the current model already handles it correctly) — the default, no overlay entry.
- **Multi-instance** — N independently-resolved sub-attacks/darts per cast, N possibly scaling with
  slot level or caster level. Known so far: Magic Missile, Scorching Ray, Eldritch Blast (cantrip,
  beam count scales with character level same as damage-die tier does for other cantrips).
- **Miss-still-damages** — deals damage (usually half) even on a failed attack roll. Known: Acid
  Arrow.
- **Delayed-tick** — schedules a second (or later) damage application at a defined future turn
  boundary. Known: Acid Arrow (only one found in core SRD on this pass; flag if the audit turns up
  more, e.g. anything phrased "at the start/end of its next turn").

Output: `scripts/spell_damage_overlay.yaml` (or `.json`, match `generate_spells.py`'s existing
style), hand-written and hand-verified against the actual spell text — this file is never
regenerated from an API, unlike everything else in `RulesetData/`. Each entry only *adds* shape
metadata; it never overrides the API's own dice numbers.

Exit criteria: overlay file committed, each entry cites the spell's exact rules text in a comment
(same evidentiary standard as this plan's own findings — no entry without a quote to back it).

### Phase 1 — Schema

Extend `SpellDefinition` (`src/CampaignVault/Data/Templates/SpellDefinition.cs`) with:

```csharp
/// <summary>dnd5e only. Number of independent damage instances per cast (e.g. Magic Missile's darts,
/// Scorching Ray's rays), keyed by spell-slot level for leveled spells or character level for
/// scaling cantrips. Omitted (null) means 1 — the existing single-instance behavior.</summary>
public Dictionary<int, int>? InstanceCountAtSlotLevel { get; init; }
public Dictionary<int, int>? InstanceCountAtCharacterLevel { get; init; }

/// <summary>dnd5e only. Per-instance damage dice, when InstanceCount* is set — this is what
/// DamageAtSlotLevel/DamageAtCharacterLevel means for a multi-instance spell (the API's own field
/// there is the pre-summed all-instances total and is kept as-is for narrative/reference only).</summary>
public Dictionary<int, string>? PerInstanceDamageAtSlotLevel { get; init; }
public Dictionary<int, string>? PerInstanceDamageAtCharacterLevel { get; init; }

/// <summary>dnd5e only. False for auto-hit spells (Magic Missile) — skips the attack roll entirely.
/// Omitted means true (the existing behavior).</summary>
public bool? RequiresAttackRoll { get; init; }

/// <summary>dnd5e only. What happens to the immediate damage instance on a missed attack roll.
/// Omitted/None means the existing behavior (0 damage on miss).</summary>
public MissBehavior? OnMiss { get; init; } // enum: None, Half

/// <summary>dnd5e only. A second damage application scheduled for a later turn boundary
/// (Acid Arrow's "2d4 at the end of its next turn"). Null means no delayed tick.</summary>
public DelayedDamageTick? DelayedTick { get; init; }
```

`DelayedDamageTick` (new small record): `DiceExpression`, `DamageType`, `TriggerAt` (enum:
`EndOfTargetNextTurn` for now — the only shape Phase 0 found; extend the enum, don't generalize
prematurely, if the audit later finds a second shape), `RequiresInitialHit` (bool — Acid Arrow's
tick only fires if the initial attack hit).

Update `SpellDefinition.Merge` to carry the new fields through (same `child ?? parent` pattern as
every existing field).

Update `generate_spells.py`: after `parse_dnd5e_mechanics(detail)`, merge in
`spell_damage_overlay.yaml`'s entry for this slug if one exists — overlay fields are added
verbatim, API-derived fields (`damageAtSlotLevel` etc.) are untouched. Update `write_spell` to
serialize the new keys (same `if body.get(key): lines.append(...)` pattern already used for every
other optional field).

Regenerate (`python3 scripts/generate_spells.py`), diff-review: every file except the Phase-0-
flagged dozen should be byte-identical to before.

Exit criteria: full build green, spell YAML diff reviewed and matches expectations, existing test
suite unaffected (no resolver code touched yet).

### Phase 2 — Multi-instance resolution (Magic Missile / Scorching Ray shape)

`AttackTargetHelper.cs` currently exposes `SelectTargets`/`ResolveAttackCount`, which *caps* how
many of the *listed* `targetIds` get attacked — it has no path for firing more instances than
targets listed (Scorching Ray's 3 rays at 1 target). Add:

```csharp
public static IReadOnlyList<string> DistributeInstances(IReadOnlyList<string> targetIds, int instanceCount)
```

Round-robin: `instances[i] -> targetIds[i % targetIds.Count]`. 3 targets + 3 instances → 1 each; 1
target + 3 instances → all 3 at that target; 2 targets + 3 instances → 2/1 split (all three are
RAW-legal distributions for Magic Missile). `attackCount`/`shots`/etc. stays as the existing
explicit override for callers who want to hand-pick the split instead (unchanged behavior for every
non-flagged spell/weapon).

In `Dnd5eRulesetResolver`:

- `ResolveAttackAsync`: when the target spell has `InstanceCountAtSlotLevel`/
  `InstanceCountAtCharacterLevel` data (looked up the same way `BuildSpellDamageWarning` already
  looks up tier data), resolve the instance count, call `DistributeInstances`, and loop over
  *instances* (each with its own `ResolveAttackAgainstTargetAsync` call, own roll, own narrative
  line) instead of looping over `targets` directly. Each instance uses `PerInstanceDamageAtSlotLevel`/
  `PerInstanceDamageAtCharacterLevel` as its `damageDice`, not the caller-supplied value — this is
  the one place a multi-instance spell's damage is *derived*, not caller-sent, because "instance
  count" and "per-instance dice" are two numbers a caller has no clean single parameter to express
  today (`damageDice` is one string). A caller can still override via a new `instanceDamageDice`
  parameter if they want to (e.g. a buffed/reduced dart), same escape hatch as every other
  parameter in this engine.
- `ResolveAttackAgainstTargetAsync`: when `RequiresAttackRoll == false` (Magic Missile), skip the
  attack roll and `ac` comparison entirely, treat as an automatic hit, apply damage directly.
- `BuildSpellDamageWarning`: when the spell has instance data, compare `damageDice` against
  `PerInstanceDamageAtSlotLevel`/`PerInstanceDamageAtCharacterLevel` instead of the flat tier table
  it uses today — this is what stops it from false-warning on a RAW-legal Magic Missile
  target-split (the actual bug named at the top of this file).

`Pf2eRulesetResolver`: no change in this phase — pf2e spell damage isn't validated at all today
(no structured source data), so there's nothing to extend it against; revisit only if a future pf2e
data source changes that premise.

New tests in `Dnd5eRulesetResolverTests.cs`, mirroring the existing
`ResolveAttack_Spell_WrongCantripTier_WarnsButStillApplies` shape:
- `ResolveAttack_MagicMissile_AutoHitsWithoutAttackRoll`
- `ResolveAttack_MagicMissile_SplitAcrossThreeTargets_AppliesIndependently`
- `ResolveAttack_ScorchingRay_ThreeRaysAtOneTarget_EachRolledIndependently`
- `ResolveAttack_ScorchingRay_PerRayDamage_DoesNotFalseWarnAgainstSummedTier`

Exit criteria: full build + full test suite green, including the new tests above.

### Phase 3 — Miss-still-damages (Acid Arrow, immediate half only)

In `ResolveAttackAgainstTargetAsync`'s `if (!isHit)` branch: if the resolved spell's `OnMiss ==
MissBehavior.Half`, roll and apply half the instance's damage (reuse the existing crit-roll-then-
combine pattern, floor division same as `TryApplySaveDamageAsync`'s half-on-save math) instead of
returning 0 with no HP change. Narrative distinguishes "Missed, but the acid still splashes for N"
from a clean miss.

New test: `ResolveAttack_AcidArrow_Miss_StillAppliesHalfDamage`.

Exit criteria: full build + test suite green.

### Phase 4 — Delayed tick (Acid Arrow's second half — the actually hard part)

This is the one piece that needs new *scheduling* infrastructure, not just resolver logic, because
nothing in the engine currently applies a mutation when a status effect expires — `StatusExpiryRule`
and `CombatTools.NextTurn`'s round-based expiry (`CombatTools.cs:301-315`) both just remove the
effect and emit a narrative string. And the existing round-based expiry only fires on **round
wraparound** (`if (next == null) { ... }`), never on an individual combatant's own turn start —
Acid Arrow's "end of *its* next turn" needs the latter, not the former.

1. **`StatusEffect` schema** (wherever it's currently defined — confirm exact file before touching;
   referenced from `CampaignVault.Models`/`SystemExtension.StatusEffects`): add two optional
   fields:
   - `PendingDamage { DiceExpression, DamageType }` — rolled and applied as an `HpChange` when this
     effect expires, instead of (in addition to) just being removed.
   - `ExpiresAtOwnTurnStart: bool` — relative expiry ("next time it becomes this character's own
     turn"), as an alternative to the existing absolute `ExpiresAtRound: int`. Needed because
     "end of target's next turn" can't be expressed as a single absolute round number at cast time
     when the caster and target don't act in the same initiative slot.

2. **`CombatTools.NextTurn`**: add a per-advance check (runs on *every* call, not just the
   round-wraparound branch) — when the engine is about to set `encounter.ActiveTurnId = next.CharacterId`,
   check `next`'s own `StatusEffects` for any with `ExpiresAtOwnTurnStart == true`. For each: if it
   carries `PendingDamage`, roll it (via the same `IRollService` already injected into the tool) and
   append an `HpChange` mutation to the batch; then remove the effect, same as the existing round-
   based path. This is new mutation-emission from inside a tool method that currently only emits
   narrative strings and direct `StatusEffects.Remove` calls — check `CombatTools`'s existing
   mutation-commit plumbing (how `HpChange` mutations reach `WorldChangeDispatcher` from other call
   sites in this file, if any) before assuming `mutations.Add(...)` is available at this point; if
   `NextTurn` doesn't already have a mutation batch in scope, this may need to route through
   `IChangeContext`/`ChangeContext` the way the resolvers do, which is the main open design question
   of this phase — resolve it by reading `WorldChangeDispatcher.cs` and one or two other
   `NextTurn`-adjacent call sites before writing code, not by guessing.

3. **`Dnd5eRulesetResolver`**: when a Phase-0-flagged spell with `DelayedTick` data hits (respecting
   `RequiresInitialHit`), emit a `StatusChange` mutation alongside the existing `HpChange` — a
   synthetic status effect (e.g. `"AcidArrowResidue"`) carrying `PendingDamage` = the tick's dice/
   type and `ExpiresAtOwnTurnStart = true`, targeted at the target character. This reuses the
   existing `StatusChange` WorldChange type and `StatusChangeHandler` — no new WorldChange type
   needed, only the new optional fields from step 1.

New tests: this phase is not resolver-unit-testable in isolation (the payoff happens on a later
`NextTurn` call, not inside `ResolveAttackAsync`) — needs an integration-style test that casts Acid
Arrow, advances through a full round via `NextTurn`, and asserts the second HP delta landed. Check
whether `CampaignVault.IntegrationTests` or a `CombatTools`-level unit test harness already exists
for turn-advance scenarios before deciding where this test lives.

Exit criteria: full build + full test suite green, including the new delayed-tick test.

### Phase 5 — `SaveSuccess`/`halfOnSave` cross-check

Cheapest remaining fix, same validator family as `BuildSpellDamageWarning`/
`BuildSpellSaveTypeWarning`, data already generated (`SpellDefinition.SaveSuccess`, populated from
`dc.dc_success` since the original spell-damage work) and simply never consulted. Add
`BuildSpellHalfOnSaveWarning` (or fold the check into `TryApplySaveDamageAsync` directly) in
`Dnd5eRulesetResolver.cs`: if the spell's `SaveSuccess == "none"` and the caller's effective
`halfOnSave` resolves to `true` (the default), warn — same soft-warn-don't-block pattern as the
other two. Bundle into this same PR since it touches the identical file/method family; no reason to
defer once the surrounding code is already open.

New test: `ResolveSpellSave_SaveSuccessNone_WarnsWhenHalfOnSaveDefaultsToTrue`.

Exit criteria: full build + test suite green.

### Phase 6 — pf2e weapon-side fixes (independent track, no dependency on Phases 0-5)

Two separate, smaller fixes in `Pf2eRulesetResolver.cs`, can be done in any order relative to the
phases above:

- **Agile MAP**: `ResolveAttackAgainstTargetAsync` (`Pf2eRulesetResolver.cs:227-230`) hardcodes
  `attackIndex * 5`. Read the weapon's `agile` trait — already present in `Item.Tags` from
  `generate_pf2e_items.py`, currently never read by `WeaponParameterResolver` (which only copies
  `Item.Properties`, not `Item.Tags`) — and use `attackIndex * 4` when present. Needs
  `WeaponParameterResolver.ApplyWeaponItemProperties` to also surface a `agile`/`traits` parameter
  from `Item.Tags`, not just `Item.Properties`, since MAP size is a per-attack decision the resolver
  makes, not a flat property copy.
- **Crit doubling**: document (in a code comment, `Pf2eRulesetResolver.cs:256-259`) that `finalDamage
  *= 2` is the sanctioned "double the total" table variant, not RAW's "double the dice, add the flat
  bonus once" — decide explicitly whether to switch to the RAW method (requires rolling damage dice
  twice and adding the bonus once, a small change to the roll composition, not just the final
  multiply) or keep the simplification and just document it. This is a judgment call for whoever
  reviews the plan, not something to decide unilaterally inside an implementation phase — flag it
  and ask.
- Deadly/Fatal weapon traits (extra un-doubled crit dice): out of scope for this phase — no data
  field exists for them yet (`generate_pf2e_items.py` doesn't extract weapon `trait_raw` values like
  `"Deadly d8"` into a structured property), and adding that is a data-generation task, not a
  resolver task. Note it as a follow-up, don't fold it into this plan.

### Phase 7 — Docs

- `claude_skills/dnd-combat/SKILL.md`: add a multi-instance spell example (Magic Missile split
  across targets) alongside the existing Fire Bolt/Fireball examples, and a note that a miss on
  Acid Arrow-shaped spells still narrates partial damage.
- `scripts/README.md`: document `spell_damage_overlay.yaml` — what it's for, why it's hand-authored
  and never regenerated, the evidentiary standard (every entry cites rules text) from Phase 0.
- `CLAUDE.md`: no change expected unless the overlay file needs a regeneration-requirements note
  (it doesn't need network access, so probably not).
- Delete this plan file once all phases are folded into the code/docs above (same convention as
  `CONTENT_GAPS_PLAN.md`).

## Compatibility note — read before starting Phase 2

Phase 2 is a deliberate behavior change for the ~12 flagged spells, not just an additive feature.
Today, a caller casting Magic Missile at one target the way the resolver currently expects sends
`damageDice="3d4+3"` and gets one roll. After Phase 2, the same cast (if the caller doesn't change
anything) still targets one character, but the engine now rolls 3 independent `1d4+1` instances
instead of 1 `3d4+3` instance — same expected value, different variance, and the narrative output
format changes (multiple hit lines instead of one). This is correct per RAW and is the entire point
of the phase, but: search the test suite and `dnd-combat/SKILL.md` for any existing example that
hardcodes the old single-roll Magic Missile shape before shipping Phase 2, and update it in the
same PR — don't let the plan's own example set go stale the way the deleted `CONTENT_GAPS_PLAN.md`
warned against for its own citations.

## Explicit non-goals

- No attempt to cover pf2e spell damage shape variance (heightening branches, multi-pool spells) —
  already ruled out; AoN gives no structured field to build any of this against, and the
  prose-parsing problem is categorically harder than dnd5e's closed-set overlay approach.
  (Reference: same chat session's AoN research pass, WorldChanges.cs `mcp__Claude_Docs__` — see
  raw `elasticsearch.aonprd.com` query results for Fireball/Blazing Bolt/Ignition.)
- No general "any spell can express any mechanic" engine — the overlay approach only covers the
  spells Phase 0 actually finds and verifies. A new spell shape discovered later gets its own
  overlay entry and, if it's a genuinely new *shape* (not just a new instance of an existing one),
  its own follow-up plan, not a speculative extension now.
- Deadly/Fatal pf2e weapon traits — noted as a follow-up in Phase 6, not built here.
