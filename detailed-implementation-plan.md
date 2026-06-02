# CampaignVault — Granular & Detailed Implementation Plan

**Source**: [detailed-code-review-2026-04.md](./detailed-code-review-2026-04.md) (April 2026 fresh review)  
**Previous Context**: `code-review-fix-plan.md` (the high-level plan from earlier in the campaign pillar)  
**Date Created**: April 2026  
**Goal**: Turn the latest code review into a concrete, trackable, executable plan with fine-grained tasks, clear acceptance criteria, file references, dependencies, and verification steps.

**Guiding Principles for Execution**
- **LLM Surface First**: The biggest risk is the gap between what the README tells LLMs to do and what the server actually supports. Prioritize README + tool descriptions heavily.
- **Semantic Commits Only**: One logical change per commit. Use conventional commit prefixes (`fix:`, `docs:`, `refactor:`, `test:`, `feat:`).
- **Verify Immediately**: After every task (or small group), run `dotnet build` + relevant tests + manual mental model check.
- **No New Features Until Debt is Addressed**: Focus on fixing what exists.
- **Update This Plan**: Mark tasks complete with commit hash as you go.
- **One Thing at a Time**: Do not batch unrelated changes.

---

## Current Status Snapshot (from review)

- **Strengths**: Multi-campaign foundation is solid. `RulesetResolverBase` exists. Combat has meaningful guards + dead filtering + partial expiry. Many original 15 review issues are closed or mitigated.
- **Biggest Problem**: The "LLM experience layer" (README system prompt + tool descriptions + examples) is badly out of date and actively misleading.
- **Key Open Bugs**:
  - StatusExpiryRule has broken logic.
  - Duplication in campaign meta creation.
  - Legacy constructor hacks in `CampaignTools`.
  - Simulation rules ignore `CampaignName` in practice.
- Many medium documentation and polish items.

---

## Overall Phases & Priority Order

| Phase | Focus                              | Rationale                                      | Target Effort | Must Finish Before |
|-------|------------------------------------|------------------------------------------------|---------------|--------------------|
| 0     | README + Critical Stale Descriptions | Highest user/LLM risk. Low code change, high impact | Low           | Everything else    |
| 1     | Correctness Bugs (esp. Status)     | Real behavioral defects                        | Medium        | Phase 2 heavy work |
| 2     | Comprehensive LLM Metadata Refresh | Descriptions, examples, new discoverability    | High          | Phase 3            |
| 3     | Architecture & Code Quality Polish | Legacy cruft, duplication, documentation       | Medium        | -                  |
| 4     | Test Expansion & Hardening         | Confidence for future changes                  | Medium-High   | Major refactors    |
| 5     | Optional / Nice-to-Have            | Deeper mechanical improvements                 | Varies        | After 0-4          |

**Rule**: Complete Phase 0 + all of Phase 1 before starting large description rewrites in Phase 2.

---

## Phase 0 — README & Immediate High-Impact Documentation Fixes (Do First)

**Goal**: Stop actively misleading users and LLMs. Make the documented mental model match reality.

### Task 0.1 — Remove or Mark Obsolete "LLM System Instructions" Block (Completed in f574934)
- **File**: `README.md` (the big markdown code block starting around line 41)
- **Actions**:
  1. Delete the entire outdated "LLM System Instructions (Recommended for System Prompts)" section (or comment it heavily as "Historical — April 2026 — Do not use").
  2. Replace it with a short, accurate pointer: "See the **Current Recommended Usage** section below (or the actual tool descriptions in your MCP client)."
- **Acceptance Criteria**:
  - The block no longer tells the LLM an outdated story about supported change types, session loop, or tools.
  - No duplicate "Open Psychological Model" sections remain.
- **Commit Message**: `docs: remove or deprecate severely outdated LLM System Instructions block in README`
- **Verification**: Open the README and confirm the section is gone or clearly marked dead.

### Task 0.2 — Add Accurate "Current Recommended Usage" Section (Completed in f574934)
- **File**: `README.md`
- **Content to add** (new section after Features or in place of the old one):
  - Multi-campaign basics (`create_campaign`, `select_campaign`, `get_config`, `set_active_system` with locking behavior).
  - Updated Sacred Loop that includes campaign selection.
  - Clear statement that `commit` now prominently supports `ruleset_action` and combat state is visible in `get_scene`.
  - Pointer to `get_config` for active ruleset.
- **Acceptance Criteria**: A new, short, accurate 15-25 line "How to use this server today" section exists.
- **Commit Message**: `docs: add accurate Current Recommended Usage section reflecting multi-campaign + combat reality`

### Task 0.3 — Fix All Stale Claims in README (Completed in f574934)
- Update "Recent Updates" section.
- Fix any remaining references to old singleton document IDs.
- Update the table of core tools if needed.
- **Files**: `README.md`
- **Commit Message**: `docs: clean up stale claims and recent updates section in README`

### Task 0.4 — Fix `SetActiveSystem` Tool Description (Critical Stale Text) (Completed in f574934)
- **File**: `src/CampaignVault/Tools/CampaignTools.cs` (around line 478)
- **Change**: Update description from "Will eventually respect lock-in" to accurate statement of current behavior.
- **Acceptance Criteria**: Description no longer lies about lock-in status.
- **Commit Message**: `docs: fix stale description on SetActiveSystem tool`

**Phase 0 Exit Criteria (COMPLETED)**
- `README.md` no longer actively misleads.
- Build still green.
- At least one semantic commit for the phase.

**Completed in:**
- Primary work: `f574934` — docs: complete Phase 0 README and description updates
- Light polish (examples + comments): subsequent commit

---

## Phase 1 — Correctness Bugs (High Priority)

### Task 1.1 — Fix StatusExpiryRule Logic (Completed in 5db33e0)
- **File**: `src/CampaignVault/Data/StatusExpiryRule.cs`
- **Specific Issues**:
  - Remove the nonsensical `context.Time.TotalDaysElapsed > 0` condition for round expiry (line 28).
  - Decide ownership: Should this rule even try to handle round-based effects, or should it only handle day-based?
  - Current behavior: It emits `StatusRemove` deltas. This interacts with `StatusRemove` handler.
- **Options to evaluate** (document decision in commit or code comment):
  - A) Make the rule only handle `ExpiresAtDay`. Round-based expiry stays only in combat `NextTurn`.
  - B) Make the rule consult the per-campaign `CombatEncounter` (load it via keys + context) when deciding round expiry.
- **Acceptance Criteria**:
  - No more obviously wrong expiry condition.
  - Clear comment explaining the design choice (day vs round ownership).
  - Existing tests still pass or are updated.
- **Commit Message**: `fix: correct broken round-based expiry logic in StatusExpiryRule + document ownership decision`

### Task 1.2 — Add Status Expiry Cleanup on Combat End (Completed in 5db33e0)
- **File**: `src/CampaignVault/Tools/CampaignTools.cs` (in `EndCombat` method)
- **Actions**:
  1. When ending combat, optionally clear round-based statuses (or at least document that they may linger).
  2. Consider calling a shared helper (see Task 1.3).
- **Acceptance Criteria**: `EndCombat` either cleans round-based effects or clearly documents that they persist.
- **Commit Message**: `fix: ensure status cleanup on EndCombat (round-based effects)`

### Task 1.3 — Extract Campaign Meta Helper (Reduce Duplication) (Completed in 5db33e0)
- **Files**: `src/CampaignVault/Tools/CampaignTools.cs` (CreateCampaign + SetActiveSystem)
- **Actions**:
  - Create a small private helper `GetOrCreateCampaignMetaAsync(session, normalizedName, defaultSystem?)`
  - Use it from both `CreateCampaign` and `SetActiveSystem`.
- **Acceptance Criteria**:
  - No duplicated meta creation logic.
  - Lock-in behavior preserved exactly.
- **Commit Message**: `refactor: extract helper for Campaign meta creation to eliminate duplication`

### Task 1.4 — Audit EffectiveCampaign / Context Handling Consistency (Completed in 5db33e0)
- **Files**:
  - `src/CampaignVault/Tools/CampaignTools.cs`
  - `src/CampaignVault/Data/ChangeHandlers/RulesetActionHandler.cs`
  - Fallback `Commit(string, narrative)` path
- **Actions**:
  - Standardize on one helper.
  - Ensure the fallback Commit path respects current context.
- **Commit Message**: `refactor: standardize EffectiveCampaign handling and fallback paths`

### Task 1.5 — Document (or Mitigate) Global Query Behavior in Simulation Rules (Completed in 5db33e0)
- **Files**: `src/CampaignVault/Data/StatusExpiryRule.cs`, `NeedsAccumulationRule.cs`, `RumorDecayRule.cs`, `ScheduleEvaluationRule.cs`, `SimulationContext.cs`
- **Actions**:
  - Add clear comment in each rule + in `CampaignDocumentKeys.cs` or a new `ARCHITECTURE.md` explaining the scoping decision.
  - Optionally thread `CampaignName` through and add a filtered query helper for future use.
- **Acceptance Criteria**: Scoping policy is documented in one obvious place.
- **Commit Message**: `docs: document entity vs singleton scoping policy for simulation rules`

**Phase 1 Exit Criteria (COMPLETED)**
- All high-severity bugs from the review have clear fixes or documented workarounds.
- Build + relevant tests green.
- 4–6 semantic commits.

**Completed in:**
- Primary work: `5db33e0` — fix: complete Phase 1 correctness bugs and metadata refactoring
- Regression tests: `eccf895` — test: add regression tests for StatusExpiryRule and EndCombat

---

## Phase 2 — LLM Metadata & Tool Description Overhaul (Highest Long-Term Value)

This phase is deliberately large and should be broken into multiple commits.

### Task 2.1 — Audit Every Tool Description (Inventory)
- **File**: `src/CampaignVault/Tools/CampaignTools.cs`
- **Deliverable**: Create a temporary checklist (in this plan or a scratch file) of every `[McpServerTool]` + its current description quality (Good / Needs Examples / Stale / Missing Campaign Context).
- **Tools to pay special attention to**:
  - All combat tools (`StartCombat`, `NextTurn`, `EndCombat`)
  - `SetActiveSystem`, `GetConfig`
  - `CreateCampaign`, `SelectCampaign`, `ListCampaigns`
  - `Commit` (improve the existing example block further)
  - `Upsert*` tools (clean up Grok Web legacy noise if possible)

### Task 2.2 — Add Discoverability Tool(s)
- **New or enhanced tool(s)**:
  - Option A (preferred): Enrich `GetConfig` to also return the `Campaign` meta (locked?, display name, created at).
  - Option B: New lightweight `GetCurrentCampaign()` tool.
  - Add `ListRulesets()` or make `RulesetSystem` enum self-describing with descriptions.
- **Acceptance Criteria**: An LLM can easily discover "what campaign am I in?" and "what systems are available?"
- **Commit Message**: `feat: add/improve discoverability of current campaign + available rulesets`

### Task 2.3 — Systematic Description + Example Refresh (by Category)
Break into separate commits:

**2.3.1 Combat Category**
- Add rich examples for `StartCombat` + `ruleset_action` interaction.
- Document what happens on death, status during combat, etc.

**2.3.2 Campaign Management Category**
- Excellent descriptions + examples for `create_campaign`, `select_campaign`, `set_active_system`, locking behavior.

**2.3.3 Commit + RulesetAction**
- Expand the existing example block.
- Add one realistic example per major ruleset (D&D Attack, PF2e Strike/Skill, Fallout).

**2.3.4 General Pass**
- Ensure every tool description mentions campaign context where relevant.
- Remove or soften legacy Grok Web parameter name warnings (move to a "Client Compatibility Notes" section at the bottom of the file or README).

**Acceptance Criteria for whole 2.3**:
- Every public tool has a description that would be useful to an LLM that has never seen the code before.
- At least one good copy-paste example exists for the hard concepts (`ruleset_action`, multi-campaign, combat + status).

### Task 2.4 — Create a High-Quality, Copy-Pasteable "Current Recommended System Prompt"
- **Location**: Either a new section in README or a dedicated small file (`docs/recommended-system-prompt.md`).
- **Requirements**:
  - Short enough to fit in most context windows (~40-70 lines ideal).
  - Accurately describes today's surface (campaign tools, combat, ruleset_action, `get_config`, status, simulation).
  - Recommends the "start with get_world_state or get_config" pattern for multi-campaign.
- **Commit Message**: `docs: publish accurate, concise recommended system prompt for current server capabilities`

### Task 2.5 — Improve Model-Level Descriptions (if gaps remain)
- `src/CampaignVault/Models/WorldChanges.cs` and related DTOs.
- Keep the excellent style already present.

**Phase 2 Exit Criteria**
- An LLM given only the tool schemas + the new recommended prompt would have a reasonably accurate mental model.

**Completed in:**
- Primary work: `3d8bee5` — feat: complete Phase 2 LLM metadata and tool description overhaul
- All stale or low-quality descriptions have been improved.

---

## Phase 3 — Architecture, Code Quality & Maintainability

### Task 3.1 — Clean Up Legacy Constructor Hacks in CampaignTools
- **File**: `src/CampaignVault/Tools/CampaignTools.cs`
- **Goal**: Remove or isolate the private property forwarding pattern introduced for test compatibility.
- **Options**:
  - Move test construction helpers into the test project.
  - Use `[Obsolete]` + internal factory methods.
- **Commit Message**: `refactor: clean up legacy constructor compatibility hacks in CampaignTools`

### Task 3.2 — Improve XML Documentation Coverage
- Focus on public API surface (`CampaignRepository` public methods, tools, models).
- **Commit Message**: `docs: add/improve XML documentation on public surfaces`

### Task 3.3 — Extract or Document Scoping Policy Once
- Create or update `ARCHITECTURE.md` (or a top section in README) that clearly states:
  - Which documents are per-campaign (singletons via keys).
  - Which are shared / ID-controlled by caller (characters, locations, etc.).
  - Why this decision was made.

### Task 3.4 — Minor Resolver Duplication Reduction (if still worthwhile)
- Continue moving common logic into `RulesetResolverBase`.

### Task 3.5 — Combat Concurrency & Edge Case Hardening
- Review `StartCombat` / `NextTurn` for remaining N+1 or race risks.
- Consider adding a simple "version" or last-modified token on `CombatEncounter`.

---

## Phase 4 — Test Expansion

## Phase 4 — Test Expansion (High Value)

- [x] Integration test: Multi-campaign tool context leak (verify `commit` only touches selected campaign). *(Completed)*
- [x] Combat End-to-End: Write a test that runs `start_combat`, applies a `ruleset_action` attack, and verifies HP changes using `HpChangeHandler`. *(Completed)*
- [x] Status expiry tests: Write a test verifying that `advance_world` properly expires day-based statuses, and `end_combat` expires round-based ones. *(Completed)*
- [x] Fuzz / Property test: Inject invalid property names into the JSON array for `commit` and assert graceful failures instead of serialization crashes. *(Completed)*

---

## Phase 5 — Optional / Deeper Improvements

- [x] Mechanical application of `StatModifiers` from statuses inside resolvers. *(Completed)*
- [x] Better structured error types instead of string narratives for some resolver failures. *(Completed)*
- [x] Rate limiting / batch size guards on `commit`. *(Completed)*
- [x] Richer `WorldPressure` from simulation. *(Completed)*
- [x] Consider a small `get_help` meta tool. *(Completed)*
## Phase 6 — Open-World & Transient Architecture

**Goal:** Establish a "Schrödinger's World" model to safely track scalable locations and transient NPCs without bloat. Support multi-campaign context seamlessly.

- [x] **Location Model Upgrades:** Add `PointsOfInterest`, `AmbientCrowd`, and `LastVisitedDay` to the `Location` model, and `KeepAlive` to the `Character` model to support Schrödinger's World flavor nodes and Engine-driven Garbage Collection. *(Completed)*
- [ ] **Location Mutation Handlers:** Add `$type: location_create` and `$type: location_update` to the `commit` tool. Ensure the `WorldChangeDispatcher` natively handles these and namespaces them to the current campaign via `CampaignDocumentKeys`.
- [ ] **Transient Auto-Garbage Collection:** Update the world advancement and/or travel rules to automatically despawn "transient" NPCs (e.g., NPCs without a `Schedule` or marked as transient) when the party leaves a region or significant time passes.
- [ ] **NPC Promotion (Opt-In Persistence):** Add a mutation type (e.g. `$type: anchor_npc` or `$type: schedule_change`) so the LLM can selectively save a transient NPC by giving them a permanent schedule, protecting them from the GC sweeps.
- [ ] **Ambient Crowd Hints:** Expand `GetSceneAsync` to dynamically check the `Location`'s crowd parameters. Provide synthetic hints to the LLM when a scene is empty, prompting them to inject temporary flavor.

---

## Cross-Cutting Work & Tracking

### Documentation & Communication
- Keep `detailed-implementation-plan.md` updated (add "Completed in commit XXXX" notes).
- After major phases, consider a short update to `detailed-code-review-2026-04.md` (e.g., "Status: Fixed in ...").

### Verification Checklist (Run After Every Phase)
- [ ] `dotnet build` clean
- [ ] Relevant tests pass (`dotnet test --filter "..."`)
- [ ] Manual review of changed tool descriptions in an MCP client (if possible)
- [ ] README renders cleanly and is accurate when skimmed

### Risk Register
- Risk: Large description changes may affect existing LLM agent behavior (mitigation: make changes additive + clear).
- Risk: Status expiry ownership decision has downstream effects (mitigation: document clearly + add tests).
- Risk: Test expansion takes longer than expected (mitigation: prioritize the highest-value multi-campaign + combat loops first).

---

## How to Use This Plan

1. Pick the next highest-priority unchecked task.
2. Do the work + write tests + run verification.
3. Make a semantic commit.
4. Update this file (mark task complete + add commit hash).
5. Move to the next task.
6. Never start a new phase until the previous phase's exit criteria are met (unless the user explicitly directs otherwise).

---

**This plan is the single source of truth for the next body of work.** It directly implements every recommendation and bug from the April 2026 detailed code review.

**Next Immediate Action (recommended)**: Begin **Phase 0** (README + the single `SetActiveSystem` description fix). These are the highest-leverage, lowest-risk changes that stop the server from actively lying to its primary users (LLMs). 

---

*Plan created directly from the fresh detailed code review after removal of obsolete prior review artifacts.*