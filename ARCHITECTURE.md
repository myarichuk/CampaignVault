# Campaign Vault Architecture

## Overview

Campaign Vault is an ASP.NET Core MCP server backed by embedded RavenDB. It exposes tools for reading world state, committing atomic narrative mutations, advancing simulation time, and resolving TTRPG mechanics deterministically. The core is organized around four cooperating systems:

1. **Repository layer** (`CampaignRepository`) — RavenDB access, scene assembly, search, and commit orchestration.
2. **World-change dispatch** (`WorldChangeDispatcher` + `IWorldChangeHandler`) — applies `commit` payloads in order.
3. **Simulation engine** (`DefaultSimulationEngine` + `ISimulationRule`) — background world evolution on `advance_world`.
4. **Pressure orchestration** (`PressureOrchestrator` + `IPressureContributor`) — proactive LLM nudges on read paths.

Ruleset math (D&D 5e, PF2e) lives in pluggable `IRulesetModule` implementations, not in the core engine.

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

Locations, characters, items, lore, rumors, events, factions, and quests use flat IDs (`chars/grog`, `locations/tavern`, etc.). Each entity has an optional `CampaignName` property set on create/upsert from the active campaign context.

**Canonical ID prefixes are enforced at the write boundary** (`CanonicalId.Normalize`, `Data/CanonicalId.cs`): the `chars/` prefix is canonical for characters (the `characters/` alias is silently rewritten); a bare ID with no prefix at all gets the canonical prefix for its entity kind prepended at single-entity write sites (e.g. `UpsertCharacterAsync`); `WorldChangeDispatcher` runs the same alias rewrite across every ID-like field of a `commit` batch before dispatch, so `chars/`-prefix checks deeper in the pipeline (e.g. `ItemTransferHandler`) are correct by construction.

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

Typical seed flow: `world_build` (`WorldBuilderTools.WorldBuild`) — one batch call, struct-of-typed-arrays (locations/factions/creatures/spells/feats/characters/items/quests/plotThreads/lore/rumors/needDescriptors), dispatched in that fixed dependency order inside a single session/save. Reuses the same per-kind `Apply*UpsertAsync` helpers and `CampaignRepository.Upsert*Async` methods that back the (non-MCP, internal-only) single-entity upsert paths, so a hard validation failure on any entry rolls back the entire batch atomically — same "resend full batch" model as `commit`.

## World-Change Dispatch

`commit` accepts a polymorphic `WorldChange[]` (`$type` discriminators in `WorldChanges.cs`; canonical list lives in `CommitTypesReference.SupportedTypesList` — don't hardcode a count here, it drifts). Each change is routed to exactly one `IWorldChangeHandler` via `ShouldHandle`.

Registered handlers (via DI in `Program.cs`). Entity *creation* is normally done via `world_build` (batch upsert), not `commit` — `location`/`faction`/`quest`/`item` have no `_create` `$type`. `character_create` is the one exception, kept as a guard: it exists to surface a structured collision error (`EntityCollisions`) if the LLM tries to `commit`-create a character that already exists, not as a primary creation path.

- Combat / stats: `HpChangeHandler`, `AttributeChangeHandler`, `StatusChangeHandler`, `RulesetActionHandler`, `LevelUpChangeHandler`, `SystemStatsChangeHandler`, `ResourceChangeHandler`
- Inventory: `ItemTransferHandler`, `ItemUpdateHandler`, `ItemEquipHandler`, `ItemUnequipHandler`, `ItemUseHandler`, `ItemPersistenceSurfacedHandler`
- Narrative: `EventOccurredHandler`, `RumorEvolvesHandler`, `RelationshipChangeHandler`, `MoodChangeHandler`, `ActivityChangeHandler`
- NPC mind: `NeedChangeHandler`, `KnowledgeUpdateHandler`, `ScheduleChangeHandler`, `MemoryDecayHandler`
- World update: `LocationUpdateHandler`, `CharacterCreateHandler` (legacy `character_create` $type, collision-safe), `CharacterUpdateHandler`
- Scene anchoring: `EngagementRelationChangeHandler`, `SpatialPositionChangeHandler`, `SceneInterruptChangeHandler`, `SceneSetupChangeHandler`
- Macro: `TravelChangeHandler`, `RestChangeHandler`, `RestRecoveryAckHandler`, `FactionReputationChangeHandler`, `FactionStateChangeHandler`, `QuestProgressHandler`, `PlotThreadProgressHandler`, `PlotThreadClueDiscoveredHandler`, `ArchiveEntityChangeHandler`

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

**Composite scene setup (`scene_setup`):** `SceneSetupChange` lets the LLM set engagement and/or spatial position against the same `targetId` in one commit item — e.g. placing two characters in a scene in a single call instead of two. `SceneSetupChangeHandler` is a thin orchestrator: it synthesizes an `EngagementRelationChange`/`SpatialPositionChange` from its `Engagement`/`Spatial` sub-objects and dispatches them via `DispatchMutationAsync` (same pattern `RulesetActionHandler` uses for derived mutations), so all existing validation, bidirectional mirroring, no-op detection, and history logging apply unchanged. No new persisted model — it writes to the same `engagementRelations[]`/`spatialPositions[]` lists. The bare `engagement_relation`/`spatial_position` types remain for single-purpose updates.

**Ruleset integration (grapple):** On successful grapple `ruleset_action` (`ContestedCheck` + `ActionCategory.Maneuver` or grapple name), resolvers emit `EngagementRelationChange` mutations via `EngagementMutationHelper`:

- **D&D 5e** — opposed Athletics (or Acrobatics) d20; tie → defender wins
- **PF2e** — Athletics vs target Fortitude DC (not opposed; matches CRB grapple)

Escape grapple (`escape: true` or escape action name) clears the engagement on success. Combat grapples need not be manually committed; unresolved RP beats (hugs, tending wounds) should be committed by the LLM.

## Simulation Engine

`advance_world` advances `CampaignTime`, builds a `SimulationContext` for the active campaign, and runs `DefaultSimulationEngine`.

Simulation context loading (`SimulationQueryHelper`) is campaign-scoped: characters include both campaign-tagged and shareable (null `CampaignName`) entities; factions, quests, and rumors are filtered similarly.

### Registered simulation rules (execution order by `ISimulationRule.Order`)

| Rule | Responsibility |
|------|----------------|
| `ScheduleEvaluationRule` | NPC schedule → activity/location updates |
| `NeedsAccumulationRule` | Hunger, thirst, tiredness (core); open-ended custom needs (paranoia, obsession, wanderlust, bloodlust, guilt, despair, etc.) |
| `RumorDecayRule` | Rumor lifecycle progression |
| `StatusExpiryRule` | Day-based status effect expiry |
| `MemorySalienceDecayRule` | NPC memory salience decay + urgency bumps |
| `NeedConflictRule` | Need interaction side-effects |
| `ClimateExposureRule` | Felt temperature per located character = ambient (`ClimateCycle`, per `ClimateZone`/time-of-day) + `WarmthRating` from equipped items — insulation raises felt temp (helps in cold, hurts in heat); writes `SystemStats.Temperature`, no auto-applied mechanical penalty |
| `FactionEcosystemRule` | Faction influence + `EconomicDemand` shifts |
| `QuestStalenessRule` | Quest urgency / staleness progression |
| `RelationalRearmRule` | Relationship cooldown re-arming |
| `AmbientItemDecayRule` | Flags ambient items past their LLM-authored expiry (never moves/archives/deletes — just flips `PressureSurfaced` once; the pressure-side nag is a separate contributor, see below) |
| `TransientEvictionRule` | Evict transient NPCs from cold locations |
| `ResourceRecoveryRule` | Recover `ResourcePools` (spell slots, ki, focus points, etc.) on long/short rest, per pool recovery types and rest hierarchy |

Rule output: narrative strings (logged as simulation events), `WorldChange` deltas (applied via the same commit path), and optional engine-level pressure items.

## Pressure System

Read-side tools call `PressureOrchestrator.CollectAndCapAsync` with a scope:

- **World** — `get_world_state`
- **Scene** — `get_scene`
- **Npc** — `get_npc_context` (urgent initiative pressures)

Contributors include rumor aging, unresolved events, character distress (including temperature extremes from `ClimateExposureRule`'s felt-temp reading), quest deadlines, travel interruptions, engagement locks, location integrity/hallucination/connectivity, faction economy/territory, memory decay, climate/gear mismatch (`ClimateShiftPressureContributor`), ambient item expiry nags (`AmbientItemExpiryPressureContributor` — reads the flag `AmbientItemDecayRule` set, distinct rule vs. contributor split), and more. Rulesets can add contributors via `IRulesetPressureContributor` (e.g. D&D 5e exhaustion).

`PressureManager` deduplicates, caps volume, and escalates repeated nags to `ENGINE WARNING:` after configurable suppression counts (`CampaignConfig.PressureEscalationCount`). Cooldown/escalation tracking (`Campaign.PressureCooldowns`, keyed `Severity:EntityId`) now also compares a normalized content signature (`PressureHelpers.ComputeContentSignature` — SHA-256 of the pressure `Text` with digits stripped, so "morale 8%" and "morale 3%" still share suppression state as the same underlying nag) against the last-surfaced signature: a materially different `Text` under the same key is treated as a fresh nag (fresh escalation cycle) instead of inheriting stale suppression state or being silently dropped by `PressureOrchestrator`'s merge (which now includes the signature in its dedup key). `advance_world` runs with cooldowns disabled, so it dedupes `SimulatorEvents` directly by content signature before building pressure items, since cooldown-based suppression isn't available on that path.

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
| `NarrativeRulesetResolver` | Narrative | Oracle-style d100 rolls for skill/combat checks when mechanical rulesets are disabled; no HP math |

`RulesetModuleSelector` validates that every `RulesetSystem` enum value has a registered module at startup. `DefaultRollService` provides deterministic dice evaluation.

### Resource pools (spell slots, ability resources)

`ResourcePoolProvider` (`IRulesetYamlProvider`) loads pool templates from `RulesetData/{system}/pools/*.yaml` (e.g. `spell_slots_1..9`, `ki_points`, `focus_points`) — configuration is data-driven, not hardcoded. Each pool tracks `Current` and `Max` (derived at character create/level-up by `ResourcePoolInitializer` via `Dnd5eCasterLevelHelper` for d&d5e multiclass stacking, class-level maps for PF2e, etc.), `Recovery` type (`LongRest`/`ShortRest`/`Daily`), and last-recovered tracking for idempotency.

Consumption goes through `ResourceChangeHandler` via `$type: "resource"` commits — validates spell level vs. slot pool level (hard-fails only for over-level spells), **clamps to [0, Max] without hard-failing on pool depletion** (appends `(Clamped: ...)` narrative note so the LLM sees it), and logs the spend. Recovery happens **immediately when a `rest` commit completes** — `RestChangeHandler` calls the shared `RestRecoveryLogic.BuildRecoveryDeltas` synchronously (applying the rest-type hierarchy LongRest ⊃ ShortRest ⊃ PerTurn) and dispatches the resulting deltas in the same commit, so a quick rest and a long narrative rest both recover pools the instant the rest finishes — no separate `advance_world` call needed, and there is no live-vs-async distinction. `ResourceRecoveryRule` (Order 38) still runs during `advance_world` as a defense-in-depth fallback using the same idempotency guard (`RestSequence`/`LastRecoveredRestSequence`), so it is normally a no-op for characters already recovered synchronously. Narrative rulesets opt out structurally: `ResourcePoolProvider` never registers pool YAML for `RulesetSystem.Narrative`, so Narrative characters skip recovery entirely.

### Relationship-based social roll modifiers

`RelationshipModifierHelper` + `SocialSkillGating` translate stored relationship scores (on `Character.Social.Relationships`) into social skill roll modifiers for Dnd5e and PF2e. Gating: `ActionCategory: Social` **or** per-ruleset skill lists (5e: Persuasion/Deception/…; PF2e: Diplomacy/Society/…). Modifiers are banded: score ≥80 → +5 (trusted friend), 60–79 → +3 (friendly), 40–59 → +1 (acquainted), −39..39 → 0 (neutral), −59..−40 → −1 (distrustful), −79..−60 → −3 (hostile), ≤−80 → −5 (hated enemy). Each resolver adds the modifier to the roll bonus and includes the label in the narrative (e.g., `(trusted friend)`). Contested checks apply the modifier only to the actor's roll. Multi-target social actions use the first `targetId` as the relationship source. `WorldChangeDispatcher` preloads `CampaignConfig` (with default fallback) only when the batch includes `ruleset_action` or `level_up`. Symmetric fallback (`CampaignConfig.SymmetricRelationshipFallback`, default false) applies only when the target→actor key is **missing**, not when explicitly 0. Narrative rulesets skip relationship modifiers structurally.

`ConversationInvolvedResolver` auto-infers or merges `involved` on `Conversation` events from pairwise `engagement_relation`, `spatial_position`, `activity`, and other batch changes — supporting 3+ participant scenes (e.g. PC + companion + barkeep) when the LLM commits multiple pairwise engagements or an explicit `involved` array.

`level_up` is the mechanical level-up path (no XP ledger): LLM commits when a milestone is earned. Eligible for `isPc` and `isPartyCompanion` characters; applies bootstrap HP gains and re-syncs resource pools. Optional `reason` field logs narrative context.

### Character bootstrap pipeline

`CharacterBootstrapOrchestrator` runs each ruleset's ordered `ICharacterBootstrapPipeline` when:

- `character_create` / `upsert_character` omits `maxHp` (or `maxHp <= 0`)
- `character_update`'s systemStats patch leaves `maxHp <= 0`
- `level_up` commits incremental HP gains

PCs omit `maxHp`; the pipeline derives HP from typed `systemStats` fields. Creature stat blocks use `maxHp` or `systemStats.statBlockHp` — these skip HP formula only; defense/proficiency steps still run. Steps live under `Rulesets/Bootstrap/`. Defense steps emit `[BOOTSTRAP HINT]` messages with copy-paste `item_create` armor JSON when worn armor is missing.

Combat flow: `start_combat` rolls initiative once per combatant, sorts turn order, stores `CombatEncounter` at the campaign key (`CombatantState.HasActedThisRound` + `CombatEncounter.ActiveTurnId` — a single pointer to whose turn it is, hard-enforced elsewhere via a "NotYourTurn" check). `next_turn` advances turns and expires round-based status effects. HP and status mutations during combat go through `commit`, not the turn tools. `get_scene` returns `ActiveCombat` when combat is active at that location.

## NPC Initiative (read-side)

`NpcInitiativeService` synthesizes behavioral initiative signals (relational, memory, need/activity conflict, disposition) for `get_npc_context` and scene NPC enrichment. This is narrative prompting, not combat turn order.

**Turn-intent signal:** `Enrich` also computes an advisory `TurnIntentSignal` (`Holder: "npc"`, `Reason`, `Confidence`) when `BehavioralTension >= CampaignConfig.BehavioralTensionSpeakingThreshold` (0-100 scale, default 60) and the top initiative candidate's `Urgency` is at least `High` — pure aggregation of signals already computed by `Enrich`, no new data source. Projected per-NPC into `NpcContextView.TurnIntent` and `NpcPresenceSummary.TurnIntent`; `SceneAssembler.Assemble` aggregates across all present NPCs into `SceneView.TurnIntentCharacterId` (the highest-`BehavioralTension` NPC among those with `Holder == "npc"`, or `null` for "open turn"). Unlike combat's `ActiveTurnId`, this is advisory only — it shares no round/action-budget machinery and is never enforced; the DM should still use judgment.

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

## Tool Surface Evolution

The public MCP tool surface has evolved through several phases to reduce LLM confusion and optimize for round-trip efficiency:

| Phase | Focus | Tool Count | Changes |
|-------|-------|-----------|---------|
| **Original** | All tools public | 48 | Initial tool set |
| **Phase A** | Retire legacy upserts | 37 → 40 | Retired 11 single-entity upserts (birth → `world_build`), added 3 lightweight query tools (GetSessionBriefing, GetSceneSummary, GetNpcSummary) |
| **Phase B** | Semantic wrappers | 42 | Added `travel_to` and `rest_at_location` (thin layers over `commit`'s `travel`/`rest` changes) |
| **Phase C.1** | Unified mutations | 39 | Added `take_turn` (unified mutation + auto-refresh), subsumes query→commit→query pattern |
| **Phase C.2** | Query tool demotion | 38 | Demoted `get_scene`, `get_npc_context`, `get_scene_summary`, `get_npc_summary` to internal (still needed internally; `take_turn` now auto-refreshes these) |
| **Phase C.3** | Enhanced WorldState | 38 | Added rumors/quests/factions/time bundling to auto-refresh; full-detail opt-in views |
| **Phase C.4** | Pressure + guidance | 38 | Added pressure items and suggested-commit examples in WorldState responses |
| **Phase C.5** | Commit demotion | 38 | Demoted `commit` to internal (replaced by `take_turn` for routine play) |
| **Phase C.6** | Guidance alignment | 38 | Updated system prompt and skill files to emphasize `take_turn` as primary pattern |
| **Phase C.7** | Behavioral synthesis | 38 | Enhanced NPC summaries with recent event context (prevents roundtrips for behavioral context) |
| **Phase C.8** | Wrapper demotion | 35 | Demoted `attack`, `travel_to`, `rest_at_location` to internal (thin layers now eliminated; `take_turn` is universal for mutations) |
| **Consolidation** | Full merge | 15 | Merged deep-dives into `get_entity`, kickoff tools into `start_session`, combat lifecycle into `combat(action:...)`, rules lookups into `get_rules_reference(kind:...)`, `list_tools` into `get_help topic=tools`, need descriptors into `world_build`; deleted the demoted wrapper code outright (no backward compatibility kept) |

**Current public tool count: 15** (down from original 48). The full surface:

- **Mutations & time:** `take_turn` (THE mutation tool — changes[] + narrative in, commit outcome + bundled fresh state out; also serves pure refreshes via includeParty/includeWorldState/full-detail opt-ins), `advance_world`
- **Session:** `start_session` (kickoff superset: recap + campaign context + world state + seed coverage + party), `end_session`
- **Discovery:** `search_world`, `recall_history`, `get_entity` (one entity full-detail by exact id: chars/, locations/, factions/, quests/, items/, plot-threads/)
- **Combat:** `combat` (action: start | next | end | status — lifecycle only; combat actions go through take_turn's ruleset_action, reactions via isReaction:true)
- **Build & campaign:** `world_build`, `create_campaign`, `list_campaigns`, `get_config`, `get_rules_reference` (kind: handbook | spells | creatures)
- **Meta:** `get_help` (topic=tools serves the catalog), `get_commit_schema`

**Design principle:** Public tools should reduce LLM decision ambiguity. Retired/demoted tools were either (1) redundant with newer patterns, or (2) thin semantic wrappers that added unnecessary tool-name confusion. Internal methods remain only where public dispatchers or tests reuse their logic — dead wrappers were deleted rather than kept.