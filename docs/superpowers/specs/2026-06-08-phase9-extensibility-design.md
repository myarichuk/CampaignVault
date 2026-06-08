# Phase 9: Ruleset & Simulation Extensibility Design

## Overview

This document outlines the architectural enhancements for Phase 9 of Campaign Vault. The goal is to make the system highly modular, supporting tunable simulation behavior and cleaner ruleset boundaries, while preventing D&D 5e (and other system-specific) mechanics from leaking into the core engine.

**Scope clarification:** Phase 9 targets two kinds of extensibility:

| Kind | What it enables | Mechanism |
|------|-----------------|-----------|
| **Tunable defaults** | Per-campaign house rules (faster need decay, longer transient grace) | `CampaignConfig` + DI-registered rules/contributors |
| **New TTRPG systems** | A fully different rules engine (PF2e, Fallout, homebrew) | `IRulesetModule` facade + resolver implementations |

True *plugin* rulesets (dynamic assembly loading, string-based system IDs beyond the `RulesetSystem` enum) are **out of scope** for Phase 9 unless explicitly added as a follow-up. Homebrew in Phase 9 means tuning config and swapping DI registrations, not loading arbitrary third-party assemblies at runtime.

---

## 1. Ruleset Architecture (Facade Pattern)

### Current State (accurate)

- `RulesetActionHandler` is already thin: it loads `CampaignConfig`, selects a resolver via `IRulesetResolverSelector`, and dispatches returned `WorldChange` mutations. It contains **no** 5e-specific logic.
- The real coupling lives in:
  - `RulesetResolverBase<TStats>` — switch on `RulesetActionType`, abstract per-action methods
  - Concrete resolvers (`Dnd5eRulesetResolver`, `Pf2eRulesetResolver`, `Fallout2d20RulesetResolver`)
  - `GetCharacterPressureAsync` — ruleset-agnostic HP/status thresholds that may not fit all systems
- `IRulesetResolver` is small today (only `ResolveAsync` + `RollInitiativeAsync`), but `RulesetActionType` already defines seven action kinds while the base class implements only three (`Attack`, `SkillCheck`, `ContestedCheck`). The interface will grow unless capabilities are split now.

### Solution

Introduce a **facade** that composes focused capability interfaces. Avoid naming it `IRulesetSystem` — that collides with the existing `RulesetSystem` enum used by `CampaignConfig.ActiveSystem` and `IRulesetResolverSelector`.

**Recommended name:** `IRulesetModule` (facade). Keep `IRulesetResolver` as a thin adapter during migration, then deprecate.

#### Focused interfaces

```csharp
/// <summary>
/// Facade for the active TTRPG system. Registered per RulesetSystem enum value.
/// </summary>
public interface IRulesetModule
{
    RulesetSystem System { get; }

    IActionResolution Actions { get; }
    ICombatRuleset Combat { get; }

    /// <summary>
    /// Optional read-side pressure hooks (narrative nags tied to ruleset state).
    /// Returns empty if the system has no read-side pressures.
    /// </summary>
    IReadOnlyList<IRulesetPressureContributor> PressureContributors { get; }
}

public interface IActionResolution
{
    Task<ResolverOutput> ResolveAsync(
        ChangeContext context,
        RulesetAction action,
        CancellationToken ct = default);
}

public interface ICombatRuleset
{
    Task<float> RollInitiativeAsync(IAsyncDocumentSession session, string characterId, CancellationToken ct = default);
    Task<float> RollInitiativeAsync(Character character, CancellationToken ct = default);
    // Future: turn order, combat state transitions, status application helpers
}
```

#### Ruleset read-side pressures vs simulation rules

`IRulesetPressureContributor` is for **LLM-facing nags on tool reads** (`get_world_state`, `get_scene`). It must not be confused with tick-time mechanical effects.

| Surface | When | Contract | Example |
|---------|------|----------|---------|
| `IRulesetPressureContributor` | `get_world_state` / `get_scene` | `WorldPressureItem` nags | "Character has 3 levels of Exhaustion — narrate cumulative penalties" |
| `ISimulationRule` | `advance_world` | `WorldChange` deltas + narratives | Poison deals 1d4 damage per tick via `HpChange` |

Poison ticking damage belongs in a simulation rule (or `StatusExpiryRule` extension), not a pressure contributor.

#### Migration strategy

1. Add `IRulesetModule` with default implementation wrapping existing resolver logic.
2. `Dnd5eRulesetResolver` implements `IRulesetModule`; `IActionResolution.ResolveAsync` delegates to current `ResolveAsync`.
3. `IRulesetResolver` becomes a compatibility shim: `class RulesetResolverAdapter : IRulesetResolver` that forwards to `IRulesetModule.Actions`.
4. `RulesetActionHandler` continues to work unchanged through the adapter.
5. Once all resolvers implement `IRulesetModule`, remove `IRulesetResolver` and update `IRulesetResolverSelector` → `IRulesetModuleSelector`.

`RulesetActionHandler` changes are minimal in Phase 9 — the handler already dispatches correctly. The refactor targets resolver internals and pressure decoupling.

---

## 2. Modular Pressure Generation

### Current Problem

`WorldPressure` generation is scattered across three locations with duplicated patterns:

| Location | Approx. responsibility |
|----------|------------------------|
| `CampaignTools.GetWorldState` | Aging rumors/events, dangling items, never-visited transients, quest deadlines, stuck travel, char-pressure hints |
| `CampaignTools.GetScene` | Location integrity, scene quests, memory decay, faction reputation/opportunism/economy, transient quest-giver guard |
| `CampaignRepository.GetCharacterPressureAsync` | HP, statuses, needs, morale/willpower/temperature, extreme attributes, relationships |

This makes it hard to add ruleset-specific read-side pressures or disable core checks for a given campaign.

### Solution

#### Two scopes: World vs Scene

Pressure contributors must declare their scope so scene logic does not run on every `get_world_state` call.

```csharp
public enum PressureScope
{
    /// <summary>Runs during get_world_state (campaign-wide kickoff view).</summary>
    World,

    /// <summary>Runs during get_scene (location-scoped exploration view).</summary>
    Scene
}

public sealed record PressureContext(
    string CampaignName,
    CampaignTime Time,
    CampaignConfig Config,
    IAsyncDocumentSession Session,

    // World scope: pre-fetched globals (minimize N+1)
    IReadOnlyList<Rumor>? ActiveRumors = null,
    IReadOnlyList<Event>? RecentEvents = null,

    // Scene scope: populated only when Scope == Scene
    SceneView? Scene = null,
    string? RequestedLocationId = null,
    bool PartyPresent = false
);

public interface IPressureContributor
{
    PressureScope Scope { get; }
    int Order { get; }  // Lower runs first; mirrors ISimulationRule.Order

    Task<IEnumerable<WorldPressureItem>> EvaluateAsync(
        PressureContext ctx,
        CancellationToken ct = default);
}
```

Reuse patterns from `SimulationContext` where sensible, but do **not** reuse `SimulationContext` directly — it is tied to `advance_world` and lacks scene/location data.

#### Aggregation: `PressureOrchestrator`

Introduce a single orchestrator injected into `CampaignTools`:

```csharp
public interface IPressureOrchestrator
{
    Task<string[]> CollectAndCapAsync(
        PressureScope scope,
        PressureContext ctx,
        CancellationToken ct = default);
}
```

Flow:

1. `CampaignTools` builds a `PressureContext` with pre-fetched data.
2. `IPressureOrchestrator` runs all registered `IPressureContributor` instances matching `scope`, ordered by `Order`.
3. Ruleset-specific contributors from `IRulesetModule.PressureContributors` are included when their scope matches.
4. Results are passed to existing `IPressureManager.FilterAndCapAsync` (cooldown/escalation logic unchanged).
5. `CampaignTools` retains **only** the copy-paste hint enrichment layer (e.g. appending commit JSON examples to critically-wounded pressures) as post-processing, or moves that into a final `PressureHintEnricher` contributor with `Order => int.MaxValue`.

#### DI registration

```csharp
// Program.cs
builder.Services.AddSingleton<IPressureContributor, AgingRumorPressureContributor>();
builder.Services.AddSingleton<IPressureContributor, DanglingItemPressureContributor>();
// ... (see inventory below)
builder.Services.AddSingleton<IPressureOrchestrator, PressureOrchestrator>();
```

Ruleset contributors are **not** registered globally — they are discovered via the active `IRulesetModule` for the campaign.

#### Contributor inventory (extraction map)

| Class | Scope | Order | Source today |
|-------|-------|-------|--------------|
| `AgingRumorPressureContributor` | World | 10 | `GetWorldState` — rumors spreading > 5 days |
| `UnresolvedEventPressureContributor` | World | 15 | `GetWorldState` — unresolved events |
| `CharacterDistressPressureContributor` | World | 20 | `GetCharacterPressureAsync` (move off repository) |
| `DanglingItemPressureContributor` | World | 30 | `GetWorldState` — orphaned item holders |
| `NeverVisitedTransientPressureContributor` | World | 35 | `GetWorldState` — transients at unvisited locations |
| `QuestDeadlinePressureContributor` | World, Scene | 40 | `AddQuestDeadlinePressures` (shared helper → contributor) |
| `StuckTravelPressureContributor` | World, Scene | 45 | Interrupted travel in both tools |
| `LocationHallucinationPressureContributor` | Scene | 10 | `GetScene` — unanchored location |
| `LocationIntegrityPressureContributor` | Scene | 15 | Missing travel commit, no exits, empty-expects-crowd |
| `LocationConnectivityPressureContributor` | Scene | 20 | Missing reverse parent link |
| `LocationFlavorPressureContributor` | Scene | 25 | Environmental tags, flavor vacuum, dead-end suggestion |
| `SceneQuestStalenessPressureContributor` | Scene | 30 | Quest stale > 10 days (scene-local) |
| `TransientQuestGiverPressureContributor` | Scene | 35 | Quest giver with `KeepAlive = false` |
| `MemoryDecayPressureContributor` | Scene | 40 | Phase 8.3 epistemic drift |
| `FactionTerritoryPressureContributor` | Scene | 50 | Hostile/allied reputation thresholds |
| `FactionOpportunisticPressureContributor` | Scene | 55 | Phase 8.2 opportunistic stance |
| `FactionEconomyPressureContributor` | Scene | 60 | Phase 8.5 economic demand |
| `FactionRecentEventPressureContributor` | Scene | 65 | Unacted simulation events involving factions |
| `PressureHintEnricher` | World | 1000 | Copy-paste commit JSON hints (optional final pass) |

Contributors that apply to both scopes (`QuestDeadline`, `StuckTravel`) register once with logic that reads `ctx.Scene` when present.

After extraction, delete `CampaignRepository.GetCharacterPressureAsync` and update tests to assert via `IPressureOrchestrator` or tool-level integration tests.

---

## 3. Simulation Rule Extensibility

### Current State

Simulation rules are **already composable** via `ISimulationRule` + DI registration in `Program.cs`. `DefaultSimulationEngine` runs rules in `Order` sequence. This is the **preferred** extension point for new tick-time behavior.

What blocks customization today:

- All default rules are `sealed` — inheritance is impossible
- Tuning parameters are hardcoded magic numbers (e.g. `NeedsAccumulationRule` uses `10f * days`, thirst `1.2f`, tiredness `0.8f`)
- `SimulationContext` already carries `CampaignConfig` (Phase 8.5) but most rules ignore it

### Solution: Composition first, inheritance second

| Strategy | When to use | How |
|----------|-------------|-----|
| **Composition** (preferred) | New behavior, replacing a rule entirely | Register a new `ISimulationRule` in DI with appropriate `Order` |
| **Inheritance** | Tweaking one method of a default rule | Unseal base rule, override `virtual` methods, register subclass in DI |
| **Config tuning** | Adjusting rates/thresholds without code | Read from `CampaignConfig` in base rule implementations |

Phase 9 changes:

1. **Remove `sealed`** from all default simulation rules.
2. **Mark `ApplyAsync` as `virtual`** on each default rule (or extract calculation into `protected virtual` helpers where `ApplyAsync` is the orchestration entry point).
3. **Add `CampaignConfig` properties** and read them in base implementations.

#### Proposed `CampaignConfig` additions

| Property | Default | Used by |
|----------|---------|---------|
| `NeedAccumulationRate` | `10f` | `NeedsAccumulationRule` — base multiplier per day |
| `ThirstAccumulationMultiplier` | `1.2f` | `NeedsAccumulationRule` |
| `TirednessAccumulationMultiplier` | `0.8f` | `NeedsAccumulationRule` |
| `MoraleDriftPerDay` | `-0.8f` | `NeedsAccumulationRule` |
| `TransientEvictionGraceDays` | `1` | `TransientEvictionRule` — days since last visit before eviction |
| `RumorAgingPressureDays` | `5` | `AgingRumorPressureContributor` (read-side, but config lives here) |
| `QuestStalenessDays` | `10` | `SceneQuestStalenessPressureContributor` |
| `CharacterPressureHpCriticalThreshold` | `0.25f` | `CharacterDistressPressureContributor` — fraction of MaxHp |

Existing config properties (`MemoryTrivialDecayDays`, `EconomicDemandDecayDays`, etc.) remain unchanged.

#### What NOT to do

- Do not make `ISimulationRule` inheritance-based as the primary path — it fights the existing DI composition model.
- Do not move tick-time mechanical effects into `IPressureContributor` — keep the read/tick boundary clean.

---

## 4. Testing

### Pressure contributors

- Migrate existing assertions in `CampaignToolsTests`, `PressureManagerIntegrationTests`, and `CampaignRepositoryTests` to tool-level integration tests that call `get_world_state` / `get_scene` and assert on `WorldPressure` output.
- Add unit tests per contributor with a minimal `PressureContext` (no full Raven session where possible).
- Ensure scope isolation: a `Scene`-only contributor must produce zero items when orchestrator runs with `PressureScope.World`.

### Simulation rules

- Extend `TransientEvictionRuleTests` and harness scenarios to verify config overrides (e.g. `TransientEvictionGraceDays = 3` delays eviction).
- Add a test that registers a custom `NeedsAccumulationRule` subclass via DI and confirms it runs instead of the default.

### Ruleset facade

- Existing `CombatE2ETests` should pass through the `RulesetResolverAdapter` shim with no behavior change.
- Add a test that `IRulesetModule.PressureContributors` surfaces ruleset-specific pressures when `ActiveSystem` is set.

---

## Execution Plan

Reordered for risk and dependency — pressure extraction is independent and delivers the biggest maintainability win early.

### PR 1: Config-driven simulation tuning (low risk)
- Add `CampaignConfig` properties from Section 3.
- Unseal default simulation rules; mark `ApplyAsync` (or inner helpers) `virtual`.
- Wire `NeedsAccumulationRule` and `TransientEvictionRule` to read config.
- Tests for config overrides.

### PR 2: Pressure contributor pipeline
- Define `PressureScope`, `PressureContext`, `IPressureContributor`, `IPressureOrchestrator`.
- Implement `PressureOrchestrator` + DI registration.
- Extract **World-scoped** contributors first (`AgingRumor`, `UnresolvedEvent`, `CharacterDistress`, `DanglingItem`, `NeverVisitedTransient`, `QuestDeadline`, `StuckTravel`).
- Refactor `GetWorldState` to use orchestrator.
- Migrate `GetCharacterPressureAsync` into `CharacterDistressPressureContributor`; remove repository method.
- Migrate world-state pressure tests.

### PR 3: Scene-scoped pressure contributors
- Extract all **Scene-scoped** contributors from `GetScene`.
- Refactor `GetScene` to use orchestrator.
- Migrate scene pressure tests (memory decay, opportunistic faction, economic demand, location integrity).

### PR 4: Ruleset facade (incremental)
- Define `IRulesetModule`, `IActionResolution`, `ICombatRuleset`, `IRulesetPressureContributor`.
- Implement facade on `Dnd5eRulesetResolver` (then PF2e, Fallout).
- Add `RulesetResolverAdapter` shim; keep `RulesetActionHandler` unchanged.
- Register ruleset pressure contributors via module (e.g. D&D exhaustion nag).

### PR 5: Cleanup
- Deprecate and remove `IRulesetResolver` + adapter once all systems implement `IRulesetModule`.
- Rename `IRulesetResolverSelector` → `IRulesetModuleSelector`.
- Update docs/comments referencing old patterns.

---

## Future (out of Phase 9 scope)

- **Dynamic plugin rulesets:** String-based `ActiveSystem`, assembly scanning, user-supplied resolver DLLs.
- **Parallel pressure evaluation:** Contributors are independent today; orchestrator can parallelize later if profiling warrants it.
- **Read-only snapshot facade for rules:** `SimulationContext` comment already notes this; pressure context may evolve similarly.