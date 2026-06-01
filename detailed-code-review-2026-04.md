# Detailed Code Review: CampaignVault (Current State — Post Multi-Campaign + Lower-Priority Sweep)

**Date of Review**: April 2026 (post campaign lock-in, propagation, StatusExpiryRule, RulesetResolverBase, combat robustness work, and the final low-traffic query sweep)  
**Reviewer**: Grok (this session)  
**Scope**: Full codebase (README, all tools + descriptions, models, resolvers + base, repository, simulation, handlers, DI/Program, tests, deployment). Focused on the user's explicit request: every bug, correctness, documentation, LLM-facing metadata/descriptions/examples, the "system prompt" in the README, and any other maintainability/UX issues.

Obsolete prior review artifacts (`review-1e78fc24.md`, `review-summary-1e78fc24.md`) were removed as the first action of this task.

---

## Executive Summary

The project has matured significantly since the original overhaul review. The multi-campaign foundation (first-class `Campaign`, strict `campaigns/{name}/...` namespacing via `CampaignDocumentKeys`, `CurrentCampaignContext`, deep propagation to every tool/repo/handler/simulation path, lock-in on `SetActiveSystem`/`CreateCampaign`, dedicated `create/list/select_campaign` tools, `SimulationContext.CampaignName`, `RulesetActionHandler` wiring, etc.) is solid. Many of the original 15 issues have been addressed (base resolver class exists, cross-system mismatch now errors cleanly in the base, combat has guards + dead filtering + some expiry, status expiry rule exists and is wired, descriptions have improved).

**However, the codebase still has a non-trivial number of correctness bugs, documentation drift, and — critically — LLM-facing problems.** The "LLM System Instructions" block in the README (the primary "system prompt" users are told to copy-paste) is now actively harmful/misleading because it is badly out of date with the actual tool surface after the combat + multi-campaign work. Tool descriptions are better than before but still inconsistent in quality, lack examples in key places, and contain stale claims.

**Overall Assessment**: Production-usable for a motivated user who reads the actual code + tool schemas, but risky for LLM agents following the documented "recommended system prompt" or relying only on tool descriptions. The gap between "what the README tells the LLM to do" and "what the server actually supports today" is the single largest usability/correctness risk.

**Recommendation**: Treat the README system prompt block + many tool descriptions as technical debt that must be brought into sync with reality before claiming the multi-campaign + combat features are "complete for LLM use."

---

## 1. Bugs & Correctness Issues (Prioritized)

### High-Severity / Real Behavioral Bugs

**Bug 1 — StatusExpiryRule logic is incorrect / incomplete (StatusExpiryRule.cs:27-28)**
- The round-based expiry condition `context.Time.TotalDaysElapsed > 0` is nonsensical and will cause almost all round-based effects to be treated as expired on the first `advance_world` call.
- The rule only looks at `TotalDaysElapsed` for round expiry; it never consults the *current combat round* (which lives in the per-campaign `CombatEncounter` document).
- `NextTurn` (in CampaignTools) already does some expiry for round-based effects — duplication + inconsistency risk.
- The simulation rule runs on `AdvanceWorld` (days passing), but round-based effects are primarily combat-time concepts.
- **Impact**: Status expiry is partially working but unreliable. Round-based effects (the most common combat use case) are likely to be removed at the wrong time or never.

**Bug 2 — Status expiry during combat is only in NextTurn, not robustly in EndCombat or on death (CampaignTools.cs ~640 and EndCombat)**
- When combat ends (`EndCombat`), active round-based statuses are not cleaned up.
- No expiry pass when the last combatant dies or combat is force-ended.
- **Impact**: "Frightened until end of combat" effects can linger on characters after the fight is over.

**Bug 3 — CreateCampaign + SetActiveSystem have duplicated + fragile meta creation logic (CampaignTools.cs:505-521 and CreateCampaign)**
- Two places create the `Campaign` meta document.
- In `SetActiveSystem`, when `campaign == null` it creates a locked one; the logic for "first time" vs "unlocked" is split and easy to get wrong in the future.
- No single `UpsertCampaignMeta` helper.

**Bug 4 — Inconsistent / fragile EffectiveCampaign handling**
- `EffectiveCampaign` in the refactored `CampaignTools` (now a method with ifs) vs the old expression-bodied version.
- Some paths (e.g. the fallback `Commit(string, narrative)`) hard-pass `null`.
- `RulesetActionHandler` still does `?? "default"` manually.
- Minor, but a maintenance smell after the class refactor for legacy ctors.

**Bug 5 — StatusExpiryRule does a full `Query<Character>()` every tick with no campaign scoping (StatusExpiryRule.cs:19)**
- Even though `SimulationContext` now carries `CampaignName`, the rule ignores it and scans *every* character in the database.
- Same pattern exists in other simulation rules (NeedsAccumulationRule, etc.).
- This is "by design" per earlier scoping decisions (entities are not namespaced; only the singleton docs are), but it means simulation cost grows with total characters across *all* campaigns.

**Bug 6 — Combat tools still allow (or can reach) bad states in edge cases**
- `StartCombat` now has good guards (empty check, only alive characters, distinct).
- `NextTurn` has improved "only alive + unacted" selection and a "CombatEnded" error path.
- Remaining risks: concurrent modification (still only protected by optimistic concurrency), a character dying exactly on the current turn, very large numbers of combatants (N+1 loads inside NextTurn for character HP checks).

### Medium / Documentation-Adjacent Correctness

- `SetActiveSystem` description still says "Will eventually respect lock-in" — it already does (stale description).
- Several places still have "TODO" or legacy comments referencing pre-campaign singleton IDs.
- The fallback `Commit(string json, ...)` path uses a very permissive `JsonSerializerOptions` (`AllowOutOfOrderMetadataProperties`) — acceptable for compatibility but a latent source of weird deserialization bugs.

---

## 2. LLM Metadata / Tool Descriptions / Examples (Major Problem Area)

This is the area the user specifically called out. It is currently the weakest part of the project for its intended audience (LLM DMs).

### Problems in the README "LLM System Instructions" Block (the "system prompt")

- **Completely omits the entire multi-campaign feature set** (`create_campaign`, `list_campaigns`, `select_campaign`, `get_config`, `set_active_system`). An LLM following this prompt literally will never discover that it can run multiple isolated campaigns with different rulesets.
- The "Supported change types" list inside the prompt is outdated (missing or de-emphasizing `ruleset_action`, `statusremove`).
- The "Sacred Session Loop" does not mention campaign selection or switching.
- The "Golden Rule" section talks about `commit` types but gives an example that is now misleadingly incomplete.
- Duplicate "Open Psychological Model" section (copy-paste error, lines ~102-127 in the file).
- "Recent Updates" section is stale and understates how much has changed (combat + multi-campaign).
- The prompt tells the LLM to use `get_world_state` etc. but never tells it about the campaign management tools or the locking model.

**Result**: Any user who follows the README's own recommendation ("paste the following into your system prompt") will be running an *outdated mental model* of the server.

### Problems in `[Description]` Attributes on Tools & Parameters

**Strengths** (improved since original review):
- `Commit` has a genuinely helpful multi-line example block with `ruleset_action`.
- Many tools now say "Respects the currently selected campaign (via select_campaign)".
- WorldChanges.cs has excellent per-property `[Description]` annotations (this is model-level metadata and is very good).

**Weaknesses & Gaps**:
- `SetActiveSystem` description is actively wrong/stale ("Will eventually respect lock-in").
- Combat tools (`StartCombat`, `NextTurn`, `EndCombat`) have decent one-sentence descriptions but almost no usage examples or guidance on how they interact with `ruleset_action`, status effects, or death.
- No tool description or dedicated discoverability tool for "what are the valid RulesetSystem values?" or "what is the current campaign + its lock status?"
- `Upsert*` tools have long notes about Grok Web legacy parameter name bugs (`c` / `l`). These are accurate but pollute the descriptions that every LLM sees.
- Several parameter descriptions are one-liners with no examples or constraints (e.g. what format should `combatantIds` be in? Are duplicates allowed? What happens on death mid-combat?).
- `DefineNeedDescriptor` and `GetNeedDescriptors` are good but the campaignName parameter descriptions are terse.
- The `ruleset_action` example in Commit is D&D-centric and does not show system-specific parameter differences (e.g. PF2e degree of success, Fallout dice pools).

**Missing High-Value LLM Aids**:
- No `get_current_campaign` or `get_campaign_info` tool that returns the meta `Campaign` document + lock state + active system in one call.
- No examples in descriptions for common `ruleset_action` patterns per system.
- No guidance on the interaction between `status` + `ruleset_action` (modifiers are now stored but mechanical application inside resolvers is still incomplete in many paths — see below).

---

## 3. Documentation & Code Quality Issues

- **Duplicate content** in README (psychological model section appears twice).
- Many XML `<summary>` / `<param>` docs are missing or minimal on public methods in `CampaignRepository`, resolvers, etc.
- `CampaignTools` still carries ugly legacy constructor + private property forwarding hacks (the "expose the injected services via the original names" pattern). This was a pragmatic fix for test compatibility but is now technical debt.
- Inconsistent naming: some tools use `GetConfig`, others `GetCampaignConfig` in internal calls.
- `JsonSanitizer` is doing heroic work but is a symptom of a deeper mixed STJ/Newtonsoft data model problem.
- Test files still contain many direct construction of repos/tools with the legacy ctors — this masks how complex the real DI has become.
- `ListCampaigns` still uses a brittle `StartsWith("campaigns/")` query on the entity (works because of how meta docs are stored, but fragile).

---

## 4. Architecture / Maintainability / Other Issues

- **Simulation rules ignore `CampaignName`** in practice (even though the field exists on the context). All rules still do global `Query<Character>()` / `Query<Rumor>()`. This is acceptable only because the design decision was made that entity documents are not per-campaign (only the singleton config/combat/time/need docs are). This decision should be explicitly documented in one place (e.g. `CampaignDocumentKeys` or a top-level ARCHITECTURE.md) rather than scattered in comments.
- Resolver implementations still have some duplication (the base class helped a lot, but concrete `ResolveAttackAsync` etc. still repeat a fair amount of parameter extraction + mutation emission logic).
- Initiative rolling in `StartCombat` still does resolver calls that can load characters again in some paths.
- No rate limiting, no size limits on commit batches, no protection against a malicious LLM creating thousands of status effects.
- The `CurrentCampaignContext` uses `AsyncLocal` — correct for the "session-like" use case inside a single MCP request, but any true concurrent work inside a tool would need care.
- Deployment / Fly.io docs are reasonably good.

---

## 5. Test Coverage & Verification Gaps

- New `CombatToolsTests.cs` exists — good.
- Still very few end-to-end tests that exercise a full `select_campaign` → `commit` with `ruleset_action` → `start_combat` → `next_turn` loop across two different campaigns with different systems.
- Almost no tests that deliberately exercise the lock-in rejection path or the auto-create behavior in `select_campaign`.
- Resolver tests are better than before but still narrow on error paths and cross-system cases.
- No property-based or fuzz tests on the polymorphic `WorldChange` deserialization (especially the fallback JSON string path).

---

## Recommended Fix Plan (Prioritized)

### Phase 0 (Immediate — Low Effort, High Value)
1. **Delete or heavily rewrite the "LLM System Instructions" block** in README (or mark it clearly as "historical example — see current tool descriptions").
2. Add a short, accurate "Multi-Campaign Quickstart" section + update the sacred loop to include `select_campaign` / `get_config`.
3. Fix the duplicate section and the stale "Will eventually respect lock-in" text.
4. Add a `get_current_campaign` (or enrich `get_config`) tool that returns the `Campaign` meta (name, system, locked?, display name).

### Phase 1 — Correctness (Bugs)
1. Fix `StatusExpiryRule` (remove the bogus `TotalDaysElapsed > 0` check; decide whether round-based expiry belongs only in combat tools or also in the rule via combat state inspection).
2. Add expiry cleanup in `EndCombat` (and on "last man standing" in NextTurn).
3. Extract a small `CampaignMetaService` or helper to eliminate duplication in Create/SetActiveSystem meta handling.
4. Audit all simulation rules for `CampaignName` usage (even if they stay global for now, add comments and optional filtering).

### Phase 2 — LLM Metadata & Documentation
1. Do a full pass over every `[Description]` in `CampaignTools.cs`, `WorldChanges.cs`, and parameter docs. Add realistic examples for `ruleset_action` (one per major system), combat flows, and multi-campaign usage.
2. Write a concise, accurate, copy-pasteable "Current Recommended System Prompt" that actually reflects today's surface (including campaign tools, combat, ruleset_action, status modifiers).
3. Add XML documentation + better inline comments where missing.
4. Consider a lightweight `get_help` or `list_available_systems` meta tool.

### Phase 3 — Polish & Hardening
- Clean up the legacy ctor hacks in `CampaignTools` (perhaps move test-only construction helpers into the test project).
- Improve combat concurrency story (or document the single-writer assumption clearly).
- Expand end-to-end tests for the happy path of "two campaigns, different systems, lock enforcement, combat + ruleset_action".
- Decide and document once (in one file) the long-term entity scoping policy.

### Phase 4 — Optional but Valuable
- Mechanical application of `StatModifiers` inside the resolvers (still mostly inert for many stats).
- Better error narratives and validation on `ruleset_action` parameters (the original unsafe Parse issues were partially mitigated by the base, but individual resolvers can still be hardened).
- Structured "WorldPressure" improvements and richer simulation narratives.

---

## Files That Need Attention (Non-Exhaustive)

- `README.md` (highest priority)
- `src/CampaignVault/Tools/CampaignTools.cs` (all descriptions + combat + campaign management methods)
- `src/CampaignVault/Data/StatusExpiryRule.cs`
- `src/CampaignVault/Rulesets/*` (continue hardening + examples)
- `src/CampaignVault/Models/WorldChanges.cs` (already good — keep the style)
- `src/CampaignVault/Data/CampaignRepository.cs` (comments + any remaining internal bypasses)
- Test files (more integration scenarios)

---

## Conclusion

The engineering work on the combat system and especially the multi-campaign foundation is high quality. The *surface that LLMs actually see and are instructed to use* has not kept up with that engineering work. Closing that documentation + metadata gap is the single most important thing to do before this server can be considered "production ready for LLM Dungeon Masters" in a multi-campaign, multi-ruleset world.

The project is in much better shape than at the time of the first review, but the "LLM experience" layer is the current bottleneck.

**Next step recommendation**: Prioritize Phase 0 + Phase 2 (README + description refresh) before adding more features. The code is ready; the instructions and tool metadata are not.

---

*This review was produced after removing the obsolete prior review files and performing a fresh full pass over the current state of the repository.*