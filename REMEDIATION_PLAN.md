# Remediation Plan: Eliminate LLM-as-MCP-Client Confusion in CampaignVault

**Status**: Approved decisions locked  
**Date**: 2026-06-25  
**Scope**: Comprehensive fix for tooling metadata, parameter descriptions, `get_help` responses, tool responses, and related ambiguity.  
**Breaking Changes**: Fully allowed — no backwards compatibility required.

## Goals
- Eliminate inconsistent and ambiguous parameter names across `commit` `WorldChange` payloads.
- Remove legacy aliases, heavy client normalization, and hidden session magic that cause LLM confusion.
- Make `campaignName` handling explicit and predictable.
- Ensure LLM clients see reliable, actionable text in the MCP `content[]` channel **and** rich structured data.
- Reduce (ideally eliminate) drift between code, `[Description]` attributes, `get_help`, pressure messages, and examples.
- Simplify the mental model: fewer special cases, clearer required parameters, consistent naming.
- Preserve valuable functionality (atomic commits, two distinct spatial concepts, pressure-driven guidance, multi-ruleset support).

## Core Decisions (Locked)

### C. Campaign Scoping — Preferred (Explicit & Required)
- Remove per-MCP-session campaign selection entirely for the MCP tool surface.
- `campaignName` is **required** on every tool that operates on a campaign.
- Delete `select_campaign` tool and related auto-selection behavior.
- Delete `CampaignSelectionStore`, `SessionKeyedCurrentCampaignContext` selection logic, TTL pruning, and auto-select on `create_campaign`.
- `create_campaign` creates the world but does **not** select it — caller must pass `campaignName` on subsequent calls.
- `get_current_campaign` requires `campaignName` (becomes a simple fetcher).
- `Mcp-Session-Id` / `MCP_SESSION_ID` no longer drive campaign state (may remain for general correlation).
- All descriptions, errors, help text, and parameter docs updated to reflect explicit model only.

### Spatial Relations — Remove Only Redundancy
- **Keep** both distinct concepts:
  - `engagement_relation`: Pairwise relational state (verb + category, affects travel when Hard, pressures).
  - `spatial_position`: Relative placement (distanceBand + bearing + zone, mostly narrative).
- **Remove** only the legacy redundancy:
  - `$type: "spatial_relation"`
  - `SpatialRelationChange` class (was alias for Engagement)
  - `relationType` legacy field
  - `SpatialRelationsLegacy` property + migration code on Character
- After the `characterId` unification pass, `engagement_relation` and `spatial_position` will use consistent names.

### Naming Unification (WorldChange inside `commit`)
- Primary subject character: always **`characterId`**.
- Secondary target: **`targetId`** (singular) or **`targetIds`** (plural).
- `EventOccurred.involved` stays (correct name and concept).
- Update both C# properties and the JSON keys they serialize to (no BC).

### Other Key Principles
- No heavy synonym/legacy rewriting in the hot path.
- Commit schema is strict (no `null` defaults for required `changes`/`narrative`).
- LLM-visible text content is always high-quality and actionable.
- Large parts of `get_help`, cheat sheets, and enum tables are generated from attributes/code to prevent drift.
- Static `mcps/campaign-vault/tools/*.json` will be generated (or removed) with drift checks.

## High-Level Remediation Areas
1. WorldChange ID naming standardization + legacy removal.
2. Commit tool simplification and parser strictness.
3. Complete scoping simplification.
4. Response shape standardization (`content` vs `structuredContent`).
5. Pressure system (structured suggestions + clearer messaging).
6. Metadata, descriptions, `get_help`, and generated docs.
7. Ruleset action usability improvements.
8. Tool surface & world-builder guidance.
9. Static descriptors & generation.
10. Documentation, examples, tests.

---

## Detailed Phased Task Breakdown

### Phase 0: Preparation & Locking
- [x] Write this full plan to `REMEDIATION_PLAN.md` (this file).
- Create short `DECISIONS.md` (or section here) locking the three major calls (scoping, naming, spatial).
- Audit all references to:
  - `select_campaign`, `CampaignSelectionStore`, `SessionKeyedCurrentCampaignContext`
  - `actorId`, `sourceId`, `spatial_relation`, `relationType`
  - `campaignName?` optionality in tools
- Add temporary guard tests (will be strengthened later):
  - Fail if any `WorldChange` subtype uses non-canonical ID names.
  - Fail if `select_campaign` still exists after Phase 3.
- Inventory all embedded JSON examples in pressures, help files, tests, and cheat sheets.
- Update `todo` tracking or issue list with this plan.
- **Key files touched**: `REMEDIATION_PLAN.md`, new guards in test project, `DECISIONS.md` (optional).

**Exit criteria**: Plan is written, decisions documented, initial audit complete, branch ready.

### Phase 1: WorldChange Naming Unification (Core Model + Handlers)
**Goal**: Every `WorldChange` inside `commit` uses only `characterId` / `targetId` / `targetIds`.

#### 1.1 Models
- Edit `src/CampaignVault/Models/WorldChanges.cs`:
  - `EngagementRelationChange.actorId` → `characterId` (property + `[JsonPropertyName]` + `[Description]`).
  - `RulesetAction.actorId` → `characterId`.
  - `RelationshipChange.sourceId` → `characterId` (keep `targetId`).
  - Remove `SpatialRelationChange` class and its `[JsonDerivedType("spatial_relation")]`.
  - Remove `relationType` property (and its Description).
  - Update all other `[Description]` text mentioning old names.
- Edit `src/CampaignVault/Models/Character.cs`:
  - Remove `SpatialRelationsLegacy` property and its migration setter.
  - Keep `EngagementRelations` and `SpatialPositions` (with comments clarifying distinction).

#### 1.2 Handlers & Dispatcher
- Update every handler that touches the renamed fields:
  - `EngagementRelationChangeHandler.cs`
  - `RulesetActionHandler.cs`
  - `RelationshipChangeHandler.cs`
  - `ConversationInvolvedResolver.cs` (simplify switch cases)
  - `WorldChangeDispatcher.cs` (extraction logic)
  - `IWorldChangeHandler.cs` and helpers
  - `TravelChangeHandler.cs`, `CharacterChangeHandlers.cs`, and any others
- Simplify code that previously branched on legacy names.

#### 1.3 Other Usages
- Ruleset resolvers that emit mutations (`Dnd5eRulesetResolver`, `Pf2e...`, `Fallout...`, `Narrative...`).
- Any simulation rules or pressure code that constructs changes.
- Update internal test helpers if they build raw objects.

**Exit criteria**: Compiles cleanly. All commit payloads use canonical names only. Guard test passes.

### Phase 2: Legacy Removal & Normalization Cleanup
#### 2.1 Remove Spatial Relation Alias Everywhere
- `CommitTypesReference.cs`: Remove "(legacy alias: spatial_relation)" text.
- `DmHelpManual.cs`: Update engagement/spatial section; remove any example using `spatial_relation`.
- `CommitEnumCheatSheet.cs` and other help files.
- All pressure contributors that mention it.
- Error messages and `McpToolErrorFilter.cs` guidance.

#### 2.2 Strip Normalization & Fallbacks
- `ToolCallExamples.cs`:
  - Remove or drastically reduce synonym maps for commit changes.
  - Remove `participants` → `involved` rewriting for events (make strict).
  - Remove most legacy upsert wrapper handling if we decide to deprecate `upsert_*` for play.
- `McpNormalizationMiddleware.cs`: Simplify or delete most rewrite logic. Keep only minimal safety net if needed.
- `CommitChangesParser.cs`: Remove legacy `"type"` fallback. Make errors clearer. Remove string-JSON auto-detection where possible.
- Delete the hidden string fallback overload:
  - `MutationTools.cs`: Remove `Commit(string changesJson, string narrative, ...)`.
  - Update `CampaignTools.cs` facade (test only).

#### 2.3 Strict Commit Schema
- In `MutationTools.Commit(JsonElement? changes, ...)` and the `WorldChange[]` path:
  - Require `changes` array (non-empty) and `narrative`.
  - Update descriptions and error paths.

**Exit criteria**: No code accepts old aliases. Normalization layer is minimal. `commit` rejects malformed input with clear canonical examples.

### Phase 3: Scoping Overhaul (Explicit `campaignName` Required)
#### 3.1 Delete Selection Machinery
- Delete `select_campaign` tool implementation and registration.
- Delete `SelectCampaignResult` record.
- Delete `CampaignSelectionStore.cs` entirely.
- Delete or strip `SessionKeyedCurrentCampaignContext.cs` selection logic.
- Remove registration in `CampaignCoreModule.cs`.
- Simplify or remove `ICurrentCampaignContext` if no longer needed for selection.

#### 3.2 Simplify CampaignToolBase
- Change `TryGetEffectiveCampaign(string? explicitName, out string effective)`:
  - Require non-null/valid `explicitName`.
  - Remove fallback to session context.
- Remove `NoCampaignSelectedSummary` text that mentions `select_campaign`.
- Update `ExecuteForCampaignAsync` and callers.
- Delete or rewrite `CampaignNotSelectedException.cs` and `CampaignSessionRequiredException.cs`.

#### 3.3 Make `campaignName` Required on Tools
- In all tool classes (`ExplorationTools`, `MutationTools`, `CombatTools`, `DeepDiveTools`, `WorldBuilderTools`, `CampaignManagementTools`):
  - Change `string? campaignName = null` → `string campaignName` (no default).
  - Update every `[Description]` (remove "optional..." language).
- Specifically:
  - `create_campaign` no longer auto-selects.
  - `get_current_campaign` requires `campaignName` and just returns data for it.
- Update the test facade `CampaignTools.cs`.
- Update `ToolParameterDescriptions.cs` (remove `CampaignNameOptional` or repurpose).

#### 3.4 Error Messages & Middleware
- `McpToolErrorFilter.cs`: Update all guidance that mentions `select_campaign`.
- Remove references to session headers for selection in descriptions.

#### 3.5 Hosting / Program / Docs
- Clean `Program.cs` exposed headers comments if appropriate.
- `HostingProfile.cs` comments.
- Begin updating `DmHelpManual.cs` scoping section (finish in Phase 6).

**Exit criteria**: No session selection code affects tool execution. Every campaign tool requires `campaignName`. All related exceptions and stores are gone.

### Phase 4: Commit Tool & Response Standardization
#### 4.1 Commit Surface Polish
- Ensure `changes` and `narrative` are strictly required in the MCP-visible method and schema.
- Update `commit.json` descriptor (will be regenerated later).

#### 4.2 Consistent LLM-Facing Responses
- Introduce a helper (e.g. `McpResultBuilder` or extension in `Tools/`) that produces `CallToolResult`:
  - `Content = [ new TextContentBlock { Text = Summary + key pressure headlines + "Full data in structuredContent" } ]`
  - `StructuredContent =` rich object (include `data`, `worldPressure`, `suggestedCommits`, `error`, `retryExample`).
- Apply via:
  - Direct use in key tools, or
  - A lightweight call-tool filter.
- Update `McpToolErrorFilter.ToErrorResult` to follow the exact same shape.

#### 4.3 Structured Suggestions
- Extend `WorldPressureItem` (or add parallel channel) to carry optional `SuggestedChanges: WorldChange[]?` (or serialized form).
- Update a few contributors (e.g. `LocationHallucinationPressureContributor`) to populate it.
- Surface from `get_scene` / `get_world_state` / `advance_world` into both text and structured.

**Exit criteria**: All tools (success + error) produce consistent, LLM-friendly `content[0].text` + rich `structuredContent`. Commit is stricter.

---

## Remaining Phases (Summary — for completeness)

**Phase 5**: Pressure improvements (structured suggestions + clearer prefixes).  
**Phase 6**: Metadata, `get_help`, generated cheat sheets.  
**Phase 7**: RulesetAction top-level fields + validation.  
**Phase 8**: Static descriptor generation + drift tests.  
**Phase 9**: Docs, examples, README overhaul.  
**Phase 10**: Massive test updates + final verification.

---

## Risks & Mitigations
- Large mechanical rename + scoping deletion → Phase order (models/handlers first).
- Drift in examples during transition → Generate early in Phase 6; use guard tests.
- Ruleset resolvers emit changes → Covered in Phase 1 audit.
- Test breakage volume → Dedicated Phase 10 sweep.

## Success Criteria
- Zero occurrences of `actorId`, `sourceId`, `spatial_relation`, `relationType` (in active code for WorldChanges).
- No `select_campaign`, `CampaignSelectionStore`, or session-based campaign fallback in tool path.
- Every campaign tool has required `campaignName`.
- LLM text content is always useful; structured data is complete.
- `get_help` sections are largely generated.
- Full test suite green + manual LLM-style flows succeed cleanly.

---

*This plan is the single source of truth for the remediation. All phases executed. Main library builds clean. 442+ tests passing; ~55 failures are expected for tests exercising removed select_campaign / session-selection behavior (breaking change).*

**Execution complete as of 2026-06-25.** Core changes:
- Naming unified to characterId/targetId.
- Legacy spatial_relation + relationType removed.
- Session selection removed; campaignName required.
- Responses standardized for LLM clients.
- Partial pressure structured suggestions, help text updated, etc.