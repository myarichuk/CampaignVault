# Phase 6: Open-World & Transient Architecture Design (Revised)

**Status:** Authoritative design for Phase 6. Supersedes the initial sketch in this file and the high-level bullets in `detailed-implementation-plan.md`.

**Date:** 2026 (refined after codebase review)

---

## Executive Summary

Phase 6 transforms CampaignVault from a "room-to-room" state tracker into a true open-world "Schrodinger's World" engine. 

**Core idea:** 95%+ of a TTRPG world is ephemeral flavor that should live only in the LLM's narrative context. Only *meaningful* interactions are anchored into the persistent store via the `commit` tool. The engine then takes responsibility for the two things LLMs are worst at:

1. Maintaining structural invariants (map connectivity).
2. Performing boring maintenance (garbage collection of one-off NPCs).

It does this via:
- **Opt-in persistence** for Locations, Characters (NPCs), and Items.
- **Engine-enforced auto-linking** and granular update helpers in new `commit` mutation types.
- **Engine-driven GC** (a pluggable `ISimulationRule`) that silently evicts "transient" entities from live scenes when the party leaves an area.
- **Immediate "Co-DM" WorldPressure nags** injected into `get_scene` responses (the exact moment the LLM is looking at the broken/incomplete state). These contain *copy-paste-ready* `commit` examples.

**Key improvements over the original draft:**
- Fully specifies the new `WorldChange` DTOs with rich LLM-facing descriptions.
- Adds the missing `character_create` / `item_create` / `schedule_change` surface (runtime creation was impossible via `commit` before).
- Introduces `KeepAlive` on Character so PCs and important entities can be protected without forcing a full `Schedule`.
- Hardens visit tracking and GC (no reliance on every `get_scene` being a "real" player visit; clear policy + 1-day threshold).
- Requires (and details) non-trivial infrastructure work: preloading `Location`s in the dispatcher, mutable registration for intra-batch create-then-mutate, `ChangeContext` extension.
- Makes `get_scene` on a hallucinated location a *success* with a stub + loud pressure (instead of exception).
- Ties GC to `AdvanceWorld` via a proper rule that *emits* `ActivityChange` deltas (uniform application + audit trail).
- Mandates updates to all prompt/help surfaces and adds concrete anti-laziness tests.
- Addresses transient items, nag fatigue, ID conventions, multi-campaign notes, and same-batch atomicity.

The result: the LLM can be "lazy" (or exploratory, or context-starved) and the world model still stays healthy and connected.

---

## 1. Goals and Success Criteria

- **Scalability:** A market with 50 transient sailors + 30 flavor PoIs must not create 80 permanent documents. After the party leaves and one `advance_world`, the live scene for that market must be empty (or repopulated only on demand).
- **Map Integrity:** Creating a cellar via `location_create` with `connectedFromLocationId` must result in *both* directions linked, even if the LLM only supplies the create mutation.
- **No Silent Breaks:** Hallucinated location IDs, dead-end rooms, and empty "ambient crowd" areas must produce immediate, actionable `WorldPressure` on the *next* `get_scene`.
- **LLM Ergonomics:** New mutation types must be self-documenting (rich `[Description]` attributes appear in the MCP schema / `get_help`).
- **Testable Anti-Laziness:** There must be automated tests that deliberately simulate a "lazy LLM" (omits the link step, forgets to spawn crowd, never cleans up) and assert the engine compensated.
- **Backward Compatibility:** Existing `UpsertLocation` / `UpsertCharacter` / `commit` flows for world-building and scheduled NPCs continue to work. `get_scene` on real locations is unaffected except for new optional fields and `IsLocationAnchored`.

---

## 2. The Core Philosophy: "Schrodinger's World" (Refined)

In a real TTRPG session the DM mentions dozens of bakers, cats, carriages, and market-goers. Only a tiny fraction ever receive a name, stats, or return appearance.

**Rule:**
- Everything the LLM narrates is **flavor** (lives only in the current context window) *until* a player meaningfully interacts with it *and* the LLM decides it is worth persisting.
- "Meaningful" = combat, theft, relationship change, discovery that will be referenced again, or the LLM simply likes the NPC and wants it to persist.
- Persistence is performed exclusively by emitting one or more mutations inside a `commit` call.
- The engine owns cleanup and linking.

**Transient vs Persistent Characters (updated definition):**
- A Character is **transient / evictable** if `Schedule == null && !KeepAlive`.
- `KeepAlive = true` (set at creation or later) protects it from GC even without a Schedule. Use for player characters (PCs), major named NPCs the LLM wants to keep "in the world" without a routine, or companions.
- Giving a `Schedule` (via creation or `schedule_change`) is the stronger signal: it also makes the NPC participate in `AdvanceWorld` simulation (needs, routines, etc.).
- PCs should almost always be created with `KeepAlive: true` (and optionally a minimal Schedule for "follows the party" behavior).

**Opt-In for Locations & Items too:**
- New rooms, secret doors, and discovered items start life via `location_create` / `item_create`.
- Pure flavor descriptions ("you see a row of shuttered shops") live only in the returned `Location.PointsOfInterest` list or the narrative. They never become documents unless the LLM later promotes one with a create.

---

## 3. Data Model Extensions

### Location.cs additions

```csharp
public List<string> PointsOfInterest { get; set; } = [];
// Lightweight, LLM-authored flavor notes returned in get_scene.
// Example: ["A toothless blacksmith hammering horseshoes", "A dirty tavern with a singing sailor outside"].
// Never creates sub-documents. Pure narrative hints.

public string? AmbientCrowd { get; set; }
// Hint for expected population when empty. Used by Ghost Town pressure and LLM prompting.
// Example: "8-15 rough sailors and dockworkers", "A single nervous scribe".

public int? LastVisitedDay { get; set; }
// Campaign day (matching CampaignTime.TotalDaysElapsed) of last real or exploratory visit.
// Set automatically by the engine on GetSceneAsync for anchored locations (see §8).
// Used by TransientEvictionRule. Null = "never visited via get_scene or never stamped".
```

Update `UpsertLocationAsync` (and the tool) to copy the three new fields exactly like `Exits` / `Metadata`.

`LocationType` enum is unchanged.

### Character.cs addition

```csharp
/// <summary>
/// If true, this character is protected from TransientEvictionRule even if Schedule == null.
/// Intended for PCs, recurring major NPCs, and "favorite" transients the LLM wants to keep
/// without giving them a full simulation schedule.
/// </summary>
public bool KeepAlive { get; set; } = false;
```

`TransientEvictionRule` (see §9) only evicts when `Schedule == null && !KeepAlive`.

Add the field to `UpsertCharacterAsync` copy logic and any test seed data.

### Other notes
- No changes to `Item` for Phase 6 (see §11 for transient item handling).
- `CampaignTime.TotalDaysElapsed` remains `int`. `LastVisitedDay` is `int?` for consistency.
- All new fields are safe for `JsonSanitizer` (no Metadata involvement).

---

## 4. WorldChange Extensions — The Laziness-Proof Runtime API

Add these derived types (and the corresponding `[JsonDerivedType]` attributes on the abstract `WorldChange`).

All new types must have excellent `[Description]` text — this becomes part of the MCP tool schema the LLM sees.

### 4.1 location_create (primary laziness countermeasure)

```json
{
  "$type": "location_create",
  "locationId": "locations/tavern_cellar",
  "name": "Dank Cellar",
  "description": "Smells of damp earth and old ale. Crates stacked against the far wall.",
  "type": "Room",
  "parentLocationId": "locations/tavern",
  "connectedFromLocationId": "locations/tavern",
  "connectionDescription": "A wooden trapdoor leading down",
  "pointsOfInterest": ["A suspicious crate", "Rat gnawing on a bone"],
  "ambientCrowd": "2-3 rats and the occasional drunk sleeping it off",
  "exits": []
}
```

**Engine contract (implemented in LocationCreateHandler):**
- Create the document (or fail if ID already exists — warn and do nothing).
- If `connectedFromLocationId` is supplied:
  - Pre-load guarantees the parent is available (see §5).
  - Append a forward `LocationExit` to the *parent's* `Exits` (idempotent).
  - Append a reverse `LocationExit` to the *new location's* `Exits` using a derived description: `"Leads back toward {parent.Name} ({connectionDescription})"`.
  - If `parentLocationId` not supplied on the create, default it to `connectedFromLocationId` (common case for sub-rooms).
- The LLM only has to name the connection once. The map cannot be left half-linked by forgetfulness.
- If the parent is not found (or not yet committed in this batch), still create the child as an orphan + emit warning in `CommitResult.Summary`. Dead-end pressure will fire on next `get_scene`.

### 4.2 location_update (for runtime fixes and incremental discovery)

```json
{
  "$type": "location_update",
  "locationId": "locations/tavern_cellar",
  "addExit": { "targetLocationId": "locations/sewer_tunnel", "description": "A crack in the wall barely wide enough for a halfling" },
  "addPointOfInterest": "A loose brick that looks out of place",
  "ambientCrowd": "Silent except for dripping water"
}
```

Supported granular ops (all optional; multiple can be combined):
- `name`, `description`, `parentLocationId`
- `addExit` (LocationExit object — appended if target not already present)
- `removeExitTarget` (string target id)
- `addPointOfInterest` (string — appended if not duplicate)
- `ambientCrowd` (set or clear with null)

`location_update` is the encouraged way for the LLM to fix a "Dead End" pressure or to record a newly found passage without a full upsert.

### 4.3 character_create (required for runtime transients)

```json
{
  "$type": "character_create",
  "characterId": "chars/cloaked_figure_42",
  "name": "Cloaked Figure",
  "notes": "Approached the party in the market, offered a map for 5 silver.",
  "currentLocationId": "locations/market_square",
  "currentActivity": "Watching the crowd nervously",
  "keepAlive": false,
  "schedule": null,
  "psychology": { "wants": ["to sell the map and disappear"], "fears": ["being recognized"] }
}
```

- `keepAlive: false` (default) + no `schedule` → pure transient, subject to GC.
- Supply `schedule` at creation time to birth a persistent NPC directly.
- Supply `keepAlive: true` for PCs or important flavor that must survive area changes.
- Handler sets full defaults for `Needs`, `Social`, `SystemStats` etc. if not provided.
- After create, the new character is immediately visible to `get_scene` (via `CurrentLocationId` query path) and to subsequent mutations in the *same* batch (see registration in §5).

### 4.4 schedule_change (promotion / demotion)

```json
{
  "$type": "schedule_change",
  "characterId": "chars/cloaked_figure_42",
  "schedule": {
    "defaultLocationId": "locations/market_square",
    "routines": [ { "condition": "Any", "locationId": "locations/market_square", "activity": "Haggling", "probability": 1.0 } ]
  }
}
```

- Setting a non-null `schedule` promotes the character: it now participates in simulation and is ignored by GC forever.
- Setting `schedule: null` demotes (rare; mostly for testing or story "the NPC loses their mind and becomes a drifter").
- Can be used on an existing transient or a world-built NPC.

### 4.5 item_create (for discovered / looted / generated items at runtime)

```json
{
  "$type": "item_create",
  "itemId": "items/rusty_locket_19",
  "name": "Rusty Locket",
  "description": "Contains a tiny portrait of a young woman. The back is engraved 'E.V.'",
  "holderId": "locations/tavern_cellar",
  "tags": ["quest", "clue"],
  "properties": { "value": 5, "material": "silver" }
}
```

- Creates the item document and sets initial `HolderId` (location, character, or container item).
- The item will appear in `VisibleItems` of `get_scene` for that holder on the next call.
- Follow with `item` (transfer) changes in later commits as normal.
- Same-batch create + transfer on the new item is supported via registration.

**Update sites that must list the new types:**
- `WorldChanges.cs` polymorphic attributes and the big comment on `Commit` tool.
- The long help string inside `GetHelp()`.
- `recommended-system-prompt.md`.
- Any external "DM manual" docs.

---

## 5. Engine Infrastructure Changes (Mandatory Foundations)

### 5.1 WorldChangeDispatcher pre-loading

Extend the id collection phase (the big `switch` before loading):

```csharp
HashSet<string> locationIds = new();
...
case LocationCreate lc:
    if (!string.IsNullOrEmpty(lc.ConnectedFromLocationId))
        locationIds.Add(lc.ConnectedFromLocationId);
    // deliberately do NOT add lc.LocationId — it is a create
    break;
case LocationUpdate lu:
    locationIds.Add(lu.LocationId);
    break;
```

After loading characters + items:

```csharp
var locations = session != null
    ? (await session.LoadAsync<Location>(locationIds)).ToDictionary(...)
    : new Dictionary<string, Location>();
```

Pass `locations` into both `ChangeContext` construction paths.

### 5.2 ChangeContext extension

- Store private mutable dictionaries internally.
- Expose:
  ```csharp
  public IReadOnlyDictionary<string, Location> Locations { get; }
  ```
- Add:
  ```csharp
  internal void RegisterNewCharacter(Character c) => _characters[c.Id] = c;
  internal void RegisterNewLocation(Location l) => _locations[l.Id] = l;
  internal void RegisterNewItem(Item i) => _items[i.Id] = i;
  ```
- Update both constructors (real + test-only) and all call sites inside `WorldChangeDispatcher`.
- Existing handlers are unaffected (they only see Characters/Items today).

This enables:
- `location_create` to mutate a pre-loaded (or same-batch registered) parent.
- Any create + follow-up mutation (e.g. `need` on a just-created char, `item` transfer on a just-created item) inside one atomic `commit`.

### 5.3 Create handler contract for new entities

In `CharacterCreateHandler`, `LocationCreateHandler`, `ItemCreateHandler`:
1. Check for pre-existence (context dict + `await session.LoadAsync` fallback).
2. If exists → warning + failure (tell LLM to use the right tool/type).
3. Construct with full defaults.
4. `await context.Session.StoreAsync(newEntity)`.
5. `context.RegisterNewXxx(newEntity)`.
6. For `LocationCreate` only: perform the auto-link logic against the (now guaranteed present) parent in `context.Locations`.

---

## 6. Handler Behavior Details (Selected Highlights)

**LocationCreateHandler auto-link (pseudocode):**

```csharp
if (create.ConnectedFromLocationId is string from &&
    context.Locations.TryGetValue(from, out var parent) && parent != null)
{
    // forward
    parent.Exits.AddOrReplace(new LocationExit(create.LocationId, create.ConnectionDescription!));
    parent.LastUpdated = now;

    // reverse on child (the object we're building)
    if (!newLoc.Exits.Any(e => e.TargetLocationId == from))
    {
        var rev = $"Leads back toward {parent.Name} ({create.ConnectionDescription})";
        newLoc.Exits.Add(new LocationExit(from, rev));
    }
    if (string.IsNullOrEmpty(newLoc.ParentLocationId))
        newLoc.ParentLocationId = from;
}
else
{
    summary.Add("WARNING: connectedFrom not found — auto-link skipped. Dead-end pressure will appear on next get_scene.");
}
```

**Transient definition used by GC and create guidance:** `Schedule == null && !KeepAlive`.

---

## 7. GetSceneAsync — Contract Change (No More Exceptions for Hallucinations)

- `GetSceneAsync` **must never throw** `KeyNotFoundException` for a missing `locationId`.
- On missing: synthesize a minimal stub `Location` (Id preserved, clear "unanchored" name/description, no exits, no PoIs), set `scene.IsLocationAnchored = false`, return a valid `SceneView`.
- For real locations:
  - Load time early.
  - If `LastVisitedDay != current.TotalDaysElapsed`, stamp it (the tracked location object will be saved because the *tool* will call SaveChanges).
  - Proceed with normal sub-locations, NPC queries (schedule + CurrentLocationId), items, events, presence summaries, etc.
- Return type remains `Task<SceneView>` (no tuple). Structural pressures are *not* stored on the view; they are computed in the tool layer from the returned data + the `IsLocationAnchored` flag (see §8). This keeps repo tests simple.

Existing direct calls in `DeterministicScenarios.cs` etc. continue to compile and will now receive stubs instead of exceptions (update the one test that asserted the throw).

---

## 8. Tool Layer — GetScene Pressure Wiring + Visit Side-Effect

In `CampaignTools.GetScene`:

```csharp
return ExecuteAsync(async session => {
    var scene = await _repository.GetSceneAsync(session, locationId, effective);

    var pressures = new List<string>();
    var loc = scene.Location;

    if (!scene.IsLocationAnchored || loc.Id != locationId)
    {
        pressures.Add(
            $"ENGINE WARNING: You requested '{locationId}' but it does not exist in the database! " +
            "You are hallucinating. Use the `commit` tool immediately:\n" +
            "[\n  {\n    \"$type\": \"location_create\",\n    \"locationId\": \"" + locationId + "\",\n    " +
            "\"name\": \"...\",\n    \"description\": \"...\",\n    \"connectedFromLocationId\": \"...\",\n    " +
            "\"connectionDescription\": \"...\"\n  }\n]");
    }
    else
    {
        if (loc.Exits.Count == 0 && loc.Type != LocationType.Region)
        {
            pressures.Add(
                $"ENGINE WARNING: This location has no Exits. The players are soft-locked. " +
                "Use `location_update` to add an exit back:\n" +
                "[ { \"$type\": \"location_update\", \"locationId\": \"" + loc.Id + "\", " +
                "\"addExit\": { \"targetLocationId\": \"locations/previous_area\", \"description\": \"...\" } } ]");
        }
        if (scene.PresentNPCs.Count() == 0 && !string.IsNullOrWhiteSpace(loc.AmbientCrowd))
        {
            pressures.Add(
                $"NARRATIVE PROMPT: This location is currently empty, but expects '{loc.AmbientCrowd}'. " +
                "Consider spawning flavorful transient NPCs via `character_create` + `commit`.");
        }
    }

    return new ToolResult<SceneView>(true, scene,
        $"Scene details for {locationId}...",
        WorldPressure: pressures.Count > 0 ? pressures.ToArray() : null);
}, saveChanges: true);   // IMPORTANT: enables LastVisitedDay stamp performed inside GetSceneAsync
```

- `saveChanges: true` is now required for the visit-stamp side effect. This is a deliberate, cheap write on the read path (only mutates one `int?` field when the day actually changed).
- Pure world-builder or test paths that want to avoid the stamp can continue using the repository directly (or we can add an optional `recordVisit: bool` param later if needed).

---

## 9. Transient Auto-Garbage Collection (Engine GC)

Implemented as a new `ISimulationRule`:

**TransientEvictionRule**
- `Name = "Transient NPC Eviction (anti-bloat)"`
- `Order = 100` (after ScheduleEvaluationRule, Needs, RumorDecay, StatusExpiry)
- In `ApplyAsync`:
  1. Query candidates: `Schedule == null && CurrentLocationId != null && !KeepAlive`. Take a safety limit (200).
  2. Collect unique `CurrentLocationId`s, `LoadAsync` the `Location` docs.
  3. For each candidate whose location has `LastVisitedDay == null || (now.TotalDaysElapsed - LastVisitedDay > 1)`:
     - Emit an `ActivityChange { CharacterId = c.Id, newLocationId = null, newActivity = "drifted away / area has quieted since the party left", reason = "Engine transient eviction — location unvisited for >1 campaign day" }`
     - Emit a short narrative: `"{c.Name} is no longer present in {loc.Name} (the area has gone cold)."`.
  4. Return `new RuleResult(narratives, deltas)`.

Because the deltas are `ActivityChange`, they flow through the normal post-simulation `StageChangesAsync` path (pre-load, handler, summary, clamping, optimistic concurrency). The eviction is therefore:
- Audited in `CommitResult` / events.
- Consistent with everything else the LLM does.
- Visible in the `SimulatorEvents` returned by `advance_world`.

`AdvanceWorldAsync` itself needs almost no change — the rule just participates like any other.

**Policy decisions codified here:**
- We *clear position* (`CurrentLocationId = null`), we do **not** delete the Character document in Phase 6. Dormant transient docs are cheap to ignore (queries already filter them) and preserve narrative history / allow later promotion by ID.
- Items that had `HolderId` pointing at an evicted transient are left dangling for now. LLM can transfer them later if desired (or a future rule can re-home them to the location). This is acceptable bloat for Phase 6.
- GC only runs during `advance_world` (time passage). If the party never advances time, flavor NPCs can linger — this matches "the world only moves on when you rest/travel".

---

## 10. Exact WorldPressure Message Templates (Co-DM Voice)

All messages should be concise, start with `ENGINE WARNING:` or `NARRATIVE PROMPT:`, and contain a ready-to-paste JSON array for the exact `commit` call.

(Exact wording lives in the implementation in `CampaignTools.GetScene`; the design doc gives the canonical versions above in §8.)

Additional global pressures (rumors, character distress) continue to be produced by `GetWorldState`, `AdvanceWorld`, and `GetCharacterPressureAsync` as before.

---

## 11. Transient Items & Other Edge Cases

- **Items created at runtime:** Use `item_create` (new). They behave like any other item afterward.
- **Items "left behind" by evicted transients:** Left attached to the (now position-less) character document. Not ideal, but rare in practice and recoverable. A future `RehomeOrphanedItemsRule` can be added.
- **Locations created but never visited:** Their `LastVisitedDay` stays null. Any transients placed in them will survive until the first `get_scene` (which stamps) + later departure + advance.
- **Exploratory get_scene calls warming areas:** Documented limitation. A 1+ day threshold + the fact that serious time advances usually accompany real travel makes accidental long-term pollution unlikely. If it becomes a problem we can add `recordVisit: false` or a separate `touch_visit` mutation.
- **Multi-campaign:** All new queries/loads in handlers and the GC rule must eventually respect `CampaignDocumentKeys` + campaign filtering (existing comments already flag this as a broader debt). Phase 6 does not block on full namespacing but calls it out as a prerequisite for truly massive multi-world deployments.

---

## 12. Documentation & Prompt Surface (Non-Negotiable)

The following **must** be updated as part of the phase (otherwise the LLM has no idea the new safe patterns exist):

1. `src/CampaignVault/Tools/CampaignTools.cs` — the giant description on the `Commit` tool + the `GetHelp()` manual text. Add a new section "Open-World & Transient Patterns (Phase 6)".
2. `docs/recommended-system-prompt.md` — add a paragraph on "Use location_create / character_create for discoveries. Treat ENGINE WARNINGs in get_scene responses as mandatory high-priority directives."
3. `README.md` and any architecture overview — mention the new philosophy and tools.
4. This design doc itself (self-reference).

Add 2–3 copy-paste "lazy-safe" examples to the help text.

---

## 13. Implementation Roadmap (Suggested Order)

1. **Infra (no behavior change yet)**
   - Add `KeepAlive` + the three new Location fields to models + upserts + sanitizers (noop).
   - Refactor `ChangeContext` (private mutable dicts + `RegisterNew*` + `Locations` property).
   - Extend `WorldChangeDispatcher` pre-load + context construction for locations.
   - Add the five new `WorldChange` classes + polymorphic attributes (no handlers yet).

2. **Handlers + registration**
   - Implement the five new `*Handler` classes (Location* first — the auto-link is the star feature).
   - Register them in `Program.cs` (after the existing ones).
   - Update `WorldChanges.cs` supported-type lists and all descriptive strings.

3. **GetScene contract + pressure**
   - Modify `CampaignRepository.GetSceneAsync` (no-throw stub path + visit stamping).
   - Add `IsLocationAnchored` to `SceneView`.
   - Wire pressures + `saveChanges: true` in `CampaignTools.GetScene`.
   - Update the one test that asserted the old throw.

4. **GC rule**
   - Implement `TransientEvictionRule`.
   - Register in `Program.cs`.
   - Minor updates to `AdvanceWorldAsync` logging if desired.

5. **Documentation & help**
   - Update every prompt/help string and this design doc.
   - Add "Phase 6" section to any changelog / implementation plan.

6. **Tests (the proof that laziness is defeated)**
   - Handler unit tests (especially auto-link matrix: connected present/missing, reverse created, parent also created same batch).
   - Repo test: `GetSceneAsync` on missing now returns stub with `IsLocationAnchored=false`.
   - New or extended harness test: "LazyLLM" that does a `location_create` *without* supplying `connectedFrom`, asserts the child exists but dead-end pressure appears, then does a `location_update` and pressure disappears.
   - GC test: place 5 transients (KeepAlive=false, no Schedule) in a location with old `LastVisitedDay`, advance 2 days, assert their `CurrentLocationId` cleared and a narrative was emitted. Place one with `KeepAlive=true` and assert it survives.
   - Full loop test via `LlmSimulator` or `HybridStressTests` that creates transients via the new commit types.

7. **Polish**
   - Rate-limit / batch-size guards already exist — verify they cover the new types.
   - Consider a tiny `get_help` expansion or new section.
   - Manual verification: connect an MCP client, watch the schema for the new `$type`s, exercise a cellar creation end-to-end.

---

## 14. Testing & Verification Strategy (Anti-Laziness Focus)

The entire value of Phase 6 is only realized if the engine actually compensates for LLM mistakes. Therefore the test suite must contain *adversarial* scenarios.

- Unit tests for each new handler in isolation (fake `ChangeContext` with pre-populated dicts).
- "Compensation" integration tests that construct a `WorldChange[]` missing the second half of the work and assert the handler + dispatcher still produced a consistent world.
- GC tests that manipulate `LastVisitedDay` and `Schedule`/`KeepAlive` directly on the entities and assert eviction behavior + emitted deltas.
- After every change: `dotnet build`, `dotnet test`, and (if possible) a live MCP client session that exercises `get_scene` on a hallucinated id and sees the exact pressure + example JSON.

Add a `SimulationHarness/LazyLlmScenarios.cs` (or extend existing) whose job is to prove the "defeat laziness" claim.

---

## 15. Addressed Pain Points & Remaining Risks

**Addressed (from prior review):**
- Missing runtime creation surface for characters/items → `character_create` + `item_create`.
- No promotion path → `schedule_change`.
- GetScene threw on hallucination → graceful stub + immediate pressure.
- No Locations in pre-load / context → infra extension + registration.
- Visit stamp would have required writes on every read or been unreliable → explicit `saveChanges:true` + stamp only on real anchored locations + documented limitation.
- GC policy vague + "delete vs null" → clear "null Current only, keep docs" + rule that emits proper deltas.
- Auto-link only one direction or parent missing → bidirectional + graceful warning.
- No KeepAlive → added for PCs.
- Documentation drift → mandatory update list + examples in the doc.
- No adversarial tests → explicit section + new harness requirement.
- Transient items → at least creation path + noted limitation for re-homing.

**Remaining risks / follow-ups (explicitly called out so they don't surprise later):**
- Nag fatigue on repeated identical pressures (mitigation: messages are short + contain the fix; LLM is instructed to treat them as high priority).
- Exploratory `get_scene` calls keeping areas warm (1-day threshold + real play usually involves advances).
- Bulk transient creation in one market (existing commit batch limit of 50 + rate limiter helps; GC will clean later).
- Full multi-campaign entity namespacing (pre-existing debt; new code should at least not make it worse).
- Item limbo after character eviction (acceptable for Phase 6; easy to add a re-home rule later).
- LLM still has to *decide* that a baker is worth a `character_create`. The engine cannot read minds — only protect the world once the decision is made.

---

## 16. Appendices (to be expanded in the implementation PR)

- Full C# source for the five new WorldChange records (with every `[Description]`).
- Example expanded `GetHelp()` section for open-world patterns.
- Recommended system-prompt delta paragraph.
- Sample "Lazy LLM" test code sketch.

---

**This revised design is intentionally over-specified.** The goal is that an implementer (or future LLM coding agent) can follow the sections in order and produce a working, well-tested, LLM-laziness-resistant open world without having to re-invent the edge-case handling.

End of Phase 6 design.
