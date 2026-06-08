# Phase 9: Ruleset & Simulation Extensibility Design

## Overview
This document outlines the architectural enhancements for Phase 9 of Campaign Vault. The goal is to make the system highly modular, supporting custom rulesets (e.g., homebrew rules, Darkest Dungeon style constraints) and preventing hardcoded ruleset logic (like D&D 5e mechanics) from leaking into the core simulation engine.

## 1. Ruleset Architecture (Facade Pattern)

### Current Problem
`IRulesetResolver` is a monolithic interface that handles everything from action resolution to initiative. `RulesetActionHandler` contains hardcoded assumptions.

### Solution
- **`IRulesetSystem` (Facade)**: We will replace `IRulesetResolver` with a facade interface named `IRulesetSystem`. This interface will act as a registry/provider for focused capabilities.
- **Focused Interfaces**:
  - `ICombatRuleset`: Handles initiative, combat flow, and status application.
  - `IActionResolution`: Handles mechanical determinism, such as advantage/disadvantage, dice pools, difficulty classes, and calculating degrees of success.
  - `IRulesetPressureContributor`: Allows the active ruleset to inject mechanical pressures (e.g., "The Poisoned condition deals 1d4 damage this turn").
- **Core Decoupling**: `RulesetActionHandler` will be refactored. Instead of executing 5e-specific logic, it will resolve `IRulesetSystem`, request the appropriate capability (e.g., `IActionResolution`), and dispatch validation and mechanical execution to the ruleset implementation. The engine will respect the outcome but continue to support the LLM's narrative override.

## 2. Modular Pressure Generation

### Current Problem
`WorldPressure` generation is tightly coupled to core classes like `CampaignRepository` (`GetCharacterPressureAsync`) and `CampaignTools` (`GetWorldState`), making it difficult for rulesets or plugins to emit their own pressures.

### Solution
- **`IPressureContributor` Interface**: 
  ```csharp
  public interface IPressureContributor
  {
      Task<IEnumerable<WorldPressureItem>> EvaluateAsync(SimulationContext ctx, IAsyncDocumentSession session);
  }
  ```
  Contributors will receive a hybrid context: a `SimulationContext` (containing pre-fetched active characters/locations to minimize N+1 queries) and an `IAsyncDocumentSession` (for deep querying if necessary).
- **Core Extraction**: The monolithic pressure blocks (transients, old events, dangling items, core needs) will be extracted into dedicated, focused internal implementations (e.g., `CoreCharacterPressureContributor`, `DanglingItemContributor`).
- **Aggregation**: The engine will aggregate results from all injected `IPressureContributor` implementations (including those provided by the active `IRulesetSystem`) during `GetWorldState` and `GetScene`.

## 3. Simulation Rule Extensibility

### Current Problem
Simulation rules (e.g., `NeedsAccumulationRule`, `TransientEvictionRule`, `RumorDecayRule`) are marked `sealed`, and their tuning parameters (decay rates, threshold limits) are hardcoded magic numbers.

### Solution
- **Open for Extension**: Remove the `sealed` modifier from all default simulation rules.
- **Virtual Methods**: Mark core evaluation methods (like `ApplyAsync` or inner calculation loops) as `virtual` so custom homebrew rules can inherit from the base classes and override specific behaviors completely.
- **Configuration-Driven Tuning**: Extract magic numbers into properties on `CampaignConfig` (e.g., `TransientEvictionDays`, `NeedDecayMultiplier`). This allows users/plugins to tune the simulation parameters per-campaign without rewriting code.

## Execution Plan
1. Introduce `CampaignConfig` properties for simulation tuning and unseal existing rules.
2. Define the `IPressureContributor` interface and extract existing pressures into modular classes.
3. Define `IRulesetSystem` and focused ruleset interfaces.
4. Refactor existing rulesets (e.g., Dnd5e) to use the facade and focused interfaces.
5. Update `RulesetActionHandler` to dispatch to the facade.
