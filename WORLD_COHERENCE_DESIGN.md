# World Coherence Design

Design document for closing five feedback gaps where CampaignVault trusts the LLM to maintain state the engine does not own.

**Last updated:** 2026-07-01 (reconciliation pass: Item 1 rewritten against as-built `ResourcePool` implementation shipped 2026-07-01 in `07c35f3`/`d3e466f`/`841a674`/`4521ade`/`0bf7eca`; **PR-DOC gate found violated** for that work — see remediation below)  
**Single source of truth** for world-coherence work. Update this file when scope or status changes.

## Problem Statement

CampaignVault excels at scene-centric narrative state, pressure nudges, and ruleset rolling — but several world-model loops are incomplete. When the LLM is attentive in a single session, gaps are papered over. Across time skips, rested spellcasters, earned relationships, hand-authored maps, and accumulated events, the world drifts unless the model manually commits every downstream effect.

This document defines implementation for all five items. **Items 1, 2, 3, and 5a are now implemented** (Item 1 architecture differs materially from the original design below — see "Item 1: As-Built"). **Item 4 core work is landed; PR-1.1 closes remaining gaps.** Items 5b–5d remain designed but not implemented.

**⚠ PR-DOC gate violation:** Item 1 shipped without the required same-PR/before-merge doc updates. `Tools/DmHelpManual.cs`, `docs/recommended-system-prompt.md`, and `ARCHITECTURE.md` still assert "Engine does not track spell slots" — false as of `d3e466f`. See **PR-DOC gate → Item 1 remediation** below. Treat this as the next required PR before any further Item 1 work.

---

## Status Overview

| # | Gap | Severity | Status |
|---|-----|----------|--------|
| 1 | Spell slots / ability resources not tracked | High | **Implemented** (2026-07-01, as generic cross-ruleset `ResourcePool` system — see rewritten section) — PR-DOC remediation pending |
| 2 | RelationshipChange has no mechanical bite | Medium | **Implemented** (`RelationshipModifierHelper.cs` + resolver integration with tests in `Dnd5eRulesetResolverTests.cs` / `Pf2eRulesetResolverTests.cs` / `Fallout2d20RulesetResolverTests.cs`) |
| 3 | One-way location links not auto-repaired | Medium | **Implemented** (`LocationConnectivityPressureContributor.cs` + `LocationConnectivityTests.cs` with full coverage) |
| 4 | TransientEvictionRule silently deletes NPCs | Medium | **Core implemented** — PR-1.1 pending (`RecentlyDepartedPressureContributor` + integration test still absent) |
| 5a | Event consequences (suggest-only, templates) | High | **Implemented** (`EventConsequenceRegistry.cs` + tests in `EventConsequenceTests.cs`) |
| 5b–5d | Event rule application, location decay, faction coupling | High | Designed (sub-phases) — not yet implemented |

### Honest Item 4 status

| Done | Pending (PR-1.1) |
|------|------------------|
| Delta bundle (Activity, Departure event, recordDeparture, CharacterUpdate, ItemTransfer) | `AdvanceWorldAsync` integration test (persisted RecentlyDeparted + events) |
| Schema + handlers + unit tests on rule deltas | `RecentlyDepartedPressureContributor` (scene nudge + optional re-promote `SuggestedCommitJson`) |
| `AdvanceResult.evictedNpcs` | Item `currentState` on drop (open question) |
| `DmHelpManual` + `Departure` enum | RecentlyDeparted TTL in simulation (scheduled under 5b) |

Unit tests prove the **rule emits deltas**. They do **not** yet prove the **world model updates after `advance_world`**.

---

## Execution Rules

### PR-DOC gate (required after every implementation PR)

No behavioral PR is "done" until **PR-DOC-N** merges with:

| Asset | Purpose |
|-------|---------|
| `Tools/DmHelpManual.cs` | Behavior contract the LLM reads via `get_help` |
| `Tools/CommitSchemaRegistry.cs` + `CommitEnumCheatSheet.cs` | Commit surface |
| `docs/recommended-system-prompt.md` | Injected client prompt (spell slots, relationships, connectivity, eviction) |
| `Tools/ToolCallExamples.cs` | At least one canonical example per new behavior |
| `ARCHITECTURE.md` | One paragraph per new rule/contributor |

**Timing (no grace window):**

PR-DOC must land in the **same PR** as the implementation **or** in a **follow-up PR that merges before** the implementation PR merges to main — there is no "ship code now, docs later" window. Concretely:

1. Implementation PR **must not merge** until PR-DOC for that item is merged (same PR is preferred).
2. **Safety gates** (strict validation, removed normalization, new required params) **must not be enabled** until PR-DOC merges.
3. If implementation and docs split across two PRs, the doc PR merges **first** or **atomically with** the code PR (stacked). Never merge code-only and "get to docs next sprint."

**Gate examples:**

- Do **not** enforce strict `slotLevel` on spells until PR-DOC-4b merges (today `DmHelpManual` still says slots are not tracked).
- Do **not** default auto-repair connectivity to `true` until PR-DOC-3 + authoring `oneWay` UX merge.

### Item 1 PR-DOC remediation (found 2026-07-01 — gate violated)

The resource-pool system (`07c35f3`→`0bf7eca`) merged without honoring this gate. Compliance as found:

| Asset | State | Evidence |
|-------|-------|----------|
| `Tools/CommitSchemaRegistry.cs` | ✓ In sync | `resource` commit type + `spell_slots_3` example documented |
| `Tools/CommitSpellHelpExamples.cs` | ~ Partial | One-liner present; no full JSON example |
| `Tools/DmHelpManual.cs` | ✗ Stale | ~L297 still: *"Engine does not track spell slots"* |
| `docs/recommended-system-prompt.md` | ✗ Stale | ~L39, same false claim |
| `ARCHITECTURE.md` | ✗ Missing | No paragraph on `ResourceRecoveryRule` / `ResourcePoolProvider` |
| `Tools/ToolCallExamples.cs` | ✗ Missing | No canonical resource-spend/rest example |

**Required next PR (documentation-only, blocks further Item 1 work):**

1. Remove the "Engine does not track spell slots" line from `DmHelpManual.cs` and `recommended-system-prompt.md`; replace with accurate `resource` commit guidance (pool names, `spellName` requirement for validation, clamp-not-fail behavior on empty pools).
2. Add an `ARCHITECTURE.md` paragraph describing `ResourcePool`/`ResourcePoolProvider`/`ResourceRecoveryRule`.
3. Add a canonical resource-spend and rest-then-recover example to `ToolCallExamples.cs`.
4. **Do not** claim "second cast fails when out of slots" in any of the above until the `ResourceChangeHandler` empty-pool clamp-vs-fail gap (see Item 1: As-Built) is actually fixed.

### Pre-implementation audit (before each PR)

```powershell
# Relationship consumers (stacking risk)
rg "Relationships" src/CampaignVault --glob "*.cs"

# Spell / rest touchpoints
rg "ResolveSpell|RestChange|SpellSlots|classResources|SystemStatsMerger" src --glob "*.cs"

# Connectivity entry paths
rg "addExit|ConnectedFrom|Exits|upsert_location" src --glob "*.cs"

# Event → state precedents
rg "EventOccurred|PlotThread|QuestStaleness|FactionEcosystem|PointOfInterestPressure" src/CampaignVault/Data --glob "*.cs"
```

Cross-check hits against the **Touchpoint Matrix** below. Do not rely on "and any others" in PR descriptions.

### Guard tests (cross-cutting)

Add or extend these in `CampaignVault.Tests` as contracts land:

| Guard | PR | Item | Asserts |
|-------|-----|------|---------|
| `AdvanceWorld_PersistsRecentlyDeparted_AndDepartureEvent` | PR-1.1 | 4 | Full `AdvanceWorldAsync` → location doc + event query — **still not written** |
| `ResourcePoolInitializer_Wizard5_FillsSlots` (as-built name) | Shipped | 1 | `ResourcePoolInitializerTests.cs` — bootstrap-equivalent populates `spell_slots_*` on create ✓ |
| `SpellCast_DecrementsSlot` (as-built via `SpellDefinitionTests.cs` fireball spend) | Shipped | 1 | Commit `resource` → pool `Current` decreases ✓ |
| `SpellCast_FailedSave_StillConsumesSlot` | **Not implemented** | 1 | No resolver-layer gate before roll exists; slot spend is a separate `resource` commit the LLM must remember to make — RAW behavior is enforced by convention, not by the engine |
| Narrative opt-out (no named test, structural) | Shipped | 1 | `ResourcePoolProvider` registers no YAML for `Narrative` → empty `ResourcePools` ✓ |
| `LongRest_RestoresSlots` / `ShortRest_RestoresKiOnly` (as-built: `ResourceRecoveryRuleTests.cs`) | Shipped | 1 | 9 facts covering long/short/daily recovery + rest-type hierarchy ✓ |
| `ResourceChangeHandler_EmptyPool_DoesNotHardFail` (gap — should be added) | **Missing** | 1 | Documents/asserts the known clamp-not-fail gap so it isn't "fixed" silently without a doc update |
| `SocialCheck_IncludesRelationshipModifier_InNarrative` | PR-2 | 2 | Resolver output contains `(trusted friend)` tag |
| `SocialCheck_Athletics_UnaffectedByRelationship` | PR-2 | 2 | Non-social skill unchanged |
| `SocialCheck_SkipsRelationship_WhenNarrativeRuleset` | PR-2 | 2 | Oracle resolver unchanged |
| `OneWayExit_SurfacesSuggestedCommitJson` | PR-3 | 3 | `WorldPressureItem.SuggestedCommitJson` populated |
| `AddExit_DoesNotAutoRepair_WhenOneWayOrConfigDisabled` | PR-3 | 3 | Intentional one-way preserved |
| `AuthoringSave_FlagsOrAcceptsOneWayExits` | PR-3 | 3 | See Item 3 acceptance tests |
| `CombatEvent_SuggestsLocationUpdate_OnNextGetScene` | PR-5a | 5 | Template → pressure only, no double-apply |
| `EventConsequenceRule_DoesNotDoubleApply` | PR-5b | 5 | Idempotency marker respected |
| `LocationDecay_SinglePressureVoice_NoPoIDuplicate` | PR-5c | 5 | After combat + time skip, at most one decay pressure from PoI **or** LocationDecayRule |

Per-item unit tests alone are insufficient for coherence — prefer **integration tests through `StageChangesAsync` / `AdvanceWorldAsync`** where persistence matters.

---

## Touchpoint Matrix

Use this as the Phase 1-style audit checklist. **M** = modify, **A** = add new, **V** = verify only, **—** = no change expected, **⊘** = explicit opt-out (see Narrative ruleset).

**Anchor** = method/section to open first (saves search time).

### Item 1 — Resource pools (as-built, supersedes original 5e-only design)

**Done** = shipped in `07c35f3`/`d3e466f`/`841a674`/`4521ade`/`0bf7eca` (2026-06-29 to 2026-07-01). This table replaces the original bootstrap-step/Dnd5eExtension design — see **Item 1: As-Built** for why the architecture diverged.

| File / area | M/A/V | Status | Anchor | Notes |
|-------------|-------|--------|--------|-------|
| `Models/Character.cs` | Done | Done | `SystemStats.ResourcePools` ~L324, `ResourcePool` record ~L331 | Generic `Dictionary<string, ResourcePool>` replaces per-system dict fields |
| `Services/ResourcePoolProvider.cs` | Done | Done | `IRulesetYamlProvider` impl | Loads `RulesetData/{dnd5e,pf2e,fallout2d20}/pools/*.yaml` |
| `Services/ResourcePoolInitializer.cs` | Done | Done | `InitializePools` | Derives `Max` via `LevelToMaxMap`; preserves spent `Current` / drops stale pools on resync |
| `Services/Dnd5eCasterLevelHelper.cs` | Done | Done | `ComputeCasterLevel` | Full/half/third-caster stacking; Warlock excluded (0) |
| `Data/ChangeHandlers/ResourceChangeHandler.cs` | Done | Done | `ApplyAsync`, `TryValidateSpellSpend` | Clamps `[0, Max]`; **does not fail on empty pool** — narrates `(Clamped: ...)` only |
| `Data/SpellSlotValidator.cs` | Done | Done | `ValidateSpend`, `IsCantrip`, `BuildConcentrationHint` | Over-level rejection, cantrip skip, concentration hint |
| `Data/Templates/SpellDefinition.cs` + `SpellDefinitionProvider` | Done | Done | YAML spell metadata | `Level`, `Classes`, `Concentration`, `CastingTime` |
| `Data/ResourceRecoveryRule.cs` | Done | Done | `ApplyAsync`, `ShouldRecoverPool` | Single `ISimulationRule` (Order 38) — no per-ruleset contributor needed |
| `Data/ChangeHandlers/RestChangeHandler.cs` | Done | Done | sets `LastRestedDay`/`LastRestType`/`RestSequence` | Encounter-interruption gate unchanged from prior design |
| `Data/ChangeHandlers/RestRecoveryAckHandler.cs` | Done | Done | idempotency marker | Consumes `RestRecoveryAck` from `ResourceRecoveryRule` |
| `RulesetData/{dnd5e,pf2e,fallout2d20}/pools/*.yaml` | Done | Done | — | dnd5e: slots 1–9, ki, superiority dice, font of magic, action surge, etc.; pf2e: slots 1–4, focus points, bon mot; fallout2d20: action points |
| `NarrativeRulesetResolver` | ⊘ | Structural opt-out | — | No YAML pools registered for Narrative → `ResourcePools` stays empty, all handlers/rules no-op |
| `Tools/CommitSchemaRegistry.cs` | Done | In sync | `resource` commit entry | Documents `poolName`/`delta`/`spellName` |
| `Tools/DmHelpManual.cs` | **Pending** | **Stale** | ~L297 | Still says "Engine does not track spell slots" — false |
| `docs/recommended-system-prompt.md` | **Pending** | **Stale** | ~L39 | Same false claim |
| `ARCHITECTURE.md` | **Pending** | **Missing** | — | No paragraph on `ResourceRecoveryRule`/`ResourcePoolProvider` |
| `Tools/ToolCallExamples.cs` | **Pending** | **Missing** | — | No canonical resource-spend/rest example |
| PF2e / Fallout | Done | **In scope now** | — | Contradicts original "out of 5e v1 scope" — both systems fully wired with tests |

### Item 2 — Relationship modifiers

| File / area | M/A/V | PR | Anchor | Notes |
|-------------|-------|-----|--------|-------|
| `Rulesets/RelationshipModifierHelper.cs` | A | 2 | `GetSocialModifier` | Band → bonus |
| `Rulesets/Dnd5eRulesetResolver.cs` | M | 2 | `ResolveSkillCheckAsync` ~L210, `ResolveContestedCheckAsync` ~L241 | Add bonus + narrative tag |
| `Rulesets/Pf2eRulesetResolver.cs` | M | 2 | same methods | Parity |
| `Rulesets/Fallout2d20RulesetResolver.cs` | M | 2 | same methods | Parity |
| `Rulesets/NarrativeRulesetResolver.cs` | ⊘ | — | `ResolveAsync` | **Opt out** — oracle rolls, not skill DCs |
| `Rulesets/RulesetResolverBase.cs` | V | 2 | `ApplyAllModifiers` ~L310 | No double-stack |
| `Data/Pressure/Contributors/CharacterDistressPressureContributor.cs` | V | — | relationship loop ~L123 | Prose only |
| `Data/Initiative/RelationalInitiativeProvider.cs` | V | — | `TryGetPersistentKey` | Independent of dice |
| `Data/Initiative/DefaultBehavioralTensionCalculator.cs` | V | — | relationship loop ~L87 | Independent |
| `Models/V4Views.cs` (`NpcPresenceSummary`) | V | v2 | record definition | Optional `Attitude` |
| `Tools/ExplorationTools.cs` | V | — | `GetNpcContext` ~L283 | Full `SocialProfile` returned |

### Item 3 — Connectivity

| File / area | M/A/V | PR | Anchor | Notes |
|-------------|-------|-----|--------|-------|
| `Models/Location.cs` / `LocationExit` record | M | 3 | `LocationExit` ~L80 | Add `OneWay` property |
| `Data/Pressure/Contributors/LocationConnectivityPressureContributor.cs` | M | 3 | `EvaluateAsync` ~L12 | Broad scan + `SuggestedCommitJson` |
| `Data/Pressure/Contributors/LocationIntegrityPressureContributor.cs` | M | 3 | `EvaluateAsync` ~L36 | `SuggestedCommitJson` on no-exits |
| `Data/ChangeHandlers/LocationChangeHandlers.cs` | M | 3 | `LocationUpdateHandler` `addExit` ~L246; `LocationCreateHandler` connectedFrom ~L162 | Auto-repair hook |
| `Models/CampaignConfig.cs` | M | 3 | flags | `AutoRepairLocationConnectivity` default **false** |
| `Tools/WorldBuilderTools.cs` | V | 3 | upsert path | Second entry point |
| `Authoring/ViewModels/*Location*` or save pipeline | M | 3 | pre-save validation | See **Authoring UI** below |
| `Authoring/Vault/Canonical/EntityCanonicalizer.cs` | M | 3 | `BuildLocation` ~L234 | Persist `OneWay` on exits |

### Item 4 — Eviction (implemented + PR-1.1)

| File / area | M/A/V | PR | Anchor | Notes |
|-------------|-------|-----|--------|-------|
| `Data/TransientEvictionRule.cs` | Done | 1 | `ApplyAsync` | Delta bundle |
| `Data/ChangeHandlers/ActivityChangeHandler.cs` | Done | 1 | location clear ~L41 | Clears departure on re-anchor |
| `Data/ChangeHandlers/LocationChangeHandlers.cs` | Done | 1 | `recordDeparture` ~L329 | Cap 10 |
| `tests/TransientEvictionRuleTests.cs` | Done | 1 | — | Rule-level only |
| `tests/*Eviction*Integration*` | A | 1.1 | — | `AdvanceWorldAsync` persistence |
| `Data/Pressure/Contributors/RecentlyDepartedPressureContributor.cs` | A | 1.1 | `EvaluateAsync` | Scene nudge |
| `AutofacModules/PressureModule.cs` | M | 1.1 | contributor registration | |
| `Data/JsonSanitizer.cs` | V | — | `SceneView` case | Serialize new fields |

### Item 5 — Event consequences

| File / area | M/A/V | PR | Anchor | Notes |
|-------------|-------|-----|--------|-------|
| `Data/EventConsequenceRegistry.cs` | A | 5a | template list | Suggest-only |
| `Data/EventConsequenceRule.cs` | A | 5b | `ApplyAsync` | Capped event scan — see performance |
| `Data/ChangeHandlers/EventOccurredHandler.cs` | V | 5a | `ApplyAsync` | No auto-mutate in v1 |
| `Data/Pressure/Contributors/PointOfInterestPressureContributor.cs` | M | 5c | `EvaluateAsync` | **Extend** — single decay voice |
| `Data/Pressure/Contributors/FactionRecentEventPressureContributor.cs` | V/M | 5d | event filter ~L25 | Extend for Combat/Betrayal |
| `Data/PlotThreadEvolutionRule.cs` | V | — | precedent | |
| `Data/LocationDecayRule.cs` | A | 5c | `ApplyAsync` | Defers to PoI when already nagging |
| `Models/Location.cs` | M | 5c | properties | `LocationCondition` |
| `Models/Event.cs` | M | 5b | `Details` dict | `consequenceApplied` keys |

---

## Item 4: Eviction Observability

### What was wrong

`TransientEvictionRule` cleared `CurrentLocationId` and **deleted** held items. `AdvanceResult.EvictedNpcIds` existed but carried bare IDs only. No durable trail for the LLM to discover who left unless it parsed `advance_world` narratives every time.

### What we built

Each eviction emits a **bundle of deltas** through the unified commit path:

| Delta | Purpose |
|-------|---------|
| `ActivityChange` | Clears `CurrentLocationId`, sets drift activity |
| `EventOccurred` (`category: Departure`) | Event log with `involved` + `relatedEntityId` (location) |
| `LocationUpdate` (`recordDeparture`) | Appends to `location.recentlyDeparted` (cap 10) |
| `CharacterUpdate` | Sets `departedAtDay` + `departedFromLocationId` |
| `ItemTransfer` | Moves held items to departed location (replaces delete) |

### Schema additions

```csharp
// Location
List<DepartedNpcRecord> RecentlyDeparted  // { characterId, name, departedAtDay, reason? }

// Character
int? DepartedAtDay
string? DepartedFromLocationId

// EventCategory.Departure

// AdvanceResult
List<EvictedNpcSummary> EvictedNpcs
```

### LLM discoverability

| Surface | What the LLM sees |
|---------|-------------------|
| `advance_world` | `evictedNpcIds` + `evictedNpcs` |
| `get_scene` | `location.recentlyDeparted` on full `Location` |
| `recall_history` / events | `Departure` category |
| `get_npc_context` | Full `Character` including `departedAtDay` / `departedFromLocationId` |

### PR-1.1 — Close remaining gaps

1. **Integration test:** `AdvanceWorldAsync` → reload location → assert `RecentlyDeparted` + query `Departure` event.
2. **`RecentlyDepartedPressureContributor`:** When `RecentlyDeparted` non-empty and party returns to location, emit `NarrativePrompt` with optional re-promote `SuggestedCommitJson`.
3. **Optional:** Set `item.currentState` to `"left behind by departed patron"` on `ItemTransfer` (decision in Open Questions).

### Recovery path

```json
[
  { "$type": "character_create", "characterId": "chars/tavern-bard", "name": "Mira", "keepAlive": true, "currentLocationId": "locations/rusty-nail" },
  { "$type": "activity", "characterId": "chars/tavern-bard", "newLocationId": "locations/rusty-nail", "updateLocation": true, "newActivity": "tuning her lute by the hearth" }
]
```

`ActivityChange` and `character_create` with a location clear departure metadata.

### Legacy data / migration (Item 4)

**No migration required.** Pre-Item-4 evictions are not recoverable in a meaningful way:

- Transient NPC **character docs were never deleted** — only `CurrentLocationId` was cleared.
- Held items **were deleted** before Item 4; those items cannot be reconstructed unless the campaign has external backups.
- New fields (`RecentlyDeparted`, `DepartedAtDay`, `Departure` events) apply **prospectively** from the first `advance_world` after deploy.

Do not backfill `RecentlyDeparted` from old simulation narratives. Optional one-time `ENGINE WARNING` on first `get_scene` after upgrade is out of scope.

---

## Item 1: Resource Pools (Spell Slots & Ability Resources) — As-Built

### Original problem

Casters could cast unlimited spells. `RestChange` handled encounter interruption only — no slot/ki recovery. `DmHelpManual` explicitly documented this gap.

### Why this section was rewritten

This item shipped (`07c35f3` → `d3e466f` → `841a674` → `4521ade` → `0bf7eca`, 2026-06-29 to 2026-07-01) with an architecture that diverges from the original per-system `Dnd5eExtension` design below in every material way: one generic schema instead of three, data-driven YAML instead of a bootstrap step, and a single cross-ruleset recovery rule instead of a per-ruleset contributor interface. The result is **more general** than v1 scoped for (it also covers PF2e and Fallout 2d20), but it shipped **without the PR-DOC gate being honored** — see remediation below. The subsections that follow describe what actually exists.

### Schema (as-built)

Generic, cross-ruleset, lives on `SystemStats` (not a per-system extension):

```csharp
// Models/Character.cs
[JsonPropertyName("resourcePools")]
public Dictionary<string, ResourcePool> ResourcePools { get; set; } = [];

public record ResourcePool
{
    public int Current { get; init; }
    public int Max { get; init; }
    public RecoveryType Recovery { get; init; } = RecoveryType.LongRest;
    public int? LastRecoveredDay { get; init; }
}
```

Pools are keyed by name (`spell_slots_1`..`spell_slots_9`, `ki_points`, `focus_points`, `action_points`, `superiority_dice`, `bardic_inspiration`, etc.). Rest-tracking fields live directly on `Character`: `LastRestedDay`, `LastRestType` (`RestType?`), `RestSequence`, `LastRecoveredRestSequence`, `LastRestRecoveredDay` (legacy fallback for pre-`RestSequence` saves).

### Population — data-driven, not a bootstrap step

`Services/ResourcePoolProvider.cs` (`IRulesetYamlProvider`) loads `ResourcePoolTemplate` records from `RulesetData/{dnd5e,pf2e,fallout2d20}/pools/*.yaml` (fields: `Name`, `ApplicableSystems`, `ApplicableClasses`, `Recovery`, `DefaultMax`, `LevelToMaxMap`, `FeatGrantedOnly`). `Services/ResourcePoolInitializer.cs` derives each pool's `Max`:

- **dnd5e spell slots**: `Services/Dnd5eCasterLevelHelper.ComputeCasterLevel` sums caster contribution per class-level entry — Full casters add their level as-is, Half casters add `level / 2`, Third casters add `level / 3` (rounded **per class entry, then summed** — not sum-then-round, so it's an approximation of PHB math for multi-half-caster multiclasses), Warlock contributes `0` (pact magic tracked separately via `warlock_invocations`). Result indexes `LevelToMaxMap`.
- **pf2e spell slots**: gated by `Pf2eCasterClasses.HasCaster`, indexed by raw character level (not a derived caster level).
- **Other pools** (ki, rage, focus points, action points): matched by `ApplicableClasses` + relevant class level, or plain character level.
- Feat-granted pools (e.g. `font_of_magic`) are added via a separate feat provider under the same gating.
- Re-sync on level-up/class-change (`Data/ChangeHandlers/CharacterChangeHandlers.cs`) preserves spent `Current` (clamped to new `Max`) and drops pools no longer applicable.

**PF2e and Fallout 2d20 are now in scope** — `RulesetData/pf2e/pools/spell_slots_1..4.yaml`, `focus_points.yaml` (class-gated: wizard/witch/cleric/druid/bard, `LevelToMaxMap` 1/10/16/20 → 1/2/3 points, `ShortRest` recovery), `bon_mot.yaml`; `RulesetData/fallout2d20/pools/action_points.yaml`. This directly contradicts the original "PF2e out of 5e v1 scope" line — treat that line as superseded.

### Consumption — `resource` commit, not a resolver-layer intercept

```json
{ "$type": "resource", "characterId": "characters/wizard-1", "poolName": "spell_slots_3", "delta": -1, "spellName": "fireball", "reason": "Cast Fireball" }
```

Handled by `Data/ChangeHandlers/ResourceChangeHandler.cs`:

- Clamps `Current` to `[0, Max]`.
- If the pool is a `spell_slots_*` pool and `spellName` is provided: resolves `SpellDefinition` via `SpellDefinitionProvider` for the character's active `RulesetSystem`, then applies `Data/SpellSlotValidator.cs` — skips validation with a `[WARNING]` on cantrips, **fails the commit** if `spell.Level > slotLevel` (before any roll), and emits a soft `[HINT]` reminding the LLM to commit a separate `status` change for concentration.
- Missing `spellName`, unresolvable system, or unknown spell name → soft `[WARNING]`, **spend still applies** (fails open, does not block the commit).

**⚠ Known gap vs. original design intent:** spending against an **already-empty pool** does **not** fail the commit. `ResourceChangeHandler.ApplyAsync` clamps `Current` to 0, computes `actualDelta = 0`, and appends `(Clamped: requested -1, actual 0)` to the narrative — but returns `success: true`. There is no hard rejection of "cast when no slots remain." The LLM must notice the clamp narrative; nothing currently blocks the action. This means the original design's "second same-level cast fails" behavior (and the RAW slot-on-failed-save callout, which assumed a resolver-layer gate before the roll) is **not implemented as originally envisioned** — only the over-level-spell case hard-fails. Treat as an open follow-up, not a shipped behavior; do not claim "second cast fails" in `DmHelpManual`/`recommended-system-prompt.md` until this is fixed.

### Narrative ruleset opt-out — structural, not a flag

There is no `TrackSpellSlotEconomy` config flag in the as-built system. `ResourcePoolProvider` simply never registers YAML loaders for `RulesetSystem.Narrative`, so Narrative characters get an empty `ResourcePools` dict and `ResourceRecoveryRule`/`ResourceChangeHandler` no-op on the empty dictionary. Functionally equivalent to the designed opt-out, achieved differently.

### Recovery — one generic rule, not per-ruleset contributors

`Data/ResourceRecoveryRule.cs` (`ISimulationRule`, Order 38) runs during `advance_world` for every scheduled character with a non-empty `ResourcePools`:

1. **Daily recovery** — any pool with `RecoveryType.Daily` refills once per campaign day, gated by `LastRecoveredDay >= currentDay`, independent of rest state.
2. **Rest recovery** — gated by `IsRestAlreadyRecovered` (`LastRecoveredRestSequence == RestSequence`, else legacy `LastRestRecoveredDay == LastRestedDay` fallback). Hierarchy: `LongRest ⊃ ShortRest ⊃ PerTurn` (a rest of a given type recovers pools tagged at that type or lower). `EncounterEnd` recovery type exists in the enum but is **not yet wired** (PerTurn Action Points still require manual LLM commits per the class doc comment).
3. Emits `ResourceChange` deltas plus one `RestRecoveryAck` per character (consumed by `Data/ChangeHandlers/RestRecoveryAckHandler.cs`) for idempotency.

`Data/ChangeHandlers/RestChangeHandler.cs` still owns the LLM-facing `rest` commit: runs the existing encounter-interruption check, and on an uninterrupted rest sets `LastRestedDay`, `LastRestType` (explicit or inferred: ≥8h → LongRest, else ShortRest), increments `RestSequence`. Recovery itself happens on the **next** `advance_world`, not immediately — `DmHelpManual` already documents this timing correctly.

There is no `IRestRecoveryContributor` interface — the single generic rule replaced the planned per-ruleset-contributor design. DI is automatic via `AutofacModules/ConventionRegistration.cs` marker-interface scanning (`ISimulationRule`, `IWorldChangeHandler`, `IRulesetYamlProvider`, `IRulesetDataInitializer`); no manual registration exists or is needed.

### Test coverage (as-built)

`ResourceRecoveryRuleTests.cs` (9 facts), `RestChangeHandlerTests.cs`, `ResourcePoolInitializerTests.cs` (10 facts incl. multiclass full/half-caster stacking, pf2e wizard/fighter gating, preserve-spent-on-resync, remove-stale-on-class-change), `SpellDefinitionTests.cs` (spell YAML golden tests, slot-validator over-level rejection, cantrip skip, fireball spend/warn/fail paths), `LevelUpResourcePoolTests.cs` (level-up gains new slot tier while preserving spent slots). This is a genuinely thorough, tested feature — not a stub.

---

## Item 2: Relationship → Mechanical Bite

### Problem

`RelationshipChange` updates directed `Social.Relationships` (-100..+100). Used for narrative pressure (±80) and relational initiative — not skill rolls.

### Design principles

- For persuasion toward NPC X, read **X's opinion of the actor**: `target.Social.Relationships[actor.Id]`.
- Apply as **roll bonus** (not DC change) so LLM-supplied DC stays the base difficulty.
- Narrative output must name the source: `Rolled 14 + 3 (trusted friend) = 17 vs DC 15`.

### Inverse relationship policy

`RelationshipChange` is **one-directional**. If `target → actor` key is missing:

| Policy (v1) | Behavior |
|-------------|----------|
| Default | Treat as **0** (neutral) |
| Optional symmetric read | If only `actor → target` exists, use **half** that value (rounded down) as fallback — **disabled by default**; enable via `CampaignConfig` if playtests need it |

Pressure at ±40 should remind LLM to commit **both directions** after major social beats.

### Stacking policy (avoid triple punishment)

| System | Uses relationships | Stacking with roll modifier |
|--------|-------------------|----------------------------|
| `CharacterDistressPressureContributor` | Narrative text ±80 | Independent — prose only |
| `RelationalInitiativeProvider` | Initiative keys | Independent |
| `RelationshipModifierHelper` | Roll bonus | **Only this affects dice** |

Do not also change DC. Intimidation toward hostile NPCs **does** apply negative modifier — that is intentional (harder to intimidate someone who already hates you).

### Skills affected (v1)

Apply modifier when `actionCategory == Social` **or** skill ∈ `{ Persuasion, Deception, Intimidation, Insight, Performance }`.

**Exclude:** Athletics, Perception (physical), general `SkillCheck` without social skill name.

`ContestedCheck` social: modifier on actor only; target rolls unmodified.

### Relationship bands → roll bonus

| Score (target → actor) | Bonus |
|------------------------|-------|
| ≥ 80 | +5 |
| 60–79 | +3 |
| 40–59 | +1 |
| -39..39 | 0 |
| -59..-40 | -1 |
| -79..-60 | -3 |
| ≤ -80 | -5 |

### PR-2 scope

~150–250 LOC + PR-DOC. See Touchpoint Matrix.

---

## Item 3: Location Connectivity Auto-Repair

### Problem

UI-authored dungeons and manual `addExit` create one-way edges. `LocationConnectivityPressureContributor` only checks parent reverse links. Fix JSON is embedded in message text, not `SuggestedCommitJson`.

### Design principles

- **Detect broadly:** any exit A→B where B lacks B→A (unless exit marked `oneWay`).
- **Suggest structurally:** always set `SuggestedCommitJson` on connectivity pressures.
- **Auto-repair opt-in:** default **off** — many dungeons use intentional one-way exits (chutes, secret doors, traps).

### Per-exit `oneWay` flag

```csharp
public record LocationExit(
    string TargetLocationId,
    string Description,
    ...
    bool OneWay = false  // when true, skip reverse-link detection and auto-repair
);
```

### Config

```csharp
public bool AutoRepairLocationConnectivity { get; set; } = false;  // changed from true
```

When `true`, commit-time hook on `addExit` (A→B) auto-appends B→A unless either exit is `oneWay`.

### Detection (broaden `LocationConnectivityPressureContributor`)

For each `exit` in `loc.Exits` where `!exit.OneWay`:

1. Load target location.
2. If target has no reverse exit to `loc.Id` (and no `oneWay` on the forward edge), emit `EngineWarning` with `SuggestedCommitJson`.

Retain existing `ParentLocationId` check; add `SuggestedCommitJson` there too.

### Authoring UI (required — server-only is insufficient)

The original bug is **UI-authored dungeons** with dead-end connectivity. PR-3 is not complete without authoring changes.

**Design sketch:**

```
Location save (Authoring)
    │
    ├─► Build exit graph from Location.Exits (+ ParentLocationId)
    │
    ├─► For each exit E: A → B where !E.OneWay
    │       if B has no reverse exit to A → WARNING (non-blocking) or ERROR (configurable)
    │
    ├─► UI: checkbox per exit "One-way (no return path)" → sets LocationExit.OneWay
    │
    └─► Optional: "Add reverse exit" button → writes paired exit on target location document
```

**Acceptance tests (PR-3):**

| Test | Given | Then |
|------|-------|------|
| `AuthoringSave_WarnsOnAccidentalOneWay` | Location A → B, B has no return, `OneWay=false` | Save shows warning listing `B` missing reverse to `A` |
| `AuthoringSave_AcceptsMarkedOneWay` | A → B with `OneWay=true` | Save succeeds, no connectivity warning |
| `AuthoringSave_PersistsOneWayFlag` | Mark exit one-way, save, reload | `LocationExit.OneWay == true` in canonical JSON |
| `AuthoringSave_OptionalAutoRepair` | User clicks "Add reverse exit" | Target location gains `addExit` back to source (same semantics as server hook) |

Server `LocationConnectivityPressureContributor` remains the runtime safety net for play/API-created locations.

### PR-3 scope

~200–350 LOC + authoring validator + PR-DOC.

---

## Item 5: Event Consequence Propagation

### Problem

`EventOccurred` appends to the event log. Downstream state (location, schedules, factions) requires manual LLM commits unless plot-thread/quest-specific machinery applies.

### Design principles

- No full event-sourcing in v1.
- Templates suggest commits; simulation applies **only** with idempotency.
- LLM remains author of *whether* an event happened; engine proposes *consequences*.
- **Extend existing rules** before adding parallel ones.

### Overlap map (do not duplicate)

| Existing | Overlap with Item 5 | Action |
|----------|---------------------|--------|
| `PointOfInterestPressureContributor` + `DmHelpManual` PoI decay | 5b / 5c location decay | **Extend** PoI contributor; don't add second decay nag |
| `FactionRecentEventPressureContributor` | 5d faction coupling | **Extend** or delegate to registry |
| `PlotThreadEvolutionRule`, `QuestStalenessRule` | 5a templates | Use as code precedent |
| `FactionEcosystemRule` | 5d | Read recent Combat events at controlled locations |
| Item 4 `Departure` events | 5b TTL | Prune `recentlyDeparted` > 30 days in simulation |

### Architecture

```
EventOccurred (commit) → Event log (unchanged)
        │
        ▼ (next get_scene at related location, or world scope)
EventConsequenceRegistry → SuggestedCommitJson on WorldPressureItem (5a)
        │
        ▼ (advance_world only, if AutoApplyEventConsequences or per-template flag)
EventConsequenceRule → WorldChange deltas with idempotency (5b)
```

**5a before 5b:** integration tests for suggest-only contract first.

### Idempotency schema (required before 5b)

```csharp
// Event.Details key written after consequence applied
"consequenceApplied": ["combat-location-damage:v1", "betrayal-rel:-15:v1"]
```

`EventConsequenceRule` skips template if key present. Keys are template-defined strings, not GUIDs.

### Phase 5a — Consequence templates

```csharp
// Data/EventConsequenceRegistry.cs
record EventConsequenceTemplate(
    string TemplateId,
    EventCategory? Category,
    string? RelatedEntityPrefix,
    Func<EventOccurred, IReadOnlyList<WorldChange>> Suggest);
```

**v1 templates (suggest only):**

| TemplateId | Match | Suggested deltas |
|------------|-------|------------------|
| `combat-location-damage:v1` | Combat + `relatedEntityId` starts with `locations/` | `location_update.newState`, `tagsToAdd` |
| `betrayal-rel:v1` | Betrayal + `involved` ≥ 2 | `relationship` deltas (document both directions) |

Surface via `SuggestedCommitJson` on next `get_scene` at that location. **Do not** auto-apply in 5a.

**Exit criteria:** Guard test `CombatEvent_SuggestsLocationUpdate_OnNextGetScene` passes.

### Phase 5b — `EventConsequenceRule`

| Pattern | Auto delta (only if `AutoApplyEventConsequences` or template allows) |
|---------|----------------------------------------------------------------------|
| Combat + location | Add PoI "scorch marks" if missing |
| Departure | Prune `recentlyDeparted` entries > 30 days |
| Betrayal | **Suggest only in v1** — auto relationship delta is risky |

**Exit criteria:** `EventConsequenceRule_DoesNotDoubleApply` passes.

**Performance (5b):** `EventConsequenceRule` runs on every `advance_world` but must stay cheap:

- Query events for **current campaign only**, `DayLogged >= (currentDay - DaysPassed - 7)` (rolling window).
- **Cap at 100** events per run; process newest first.
- Index via existing `Event_Search` by `DayLogged` + `CampaignName` (add index field if missing).
- No full-table scan; no per-event location loads unless `relatedEntityId` is set.
- Target: &lt; 50ms on typical campaign in integration test.

### Phase 5c — `LocationCondition` enum

```csharp
public enum LocationCondition { Normal, Damaged, Ruined, Abandoned, UnderConstruction, Quarantined }
```

`LocationDecayRule`: Damaged + 7 days → pressure to normalize. **Coordinate with `PointOfInterestPressureContributor`** — if PoI contributor already emitted a decay/cleanup pressure for this location in the same `get_scene` batch, `LocationDecayRule` suppresses its own nag (`LocationDecay_SinglePressureVoice_NoPoIDuplicate` guard test).

`AutoEvolveLocationState` default `false`.

### Phase 5d — Faction territory

Extend `FactionRecentEventPressureContributor` or `FactionEcosystemRule` to read Combat/Betrayal at `ControllingFactionId` locations. Suggest `faction_state` via `SuggestedCommitJson`.

### `scheduleImpact` on `EventOccurred`

**Deferred past 5a.** Templates can emit `schedule_change` without new schema if `involved` + `relatedEntityId` suffice.

---

## Cross-Cutting Concerns

### Config flags (`CampaignConfig`)

```csharp
public bool AutoRepairLocationConnectivity { get; set; } = false;
public bool AutoEvolveLocationState { get; set; } = false;
public bool AutoApplyEventConsequences { get; set; } = false;
public bool SymmetricRelationshipFallback { get; set; } = false;
```

**Note:** `TrackSpellSlotEconomy` never shipped — the as-built resource-pool system has no such flag. Tracking is structural: a `RulesetSystem` either has YAML pool templates registered (dnd5e/pf2e/fallout2d20) or doesn't (Narrative). See **Item 1: As-Built → Narrative ruleset opt-out**.

### Pressure item consistency

All connectivity and consequence contributors must set `SuggestedCommitJson`. `ExplorationTools` already harvests these into `SuggestedCommitExamples` on scene views.

### Narrative ruleset (`ActiveSystem == Narrative`)

**Decision (locked for v1): opt out of Items 1 and 2 mechanics.**

| Item | Narrative behavior | Rationale |
|------|-------------------|-----------|
| **1 Spell slots** | **No tracking, no consumption, no recovery — confirmed as-built.** No config flag involved; `ResourcePoolProvider` registers no pool YAML for `RulesetSystem.Narrative`, so `ResourcePools` stays empty and `ResourceRecoveryRule`/`ResourceChangeHandler` no-op. Spells use oracle `1d6` in `NarrativeRulesetResolver.ResolveAsync` (~L28). | Narrative mode has no slot economy; oracle replaces spell math. |
| **2 Relationships** | **No roll modifiers.** Oracle rolls are not skill checks; no DC, no target-bound persuasion pipeline. Item 2 unimplemented, so this is currently true for all rulesets, not just Narrative. | Relationship pressure + initiative still apply for prose. |

**PR-DOC language (required in PR-DOC-2 and the Item 1 remediation PR):**

> When `set_active_system` is **Narrative**, the engine does **not** track spell slots or apply relationship modifiers to rolls. Use oracle outcomes and narrative pressure instead. Switch to **Dnd5e** (or PF2e) for tracked slots and social roll bonuses.

Touchpoint Matrix marks `NarrativeRulesetResolver` as **⊘** (opt-out), not **V** (verify). No code changes expected in that file for Items 1–2 unless oracle behavior later gains optional relationship nudges (v2).

---

## PR Plan (DAG)

```
PR-1:  Eviction observability (core) ✅
  │
  ├─► PR-1.1: Integration test + RecentlyDeparted pressure + PR-DOC polish  [still open]
  │
  PR-Resources: Resource pool system (Item 1) ✅ shipped 2026-07-01
  │   (07c35f3 → d3e466f → 841a674 → 4521ade → 0bf7eca — superseded original PR-4a/4b/4c split,
  │    which never ran; do not plan further work against the old split)
  │
  └─► PR-DOC-Resources-fix: Close PR-DOC gate violation for the shipped resource system
        (DmHelpManual, recommended-system-prompt.md, ARCHITECTURE.md, ToolCallExamples —
         see "Item 1 PR-DOC remediation" above) — REQUIRED NEXT, blocks further Item 1 work
        │
        ├─► PR-2: Relationship roll modifiers + PR-DOC-2
        │
        ├─► PR-3: Connectivity detect + SuggestedCommitJson + oneWay + opt-in repair + Authoring + PR-DOC-3
        │
        └─► PR-5a: Event consequence templates + integration tests + PR-DOC-5a
              └─► PR-5b: EventConsequenceRule + idempotency + Departure TTL
                    └─► PR-5c: LocationCondition + LocationDecayRule (coordinate PoI)
                          └─► PR-5d: Faction event coupling (optional)
```

PR-1.1, PR-2, and PR-3 can run in parallel with/after the PR-DOC-Resources-fix. PR-5b blocked on 5a tests.

PR-DOC merges **with or immediately before** implementation PR — see **PR-DOC gate → Timing** (no grace window). **This rule was violated for the resource-pool work; PR-DOC-Resources-fix is the correction.**

---

## Sanity check (design vs repo)

| Check | Status |
|-------|--------|
| Item 4 core logic matches git (delta bundle, handlers, Departure) | ✓ Landed |
| PR-1.1 items listed as **A** in matrix, not claimed done | ✓ — confirmed still absent (2026-07-01 re-check) |
| Schema additions non-breaking (nullable / new lists only) | ✓ |
| Config defaults safe-by-default (`AutoRepair=false`, `AutoApply=false`) | ✓ (n/a yet — Items 2/3/5 unimplemented, no flags exist to check) |
| Item 5 PoI overlap has guard test `LocationDecay_SinglePressureVoice_NoPoIDuplicate` | Not yet — Item 5 unimplemented as of 2026-07-01 |
| Narrative ruleset explicitly opt-out, not "verify" | ✓ Locked (design); ✓ confirmed structurally true for the shipped resource system |
| Pre-Item-4 deleted items migration | ✓ Documented as unrecoverable |
| **Item 1 architecture matches as-built code** | **✗ Original design superseded — see "Item 1: As-Built"; doc rewritten 2026-07-01** |
| **PR-DOC gate honored for Item 1** | **✗ Not honored — `DmHelpManual`/`recommended-system-prompt.md`/`ARCHITECTURE.md` stale; see remediation** |
| Items 2, 3, 5 confirmed absent (no renamed/partial implementation) | ✓ Re-verified 2026-07-01 via direct grep of resolvers, contributors, config, Autofac registration |

---

## Open Questions

| # | Question | Proposed default |
|---|----------|------------------|
| 1 | Full PHB multiclass slot math? | **Resolved as-built**: `Dnd5eCasterLevelHelper.ComputeCasterLevel` already does full/half/third-caster stacking (Warlock excluded), rounding per-class-entry then summing — closer to PHB than the original "full-caster-only sum" v1 plan, though not an exact match for multi-half-caster multiclasses (PHB sums levels first, then divides once). No further v2 work needed unless that edge case matters. |
| 2 | Auto-apply consequences without LLM commit? | `AutoApplyEventConsequences = false`; 5b limited to safe deltas (PoI, TTL) — unchanged, Item 5 not started |
| 3 | RecentlyDeparted TTL? | 30 days; prune in 5b `EventConsequenceRule` — unchanged, not started |
| 4 | Item `currentState` on eviction drop? | PR-1.1 optional; `"left behind by departed patron"` — unchanged, PR-1.1 not started |
| 5 | Narrative ruleset spell slots / relationships? | **Locked: opt out.** Confirmed as-built for resource pools (structural, no YAML registered for Narrative). Relationships (Item 2) unimplemented so N/A there. |
| 6 | Symmetric relationship fallback? | Off by default (`SymmetricRelationshipFallback`) — unchanged, Item 2 not started |
| 7 | Refund slot on failed save (house rule)? | **Reframed**: the as-built system doesn't have a resolver-layer gate to refund *from* — slot spend is a manual `resource` commit the LLM makes alongside a spell cast. No refund mechanism exists or is needed until a resolver-layer cast pipeline is built. Off; revisit only if that pipeline is added. |

---

## Success Criteria (program-level)

- [ ] Item 4: integration test proves persisted eviction trail after `advance_world`
- [ ] Item 2: social skill narrative includes relationship bonus tag
- [ ] Item 3: one-way **accidental** links surface `SuggestedCommitJson`; intentional `oneWay` exits silent
- [ ] Item 1: second leveled spell cast fails without rest when tracking enabled — **not met as-built; `ResourceChangeHandler` clamps silently on empty pools instead of failing (see "Item 1: As-Built" gap)**
- [x] Item 1: bootstrap/initializer populates slots for a leveled caster on create (`ResourcePoolInitializerTests.cs`)
- [x] Item 1: rest recovery restores pools per the LongRest ⊃ ShortRest ⊃ PerTurn hierarchy (`ResourceRecoveryRuleTests.cs`)
- [ ] Item 5a: combat at location produces consequence pressure on next `get_scene` without double-apply
- [ ] Every PR merged with PR-DOC updates to `DmHelpManual` + `recommended-system-prompt.md` — **violated for the resource-pool PRs; remediation required**
- [ ] Full test suite green; new guard tests listed above pass

---

## Developer experience notes

1. **Touchpoint Matrix** — use the **Anchor** column first; line numbers are approximate against current main.
2. **Guard tests** — **PR** column tells you which tests to add in which PR; Item 1's original PR-4a/4b/4c split never ran — it shipped as one connected body of work under a different architecture (see "Item 1: As-Built").
3. **PR-DOC** — same PR or doc-first stack; no merge window without docs (see Execution Rules). **This was violated for Item 1 — check `Tools/DmHelpManual.cs` line ~297 before trusting anything it says about spell slots.**
4. **Migration** — Item 4 is prospective only; do not build backfill jobs.
5. **5b performance** — respect event cap + day window; add a timing assertion in integration test if scans regress.
6. **Item 1 empty-pool gap** — do not assume "out of slots" blocks a cast. It doesn't yet; `ResourceChangeHandler` clamps to 0 and narrates, but returns success. Fix this (and its guard test) before claiming the original design's success criterion is met.

---

## Summary

**Item 1 (Spell Slots & Ability Resources) shipped 2026-07-01**, generalized into a cross-ruleset `ResourcePool` system (dnd5e + pf2e + fallout2d20, data-driven YAML, single recovery rule) — architecturally different from and broader than originally designed, but its PR-DOC gate was violated (`DmHelpManual`, `recommended-system-prompt.md`, `ARCHITECTURE.md` still describe the old gap) and it has one known behavioral hole (empty pools clamp silently instead of failing the cast). **PR-DOC-Resources-fix is the required next step**, before any further Item 1 work.

Item 4 fixed the eviction observability hole in core code; PR-1.1 (persistence + discoverability loop) is still open and unstarted. Items 2, 3, and 5 remain fully unimplemented, each with a **touchpoint matrix with anchors**, **PR-scoped guard tests**, **locked Narrative opt-out**, **authoring acceptance tests**, and **5b performance bounds** — none of that has changed since the last design pass.

**Execute in this order:** PR-DOC-Resources-fix (closes the Item 1 gate violation) → PR-1.1, PR-2, PR-3 in parallel → PR-5a onward.