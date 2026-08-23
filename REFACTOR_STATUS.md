# CampaignVault Refactor Status

**Last Updated:** 2026-08-17  
**Overall Progress:** Phases 0-4 complete (this table was stale — see audit note below); Phase 5.3 in progress

---

## Phase Summary

| Phase | Task | Status | Notes |
|---|---|---|---|
| **0** | Bug fixes (stdout, dead code) | ✅ **COMPLETE** | Verified 2026-08-17: no stray Console.WriteLine outside Program.cs; dead code removed. Table below was stale — not actually pending. |
| **1** | Commit metadata SSoT | ✅ **COMPLETE** | `CommitMetadataAttributes.cs` + `Schema/CommitSchemaModel.cs` exist; `CommitSchemaRegistry.cs` is a 49-line projection (was 386). |
| **2** | Tool schema (35-45k → 12-16k tokens) | ✅ **COMPLETE** | `TakeTurnSchemaBuilder`/`WorldBuildSchemaBuilder` + `McpSchemaInstaller` (PostConfigure) installed and live; `MinutesElapsed` confirmed emitted once via `$ref`, not 40×. Live-measured 2026-08-17: `tools/list` inputSchema total is 46,147 chars (~11.5k tok) — Phase 2's InputSchema half of the goal held. See new finding below: OutputSchema was never brought into scope and now dominates instead. |
| **3** | Guidance push (replaces get_help) | ✅ **COMPLETE** | `DmHelpManual.cs` shrunk to 143 lines (commit 768da45). |
| **4** | Plugin system (RulesetSystem enum → string) | ✅ **COMPLETE** | `RulesetSystem` is now `Models/RulesetEnums.cs`'s static string-constants class, not an enum (commit dc5e452). One migration straggler found and fixed 2026-08-17 — see below. |
| **5.1** | Split CampaignRepository (3,155→1,975 lines) | ✅ **COMPLETE** | CampaignSession wrapping ✅; Sanitize* deleted ✅; Suggest* extracted ✅; Upsert* methods extracted ✅ (4 services created: ItemManager, LocationManager, CharacterManager, EntityManager) |
| **5.2** | Dispatcher dict lookup (48→1 handler resolution) | ✅ **COMPLETE** | BuildHandlerDictionary caches Type→Handler mapping at startup; FindHandler does O(1) dict lookup |
| **5.3** | Delete null-session test path | 🔄 In Progress | **Production:** 70+ null-checks removed ✅ (24 handlers); **Tests:** 1/55 sites migrated to RavenDBFixture; 54 remaining |
| **5.4** | ID-prefix classifier fix | ⏳ Pending | Full prefix match, consolidate duplicates |
| **5.5** | Dedupe Suggest* (560 lines) | ⏳ Pending | Extract IEntityResolver |
| **5.6** | Rule ordering validation | ⏳ Pending | Fail on Order collisions, phase enum |
| **5.7** | Unsafe retry loop fix | ⏳ Pending | Make actions idempotent or scope retry |
| **5.8** | Query performance optimization | 🔄 Documented | 84× WaitForNonStaleResults calls; RavenDB constraint prevents parallel session queries; per-method optimization needed |
| **5.9** | Response-shape redundancy | ✅ **COMPLETE** | `MutationTools.DedupeNpcsCoveredByScenes` (called from `Finalize`) drops a `TurnResult.Npcs` entry when the same NPC is already covered by a refreshed `Scenes[].PresentNPCs`, merging any `Initiative` data onto the surviving `NpcPresenceSummary` first; `EnsureInitiativeSurfacedAsync` also now treats scene-covered NPCs as already surfaced. Also stripped 3 raw-entity bookkeeping leaks found along the way: `WorldStateView`/`WorldStateDeltaView.Time` (new `CampaignTimeView`), `SceneView.ActiveCombat` (new `CombatEncounterView`), `NpcPresenceSummary.TagProvenance` (`[JsonIgnore]`, matching its `Memories` sibling). |
| **5.10** | Test isolation fix | ⏳ Pending | One DB mode, move RavenDBFixture, lower MaxRequests |
| **5.11** | Delete CampaignTools facade | ⏳ Pending | Move test helpers, delete legacy |
| **5.12** | DI cleanups | ⏳ Pending | Static mutable state, two containers, hosted service |

---

## Phase 5.3 — Delete Null-Session Test Path

**Goal:** Remove dependency on null-session `ChangeContext` constructor (test-only path) to allow production code to assume non-null sessions.  
**Status:** Production cleanup complete; test infrastructure refactoring in progress

### Completed Deliverables

- [x] **Production code cleanup:** 70+ defensive `context.Session != null` checks removed across 24 handler files
  - All production handlers assume non-null sessions
  - Tests pass because most don't require database queries
  - Real queries (PressureQueryHelper, ItemHolderQueryHelper, etc.) have appropriate guards

### In-Progress Deliverables

- [🟡] **Test infrastructure migration:** 1 of 55 test sites migrated to use `RavenDBFixture`
  - **Completed:** SceneInterruptChangeHandlerTests (uses real RavenDB sessions)
  - **Remaining:** ~54 other test sites still using test-only constructor
  - **Approach:** Implement `IClassFixture<RavenDBFixture>` in test classes, replace null-session `ChangeContext` calls with real sessions

### Pending Deletions (Blocked Until Tests Updated)

- [ ] **Delete test-only constructor** (ChangeContext.cs lines 112-142)
  - 10-parameter variant accepting `IAsyncDocumentSession? sessionForTests`
  - Provides default no-op callbacks for mocked testing
  - Cannot delete until all 54 test sites refactored

- [ ] **Delete WorldChangeDispatcher null-session branch** (WorldChangeDispatcher.cs line 381)
  - Removed session check from `ApplyAmbientInterruptCheckAsync` condition
  - Was: `if (_encounterResolver is null || context.Session is null || context.ActiveCombat != null)`
  - Now: `if (_encounterResolver is null || context.ActiveCombat != null)`
  - Can be deleted once test-only constructor is gone

### Test Site Migration Pattern

Each test file that creates ChangeContext needs to:
1. Add `IClassFixture<RavenDBFixture>` to class declaration
2. Add `[Collection("RavenDB")]` attribute
3. Inject fixture into constructor
4. Use `_fixture.Store.OpenAsyncSession()` to get real session
5. Pass session to ChangeContext constructor

**Example:** SceneInterruptChangeHandlerTests (commit cbacb0a)

---

## 2026-08-17 — Main-MCP Token Audit

Requested: audit `src/CampaignVault` tool-facing token cost and fix any test issues found. Scope excluded `CampaignVault.Authoring`.

**Critical bug found and fixed:** `Campaign.System` (`Models/Campaign.cs`) carried a stale `[JsonConverter(typeof(JsonStringEnumConverter))]` left over from the Phase 4 enum→string migration — the converter is invalid on a `string` property. This threw `InvalidOperationException` from `JsonSchemaExporter` the moment the MCP endpoint tried to build schemas for **any** tool, crashing `MapMcp()` at startup in both stdio and HTTP transport. The server could not start. No existing test caught it — schema generation for the full tool set was never exercised. Fixed by removing the attribute; added `tests/CampaignVault.UnitTests/McpToolReflectionSchemaTests.cs`, which walks every `[McpServerTool]` method's parameter/return types through `AIJsonUtilities.CreateJsonSchema` (the exact call that crashed) and would have caught this on day one. Verified the new test both passes with the fix and fails (3/18 cases) with the bug reintroduced.

**Token measurement (live `MCP_STDIO=1` `tools/list` capture, 18 tools):** 146,424 chars (~36.6k tokens), matching REFACTOR_PLAN's cited 35-45k baseline — Phase 2 was never actually taking total `tools/list` size below that range. Breakdown: `outputSchema` 84,411 chars (58%), `inputSchema` 46,147 chars (32%, this is where Phase 2's `TakeTurnSchemaBuilder`/`WorldBuildSchemaBuilder` work lives and is verified working — `MinutesElapsed` appears once via `$ref`, not 40×), `description` 13,573 chars (9%).

**OutputSchema is the unclaimed cost, but it cannot be cut the easy way.** Phase 2 built compact hand-written schemas for `InputSchema` only; `OutputSchema` is still 100% reflection-generated with no `$ref`/`$defs` reuse, and it's larger than the already-optimized input side (`take_turn`: 45,011 output vs 34,920 input chars; `start_session`: 21,104 output vs 517 input chars). Tried nulling `Tool.OutputSchema` in `McpSchemaInstaller` for all 18 tools (drops `tools/list` to ~15.4k tokens, hitting Phase 2's original 12-16k target) — **reverted** after live-testing: the SDK only populates `CallToolResult.StructuredContent` when `OutputSchema` is non-null, and `[McpServerTool(UseStructuredContent = true)]` is set on nearly every tool (`McpResponseCleaner` depends on `StructuredContent` being present to collapse the `Content` block down to just the narrative summary). Nulling the schema silently doubles the *per-call* response payload for the hot path instead — worse than the `tools/list` saving, since `take_turn` fires many times per session versus `tools/list` once. Confirmed by capturing a live `get_config` call with and without the change (`structuredContent` key present/absent). A real fix needs a hand-built `$defs`-based output schema (mirroring `TakeTurnSchemaBuilder`) at minimum for `take_turn` and `start_session` (78% of the 84KB), which is deferred — same order of effort as Phase 2, plus correctness risk against `SystemExtension`'s `[JsonDerivedType]` hierarchy. Tracked as **Phase 2.1** below.

**Ruled out (checked, not bugs):**
- Per-call response DTOs (`TurnModels.cs`, `NpcViews.cs`) are already deduplicated — `CharacterDetailView` doc comments explicitly flag prior work ("read Psychology/Social off this object... to avoid shipping them twice"); NPC/scene refresh lists are capped (6/3) and expensive sections (`Party`, `WorldState`) are opt-in and default off.
- Advisory/hint helpers (`EntitySeedingAdvisor`, `LocationPlausibilityAdvisor`, `ModelEnumErrorHints`, `SideEffectDuplicationGuard`, `ToolCallExamples`) are all gated behind error paths or narrow conditions (e.g. `partyPresent`, entity-not-found) — none ride along unconditionally on successful calls.

**Test suite:** 1188 passed / 1 skipped / 0 failed on clean master, confirmed on two separate runs. One flaky failure (`Phase7HandlersTests`, alternating between `TravelChange_WithoutHoursOverride_UsesExitMetadataForHoursAndTiredness` and its `_FallsBackToSessionLoadForOriginExitMetadata` sibling) appeared only when timing/ordering shifted after adding the new test file — passes in isolation, reproduces intermittently, and is pre-existing (unrelated to the Campaign.cs fix). Symptom (wrong `hoursTraveled` value bleeding from a different test) is consistent with the cross-test-class RavenDB state sharing already tracked as **Phase 5.10** below — not something to patch ad hoc alongside this audit.

### Phase 2.1 — Own the output schema (attempted 2026-08-17, hypothesis disproven)

Original plan: hand-build `$defs`-deduped `OutputSchema` for `take_turn`/`start_session` (78% of `outputSchema`'s 84KB), assuming — by analogy with `TakeTurnSchemaBuilder`'s win on the input side — that repeated nested types (`ItemSummaryView` under both `Equipped`/`Carried`, etc.) were being inlined redundantly.

**Built instead a safer, generic version of the same idea**, per review advice: rather than hand-typing schemas (the existing `TakeTurnSchemaBuilder`/`WorldBuildSchemaBuilder` were found to have real bugs from doing exactly that — see note below), a structural post-processor (`OutputSchemaDeduplicator`) walked the *already-correct* reflection-generated `OutputSchema`, hashed every object subtree, and hoisted byte-identical repeats into `$defs`/`$ref`. Lossless by construction (only merges subtrees that are already 100% identical) and installed via the same `PostConfigure<McpServerOptions>` hook as the input schemas.

**Live-measured result: negligible.** `take_turn` outputSchema: 45,011 → 44,329 chars (−1.5%). `start_session`: 21,104 → 21,104 chars (0 — nothing to hoist). Exhaustive duplicate scan (no size floor) capped the theoretical ceiling at ~21KB of *overlapping* (nested-within-nested, double-counted) matches — the real achievable non-overlapping savings is the ~700 bytes actually hoisted.

**Root cause: the hypothesis was wrong.** Inspecting the raw schema showed .NET's `JsonSchemaExporter` already deduplicates repeat occurrences of the same CLR type via same-document JSON Pointer `$ref` (e.g. `"$ref": "#/properties/data/properties/npcs/items/properties/equipped/items"`) — it just doesn't use `$defs` to do it. `ItemSummaryView`, despite backing both `NpcSummaryView.Equipped` and `.Carried`, is inlined exactly once. The 84KB `outputSchema` total is therefore **not unclaimed duplication** — it's genuine, non-redundant content: `SystemExtension`'s full `dnd5e`/`pf2e` stat blocks, and the real field count across `NpcContextView`, `SceneView`, `WorldStateView`, `PartyMemberView`, `CharacterDetailView`, etc. Shrinking it further would mean cutting response fields or descriptions — both already ruled out as accuracy risks elsewhere in this audit.

**Verified safe before reverting:** live `MCP_STDIO=1` probe against `get_config` confirmed `structuredContent` was still populated identically with the deduped schema installed (same code path/risk as the earlier nulling experiment, this time not a regression). Reverted anyway — `OutputSchemaDeduplicator.cs` deleted, `McpSchemaInstaller.cs` restored — because <1% savings doesn't justify the added surface area.

**Separate finding, fixed 2026-08-17:** `TakeTurnSchemaBuilder.cs` (the Phase 2 *input*-schema hand-builder) had three real bugs, unrelated to token size, all now fixed:
1. `properties` omitted `fullDetailCharacterId`/`fullDetailLocationId`, both of which `TakeTurnRequest`'s own description and a `MutationTools.cs` validation message tell the model to pass. **Fixed:** added both properties.
2. `required: ["changes","narrative"]` contradicted the DTO — `Changes` is optional for pure queries, and `Narrative` is only required *if* `Changes` is provided. **Fixed:** replaced with `dependentRequired: {"changes": ["narrative"]}`, which expresses the actual conditional constraint (JSON Schema draft 2019-09+; degrades to no constraint on older validators, which is strictly no worse than the previous false claim).
3. `oneOf` → `#/$defs/dnd5eExtension` / `#/$defs/pf2eExtension` were dangling refs (neither def was ever emitted), AND the `systemExtension` def itself was unreachable — no field ever pointed `$ref` at it, because `GetJsonType` sends any non-primitive CLR type (including `SystemExtension`) to a bare `"object"`. The real target, `CharacterUpdate.SystemStats` (`character_update`'s `systemStats` field), was silently getting the generic fallback instead. **Fixed:** deleted the broken `oneOf`/dangling defs (hand-modeling `SystemExtension`'s polymorphism — `$system`-keyed `[JsonPolymorphic]` with `FallBackToBaseType`, a custom-converter `EngagementRelation`, a write-only legacy alias property, several enum converters — was assessed as high risk of introducing new schema bugs for a rarely-used, deliberately "use sparingly" field). Replaced with a plain `{"type":"object"}` plus a hand-written description naming the `$system` discriminator and the handful of bootstrap keys that matter (`armorClass`, the six abilities, `hitDie`, `level`, `classLevels`) — correct and honest about shape without re-deriving the full polymorphic model.

`WorldBuildSchemaBuilder.cs` had the same dead `systemExtension` def (unreferenced, no dangling inner refs) — deleted for the same reason. It still types every `world_build` array field as bare `{"type":"object"}` with no property detail; **not fixed** — per review, that's a large job that *adds* tokens rather than fixing a correctness bug, so it's left as a known limitation, not a bug.

`ToolSchemaBudgetTests.TakeTurnSchema_HasValidReferences` passed throughout despite bug #3 because it only asserted `$ref`/`$defs` *presence*, not that every `$ref` resolves — **fixed**: it now walks every `#/$defs/<name>` ref in the built schema and asserts each name exists as a `$defs` key, failing loudly on any future dangling reference. Full suite verified green after all fixes: 1206 passed, 1 skipped, 0 failed.

### Phase 2.2 — Strip write-guidance descriptions from OutputSchema (done 2026-08-17)

Re-measured `outputSchema` description text on the un-deduped, bug-fixed schema and split it by origin: descriptions reached via a `systemStats` property (`SystemExtension`/`Dnd5eExtension`/`Pf2eExtension` — race/background/feat template names, spell-DC derivation formulas, multiclass examples) vs. everything else. Result: `take_turn` — 4,390 of 45,011 chars (9.8%) is `systemStats`-nested, only 1,721 chars (3.8%) is genuinely output-relevant (e.g. "capped at 6 NPCs", "otherwise null"); `start_session` — 2,374 of 21,104 (11.2%) vs. 611 (2.9%).

The `systemStats`-nested text exists to guide the model *writing* a `character_update`/`character_create` commit (which race template to reference, how `spellSaveDc` auto-derives, etc.) — it's along for the ride only because `SystemExtension` is the same CLR type used read-only in every NPC/party summary. Zero output-side value, real input-side value (left untouched in `TakeTurnSchemaBuilder`'s hand-built `systemStats` field).

**Fix:** new `OutputSchemaTrimmer.cs` — walks each tool's reflection-generated `OutputSchema` and strips `description` from any subtree reached via a `systemStats` property key (generic, not path-hardcoded, so it doesn't care that the schema exporter happened to place two full copies at `party/items/character/systemStats` and `scenes/items/presentNPCs/items/systemStats` with a third `$ref`-ing back to one of them). Wired into the existing `McpSchemaInstaller` `PostConfigure` hook, same low-risk pattern as the input-schema installs.

**Live-measured result:** `take_turn` outputSchema 45,011 → 39,886 chars (−11.4%). `start_session` 21,104 → 18,350 chars (−13.0%). Verified zero leftover `systemStats` descriptions and `structuredContent` still populates correctly on a live `get_config` probe (no regression, unlike the earlier OutputSchema-nulling attempt). Full suite green after the change: 1206 passed, 1 skipped, 0 failed.

**Considered and rejected: auditing `UseStructuredContent` per tool.** 17 of 18 tools have it on. `McpResponseCleaner.cs`'s own doc comment confirms the SDK always serializes the full return value into `Content` as a JSON-dump fallback, and only collapses `Content` down to the narrative `Summary` when `StructuredContent` is populated. So disabling `UseStructuredContent` doesn't remove cost — it moves the tool's schema bytes (paid once, in `tools/list`) into a full JSON dump paid on *every call* to that tool instead of a short summary. Every non-`take_turn`/`start_session` tool's `outputSchema` is already tiny (327–3,659 chars per earlier live measurement) — cheaper than a single extra full-JSON-dump call would cost. Net: this lever is actively counterproductive for tools called more than once or twice per session, which is effectively all of them. Not implemented.

### Phase 2.3 — Bottleneck audit of the full tools/list surface (done 2026-08-18)

Full audit of `src/CampaignVault`'s token footprint, live-measured via stdio probes (`tools/list` size, and real `structuredContent` on seeded `take_turn` calls). Ranked findings:

**#1 — Per-call response payload (not fixed here; largest lever by total session tokens, needs a separate pass).** `tools/list` is a one-time, cacheable cost. `take_turn`'s response is new content on *every* call — dozens of times per session. Live-measured: a single minimal party member (no equipment, default psychology/needs) already costs 1,350 bytes; a near-empty `worldState` costs 1,314 bytes — before any NPCs, items, or narrative content accumulate. The response DTOs (`Character`, `NpcPresenceSummary`, `PartyMemberView`) serialize every optional dict/list field even when empty (`"damageModifiers": {}`, `"statusEffects": []`, `"resourcePools": {}`, etc.) because no `JsonSerializerOptions` passed to `AddMcpServer()` sets `DefaultIgnoreCondition` — confirmed via `grep -rn "DefaultIgnoreCondition" src/` (only one unrelated hit in `SystemStatsMerger.cs`). Omitting empty collections costs zero narrative accuracy (empty is empty either way) and would shrink every response, compounding over a session far more than any one-time schema trim. Recommended next step: set `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull` on the MCP server's JSON options and change the relevant DTOs' empty-collection default initializers to `null` (needed since `WhenWritingDefault` only skips `null` for reference types, not `[]`/`{}`). Not implemented this pass — touches serialization broadly enough (all 18 tools' responses) to warrant its own build+test+probe cycle rather than folding into this one.

**#2 — `take_turn`/`start_session` OutputSchema was pure one-time scaffolding with no functional payoff (fixed).** Live-verified that `[McpServerTool(UseStructuredContent = true)]` only needs `Tool.OutputSchema` to be non-null to populate `StructuredContent` — the SDK never validates the return value against the schema's actual shape. Confirmed by temporarily stubbing `get_config`'s `OutputSchema` to `{"type":"object"}` and observing `structuredContent` still populated in full and `content` still collapsed to the short summary. **Fix:** `McpSchemaInstaller` now installs a shared `{"type":"object"}` stub as `OutputSchema` for `take_turn` and `start_session` only (every other tool's `outputSchema` is small enough — 327–3,659 chars — that the anticipatory value is worth keeping). Verified no consumer reads `ProtocolTool.OutputSchema` anywhere in `src/` or `tests/` before shipping. **Result:** `take_turn` outputSchema 39,886 → 18 bytes; `start_session` 18,350 → 18 bytes.

**#3 — `take_turn`'s cold-tier commit variants inlined full per-field descriptions despite an existing on-demand lookup tool (fixed).** `CommitVariantModel.IsHotTier` (10 of 40 commit types — the ones used almost every turn: `hp`, `item`, `status`, `event`, `relationship`, `mood`, `activity`, `ruleset_action`, `travel`, `engagement_relation`) was computed by reflection and its own doc comment says "Marks a variant for full-detail treatment in the emitted tool schema" — but `TakeTurnSchemaBuilder.BuildVariantDef` never consumed it; every one of the 40 variants got identical full treatment. The 30 cold-tier variants' `$defs` entries were 23,724 of the 33,244 `$defs` bytes (71%), of which 9,319 bytes (39%) were field-level descriptions. Confirmed `get_commit_schema` (a dedicated MCP tool, ~2KB total) already returns required/optional field *names* + side effects + co-commit hints + a full example payload per type on demand via `CommitSchemaRegistry.cs` — enough to safely defer field-level description text there. **Fix:** cold-tier variants keep field names/types (needed to construct a valid payload) but drop descriptions; their variant-level summary gets a `(field details: get_commit_schema type='X')` pointer. Hot-tier variants are untouched (still fully described inline, since those are used constantly). **Result:** `take_turn` inputSchema 35,299 → 26,518 chars (−24.9%).

**Bonus correctness bug found and fixed while investigating #3:** `CommitSchemaModel.BuildVariants()` reads each derived type's `[Description]` via `GetCustomAttribute<DescriptionAttribute>()` (default `inherit: true`). None of the 40 `WorldChange`-derived commit classes had their own class-level `[Description]` — they only had `///` XML doc summaries, which reflection can't see — so *every single one* silently inherited the base `WorldChange` class's generic `[Description("REQUIRED: Every WorldChange object MUST include the exact '$type' discriminator...")]`. This meant every variant's schema-level description (and `get_commit_schema`'s `Description` field, which reads the same `Summary`) was identical, non-differentiating boilerplate — not just token waste, but an active accuracy gap: nothing distinguished `location_update` from `scene_setup` from `spatial_position` by description alone. Along the way also found `ItemUpdate`'s XML doc comment literally read "Create a new faction." (an unrelated copy-paste artifact, never previously reflectable so it never leaked into the model-facing schema — still fixed). **Fix:** added accurate, class-specific `[Description(...)]` attributes to all 42 `WorldChange`-derived classes (40 reachable via `take_turn`'s `[JsonDerivedType]` union + `CharacterCreate`/`RumorCreate`, which are `world_build`-only and unreachable from `CommitSchemaModel` but fixed for consistency).

**Combined result, live-measured via a seeded `create_campaign` → `world_build` → `start_session` → `take_turn` probe:** total `tools/list` payload 136,110 → 68,980 chars (−49.3%). `structuredContent` still populates correctly on a real `take_turn` call (2,804 bytes) with `content` still collapsed to the short summary — no regression. Full suite green throughout: 1206 passed, 1 skipped (pre-existing, unrelated `Commit_RejectsWhenRateLimitExceeded`), 0 failed.

### Phase 2.4 — Delta-mode `take_turn` (done 2026-08-18)

Implements Phase 2.3's #1 recommendation (per-call response payload, the largest lever by total session tokens). `take_turn` now alternates between **Full** snapshots and **Delta** responses (only what changed this turn) instead of always returning full `Party`/`WorldState` sections when requested.

- **New per-campaign `TurnCursor` document** (`campaigns/{name}/state/turn-cursor`) tracks `TurnsSinceReseed`, incremented every `take_turn` call, reset to Full when it hits `CampaignConfig.DeltaModeReseedIntervalTurns` (default 30, configurable per campaign). Absence of the cursor doc means "first-ever call" → naturally Full. `CampaignConfig.DeltaModeEnabled` (default true) is a full kill switch.
- **`TakeTurnRequest.ForceFullReseed`** (exposed in the hand-authored `TakeTurnSchemaBuilder` schema) lets the client force a Full response and reset the counter on demand — e.g. after its own context was compacted/summarized.
- **`advance_world` gap closed:** it can run simulation ticks outside the `take_turn` pipeline; it now sets `TurnCursor.ForcedFullReseedPending`, forcing the next `take_turn` call to Full so drift from the skip isn't missed.
- **Prerequisite bug fixed:** `CampaignRepository.StageChangesAsync` discarded `RunSimulationTickAsync`'s return value — ambient drift (needs/memory decay triggered by a day-boundary crossing) was persisted to RavenDB correctly but was invisible to the caller. Now captured into new `CommitResult.AmbientDeltas`/`AmbientNarrativeSummaries` fields.
- **Delta content is an echo, not a diff:** `TurnResult.PartyDelta`/`WorldStateDelta` (new sibling fields — `Party`/`WorldState` stay null in Delta mode, and vice versa, so the MCP-advertised schema stays honest about which shape a response is) are built by filtering `ctx.AppliedChanges` (the caller's own `Changes[]` plus ambient deltas) by target entity — the `WorldChange` DTOs are already delta objects (`NeedChange{Need,Delta}`, `RumorEvolves`, etc.), so no new diffing infrastructure was needed.
- **Capped NPC initiative/memory surfacing (mode-independent, new):** up to 2 NPCs per call now carry RP-advisory initiative/memory (`NpcInitiativeEnrichment`, reusing the existing `CampaignRepository.EnrichNpcInitiativeAsync`/`DefaultRelevantMemorySelector` machinery already used by `get_scene`) — one guaranteed slot for a randomly-picked party companion when present, the other slot preferring a non-companion NPC at the party's current location. This runs regardless of `includeParty`/`Mode` (gated only by `autoRefreshInvolved`, honoring its existing bulk/seeding opt-out contract), so `take_turn` alone carries a "who might act/speak next" signal even without a `get_scene` call.
- **Correctness fix caught in review before shipping:** `NpcInitiativeService.Enrich` has a persisted side effect — it marks surfaced candidates as consumed on the campaign doc via `IInitiativeSuppressionStore`, so a candidate that's enriched but never shown to the model is silently burned (worse than not enriching at all: a later `get_scene` call would find it already suppressed). The initial wiring only attached the cached enrichment when the selected NPC happened to already be in `Npcs` (via `InvolvedEntities`/`extraCharacterIds`) or `Party` (via `includeParty`) — meaning the common bare `take_turn(changes, narrative)` call computed and persisted the suppression state but never surfaced it anywhere. Fixed with a new `EnsureInitiativeSurfacedAsync` step that appends a lightweight `NpcSummaryView` to `Npcs` for any selected NPC not already covered by another section — guaranteeing every enrichment that's computed (and every suppression-store write that goes with it) is actually visible in the response.
- **Tests:** new `tests/CampaignVault.UnitTests/TakeTurnDeltaModeTests.cs` (cursor mechanics incl. forced reseed and the `advance_world` gap, a deterministic ambient-drift regression guard — rigged `EncounterResolver`/minimal `DefaultSimulationEngine` so it doesn't depend on encounter-interruption RNG, delta-content correctness incl. a payload-size ratio check, the initiative-cap selection, and the bare-call initiative-surfacing regression guard above) plus a schema test in `ToolSchemaBudgetTests.cs`. Two pre-existing tests (`TakeTurn_WithIncludeWorldState_ReturnsWorldContext`, `TakeTurn_WithIncludeParty_ReturnsPartyMembers`) asserted on `Party`/`WorldState` against the shared test campaign and were now flaky depending on that campaign's accumulated reseed-cursor state — fixed by pinning them to `ForceFullReseed: true`, matching their actual intent (verify the full-detail shape). Full suite green: 1213 passed, 1 skipped (pre-existing), 0 failed.
- **Not done at the time:** the `Npcs`/`Scenes` auto-refresh section (already capped at 6/3, already summary-shaped) was deliberately left untouched — flagged as a possible future follow-up, not required to capture the bulk of the win. **Done as of Phase 5.9** — see that row: an NPC covered by both a refreshed scene and the top-level `Npcs` list is now deduped, keeping the richer `Scenes[].PresentNPCs` entry.

---

## Next Steps (Prioritized)

1. **Phase 5.3 continuation:** Migrate remaining 54 test sites
   - High-value targets: Phase6HandlersTests, WorldChangeDispatcherTests, handler-specific tests
   - Batch by file to minimize context switching
   - Verify all tests pass after each batch

2. **Phase 5.3 completion:** Delete test-only constructor + WorldChangeDispatcher branch

3. **Phase 5.8:** Query performance optimization (84× WaitForNonStaleResults calls)
   - **High-impact targets:** AdvanceWorld simulation (18s latency); EntitySuggester (10 calls)

4. **Follow-up found during Phase 5.9:** `AdvanceResult.NewTime` (`Models/V4Views.cs`, the `advance_world`
   tool's response) still returns a raw `CampaignTime` — same `Id`/`LastUpdated` bookkeeping leak as
   `WorldStateView.Time` had before Phase 5.9 fixed it there. Same fix (`CampaignTimeView.From`) applies;
   out of scope for 5.9 since it's a different tool's response shape.

---

## Context & Background

**Why delete the null-session test path?**
- Production code cannot cleanly handle genuinely null sessions without 100+ defensive checks
- Tests using mock callbacks + null sessions are not representative of real usage
- Real RavenDB sessions provide better test fidelity and catch more bugs early

**RavenDBFixture availability:**
- Already used in 15+ test classes (LevelUpResourcePoolTests, Phase4_ThirdSystemRoundTripTests, etc.)
- Manages embedded RavenDB instance for unit tests
- Provides `Store` property for session creation

**Stale binary gotcha fixed:**
- Previous attempts debug by stale test assemblies; fresh builds now mandatory
- Always rebuild test project separately: `dotnet build tests/CampaignVault.UnitTests/CampaignVault.UnitTests.csproj`
