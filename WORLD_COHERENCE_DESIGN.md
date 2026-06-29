# World Coherence Design

Design document for closing five feedback gaps where CampaignVault trusts the LLM to maintain state the engine does not own.

**Last updated:** 2026-06-29 (review pass: PR-DOC timing, Narrative opt-out, authoring acceptance tests, slot RAW callout, multiclass example, guard-by-PR, migration, 5b performance)  
**Single source of truth** for world-coherence work. Update this file when scope or status changes.

## Problem Statement

CampaignVault excels at scene-centric narrative state, pressure nudges, and ruleset rolling — but several world-model loops are incomplete. When the LLM is attentive in a single session, gaps are papered over. Across time skips, rested spellcasters, earned relationships, hand-authored maps, and accumulated events, the world drifts unless the model manually commits every downstream effect.

This document defines implementation for all five items. **Item 4 core work is landed; PR-1.1 closes remaining gaps.** Items 1–3 and 5 are designed but not implemented.

---

## Status Overview

| # | Gap | Severity | Status |
|---|-----|----------|--------|
| 1 | Spell slots / ability resources not tracked | High | Designed (5e v1 scoped) |
| 2 | RelationshipChange has no mechanical bite | Medium | Designed |
| 3 | One-way location links not auto-repaired | Medium | Designed |
| 4 | TransientEvictionRule silently deletes NPCs | Medium | **Core implemented** — PR-1.1 pending |
| 5 | Events are append-only, not structured state | High | Designed (4 sub-phases) |

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
| `AdvanceWorld_PersistsRecentlyDeparted_AndDepartureEvent` | PR-1.1 | 4 | Full `AdvanceWorldAsync` → location doc + event query |
| `SpellBootstrap_FillsSlots_Wizard5` | PR-4a | 1 | Bootstrap step populates `spellSlots` on create |
| `SpellCast_DecrementsSlot_WhenTrackSpellSlotEconomy` | PR-4b | 1 | Commit `ruleset_action` → `get_scene` shows reduced slot |
| `SpellCast_FailedSave_StillConsumesSlot` | PR-4b | 1 | Target saves → slot remains decremented (5e RAW) |
| `SpellCast_SkipsSlots_WhenNarrativeRuleset` | PR-4b | 1 | `ActiveSystem == Narrative` → no slot check/consumption |
| `LongRest_RestoresSlots` | PR-4c | 1 | Safe 8h rest resets to `spellSlotsMax` |
| `ShortRest_RestoresKiOnly` | PR-4c | 1 | 1h rest restores classResources, not spell slots |
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

### Item 1 — Spell slots (5e v1)

| File / area | M/A/V | PR | Anchor | Notes |
|-------------|-------|-----|--------|-------|
| `Models/Dnd5eExtension.cs` | M | 4a | property block ~L57 | `spellSlots`, `spellSlotsMax`, `classResources`, `classResourcesMax` |
| `Rulesets/Bootstrap/Dnd5eDeriveSpellSlotsStep.cs` | A | 4a | `ApplyAsync` | After `Dnd5eDeriveSpellcastingStep` |
| `Rulesets/Bootstrap/CharacterBootstrapOrchestrator.cs` | M | 4a | Dnd5e pipeline ctor | Register new step |
| `Rulesets/SystemStatsMerger.cs` | M | 4a | `Merge()` ~L24, `DeepMerge` ~L83 | Dict fields merge by JSON deep-merge |
| `Rulesets/SystemStatsCompleteness.cs` | M | 4a | `GetDnd5eMissing`, `BuildExampleCommit` | Caster hints |
| `Rulesets/Dnd5eRulesetResolver.cs` | M | 4b | `ResolveSpellSaveAsync`, `ResolveSpellUtilityAsync` | Consumption before roll |
| `Rulesets/NarrativeRulesetResolver.cs` | ⊘ | — | `ResolveAsync` ~L28 | **Opt out** — see Narrative ruleset |
| `Data/ChangeHandlers/RulesetActionHandler.cs` | V | 4b | `ApplyAsync` | Dispatches to active resolver |
| `Data/ChangeHandlers/RestChangeHandler.cs` | M | 4c | post-`EvaluateAsync` safe-rest branch ~L67 | Call `IRestRecoveryContributor` |
| `Rulesets/IRestRecoveryContributor.cs` + Dnd5e impl | A | 4c | `ComputeRecovery` | DI in ruleset Autofac module |
| `Rulesets/Contributors/SpellSlotPressureContributor.cs` | A | 4b | `EvaluateAsync` | Scene scope |
| `Data/Scenes/SceneNpcPresenceFactory.cs` | V | — | `Create` ~L56 | `SystemStats` already exposed |
| `Models/CampaignConfig.cs` | M | 4a | flags section | `TrackSpellSlotEconomy` (default true; ignored when Narrative) |
| `Tools/DmHelpManual.cs`, `CommitSchemaRegistry.cs`, `ToolCallExamples.cs` | M | PR-DOC | — | Required before 4b gates |
| PF2e / Fallout | — | — | — | Out of 5e v1 scope |

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

## Item 1: Spell Slots & Ability Resources

### Problem

Casters can cast unlimited spells. `RestChange` handles encounter interruption only — no slot/ki recovery. `DmHelpManual` explicitly documents this gap.

### v1 scope

- **5e only.** PF2e focus points and Fallout AP are separate PRs.
- Track **economy** (slots remaining), not spell lists.
- Recovery in ruleset layer, not LLM commits.
- Gated by `CampaignConfig.TrackSpellSlotEconomy` (default `true`).

### Schema: `Dnd5eExtension`

```csharp
[JsonPropertyName("spellSlots")]
public Dictionary<string, int> SpellSlots { get; set; } = [];

[JsonPropertyName("spellSlotsMax")]
public Dictionary<string, int> SpellSlotsMax { get; set; } = [];

[JsonPropertyName("classResources")]
public Dictionary<string, int> ClassResources { get; set; } = [];  // ki, rage, channelDivinity

[JsonPropertyName("classResourcesMax")]
public Dictionary<string, int> ClassResourcesMax { get; set; } = [];
```

### Spell level resolution (avoids LLM retry loops)

**v1 requires explicit `parameters.slotLevel`** on leveled spells. Do not default missing slotLevel to 1 (that hides bugs).

| Input | Behavior |
|-------|----------|
| `parameters.slotLevel` missing + `actionName` matches known cantrip list | No consumption |
| `parameters.slotLevel` missing + leveled spell | `ResolverResult.Fail` with `CommitJsonErrorHints` + example |
| `parameters.ritual: true` | No consumption (v2 formalize) |
| `parameters.slotLevel` > highest slot in table | Fail before roll |
| Cast fails (save negates, etc.) | **Still consume slot** — see RAW callout below |

Cantrips: never consume. Upcast: consume at declared `slotLevel`.

### Slot consumption on failed casts (PR-DOC callout — required)

> **ENGINE BEHAVIOR (5e RAW):** A spell slot is consumed when the cast **completes**, not when the effect succeeds. If the target succeeds on a saving throw, the slot is **still spent**. CampaignVault does **not** refund slots on "failed" saves, counterspell, or fizzled narrative outcomes unless the LLM commits a manual `system_stats` correction for a documented house rule.
>
> PR-DOC-4b must include this block verbatim in `DmHelpManual` and `recommended-system-prompt.md` so the LLM does not argue for refunds or loop retries expecting slot recovery.

Optional v2: `CampaignConfig.RefundSlotsOnFailedSave` (default `false`).

### Multiclass (v1 decision — simplified, not PHB)

**Rule:** Sum levels from all entries in `classLevels` where the class is a **known caster** (Bard, Cleric, Druid, Sorcerer, Warlock, Wizard, Paladin, Ranger per `Dnd5eClassProfileResolver`). Look up the **full caster** slot table for that total level. Non-caster class levels in the array do not add slots in v1 (Fighter 5 / Wizard 3 → **3rd-level caster table**, not 8th).

**Worked example (must appear in PR-DOC-4a):**

```
classLevels: [{ class: Bard, level: 2 }, { class: Wizard, level: 3 }]
→ casterLevel = 5
→ spellSlots / spellSlotsMax: { "1": 4, "2": 3, "3": 2 }  // full-caster 5th-level row
```

```
classLevels: [{ class: Fighter, level: 5 }, { class: Wizard, level: 3 }]
→ casterLevel = 3  (Fighter ignored for slot table in v1)
→ spellSlots: { "1": 4, "2": 2 }
```

**PR-DOC must state:** "This is a deliberate simplification. Full PHB multiclass slot math is v2." Warlock pact magic (separate slot count) is v2 — document as known gap.

### PR split (was one ~500 LOC PR)

| PR | Deliverable | Exit criteria |
|----|-------------|---------------|
| **PR-4a** | `Dnd5eDeriveSpellSlotsStep` + merge + completeness hints | Wizard 5 bootstrap fills slots in `get_scene` |
| **PR-4b** | Cast consumption in resolver + `SpellSlotPressureContributor` | Second same-level cast fails when tracking enabled |
| **PR-4c** | `IRestRecoveryContributor` + `RestChangeHandler` hook | Long rest restores slots; short rest restores ki |

### `IRestRecoveryContributor`

```csharp
interface IRestRecoveryContributor
{
    RulesetSystem System { get; }
    IReadOnlyList<WorldChange> ComputeRecovery(Character character, int hoursRested);
}
```

Register in Autofac ruleset module alongside resolvers. Interrupted rest → no recovery (existing behavior).

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
public bool TrackSpellSlotEconomy { get; set; } = true;
public bool SymmetricRelationshipFallback { get; set; } = false;
```

### Pressure item consistency

All connectivity and consequence contributors must set `SuggestedCommitJson`. `ExplorationTools` already harvests these into `SuggestedCommitExamples` on scene views.

### Narrative ruleset (`ActiveSystem == Narrative`)

**Decision (locked for v1): opt out of Items 1 and 2 mechanics.**

| Item | Narrative behavior | Rationale |
|------|-------------------|-----------|
| **1 Spell slots** | **No tracking, no consumption, no recovery.** `TrackSpellSlotEconomy` is ignored. Spells use oracle `1d6` in `NarrativeRulesetResolver.ResolveAsync` (~L28). | Narrative mode has no slot economy; oracle replaces spell math. |
| **2 Relationships** | **No roll modifiers.** Oracle rolls are not skill checks; no DC, no target-bound persuasion pipeline. | Relationship pressure + initiative still apply for prose. |

**PR-DOC language (required in PR-DOC-2 and PR-DOC-4b):**

> When `set_active_system` is **Narrative**, the engine does **not** track spell slots or apply relationship modifiers to rolls. Use oracle outcomes and narrative pressure instead. Switch to **Dnd5e** (or PF2e) for tracked slots and social roll bonuses.

Touchpoint Matrix marks `NarrativeRulesetResolver` as **⊘** (opt-out), not **V** (verify). No code changes expected in that file for Items 1–2 unless oracle behavior later gains optional relationship nudges (v2).

---

## PR Plan (DAG)

```
PR-1:  Eviction observability (core) ✅
  │
  └─► PR-1.1: Integration test + RecentlyDeparted pressure + PR-DOC polish
        │
        ├─► PR-2: Relationship roll modifiers + PR-DOC-2
        │
        ├─► PR-3: Connectivity detect + SuggestedCommitJson + oneWay + opt-in repair + Authoring + PR-DOC-3
        │
        ├─► PR-4a: Spell slot bootstrap + PR-DOC-4a
        │     └─► PR-4b: Cast consumption + PR-DOC-4b
        │           └─► PR-4c: Rest recovery + PR-DOC-4c
        │
        └─► PR-5a: Event consequence templates + integration tests + PR-DOC-5a
              └─► PR-5b: EventConsequenceRule + idempotency + Departure TTL
                    └─► PR-5c: LocationCondition + LocationDecayRule (coordinate PoI)
                          └─► PR-5d: Faction event coupling (optional)
```

PR-2 and PR-3 can run in parallel after PR-1.1. PR-4a–c are sequential. PR-5b blocked on 5a tests.

PR-DOC merges **with or immediately before** implementation PR — see **PR-DOC gate → Timing** (no grace window).

---

## Sanity check (design vs repo)

| Check | Status |
|-------|--------|
| Item 4 core logic matches git (delta bundle, handlers, Departure) | ✓ Landed |
| PR-1.1 items listed as **A** in matrix, not claimed done | ✓ |
| Schema additions non-breaking (nullable / new lists only) | ✓ |
| Config defaults safe-by-default (`AutoRepair=false`, `AutoApply=false`) | ✓ |
| Item 5 PoI overlap has guard test `LocationDecay_SinglePressureVoice_NoPoIDuplicate` | ✓ Added |
| Narrative ruleset explicitly opt-out, not "verify" | ✓ Locked |
| Pre-Item-4 deleted items migration | ✓ Documented as unrecoverable |

---

## Open Questions

| # | Question | Proposed default |
|---|----------|------------------|
| 1 | Full PHB multiclass slot math? | v2; v1 uses caster-class level sum + full-caster table |
| 2 | Auto-apply consequences without LLM commit? | `AutoApplyEventConsequences = false`; 5b limited to safe deltas (PoI, TTL) |
| 3 | RecentlyDeparted TTL? | 30 days; prune in 5b `EventConsequenceRule` |
| 4 | Item `currentState` on eviction drop? | PR-1.1 optional; `"left behind by departed patron"` |
| 5 | Narrative ruleset spell slots / relationships? | **Locked: opt out** (see Narrative ruleset section) |
| 6 | Symmetric relationship fallback? | Off by default (`SymmetricRelationshipFallback`) |
| 7 | Refund slot on failed save (house rule)? | Off; `RefundSlotsOnFailedSave` v2 config |

---

## Success Criteria (program-level)

- [ ] Item 4: integration test proves persisted eviction trail after `advance_world`
- [ ] Item 2: social skill narrative includes relationship bonus tag
- [ ] Item 3: one-way **accidental** links surface `SuggestedCommitJson`; intentional `oneWay` exits silent
- [ ] Item 1: second leveled spell cast fails without rest when tracking enabled
- [ ] Item 5a: combat at location produces consequence pressure on next `get_scene` without double-apply
- [ ] Every PR merged with PR-DOC updates to `DmHelpManual` + `recommended-system-prompt.md`
- [ ] Full test suite green; new guard tests listed above pass

---

## Developer experience notes

1. **Touchpoint Matrix** — use the **Anchor** column first; line numbers are approximate against current main.
2. **Guard tests** — **PR** column tells you which tests to add in which PR; do not land all Item 1 guards in 4a.
3. **PR-DOC** — same PR or doc-first stack; no merge window without docs (see Execution Rules).
4. **Migration** — Item 4 is prospective only; do not build backfill jobs.
5. **5b performance** — respect event cap + day window; add a timing assertion in integration test if scans regress.

---

## Summary

Item 4 fixed the eviction observability hole in core code; PR-1.1 closes the persistence and discoverability loop. Items 1–3 and 5 have a **touchpoint matrix with anchors**, **PR-scoped guard tests**, **locked Narrative opt-out**, **authoring acceptance tests**, **slot RAW PR-DOC callout**, **multiclass worked examples**, and **5b performance bounds**. Execute PR-1.1 next, then PR-2 or PR-3 in parallel.