# Campaign Vault Architecture

## Overview

Campaign Vault is an ASP.NET Core MCP server backed by embedded RavenDB. It exposes tools for reading world state, committing atomic narrative mutations, advancing simulation time, and resolving TTRPG mechanics deterministically. The core is organized around four cooperating systems:

1. **Repository layer** (`CampaignRepository`) — RavenDB access, scene assembly, search, and commit orchestration.
2. **World-change dispatch** (`WorldChangeDispatcher` + `IWorldChangeHandler`) — applies `commit` payloads in order.
3. **Simulation engine** (`DefaultSimulationEngine` + `ISimulationRule`) — background world evolution on `advance_world`.
4. **Pressure orchestration** (`PressureOrchestrator` + `IPressureContributor`) — proactive LLM nudges on read paths.

Ruleset math (D&D 5e, PF2e, Fallout 2d20) lives in pluggable `IRulesetModule` implementations, not in the core engine.

## Multi-Campaign Scoping

The database holds multiple campaigns in one RavenDB instance. Isolation is **tiered**, not absolute.

### Per-campaign singletons (strictly namespaced)

Stored under `campaigns/{name}/...` via `CampaignDocumentKeys`:

| Document | ID pattern | Purpose |
|----------|------------|---------|
| `Campaign` | `campaigns/{name}/meta` | Display name, ruleset, system lock-in |
| `CampaignConfig` | `campaigns/{name}/config` | Active ruleset + house-rule options |
| `CampaignTime` | `campaigns/{name}/state/time` | World clock |
| `CombatEncounter` | `campaigns/{name}/combat/current` | Active combat state |
| `NeedDescriptorsConfig` | `campaigns/{name}/config/need-descriptors` | Per-campaign shared need descriptions |

These never cross-contaminate between campaigns.

### World entities (flat document IDs)

Locations, characters, items, lore, rumors, events, factions, and quests use flat IDs (`characters/grog`, `locations/tavern`, etc.). Each entity has an optional `CampaignName` property set on create/upsert from the active campaign context.

Queries apply one of two filters:

| Filter | Entities | Rule |
|--------|----------|------|
| **Loose** | Characters, locations, items, lore, factions, quests, rumors (most read paths) | Include entities where `CampaignName` matches the active campaign **or** is null/empty (shared-universe / legacy shareables) |
| **Strict** | Events | Include only entities where `CampaignName` exactly matches the active campaign |

**Practical implication:** campaign singletons (time, combat, config) are isolated, but two campaigns can still see the same character or location if they share an ID or the entity has no `CampaignName`. Events are always campaign-private. Callers should use distinct IDs per campaign when strict separation is required.

`ICurrentCampaignContext` is a process-wide singleton (not `AsyncLocal`) so `select_campaign` survives across stateless MCP HTTP requests.

## MCP Hosting & Request Flow

- **Transports:** HTTP (stateless) and stdio, registered in `Program.cs` via `ModelContextProtocol.AspNetCore`.
- **Tool surface:** `CampaignTools` — all `[McpServerTool]` methods.
- **Auth:** optional `BEARER_TOKEN` env var → `AuthMiddleware` (timing-safe compare; `/` and `/health` exempt).
- **CORS:** `CORS_ALLOWED_ORIGINS` env var (`*` or comma-separated origins).
- **Concurrency:** `CampaignTools.ExecuteAsync` retries on RavenDB `ConcurrencyException` (state drift).

Typical read flow: tool → `CampaignRepository` → RavenDB query → optional `PressureOrchestrator` → `ToolResult` / view DTO.

Typical write flow: `commit` → `WorldChangeDispatcher` → handler(s) per change → `session.SaveChangesAsync`.

## World-Change Dispatch

`commit` accepts a polymorphic `WorldChange[]` (27 `$type` discriminators in `WorldChanges.cs`). Each change is routed to exactly one `IWorldChangeHandler` via `ShouldHandle`.

Registered handlers (via DI in `Program.cs`):

- Combat / stats: `HpChangeHandler`, `AttributeChangeHandler`, `StatusChangeHandler`, `RulesetActionHandler`
- Inventory: `ItemTransferHandler`, `ItemCreateHandler`, `ItemUpdateHandler`
- Narrative: `EventOccurredHandler`, `RumorEvolvesHandler`, `RelationshipChangeHandler`, `MoodChangeHandler`, `ActivityChangeHandler`
- NPC mind: `NeedChangeHandler`, `KnowledgeUpdateHandler`, `ScheduleChangeHandler`
- World building: `LocationCreateHandler`, `LocationUpdateHandler`, `CharacterCreateHandler`, `CharacterUpdateHandler`
- Macro: `TravelChangeHandler`, `RestChangeHandler`, `FactionCreateHandler`, `FactionReputationChangeHandler`, `FactionStateChangeHandler`, `QuestCreateHandler`, `QuestProgressHandler`

`RulesetActionHandler` loads the campaign's `CampaignConfig`, selects the active `IRulesetModule`, calls `IActionResolution.ResolveAsync`, and dispatches any returned follow-up mutations (HP, status, etc.).

## Simulation Engine

`advance_world` advances `CampaignTime`, builds a `SimulationContext` for the active campaign, and runs `DefaultSimulationEngine`.

Simulation context loading (`SimulationQueryHelper`) is campaign-scoped: characters include both campaign-tagged and shareable (null `CampaignName`) entities; factions, quests, and rumors are filtered similarly.

### Registered simulation rules (execution order by `ISimulationRule.Order`)

| Rule | Responsibility |
|------|----------------|
| `ScheduleEvaluationRule` | NPC schedule → activity/location updates |
| `NeedsAccumulationRule` | Hunger, thirst, tiredness, mood drift |
| `RumorDecayRule` | Rumor lifecycle progression |
| `StatusExpiryRule` | Day-based status effect expiry |
| `MemorySalienceDecayRule` | NPC memory salience decay + urgency bumps |
| `NeedConflictRule` | Need interaction side-effects |
| `FactionEcosystemRule` | Faction influence + `EconomicDemand` shifts |
| `QuestStalenessRule` | Quest urgency / staleness progression |
| `RelationalRearmRule` | Relationship cooldown re-arming |
| `TransientEvictionRule` | Evict transient NPCs from cold locations |

Rule output: narrative strings (logged as simulation events), `WorldChange` deltas (applied via the same commit path), and optional engine-level pressure items.

## Pressure System

Read-side tools call `PressureOrchestrator.CollectAndCapAsync` with a scope:

- **World** — `get_world_state`
- **Scene** — `get_scene`
- **Npc** — `get_npc_context` (urgent initiative pressures)

Contributors include rumor aging, unresolved events, character distress, quest deadlines, travel interruptions, location integrity/hallucination/connectivity, faction economy/territory, memory decay, and more. Rulesets can add contributors via `IRulesetPressureContributor` (e.g. D&D 5e exhaustion).

`PressureManager` deduplicates, caps volume, and escalates repeated nags to `ENGINE WARNING:` after configurable suppression counts (`CampaignConfig.PressureEscalationCount`).

**Response shape:** `get_scene` and `advance_world` attach formatted pressure strings on `ToolResult.WorldPressure`. `get_world_state` embeds them in `Data.WorldPressure` on the view object.

## Ruleset Architecture

Implemented and registered at startup:

| Interface | Role |
|-----------|------|
| `IRulesetModule` | Per-system facade |
| `IActionResolution` | Resolves `ruleset_action` commits (attacks, saves, skill checks) |
| `ICombatRuleset` | Rolls initiative for `start_combat` |
| `IRulesetPressureContributor` | System-specific read-side pressures |

| Implementation | System | Notable mechanics |
|----------------|--------|-------------------|
| `Dnd5eRulesetResolver` | D&D 5e | Advantage/disadvantage, saving throws, exhaustion pressure |
| `Pf2eRulesetResolver` | Pathfinder 2e | Four degrees of success |
| `Fallout2d20RulesetResolver` | Fallout 2d20 | d20 dice pools, target numbers |

`RulesetModuleSelector` validates that every `RulesetSystem` enum value has a registered module at startup. `DefaultRollService` provides deterministic dice evaluation.

Combat flow: `start_combat` rolls initiative once per combatant, sorts turn order, stores `CombatEncounter` at the campaign key. `next_turn` advances turns and expires round-based status effects. HP and status mutations during combat go through `commit`, not the turn tools. `get_scene` returns `ActiveCombat` when combat is active at that location.

## NPC Initiative (read-side)

`NpcInitiativeService` synthesizes behavioral initiative signals (relational, memory, need/activity conflict, disposition) for `get_npc_context` and scene NPC enrichment. This is narrative prompting, not combat turn order.

## Environmental & Economic Simulation

- **Location state:** `CurrentState`, `VisualTags`, `DistinctiveFeatures`, `PointsOfInterest`, `AmbientCrowd` — surfaced in `get_scene` and monitored by `LocationFlavorPressureContributor`.
- **Faction economics:** `Faction.EconomicDemand` dictionaries; `FactionEcosystemRule` simulates decay/recovery; `FactionEconomyPressureContributor` surfaces opportunities when the party carries demanded items.
- **Travel:** `TravelChangeHandler` + `EncounterResolver` apply time/need costs, optional random encounters, and interrupted-travel activity states.

## Indexes & Search

RavenDB static indexes (`Character_Search`, `Location_Search`, `Lore_Search`, `Event_Search`, `Item_Search`, `Faction_Search`, `Quest_Search`, `Rumor_Search`) back queries.

`search_world` runs wildcard full-text search across characters, lore, and locations (not items). `recall_history` searches event summaries by keyword. Neither uses vector/semantic embeddings.

## Key Source Locations

| Area | Path |
|------|------|
| MCP tools | `src/CampaignVault/Tools/CampaignTools.cs` |
| Repository | `src/CampaignVault/Data/CampaignRepository.cs` |
| World changes | `src/CampaignVault/Models/WorldChanges.cs` |
| Handlers | `src/CampaignVault/Data/ChangeHandlers/` |
| Simulation rules | `src/CampaignVault/Data/*Rule.cs` |
| Pressure | `src/CampaignVault/Data/Pressure/` |
| Rulesets | `src/CampaignVault/Rulesets/` |
| DI wiring | `src/CampaignVault/Program.cs` |