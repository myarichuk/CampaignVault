# CampaignVault — Code Review & Viability Assessment

## Overall Impression

This is a **genuinely impressive solo/small-team project**. The core idea — using an MCP server as a persistent, simulation-aware "Co-DM brain" for LLM-driven TTRPG sessions — is original and well-executed. The architecture is clean, the pressure/nag system is clever, and the code quality is far above average for a project at this stage. Most of what follows is genuine bugs and completeness gaps, not cosmetic complaints.

---

## Prioritization (Updated June 2026)

 #  │ File           │ Severity │ Status   │ Summary
  ────┼────────────────┼──────────┼──────────┼───────────────────────────────────────────────────────────────────────────────────────────────
   1  │ CampaignRepository.cs │ 🔴 High  │ ✅ FIXED  │ GetSceneAsync now uses markVisited parameter.
   8  │ CampaignRepository.cs │ 🔴 High  │ ✅ FIXED  │ UpsertCharacter now copies KeepAlive.
   9  │ CampaignRepository.cs │ 🔴 High  │ ✅ FIXED  │ Fallback handler list now includes all Phase 6 handlers.
   3  │ ScheduleEvaluationRule.cs │ 🟠 Med   │ ✅ FIXED  │ Rule ordering reversed: Schedule (30) runs before Needs (35).
   11 │ CampaignRepository.cs │ 🟠 Med   │ ✅ FIXED  │ NpcPresenceSummary falls back to "Idle at default location".
   7  │ CampaignTools.cs │ 🟡 Low   │ ✅ FIXED  │ Empty catch swallowed exceptions (fixed via Phase 7 refactor).
   10 │ CampaignTools.cs │ 🟡 Low   │ ✅ FIXED  │ Ambient pressure now checks for NPCs/PoIs before firing.
   2  │ CampaignRepository.cs │ 🔴 High  │ ✅ FIXED  │ Scoping hardened: CampaignName filters added to queries.
   5  │ CampaignTools.cs │ 🟡 Low   │ ✅ FIXED  │ SelectCampaign returns error if campaign missing.
   4  │ CampaignRepository.cs │ 🟠 Med   │ ⚠️ KNOWN  │ Simulation transaction rollback (documented limitation).
   6  │ CampaignRepository.cs │ 🔴 High  │ ✅ FIXED  │ GetSceneAsync fallback Take(200) scan removed.
   12 │ CampaignRepository.cs │ 🟠 Med   │ ✅ FIXED  │ SimulationContext now includes transient NPCs.
   13 │ CampaignRepository.cs │ 🟠 Med   │ ✅ FIXED  │ AdvanceWorldAsync now surfaces structured WorldPressure.


## Actual Bugs

### 1. `GetSceneAsync` writes `LastVisitedDay` on pure read paths
**Status:** ✅ FIXED. Added `markVisited` parameter to `GetSceneAsync`. `GetScene` tool now passes `partyPresent` into it.

---

### 2. `GetCharacterPressureAsync` was not campaign-scoped
**Status:** ✅ FIXED. Scoping hardened project-wide. Queries now filter by `CampaignName` where applicable.

---

### 3. `ScheduleEvaluationRule` vs `NeedsAccumulationRule` ordering
**Status:** ✅ FIXED. `ScheduleEvaluationRule` is now Order 30, `NeedsAccumulationRule` is Order 35.

---

### 4. `AdvanceWorldAsync` transactionality
**Status:** ⚠️ KNOWN LIMITATION. Simulation logs and deltas happen in the same session without intermediate save. Acceptable for hobby use.

---

### 5. `SelectCampaign` auto-creates campaign silently
**Status:** ✅ FIXED. Now returns `NotFound` error if the campaign doesn't exist.

---

### 6. `GetSceneAsync` fallback `Take(200)` full table scan
**Status:** ✅ FIXED. Fallback blocks removed. System relies on reliable `Character/Search` index.

---

### 7. Empty `catch {}` in GetScene pressure check
**Status:** ✅ FIXED. Refactored into `PressureManager` and Contributors during Phase 7; empty catch removed.

---

## Design / Completeness Issues

### 8. `UpsertCharacter` doesn't copy `KeepAlive`
**Status:** ✅ FIXED. `KeepAlive` is now explicitly copied during mutation.

---

### 9. `CampaignRepository` constructor handler fallback
**Status:** ✅ FIXED. Fallback list updated to include all Phase 6/7/8 handlers.

---

### 10. `GetSceneAsync` noisy ambient pressure
**Status:** ✅ FIXED. Added checks for `PresentNPCs.Any()` and `PointsOfInterest.Count == 0`.

---

### 11. `NpcPresenceSummary` activity fallback
**Status:** ✅ FIXED. Falls back to `"Idle at default location"`.

---

### 12. `SimulationContext` missing transients
**Status:** ✅ FIXED. Filter `Where(x => x.Schedule != null)` removed from `AdvanceWorldAsync` query.

---

### 13. `AdvanceWorldAsync` ignores `WorldPressure`
**Status:** ✅ FIXED. `AdvanceResult` and `AdvanceWorld` tool updated to surface structured pressure items.
