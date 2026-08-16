# CampaignVault Refactor Status

**Last Updated:** 2026-08-17  
**Overall Progress:** Phase 5.3 in progress (50% of full plan)

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
| **5.3** | Delete null-session test path | 🔄 In Progress | **Production:** 70+ null-checks removed ✅ (24 handlers); **Tests:** 1/55 sites migrated to RavenDBFixture; 54 remaining |
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

## Next Steps (Prioritized)

1. **Phase 5.3 continuation:** Migrate remaining 54 test sites
   - High-value targets: Phase6HandlersTests, WorldChangeDispatcherTests, handler-specific tests
   - Batch by file to minimize context switching
   - Verify all tests pass after each batch

2. **Phase 5.3 completion:** Delete test-only constructor + WorldChangeDispatcher branch

3. **Phase 5.8:** Query performance optimization (84× WaitForNonStaleResults calls)
   - **High-impact targets:** AdvanceWorld simulation (18s latency); EntitySuggester (10 calls)

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
