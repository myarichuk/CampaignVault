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
| **Loose** | Characters, locations, items, lore, factions, quests (most read paths) | Include entities where `CampaignName` matches the active campaign **or** is null/empty (shared-universe canon) |
| **Strict** | Events, rumors | Include only entities where `CampaignName` exactly matches the active campaign |

**Practical implication:** campaign singletons (time, combat, config) are isolated per slug. Entities with no `CampaignName` (e.g. shared NPCs like Bob the assassin) are visible in every campaign. Campaign-owned entities should use slug-prefixed IDs and are tagged on create. Events and rumors are always campaign-private.

### Campaign scoping

Every campaign-scoped MCP tool requires an explicit **`campaignName`** slug. There is no session-based campaign selection.

Slugs are canonicalized via `CampaignSlug.Canonicalize` (lowercase, hyphens) everywhere: document keys (`CampaignDocumentKeys`), and entity tagging.

## MCP Hosting & Request Flow

- **Transports:** HTTP (stateless) and stdio (`MCP_STDIO=1`), registered in `Program.cs`.
- **Tool surface:** Domain `*Tools` classes (`ExplorationTools`, `MutationTools`, etc.) — `CampaignTools` is a test facade only.
- **Auth:** optional `BEARER_TOKEN` env var → `AuthMiddleware` (timing-safe compare; `/` and `/health` exempt).
- **CORS:** `CORS_ALLOWED_ORIGINS` env var (`*` or comma-separated origins).
- **Concurrency:** `CampaignTools.ExecuteAsync` retries on RavenDB `ConcurrencyException` (state drift).

Typical read flow: tool → `CampaignRepository` → RavenDB query → optional `PressureOrchestrator` → `ToolResult` / view DTO.

Typical write flow: `commit` → `WorldChangeDispatcher` → handler(s) per change → `session.SaveChangesAsync`.

## World-Change Dispatch

`commit` accepts a polymorphic `WorldChange[]` (33 `$type` discriminators in `WorldChanges.cs`, including legacy `spatial_relation` and `level_up`). Each change is routed to exactly one `IWorldChangeHandler` via `ShouldHandle`.

Registered handlers (via DI in `Program.cs`):

- Combat / stats: `HpChangeHandler`, `AttributeChangeHandler`, `StatusChangeHandler`, `RulesetActionHandler`, `LevelUpChangeHandler`, `SystemStatsChangeHandler`
- Inventory: `ItemTransferHandler`, `ItemCreateHandler`, `ItemUpdateHandler`
- Narrative: `EventOccurredHandler`, `RumorEvolvesHandler`, `RelationshipChangeHandler`, `MoodChangeHandler`, `ActivityChangeHandler`
- NPC mind: `NeedChangeHandler`, `KnowledgeUpdateHandler`, `ScheduleChangeHandler`
- World building: `LocationCreateHandler`, `LocationUpdateHandler`, `CharacterCreateHandler`, `CharacterUpdateHandler`
- Scene anchoring: `EngagementRelationChangeHandler`, `SpatialPositionChangeHandler`
- Macro: `TravelChangeHandler`, `RestChangeHandler`, `FactionCreateHandler`, `FactionReputationChangeHandler`, `FactionStateChangeHandler`, `QuestCreateHandler`, `QuestProgressHandler`

`RulesetActionHandler` loads the campaign's `CampaignConfig`, selects the active `IRulesetModule`, calls `IActionResolution.ResolveAsync`, and dispatches any returned follow-up mutations (HP, status, engagement relations from grapple maneuvers, etc.).

### Engagement Relations & Spatial Positioning

Two complementary primitives on `SystemExtension` track *who is doing what to whom* vs. *where someone is relative to something else*. They are intentionally separate from location IDs (`CurrentLocationId`) and social graph edges (`RelationshipChange`).

| Primitive | `$type` | Stored on | Purpose |
|-----------|---------|-----------|---------|
| **Engagement relation** | `engagement_relation` (legacy: `spatial_relation`) | `engagementRelations[]` | Pairwise state anchor — freeform `verb` + `category` |
| **Spatial position** | `spatial_position` | `spatialPositions[]` | Relative placement — `distanceBand`, optional `bearing` / `zone` |

**Engagement relation shape** (`EngagementRelation`):

- `targetId` — other character or object anchor
- `category` — `Physical`, `Social`, `Medical`, `Attention`, `Proximity`
- `verb` — freeform string (e.g. `grappling`, `ranting at`, `stitching`); legacy JSON key `relationType` still deserializes as `verb`
- `restrictionLevel` — optional override of category default (`None` / `Soft` / `Hard`)

**Category defaults** (`EngagementRelationCatalog`):

| Category | Default restriction | Blocks `travel`? | Scene pressure? |
|----------|---------------------|------------------|-----------------|
| Physical, Medical | Hard | yes | yes |
| Social | Soft | no | yes |
| Attention, Proximity | None | no | no |

`EngagementRelationChangeHandler` supports bidirectional inverse pairs for asymmetric verbs (e.g. `Grappling` ↔ `GrappledBy`); symmetric categories copy the same verb to both sides.

**Spatial position shape** (`SpatialPosition`):

- `targetId` — reference entity (PC, bar zone, etc.)
- `distanceBand` — `Touch`, `Close`, `Near`, `Far`, `Distant`
- `bearing`, `zone` — optional freeform scene hints

**Enforcement:**

- `TravelChangeHandler` blocks travel when any `engagementRelations` entry has Hard restriction.
- `EngagementRelationPressureContributor` emits `NARRATIVE PROMPT` for Soft and Hard engagements on scene NPCs (`Character:EngagementLock`).

**Ruleset integration (grapple):** On successful grapple `ruleset_action` (`ContestedCheck` + `ActionCategory.Maneuver` or grapple name), resolvers emit `EngagementRelationChange` mutations via `EngagementMutationHelper`:

- **D&D 5e** — opposed Athletics (or Acrobatics) d20; tie → defender wins
- **PF2e** — Athletics vs target Fortitude DC (not opposed; matches CRB grapple)
- **Fallout 2d20** — opposed success-count pools (Strength + Athletics default)

Escape grapple (`escape: true` or escape action name) clears the engagement on success. Combat grapples need not be manually committed; unresolved RP beats (hugs, tending wounds) should be committed by the LLM.

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
| `ResourceRecoveryRule` | Recover `ResourcePools` (spell slots, ki, focus points, etc.) on long/short rest, per pool recovery types and rest hierarchy |

Rule output: narrative strings (logged as simulation events), `WorldChange` deltas (applied via the same commit path), and optional engine-level pressure items.

## Pressure System

Read-side tools call `PressureOrchestrator.CollectAndCapAsync` with a scope:

- **World** — `get_world_state`
- **Scene** — `get_scene`
- **Npc** — `get_npc_context` (urgent initiative pressures)

Contributors include rumor aging, unresolved events, character distress, quest deadlines, travel interruptions, engagement locks, location integrity/hallucination/connectivity, faction economy/territory, memory decay, and more. Rulesets can add contributors via `IRulesetPressureContributor` (e.g. D&D 5e exhaustion).

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
| `ICharacterBootstrapPipeline` | Per-ruleset HP/defense/proficiency derivation at create, upsert, and level-up |

| Implementation | System | Notable mechanics |
|----------------|--------|-------------------|
| `Dnd5eRulesetResolver` | D&D 5e | Advantage/disadvantage, saving throws, contested checks, grapple → engagement mutations, exhaustion pressure |
| `Pf2eRulesetResolver` | Pathfinder 2e | Four degrees of success, Athletics vs Fortitude DC grapple |
| `Fallout2d20RulesetResolver` | Fallout 2d20 | d20 dice pools, target numbers, opposed pool contested checks |
| `NarrativeRulesetResolver` | Narrative | Oracle-style d100 rolls for skill/combat checks when mechanical rulesets are disabled; no HP math |

`RulesetModuleSelector` validates that every `RulesetSystem` enum value has a registered module at startup. `DefaultRollService` provides deterministic dice evaluation.

### Resource pools (spell slots, ability resources)

`ResourcePoolProvider` (`IRulesetYamlProvider`) loads pool templates from `RulesetData/{system}/pools/*.yaml` (e.g. `spell_slots_1..9`, `ki_points`, `focus_points`) — configuration is data-driven, not hardcoded. Each pool tracks `Current` and `Max` (derived at character create/level-up by `ResourcePoolInitializer` via `Dnd5eCasterLevelHelper` for d&d5e multiclass stacking, class-level maps for PF2e, etc.), `Recovery` type (`LongRest`/`ShortRest`/`Daily`), and last-recovered tracking for idempotency.

Consumption goes through `ResourceChangeHandler` via `$type: "resource"` commits — validates spell level vs. slot pool level (hard-fails only for over-level spells), **clamps to [0, Max] without hard-failing on pool depletion** (appends `(Clamped: ...)` narrative note so the LLM sees it), and logs the spend. Recovery happens on the **next `advance_world`** after a rest, not at rest time, via `ResourceRecoveryRule` (Order 38) which applies the rest-type hierarchy (LongRest ⊃ ShortRest ⊃ PerTurn). Narrative rulesets opt out structurally: `ResourcePoolProvider` never registers pool YAML for `RulesetSystem.Narrative`, so Narrative characters skip recovery entirely.

### Character bootstrap pipeline

`CharacterBootstrapOrchestrator` runs each ruleset's ordered `ICharacterBootstrapPipeline` when:

- `character_create` / `upsert_character` omits `maxHp` (or `maxHp <= 0`)
- `system_stats` patch leaves `maxHp <= 0`
- `level_up` commits incremental HP gains

PCs omit `maxHp`; the pipeline derives HP from typed `systemStats` fields. Creature stat blocks use `maxHp` or `systemStats.statBlockHp` — these skip HP formula only; defense/proficiency steps still run. Steps live under `Rulesets/Bootstrap/`. Defense steps emit `[BOOTSTRAP HINT]` messages with copy-paste `item_create` armor JSON when worn armor is missing.

Combat flow: `start_combat` rolls initiative once per combatant, sorts turn order, stores `CombatEncounter` at the campaign key. `next_turn` advances turns and expires round-based status effects. HP and status mutations during combat go through `commit`, not the turn tools. `get_scene` returns `ActiveCombat` when combat is active at that location.

## NPC Initiative (read-side)

`NpcInitiativeService` synthesizes behavioral initiative signals (relational, memory, need/activity conflict, disposition) for `get_npc_context` and scene NPC enrichment. This is narrative prompting, not combat turn order.

## Environmental & Economic Simulation

- **Location state:** `CurrentState`, `VisualTags`, `DistinctiveFeatures`, `PointsOfInterest`, `AmbientCrowd` — surfaced in `get_scene` and monitored by `LocationFlavorPressureContributor`.
- **Faction economics:** `Faction.EconomicDemand` dictionaries; `FactionEcosystemRule` simulates decay/recovery; `FactionEconomyPressureContributor` surfaces opportunities when the party carries demanded items.
- **Travel:** `TravelChangeHandler` + `EncounterResolver` apply time/need costs, optional random encounters, interrupted-travel activity states, and Hard engagement locks.

## Indexes & Search

RavenDB static indexes (`Character_Search`, `Location_Search`, `Lore_Search`, `Event_Search`, `Item_Search`, `Faction_Search`, `Quest_Search`, `Rumor_Search`) back queries.

`search_world` runs wildcard full-text search across characters, lore, and locations (not items). `recall_history` searches event summaries by keyword. Neither uses vector/semantic embeddings.

## Key Source Locations

| Area | Path |
|------|------|
| MCP tools | `src/CampaignVault/Tools/*Tools.cs` (domain classes; `CampaignTools.cs` is test facade) |
| Repository | `src/CampaignVault/Data/CampaignRepository.cs` |
| World changes | `src/CampaignVault/Models/WorldChanges.cs` |
| Engagement / spatial models | `src/CampaignVault/Models/EngagementRelationMetadata.cs`, `SpatialDistanceBand.cs` |
| Handlers | `src/CampaignVault/Data/ChangeHandlers/` |
| Simulation rules | `src/CampaignVault/Data/*Rule.cs` |
| Pressure | `src/CampaignVault/Data/Pressure/` |
| Rulesets | `src/CampaignVault/Rulesets/` |
| DI wiring | `src/CampaignVault/Program.cs` |