# CampaignVault Remediation Plan (11 findings)

Verified against current code via Explore agents. Ordered by implementation sequence (small/isolated first, cross-cutting last).

## 1. Finding 8 — Failed commit responses read as partial success (trivial)
**File:** `src/CampaignVault/Tools/MutationTools.cs` `Commit`, ~line 137-142.
Prepend a "NO CHANGES WERE SAVED — entire batch rolled back, resend the FULL batch" banner to the error summary returned on `!result.Success`.

## 2. Finding 10 — Commit narrative events logged with DayLogged=0 (trivial)
**File:** `src/CampaignVault/Tools/MutationTools.cs` `Commit`, ~line 144-149.
Fetch `time` via `_repository.GetTimeAsync(session, effective)` (same helper `AdvanceWorldAsync` uses) and stamp `DayLogged = time.TotalDaysElapsed` on the SceneCommit event.

## 3. Finding 11 — Correctness bundle (5 independent one-file fixes)
- **11a** `Data/ChangeHandlers/RumorEvolvesHandler.cs:24-32` — load rumor first, fail with "not found" if missing instead of blind patch.
- **11b** same file, `RumorCreateHandler` (~line 76) — add collision guard: fail if ID already exists (matches event-creation pattern from commit 55463bd).
- **11c** `Data/DefaultSimulationEngine.cs:45-49` — on rule exception, add a narrative `"[Engine] Rule 'X' failed and was skipped this tick: {msg}"` to surface into `SimulatorEvents`, not just server logs.
- **11d** `Tools/ExplorationTools.cs` `GetNpcContext` (~line 304) — change `saveChanges: false` → `true` so cooldown mutations from `FilterAndCapAsync` actually persist.
- **11e** `Data/Pressure/Contributors/TransientQuestGiverPressureContributor.cs:25` — fix wording from "The engine will delete them" to accurately describe recoverable eviction (location nulled, not deleted).

## 4. Finding 5 — chars/ vs characters/ prefix split
- **5a** `Tools/CommitSchemaRegistry.cs` lines 35, 358 — change `"characters/grog"` → `"chars/grog"`, `"characters/wizard-1"` → `"chars/wizard-1"` for consistency with the other 8 examples already using `chars/`.
- **5b** (real bug) `Authoring/Vault/Sync/VaultSyncEngine.cs:618` `InferRelativePathFromId(entityId) => $"{entityId}.md"` — naive concat produces `chars/grog.md` at vault root, which `CollectLocalEntityPaths` never scans (only walks configured `characters/`, `locations/`, etc. folders) → entity permanently stuck `RemoteOnly`. Add `VaultFrontmatter.InferRelativePathFromId(entityId, entityType)` that maps via `VaultPaths.EntityFolders` (mirrors existing `InferIdFromRelativePath`'s inverse), strips any existing prefix segment from `entityId`, and re-roots under the canonical folder. Call sites: `VaultSyncEngine.cs:263` and `:612`. `VaultEntitySyncPlan` already carries `EntityType`, so no new plumbing needed.
- **5c** No server-side change needed — `CampaignSyncService` correctly returns/accepts raw `chars/` doc IDs; the folder mapping belongs entirely on the vault side.

## 5. Finding 9 — MaxPressuresPerResponse caps groups not items
**File:** `Data/PressureManager.cs:79-107` `FilterAndCapAsync`.
Change `groups.Take(maxPressures)` (caps group count) to cap at the **item** level: iterate severity-ordered groups, accumulate whole groups while running item count stays under `maxPressures`, always include at least one group even if it alone exceeds the cap (never silently suppress the single most severe pressure). No changes needed to `ToDisplayStrings` or the cooldown-tracking block.

## 6. Finding 1 — PCs/companions can be silently evicted
- **1a** `Data/Character_Search.cs` — add `c.IsPc`, `c.IsPartyCompanion` to the index `Map` (additive, auto-reindexes).
- **1b** `Data/SimulationQueryHelper.cs:102-147` `QueryEvictableTransientCharactersAsync` — add `.WhereEquals(x => x.IsPc, false).AndAlso().WhereEquals(x => x.IsPartyCompanion, false)` to both the campaign-scoped and shareable sub-queries.
- **1c** `Data/ChangeHandlers/CharacterChangeHandlers.cs:158-174` (create path) — force `KeepAlive = cc.KeepAlive || cc.IsPc || cc.IsPartyCompanion`.
- **1d** Same file, update path (~line 470-490) — when `IsPc`/`IsPartyCompanion` is flipped true via update, force `character.KeepAlive = true` too (this call site has the identical gap, not just create).
- No change needed to `TransientEvictionRule.cs` — with 1a/1b, PCs/companions never enter the candidate query; existing quest-giver guard stays as the only in-loop special case.

## 7. Finding 2 — get_scene recent-events lookup contradicts locationId guidance
**File:** `Data/CampaignRepository.cs:283-293` `LoadSceneEventsAsync`.
Current code takes 5 events then filters by `Involved.Contains(locationId)` — loses recent events (take-then-filter bug) and can't see `LocationId`/`RelatedLocationIds` set by newer code.
Fix: query `QueryEventsAsync(session, null, null, 5, effectiveCampaign, locationId: locationId)` (indexed, existing param — same pattern `GetNpcContext` already uses for `involvedCharacterId`) as the primary source. **Keep a fallback union** for legacy events that only recorded location via `Involved` (confirmed still actively written by `EncounterResolver.cs`, `AmbientCrowdHeuristics.cs`, etc.) — merge in events where `LocationId`/`RelatedLocationIds` are both empty AND `Involved.Contains(locationId)`, dedupe by Id, re-sort, take 5.

## 8. Finding 6 — authoring push overwrites live play-state
- **6a (ship now, no proto change)** `Authoring/Vault/Sync/VaultSyncEngine.cs` push path — before calling `PushCampaignEntity`, re-run `GetCampaignEntities` to refresh the remote cache and re-classify the entity's sync state. If it's no longer push-eligible (state moved from e.g. `AheadOfVault` to `Conflict`/`BehindVault`), abort the push for that entity with a clear message ("remote has changed since last Fetch — likely simulation activity — re-review before pushing"). No single-entity fetch RPC exists in `vault_sync.proto`, so this re-fetches the whole campaign (coarser but correct and zero proto footprint).
- **6b (flagged as separate follow-up, not in this pass)** Server-side change-vector concurrency guard: add `expectedChangeVector`/`changeVector` fields to `vault_sync.proto`, wire through `CampaignSyncService.PushCampaignEntity` using RavenDB's `StoreAsync(entity, changeVector, id)` overload + `UseOptimisticConcurrency`. Requires proto regen and threading the change vector through `VaultRemoteCache`/`VaultEntitySyncPlan`. Real added scope — do as its own follow-up PR.
- **6c** `Authoring/Vault/Canonical/EntityCanonicalizer.cs` `ClearSyncExcludedFields` — add simulation-only fields (`CurrentHp`, `Needs`, `Psychology`, `CurrentLocationId`, `CurrentActivity`) to the exclusion list used for the **author-facing conflict/churn UI hash only** (so authors stop seeing false "changed" badges from simulation activity). Must NOT share this method with 6a's overwrite-safety check, which needs the full-fidelity comparison — split into two methods if they currently share one (`ComputeCanonicalHash` for UI, a separate full-fidelity hash/raw-content compare for 6a).

## 9. Findings 3 & 7 — event bloat + Climax permanent nag (largest, cross-cutting, do last)

**Shared root cause:** `RuleResult.NarrativeEvents` is `IReadOnlyList<string>` with no per-item persistence flag; `AdvanceWorldAsync` persists every narrative as a permanent Event.

- **3a** Widen `Data/RuleResult.cs`: add `RuleNarrative(string Text, bool Persist = true)` record; change primary `Narratives` field to `IReadOnlyList<RuleNarrative>`; add a back-compat constructor overload accepting `IReadOnlyList<string>` that wraps each as `Persist: true` (so all ~8 existing rules compile unchanged, defaulting to current behavior) plus a `NarrativeEvents` convenience property (`Narratives.Select(n => n.Text)`) for the existing `SimulatorEvents` surfacing path (which must keep showing ALL narratives this-turn regardless of persistence).
- **3b** Opt 4 specific rules into `Persist: false` for their routine/repeating narratives (real story-beat narratives stay default `Persist: true`):
  - `MemorySalienceDecayRule.cs:52` — the "Memory 'X' is fading" line.
  - `RumorDecayRule.cs` — routine decay lines (check for any real state-transition lines to keep persisting).
  - `QuestStalenessRule.cs:73-79` — per-missed-deadline nag (note: also mints a `RumorCreate` delta — separate from Finding 4's rumor-archival item, not touched here).
  - `PlotThreadEvolutionRule.cs:58` — the Climax "URGENT: ..." re-nag (keep the Active→Escalating / Escalating→Climax transition narratives as `Persist: true`).
- **3c** `Data/CampaignRepository.cs:429-439` `AdvanceWorldAsync` — only call `LogEventAsync` for `simResult.Narratives.Where(n => n.Persist)`; `SimulatorEvents` in the response still returns all narratives unfiltered. `DefaultSimulationEngine`'s aggregation (wherever it builds the combined `SimulationResult`) needs the same `Narratives` plumbing.
- **7-i** Add `IsEngineAuthored` bool to `WorldChange` base class (`Models/WorldChanges.cs`), set once in `DefaultSimulationEngine.RunAsync` right after collecting each rule's `result.Deltas` (single call site, not per-rule). In `CampaignRepository.StageChangesAsync` (~143-151), only clear `PressureCooldowns` for entities touched by non-engine-authored changes — requires tracking a separate `AgentInvolvedEntities` set (vs the existing all-inclusive `InvolvedEntities`) in `WorldChangeDispatcher`'s involvement-tracking loop, checking `!change.IsEngineAuthored` before adding to the agent-only set.
- **7-ii** Climax auto-resolution: add `PlotThread.ClimaxEnteredDay` (nullable int, stamped once on the Active→Climax transition via a new optional field on the `PlotThreadProgress` delta type, consumed by `PlotThreadChangeHandlers`). Add `CampaignConfig.PlotThreadClimaxAutoResolveDays` (default 10). In `PlotThreadEvolutionRule.ApplyAsync`, when a thread has sat at Climax ≥ that many days, emit a `PlotThreadProgress` delta forcing `NewState: Resolved` with a "disaster outcome" narrative (`Persist: true` — real story beat) instead of re-emitting the URGENT nag forever.
- **7-iii** Staleness fix: `PlotThreadChangeHandlers.cs:112-113` — only stamp `thread.LastUpdatedDay = time.TotalDaysElapsed` when `!ptp.IsEngineAuthored` (reuses the 7-i flag), so `PlotThreadStalenessContributor`'s "days since last engagement" metric reflects actual agent engagement, not the engine's own auto-progress ticking.

## 10. Finding 4 — nothing decays to deletion (lightweight caps, not a new subsystem)
- **4a** `MemorySalienceDecayRule.cs` — after existing decay loop, remove non-Core memories that have sat at their floor salience beyond a threshold; cap total `Psychology.Memories` count (e.g. 40) by evicting lowest-salience non-Core entries beyond the cap. Direct dictionary mutation on the already-tracked entity, matching the rule's existing direct-mutation style (no new delta/handler).
- **4b** `Data/ChangeHandlers/CharacterChangeHandlers.cs:647` — `knowledge_update` on a topic already at salience floor should nudge salience up (not just reset `DayAcquired`), so decay tracking stays honest instead of being silently reset.
- **4c** New low-priority `RumorArchivalRule` (`ISimulationRule`, ordered after `RumorDecayRule`) — queries `Forgotten` rumors directly (bypassing `ActiveRumors`, which already excludes them) older than `CampaignConfig.RumorArchiveAfterDays` (default 30) and calls `context.Session.Delete(rumor)` directly, matching `TransientEvictionRule`'s pattern of ad-hoc in-rule queries.
- **4d** `Data/CampaignRepository.cs` `AdvanceWorldAsync` — after simulation runs, cap `Campaign.PressureCooldowns` dictionary size (e.g. 500 entries), evicting oldest-surfaced entries beyond the cap. Periodic sweep (once per advance), not per-commit.

---

## Suggested implementation order
1. #1 (Finding 8) — trivial banner
2. #2 (Finding 10) — trivial DayLogged fix
3. #3 (Finding 11a-e) — 5 independent one-file fixes
4. #4 (Finding 5a/5b) — text fix + real vault path bug
5. #5 (Finding 9) — PressureManager item cap
6. #6 (Finding 1) — PC eviction guard (3 files)
7. #7 (Finding 2) — scene events location filter + legacy fallback
8. #8 (Finding 6a) — re-fetch-before-push mitigation; flag 6b as separate follow-up PR
9. #9 (Findings 3+7) — RuleResult widening, per-rule Persist flags, IsEngineAuthored, Climax auto-resolution, staleness fix — largest, most cross-cutting, do after everything else is stable
10. #10 (Finding 4) — memory/rumor/cooldown pruning — can run in parallel with #9 (minimal file overlap)

## Verification
- `dotnet build` across the solution after each numbered step (RuleResult widening in particular touches ~8 rule files — must compile clean before moving on).
- `dotnet test` if a test suite exists (confirm during implementation).
- Manual MCP tool smoke test via the running server: `character_create` with `isPc: true` and no `keepAlive` → confirm `KeepAlive` is `true` in the stored doc; `advance_world` across a grace-period boundary → confirm the PC is not evicted.
- `get_scene` after logging an event with `locationId` set → confirm it appears in `RecentEvents`.
- Commit a batch with an intentional error in the middle → confirm the response leads with "NO CHANGES WERE SAVED".
- `advance_world` repeatedly on a campaign with several NPCs holding decaying memories → confirm event count growth is bounded (spot-check `EventCategory.Simulation` document count before/after).
- Push an entity from the vault after simulating changes server-side without an intervening Fetch → confirm 6a aborts the push with a clear message instead of overwriting.
