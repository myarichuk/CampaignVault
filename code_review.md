# CampaignVault — Code Review & Viability Assessment

## Overall Impression

This is a **genuinely impressive solo/small-team project**. The core idea — using an MCP server as a persistent, simulation-aware "Co-DM brain" for LLM-driven TTRPG sessions — is original and well-executed. The architecture is clean, the pressure/nag system is clever, and the code quality is far above average for a project at this stage. Most of what follows is genuine bugs and completeness gaps, not cosmetic complaints.

---

## Prioritization

 #  │ File           │ Severity │ Summary
  ────┼────────────────┼──────────┼───────────────────────────────────────────────────────────────────────────────────────────────
   1  │ CampaignRepository.cs │ 🔴 High  │  GetSceneAsync  stamps  LastVisitedDay  on every call — LLM browsing pressures will
prevent
      │                │          │ transient eviction
   8  │ CampaignRepository.cs │ 🔴 High  │  UpsertCharacter  silently drops  KeepAlive  on existing chars — PCs become eviction-
eligible
   9  │ CampaignRepository.cs │ 🔴 High  │ Fallback handler list in constructor is missing all Phase 6 handlers ( location_create ,
      │                │          │ character_create , etc.)
   3  │ ScheduleEvaluationRule.cs │ 🟠 Med   │ Rule ordering wrong — Needs accumulate (Order 10) before Schedule moves NPCs (Order
30),
      │                │          │ moods/morale projections are stale
   11 │ CampaignRepository.cs │ 🟠 Med   │  NpcPresenceSummary.CurrentActivity  falls back to a location ID string — will confuse
the
      │                │          │ LLM
   7  │ CampaignTools.cs │ 🟡 Low   │ Bare  catch {}  swallows all exceptions including DB failures and cancellation
   10 │ CampaignTools.cs │ 🟡 Low   │ "Room with exits but no ambient" pressure fires on almost every room — noisy/unconditional


## Actual Bugs

### 1. `GetSceneAsync` writes `LastVisitedDay` on pure read paths — **silent data corruption risk**
**File:** [`CampaignRepository.cs` L323](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Data/CampaignRepository.cs#L323)

```csharp
location.LastVisitedDay = time.TotalDaysElapsed;
```

This line mutates the entity on every `get_scene` call — including calls made with `saveChanges: false`. However, `GetScene` in the tool calls `ExecuteAsync` with `saveChanges: true` (the default). That means every call to `get_scene` modifies the location document, even when the LLM is just browsing or checking pressures. Worse: the `TransientEvictionRule` uses `LastVisitedDay` as its eviction signal. If the LLM repeatedly calls `get_scene` on a location the party has long since left (e.g. to check pressures), it will keep resetting the day and prevent transients from ever being evicted from that location.

**Fix:** Stamp `LastVisitedDay` only when an explicit "player visit" flag is passed, or move the stamp into a dedicated `visit_location` mutation/handler, or simply accept it as a semantic choice and document it clearly.

---

### 2. `GetCharacterPressureAsync` uses `LoadStartingWith` — **not campaign-scoped**
**File:** [`CampaignRepository.cs` L149](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Data/CampaignRepository.cs#L149)

```csharp
var characters = await session.Advanced.LoadStartingWithAsync<Character>(prefix);
```

`prefix = "chars/"` is hardcoded. This loads every character across every campaign. The `campaignName` parameter is accepted but immediately discarded (`_ = ResolveCampaign(campaignName)`). In a multi-campaign database this will produce pressure items for NPCs from completely different campaigns. There's even a comment acknowledging the scoping gap, but it's left as a known issue rather than an active guard.

---

### 3. `ScheduleEvaluationRule` has `Order = 30`, but `NeedsAccumulationRule` has `Order = 10`
**File:** [`ScheduleEvaluationRule.cs` L24](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Data/ScheduleEvaluationRule.cs#L24), [`NeedsAccumulationRule.cs` L17](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Data/NeedsAccumulationRule.cs#L17)

Needs accumulate *before* location/activity is evaluated by the schedule rule. This means an NPC's need deltas are computed against their *stale* location — before the schedule moves them. The mood/morale projections in `NeedsAccumulationRule` also use the pre-schedule location, so an NPC at their "home" might get tiredness from a "march" routine that hasn't fired yet. The order should almost certainly be reversed: Schedule first (Order 10), then Needs (Order 20+).

> **Note:** Phase 7 plan proposes `TravelEncounterRule` at Order 25, which would land between these two broken ones — worth fixing the existing ordering first.

---

### 4. `AdvanceWorldAsync` applies simulation deltas via `StageChangesAsync` which calls `DispatchAsync` with the same session — then `SaveChangesAsync` is called once by `ExecuteAsync` — **but simulation's `LogEventAsync` is also called inside, with the same session**
**File:** [`CampaignRepository.cs` L387-L401](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Data/CampaignRepository.cs#L387)

The simulation logs narrative events *and* stages deltas in the same session. The session isn't saved between these two. This is fine in practice under RavenDB's unit-of-work model, but if `StageChangesAsync` throws after several events have been stored in session but before `SaveChanges`, you get a half-committed simulation tick. There's no rollback/compensation. For a hobby project this is acceptable, but worth documenting as a known limitation.

---

### 5. `SelectCampaign` auto-creates a campaign with `RulesetSystem.Dnd5e` as default — silently
**File:** [`CampaignTools.cs` L921](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Tools/CampaignTools.cs#L921)

```csharp
await GetOrCreateCampaignMetaAsync(session, normalized, RulesetSystem.Dnd5e, forceLock: false);
```

If you select a non-existent campaign, it silently creates one defaulting to D&D 5e. This is a footgun: an LLM that typos a campaign name will silently create a brand new, empty D&D 5e campaign and then get confused about why the world is empty. The response message does tell you it was created, but the LLM may not surface that to the user.

---

### 6. `GetSceneAsync` fallback `Take(200)` full table scan
**File:** [`CampaignRepository.cs` L233](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Data/CampaignRepository.cs#L233)

```csharp
var recentChars = await session.Query<Character>().Take(200).ToListAsync();
```

When the `Character_Search` index hasn't caught up (common during tests, but can happen briefly in production too), this falls back to loading 200 characters into memory and doing client-side LINQ. The TODO comment acknowledges this but doesn't guard it. At 200+ characters this is a correctness problem (some characters may be missed), and at scale it's a performance hazard on every `get_scene` call.

---

### 7. Empty `catch {}` in GetScene pressure check swallows all exceptions silently
**File:** [`CampaignTools.cs` L257](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Tools/CampaignTools.cs#L257)

```csharp
catch { /* non-fatal for pressure */ }
```

This swallows *everything* — including `OperationCanceledException`, DB connection failures, and programming errors. At minimum this should be `catch (Exception ex) { _logger.LogWarning(ex, "..."); }`. As written you will never know if the parent-location reverse-link check is broken.

---

## Design / Completeness Issues

### 8. `UpsertCharacter` doesn't copy `KeepAlive`
**File:** [`CampaignRepository.cs` L497-L510](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Data/CampaignRepository.cs#L497)

The manual property-by-property upsert for existing characters explicitly copies most fields, but **`KeepAlive` is missing**. If you call `UpsertCharacter` on an existing PC with `keepAlive: true`, the flag will silently be lost (defaults to `false`), and the PC will become eligible for `TransientEvictionRule` GC on the next `advance_world`.

---

### 9. `CampaignRepository` constructor handler fallback is self-described as "brittle as fuk"
**File:** [`CampaignRepository.cs` L47](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Data/CampaignRepository.cs#L47)

```csharp
if (handlersList.Count == 0) //TODO: consider what should be done here - this is brittle as fuk
```

The new Phase 6 handlers (`LocationChangeHandlers`, `CharacterChangeHandlers`, `ItemChangeHandlers`, `RulesetActionHandler`, `ScheduleChange`) are **not in this fallback list**. So if `CampaignRepository` is constructed via DI with an empty handler list, or via the convenience single-arg constructor, the new `location_create`, `character_create`, `item_create`, and `schedule_change` mutations will silently fail with "WARNING: Unhandled change type." The tests that use the convenience constructor may pass (because they avoid those types) but real LLM sessions via DI would hit this.

---

### 10. `GetSceneAsync` pressure for "Room with exits but no ambient" fires unconditionally — noisy nag
**File:** [`CampaignTools.cs` L271-L277](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Tools/CampaignTools.cs#L271)

```csharp
if (loc.Exits.Count > 0 && loc.Type == LocationType.Room && string.IsNullOrWhiteSpace(loc.AmbientCrowd))
```

This pressure fires on **every single room that has exits**, which is almost all rooms. Dungeon corridors, locked vaults, and attic passages will all nag about missing ambient. This is almost certainly unintentional noise — the condition probably should also require `PresentNPCs.Count == 0` and `PointsOfInterest.Count == 0` (mirroring the flavor vacuum check above it) to avoid double-firing with the next nag.

---

### 11. `NpcPresenceSummary` shows `CurrentActivity` falling back to `Schedule.DefaultLocationId`
**File:** [`CampaignRepository.cs` L306](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Data/CampaignRepository.cs#L306)

```csharp
CurrentActivity: npc.CurrentActivity ?? npc.Schedule?.DefaultLocationId,
```

The fallback is a **location ID**, not an activity string. An LLM seeing `CurrentActivity: "locations/rusty-nail"` for a character is confusing and incorrect. Should fall back to `"Idle at default location"` or similar.

---

### 12. `SimulationContext` NPCs list is only scheduled NPCs — transients are invisible to rules
**File:** [`CampaignRepository.cs` L369](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Data/CampaignRepository.cs#L369)

```csharp
var npcs = await session.Query<Character>().Where(x => x.Schedule != null).ToListAsync();
```

Only characters with a `Schedule` are passed to `SimulationContext`. `TransientEvictionRule` has its own internal query (it doesn't use `context.ScheduledNpcs`), which is correct. But future rules like `FactionEcosystemRule` or `QuestStalenessRule` that need to check *all* characters (including unscheduled ones) will silently miss transients if they rely on `context.ScheduledNpcs`. This is a design gap to document and potentially fix before Phase 7 rules land.

---

### 13. `AdvanceWorldAsync` ignores `simResult.WorldPressure` entirely
**File:** [`CampaignRepository.cs` L404-L410](file:///C:/Users/myarichuk/source/repos/CampaignVault/src/CampaignVault/Data/CampaignRepository.cs#L404)

```csharp
// WorldPressure from the engine can be surfaced by the caller (AdvanceWorld tool) if desired.
// For now we keep AdvanceResult focused on time + narratives
```

The `SimulationResult` already has a `WorldPressure` list, and `DefaultSimulationEngine` already collects it (including a basic rumor pressure). But `AdvanceWorldAsync` silently drops it. The `AdvanceWorld` tool then returns `result.SimulatorEvents` as pressure — which are the narrative strings, not the structured WorldPressure items. These are two different things, and the naming conflation could cause the LLM to see generic "rumor is circulating" messages where it should see actionable fix-JSON.

---

## Project Viability — Honest Assessment

### The Good

**The core concept is genuinely viable** for its stated use case: an LLM-driven TTRPG Co-DM assistant. The key insight — that LLMs need an external state machine precisely *because* they can't reliably maintain world state across long sessions — is correct, and the pressure/nag system with copy-pasteable JSON is a genuinely clever solution to the LLM laziness problem. This is not a toy.

The architecture is also **remarkably well-suited for evolution**. RavenDB's document model and session unit-of-work pattern map perfectly onto the "commit a batch of mutations" gameplay loop. The `ISimulationRule` / `IWorldChangeHandler` plugin pattern means Phase 7 features slot in cleanly without touching the core. The `WorldChangeDispatcher` pre-load pattern is correct and efficient.

### The Honest Concerns

**1. The multi-campaign isolation is incomplete — and that matters.**
Almost all queries (`GetCharacterPressureAsync`, `AdvanceWorldAsync`, `ScheduleEvaluationRule`, `NeedsAccumulationRule`) are globally scoped across all campaigns. The code acknowledges this repeatedly in comments. For a single-campaign deployment this is fine. For multiple campaigns (the feature exists!), you'll get NPCs from Campaign A showing up in pressure lists for Campaign B, and simulation rules will advance the needs of all NPCs from all campaigns simultaneously. This needs to be resolved before the multi-campaign feature is considered usable.

**2. The `get_scene` write-on-read is architecturally awkward.**
The fact that reading a scene causes a DB write (stamping `LastVisitedDay`) makes the read path stateful and expensive in ways that are hard to reason about. It also creates a footgun with the eviction rule (bug #1 above). Separating "visit" from "observe" — even if just via a parameter — would clean this up significantly.

**3. The LLM compliance rate is the largest unknown.**
The entire system is predicated on the LLM reliably reading and acting on `WorldPressure` items. In practice, LLMs with context pressure (long sessions, many tools, complex narrative) will increasingly ignore the JSON in WorldPressure — exactly the scenario you're trying to protect against. The pressure system is the right idea, but its effectiveness degrades precisely when the world is most complex and the LLM most needs it. The Phase 7 pressure cap (≤5 items) helps, but you should also think about **priority escalation into the main `Summary` field** for critical structural warnings (`ENGINE WARNING` tier), so they can't be buried in WorldPressure arrays that the LLM deprioritizes.

**4. RavenDB as the backing store is a double-edged sword.**
RavenDB's document model, change tracking, and index system are genuinely excellent for this use case. But it adds operational complexity for anyone self-hosting (RavenDB server process, index management, Embedded in the Dockerfile). If you ever want to make this widely distributable, a SQLite-backed alternative (via EF Core or Dapper with a repository abstraction) would dramatically lower the barrier to entry. The `IDocumentStore` abstraction is theoretically swappable, but RavenDB-specific APIs (`LoadStartingWith`, `Advanced.AsyncDocumentQuery<T, TIndex>`, optimistic concurrency via `OptimisticConcurrencyMode.Writes`) are used throughout, making a swap non-trivial.

**5. The test coverage has gaps in exactly the wrong places.**
Combat, serialization, and the roll service are well-tested. But `GetSceneAsync`, `GetWorldState`, and `AdvanceWorldAsync` — the three tools the LLM will call most — have minimal test coverage (the test file for tools is 2.7KB with essentially a stub). This is where bugs will bite in production sessions. The `SimulationHarness` is a good start but needs more deterministic scenario coverage before Phase 7 lands.

### Verdict

**Highly viable as a personal/community tool.** The engine already works, the philosophy is sound, and the code quality is good enough to build on confidently. The bugs above are real but none are showstoppers for single-campaign use.

**Not quite ready for multi-campaign production or wide distribution** without: fixing the campaign scoping gaps, separating read/write paths in `get_scene`, and adding a SQLite option or at minimum a simpler deployment story.

The Phase 7 plan (phase7.md) is well-targeted at the right problems. Fix bugs #1, #8, and #9 first — they're the most likely to cause silent data corruption in actual play sessions.
