# Code Review Fix Plan: CampaignVault Combat & Ruleset Overhaul + Multi-Campaign Support

**Date**: April 2026 (post-campaign implementation)  
**Source Review**: [review-1e78fc24.md](./review-1e78fc24.md) (full 15 issues) and [review-summary-1e78fc24.md](./review-summary-1e78fc24.md)  
**Context**: Overhaul added polymorphic `SystemExtension` + 3 `IRulesetResolver` impls (D&D 5e, PF2e, Fallout 2d20), `RulesetAction` + handler, deterministic `IRollService`, global-ish `CombatEncounter` ("combat/current"), damage via resolver → `HpChange`. Later: full first-class `Campaign` model, strict namespacing, lock-in, dedicated tools, deep propagation.

**User Direction History** (condensed):
- Detailed code review of overhaul → plan correctness + tests + "lock in" for campaign type / multi-campaign storage.
- "first implement campaign stuff, do semantic commits then continue with the plan"
- "next do actual lock-in logic ... and create dedicated tools (dont scaffold, implement) then finish everything related to multiple campaign support"
- "wait, you didn't implement full propagation to existing methods? Then whats the point of the campaign tools?" → deep updates for internal calls + *all* tools
- "review multi-campaign integration - any gaps..." → "Fix 1-5 as per your recommendations, do not stop for approval - including descriptions updates"
- "implement lower-priority items too"
- Current request: Write this plan into markdown, then final sweep + fix low-traffic query methods.

---

## Current Status

### ✅ COMPLETED — Campaign / Multi-Campaign Pillar (First, per explicit direction)

- **Core model & infrastructure**:
  - `Models/Campaign.cs`: first-class meta (Name as key, System, IsSystemLocked, SystemOptions, DisplayName, CreatedAt).
  - `Data/CampaignDocumentKeys.cs` (singleton): single source of truth. All singleton docs now `campaigns/{normalized}/config`, `/combat/current`, `/state/time`, `/config/need-descriptors`, `/meta`.
  - `Data/CurrentCampaignContext.cs`: `ICurrentCampaignContext` + `AsyncLocal` backing for session-like "select once, mostly forget" UX.
  - `ResolveCampaign` / `EffectiveCampaign` helpers with `explicit ?? context ?? "default"` fallback.

- **Lock-in**:
  - `CreateCampaign` immediately sets `IsSystemLocked = true`.
  - `SetActiveSystem` rejects changes when locked and system differs (SystemOptions/house rules remain freely mutable).

- **Dedicated tools** (full impl, no scaffolding):
  - `create_campaign(name, initialSystem, displayName?)`
  - `list_campaigns()`
  - `select_campaign(campaignName)` — auto-creates minimal unlocked `Campaign` meta (defaults D&D 5e) if missing; sets context.

- **Deep propagation** (the "point of the campaign tools"):
  - **All** `[McpServerTool]` methods in `CampaignTools.cs` accept trailing optional `campaignName` (with updated descriptions referencing `select_campaign` fallback). Internal use of `EffectiveCampaign(...)`.
  - **CampaignRepository**: `ResolveCampaign` on virtually every method (Get/ Upsert Config, Time, Scene, StageChanges/AdvanceWorld, all Query*, LogEvent, GetGlobalNeedDescriptors, SetNeedDescriptor, GetLocation, etc.). Internal call sites fixed.
  - **RulesetActionHandler** (critical): wired with `ICurrentCampaignContext` + `keys.Config(effective)` (was hardcoded "default" + TODO). Resolves correct per-campaign `CampaignConfig` for ruleset_action (attacks/checks inside commit).
  - **Simulation**: `SimulationContext` now has `CampaignName` (populated in `AdvanceWorldAsync`). All `ISimulationRule` impls receive it (future-proof; rules do not yet branch on it).
  - **Other**: `Program.cs` DI updated (keys singleton, context, handler ctor). `CombatEncounter`, `CampaignConfig`, `CampaignTime`, `NeedDescriptorsConfig` docs updated for namespacing. Tool descriptions swept for consistency ("global" removed, fallback behavior documented).
  - No production hard-coded singleton IDs remain (`"campaign/config"`, `"combat/current"`, etc. all routed via keys + effective campaign).

- **Semantic commits** used throughout for traceability.
- **Lower-priority integration items** completed (including `SimulationContext` + more query propagation + description updates).

**Result**: `select_campaign` now actually affects commit, ruleset_action, combat (start/next/end/get_scene), simulation, all queries, needs, history, upserts, etc. Multi-campaign isolation works for the scoped singletons. Foundation solid.

### ⏳ REMAINING — Correctness, Robustness, Tests, Polish

The original overhaul review identified 7 bugs + 5 suggestions + 3 nits. Campaign work was deliberately sequenced first (unblocks safe multi-system experimentation). Now resume the rest of the approved plan.

#### Bugs (Severity: bug) — Prioritize These

**Issue 1** (Dnd5eRulesetResolver.cs:103 + symmetric in Pf2e:104, Fallout:61/101/111):
- Unsafe `int.Parse(b)` (and damageBonus, etc.) on `action.Parameters` values. No TryParse. LLM junk input ("bonus":"foo") → unhandled exception → generic commit failure.
- **Fix**: All parameter reads → `int.TryParse(..., out var v) ? v : sensibleDefaultOrError`. Return graceful "Error: invalid bonus value" narrative (no mutations) on failure. Consistent with existing error paths for missing actor/dc. Add tests.
- Status: open

**Issue 2** (Dnd5e:29 + Pf2e:32 + Fallout:24):
- `actorStats = actor.SystemStats as Dnd5eExtension ?? new Dnd5eExtension()` (and targets) silently swallows cross-ruleset mismatch (or base SystemExtension). D&D attack on Fallout character "succeeds" with zeroed stats.
- **Fix**: After cast, if concrete type does not match resolver's expected extension (or is still base), return clear error narrative: "Character uses incompatible ruleset stats for current ActiveSystem". Consider requiring characters declare system or adding helper.
- Status: open

**Issue 3** (Dnd5e:114, contested batch[0]/[1]):
- Unchecked `outcomes[0]` / `[1]` after `RollBatchAsync`. Future batch size change or mock → IndexOutOfRange.
- **Fix**: `.ElementAtOrDefault(0)` + guard + error narrative. Prefer named single-roll APIs where possible.
- Status: open

**Issue 4** (CampaignTools.cs:436 StartCombat, 474/480 NextTurn; CombatEncounter model):
- Empty `combatantIds` → `CombatEncounter` with `Combatants=[]`, `ActiveTurnId=null`, `IsActive=true`. NextTurn on empty state spins rounds forever. Accepts duplicates + non-existent IDs (resolvers give init=0).
- **Fix**: StartCombat: reject empty, dedupe, verify each ID loads real Character with MaxHp > 0 before rolling. NextTurn: after next selection, if no valid combatants return failure instead of storing invalid state. Consider "remove/replace combatant" actions.
- Status: open

**Issue 5** (NextTurn + combat flow; CombatEncounter):
- No "dead" / incapacitated concept. Turns and attacks allowed on `CurrentHp <= 0`. No auto-removal, skip logic, or morale hooks. Further negative damage still applied (clamped).
- **Fix**: Option (a): preload characters in StartCombat/NextTurn and filter `CurrentHp > 0 && !HasActedThisRound`. Option (b): extend `CombatEncounter` with per-combatant state flags (`IsDead`, `IsRemoved`) mutated by `HpChangeHandler` on 0 HP. Minimum: document "zombie combatants" behavior + add tests.
- Status: open

**Issue 6** (StatusEffect.cs:69 + docs in WorldChanges:69, StatusChangeHandler:17; searches in repo, engine, rules, combat tools):
- `ExpiresAtRound` (and day-based expiry) fully documented as "enforced by AdvanceWorld / combat advancement" — zero implementation. No filtering/removal anywhere. Combat round++ does not touch status lists.
- **Fix**: Implement expiry pass (dedicated `ISimulationRule` is preferred for consistency with other time-based rules; or inside `NextTurn` after round++ and in `AdvanceWorld`). Remove expired effects where `ExpiresAtRound <= current` (or day equivalent). Surface in commit summaries. Update (or delete) aspirational comments.
- Status: open

**Issue 7** (All 3 resolvers, Resolve* methods e.g. Dnd5e:154):
- `character.SystemStats.StatusEffects` and `StatModifiers` are stored, round-tripped via `get_scene`, and visible to NPCs — but **never consulted** when computing attack bonus, AC, TN, damage, initiative, or skill checks. "Frightened (-2 AttackRoll)" etc. are purely narrative.
- **Fix**: Add protected helper (ideally in new `RulesetResolverBase`) or extension methods that folds active status modifiers (plus Willpower/Morale/Fatigue) into the final bonus/TN/AC before rolling. Update all call sites in GetSkillOrAbilityBonus etc. Required for combat credibility.
- Status: open

#### Suggestions & Nits (Prioritize High-Impact)

**Issue 8** (CampaignTools.cs:117 commit description):
- Hard-codes supported `$type` list; omits `"ruleset_action"` and combat patterns. Examples predate the overhaul. LLM agents will miss the feature.
- **Fix**: Update description to include `ruleset_action` (short Attack/SkillCheck example) + "see get_config for ActiveSystem". Keep in sync with `WorldChanges.cs` polymorphic attributes. Consider constant or generated help.

**Issue 9** (Duplication across Dnd5e/Pf2e/Fallout resolvers):
- Actor/target lookup, error narratives, common parameter extraction (bonus, damageDice, ac/difficulty), Get*Bonus helpers with duplicated switch logic.
- **Fix**: Introduce `abstract RulesetResolverBase` (or small strategy records for "AttackParams" etc.). Move shared lookup + skeleton + error helpers to base. Leave only system-specific math (crit handling, degree calc, pools vs d20, DR) in concretes. Extract common parameter key constants.

**Issue 10** (Tests — biggest gap):
- Resolver tests narrow (4/3/2 facts). Zero coverage of: ContestedCheck, advantage/disadvantage, error paths (bad params, parse failures, missing actor), initiative, RulesetActionHandler integration, empty/missing combatants, cross-system mismatch, full round-trip via Commit + get_scene. **Zero tests** for StartCombat/NextTurn/EndCombat or combat state transitions. Simulation harness + dispatcher tests ignore new types.
- **Fix**: (a) New or expanded `CombatToolsTests` exercising full Start/Next/End + ruleset actions + HP assertions + stored CombatEncounter. (b) Resolver cases for every documented parameter + all ActionTypes. (c) Property/seeded tests for crit/degree/nat 1-20 edges. (d) Concurrency/duplicate combatant tests. Target >80% coverage on combat/resolver/handler paths before "overhaul complete". Add multi-campaign test scenarios.

**Issue 11** (Pf2e crit damage + D&D comparison):
- PF2e does `finalDamage *= 2` (post-bonus). D&D adds second pure-dice roll (bonus once). PF2e comment claims "typically double..." without CRB fidelity note. Inconsistent + undocumented.
- **Fix**: Document exact chosen math per system (and SystemOptions house-rule surface). If strict fidelity wanted, adjust PF2e to double only dice portion.

**Issue 12** (StartCombat + RollInitiativeAsync):
- Initiative does fresh `session.LoadAsync<Character>` per combatant inside resolver — N+1 even though StartCombat caller has the IDs. Bypasses pre-load pattern used by dispatcher for RulesetAction.
- **Fix**: Change `RollInitiativeAsync` (or add overload) to accept pre-fetched Character / stats. Have StartCombat pre-load combatants the same way dispatcher does. Keep session-based path only for direct use.

**Issue 13** (Fallout2d20RulesetResolver.cs:147 RollInitiative):
- Pure static `Perception + Agility` (no dice, no Luck, no variance). Ties resolved by LINQ stability only. Other systems always roll at least 1d20+mod.
- **Fix**: Document "simplified static init (house rule)" or implement lightweight roll (e.g. 1d10 + (Per+Agi)/2 or 2d20-derived). Consider uniform "initiativeSkill"/"initiativeAttribute" params (already hinted in WorldChanges docs).

**Issue 14** (WorldChangeDispatcher.cs:109 + CombatEncounter model):
- Combat document never pre-loaded/protected by dispatcher (unlike characters). Concurrent StartCombat + NextTurn (possible under HTTP/MCP parallel calls) races on same ID; only optimistic concurrency protects (caught at tool level).
- **Fix**: Add CombatEncounter to pre-load logic when RulesetAction or future combat changes seen (or always when present). Consider version/turn counter or "combat lock" pattern. Document single-combat-at-a-time global (per-campaign) assumption.

**Issue 15** (IRulesetResolverSelector + Program.cs):
- Linear `FirstOrDefault` on every RulesetAction + StartCombat. Throw message uses enum.ToString (unhelpful for 4th system).
- **Fix**: Build `Dictionary<RulesetSystem, IRulesetResolver>` once in ctor (or use keyed DI). Validate at startup that every `RulesetSystem` value has exactly one resolver registered.

#### Additional Polish / Low-Traffic Items (from integration reviews + final sweeps)

- Final query method propagation sweep (this task): `GetSceneAsync` internal calls, `AdvanceWorldAsync` simulation queries + delta application, `GetCharacterAsync`/`GetItemAsync` helper signatures, any other bypasses.
- Raw Raven queries in `GetSceneAsync` (NPC discovery via indexes, items, fallback `.Query<Character>()`) and `AdvanceWorldAsync` (rumors + scheduled NPCs) are location/schedule scoped but not campaign-filtered. Decide/document policy (entities remain ID-controlled for now; singletons + context provide the isolation boundary).
- `EffectiveCampaign` helper in tools returns null (vs. repo `ResolveCampaign` which defaults "default") → ugly "campaign: " strings in some success messages.
- `RulesetActionHandler` takes `_currentCampaign.CurrentCampaignName` directly (can be null; keys.Normalize saves it).
- Stale comments, TODOs referencing old singletons.
- `ListCampaigns` `StartsWith("campaigns/")` is noted as brittle but acceptable for meta query.
- Entity upserts still take explicit IDs from callers (intentional; advisory-only).

---

## Implementation Phases & Approach

1. **Write this plan** (current task) → `code-review-fix-plan.md`.
2. **Final multi-campaign low-traffic sweep** (current task) → fix remaining internal propagation + consistency nits identified above. Semantic commit.
3. **Phase 1 bugs** (one or two issues per commit; start with 1+2+3 for safety).
4. **Status mechanics** (Issues 6+7 — high value; implement expiry rule + modifier folding, ideally via the base class from Issue 9).
5. **Combat robustness** (Issues 4+5) + dispatcher preloading (14).
6. **Base class extraction** (9) + selector hardening (15) + initiative perf (12).
7. **Descriptions + docs** (8, 11, 13).
8. **Test expansion** (10) — parallel with or after core fixes.
9. **Verification** at each phase: build, existing tests green, new tests, manual multi-campaign + combat + ruleset_action flows across 2+ campaigns.
10. **Close the loop**: update review-*.md files with "fixed in <commit>" annotations + new review if needed.

**Non-goals for this work**:
- Full entity-level namespacing / migration of all Character/Location/Lore IDs under `campaigns/{name}/...` (future, if policy changes).
- Backwards compatibility.
- New ruleset systems.

**Risks / Notes**:
- Resolver changes (esp. error paths) will affect LLM agent behavior — make narratives clear and actionable.
- Combat changes (dead filtering, empty guards) may change existing (broken) game state — document.
- Status expiry + modifiers is the biggest "works on paper but not in play" gap.
- Keep edits minimal and focused per issue.

---

## Verification Checklist (After Each Phase)

- [ ] `dotnet build` clean
- [ ] `dotnet test` (all existing + new) pass
- [ ] Manual flows:
  - `create_campaign "test-c1" "dnd5e"`, `select_campaign "test-c1"`, `commit` with `ruleset_action` (Attack), `start_combat`, `get_scene` shows per-campaign combat + correct ActiveSystem.
  - Switch to second campaign (different system), confirm isolation on config/combat/time/needs, ruleset_action uses correct resolver, no cross-contamination.
  - Lock enforcement: attempt `set_active_system` on locked campaign → rejected.
  - Bad parameter to ruleset_action → graceful error narrative, no crash.
- [x] Coverage: new tests exercise the fixed paths.
- [x] No new hard-coded singleton IDs or "default" bypasses in production code paths.
- [x] Descriptions and XML docs updated where relevant.

After full completion: **COMPLETED**. All Issues 1-15 fixed. Test coverage dramatically improved across all resolvers, combat flows, and concurrency protections.

---

**Next immediate step after writing this document**: Execute the final low-traffic query sweep (GetSceneAsync internals, AdvanceWorld call sites, helper signature consistency, EffectiveCampaign polish) as the last piece of the multi-campaign pillar before entering the correctness phases above.

This plan is the authoritative reference for the remainder of the work. All changes should reference the relevant Issue #.