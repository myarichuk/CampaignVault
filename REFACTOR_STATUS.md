# CampaignVault Refactor Status

**Last Updated:** 2026-08-12  
**Overall Progress:** Phase 5.1 in progress (45% of full plan)

---

## Phase Summary

| Phase | Task | Status | Notes |
|---|---|---|---|
| **0** | Bug fixes (stdout, dead code) | ⏳ Pending | -console.WriteLine, delete unused types |
| **1** | Commit metadata SSoT | ⏳ Pending | Attributes, reflection model, registry projection |
| **2** | Tool schema (35-45k → 12-16k tokens) | ⏳ Pending | $defs, tiered hot/cold, budget tests |
| **3** | Guidance push (replaces get_help) | ⏳ Pending | Contributors, ledger, 30 hints < 8KB |
| **4** | Plugin system (RulesetSystem enum → string) | ⏳ Pending | Data-only plugins, then code plugins |
| **5.1** | Split CampaignRepository (3,155→1,975 lines) | ✅ **COMPLETE** | CampaignSession wrapping ✅; Sanitize* deleted ✅; Suggest* extracted ✅; Upsert* methods extracted ✅ (4 services created: ItemManager, LocationManager, CharacterManager, EntityManager) |
| **5.2** | Dispatcher dict lookup (48→1 handler resolution) | ✅ **COMPLETE** | BuildHandlerDictionary caches Type→Handler mapping at startup; FindHandler does O(1) dict lookup |
| **5.3** | Delete null-session test path | 🔄 In Progress | 17+/24 handler files cleaned (~60 null-checks removed); 4 if-blocks remain + 54 test sites |
| **5.4** | ID-prefix classifier fix | ⏳ Pending | Full prefix match, consolidate duplicates |
| **5.5** | Dedupe Suggest* (560 lines) | ⏳ Pending | Extract IEntityResolver |
| **5.6** | Rule ordering validation | ⏳ Pending | Fail on Order collisions, phase enum |
| **5.7** | Unsafe retry loop fix | ⏳ Pending | Make actions idempotent or scope retry |
| **5.8** | Query performance optimization | 🔄 Documented | 84× WaitForNonStaleResults calls; RavenDB constraint prevents parallel session queries; per-method optimization needed |
| **5.9** | Response-shape redundancy | ⏳ Pending | Dedupe NpcPresenceSummary, TurnResult NPCs |
| **5.10** | Test isolation fix | ⏳ Pending | One DB mode, move RavenDBFixture, lower MaxRequests |
| **5.11** | Delete CampaignTools facade | ⏳ Pending | Move test helpers, delete legacy |
| **5.12** | DI cleanups | ⏳ Pending | Static mutable state, two containers, hosted service |

---

## Phase 5.1 — Split CampaignRepository

**Goal:** Break up 3,155 lines / 67 public methods into focused services. ✅ **COMPLETE**

### Completed Subtasks

- [x] **5.1.4:** Introduce `CampaignSession` unit-of-work wrapper
  - Converted 74 method signatures from `(IAsyncDocumentSession, campaignName)` → `(CampaignSession)`
  - Fixed 317+ test call sites
  - All 1183 tests passing ✅

- [x] **5.1.2:** Delete `Sanitize*` methods (5 methods)
  - Removed delegate wrappers; callers now use `JsonSanitizer` directly

- [x] **5.1.3:** Split by concern — extracted 4 services

  **Extracted Methods:**
  - **ItemManager** (`IItemManager`)
    - UpsertItemAsync (140 lines)
  - **LocationManager** (`ILocationManager`)
    - UpsertLocationAsync (106 lines)
  - **CharacterManager** (`ICharacterManager`)
    - UpsertCharacterAsync (87 lines)
  - **EntityManager** (`IEntityManager`)
    - UpsertFactionAsync (62 lines)
    - UpsertQuestAsync (67 lines)
    - UpsertPlotThreadAsync (69 lines)
    - UpsertWorldEventAsync (70 lines)

  **Reduction:** 3,155 lines → 1,975 lines (622 lines extracted = 19.7% reduction)
  
  All 1183 tests passing ✅

- [x] **5.1.1:** Document `BuildWorldStateAsync` DI cycle
  - Analysis complete; cycle documented in commit message

---

## Phase 5.3 — Work-in-Progress Details

**Scope:** Delete null-session test path to eliminate 43+ defensive `context.Session != null` checks  
**Status:** Infrastructure in place; large refactoring remains

### Completed
- Created `ChangeContextTestHelper.Create()` to simplify test code creation
- Documented all 70+ production null-check sites
- Identified 54 test sites using old test-only constructor signature

### Remaining Work (High-to-low impact)
1. **Production code cleanup** (70+ changes across 24 files):
   - HpChangeHandler, ItemEquipHandler, CharacterChangeHandlers (most checks, ~30 total)
   - ItemTransferHandler, TravelChangeHandler (nested logic, ~15 checks)
   - EventOccurredHandler, CampaignUpdateChangeHandler, RumorEvolvesHandler (conditional blocks)
   
2. **Test updates** (54 sites):
   - Replace `new ChangeContext(sessionForTests: null, ...)` with `ChangeContextTestHelper.Create(...)`
   - Replace ternary `context.Session != null ? ... : null` with direct calls

### Strategy for future work
- Update production handlers one at a time; run tests after each change
- Batch test updates by file (e.g., all Phase6HandlersTests at once)
- Delete test-only constructor only after all tests updated

---

## Next Steps (Prioritized)

1. **Phase 5.8:** Query performance optimization (84× WaitForNonStaleResults calls)
   - **Constraint:** RavenDB session disposal fails if multiple async queries run in parallel
   - **Strategy:** Optimize per-method; reduce timeouts where safe; identify critical-path queries needing staleness checks
   - **High-impact targets:** AdvanceWorld simulation tick (6 sequential 3s queries = 18s latency); EntitySuggester (10 calls); ChangeContext (10 calls)

2. **Phase 5.3:** Delete null-session test path (ChangeContext constructor)

3. **Phases 0–4:** Foundation work (token budget, guidance push, plugin system) — high impact but later priorities

---

## Completed Work (Earlier Conversations)

- ✅ Phase 5.1.4: CampaignSession introduction + test fixes (1183 passing, 1 skipped)
- ✅ Plot thread wiring fixes (LLM guidance, narrative scaffolding)
- ✅ Campaign onboarding Session 0 (conversation flow, prevent hallucination)
