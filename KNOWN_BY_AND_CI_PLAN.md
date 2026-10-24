# knownBy, Unity release CI, embedded server default

Groomed 2026-10-24. Three workstreams, each independent. Nothing below is implemented yet.

## Findings (what exists today)

**Knowledge model.** Knowledge is stored per NPC, not per fact.
- `MemoryNode` (`src/CampaignVault.PluginSdk/Models/Character.cs`) lives in `Psychology.Memories`. It carries `Source` (Witnessed, Heard, Told, Experienced, Trauma, Conditioned), `SourceEventIds`, `Salience`, `Importance`, `TriggerCondition`.
- `knowledge_update` with `source=Witnessed|Experienced` must cite `sourceEventIds`, enforced in `CharacterChangeHandlers.cs` (~line 1127). `ItemDetail` participants (Caused/Witnessed) push memories automatically.
- `DefaultRelevantMemorySelector` picks an NPC's initiative memories from its own `Memories` only.
- `Event` has `Involved`, `LocationId`, `InitiatorId` but **no audience / knownBy**. `WorldEvent`, `RumorCreate`, `PlotThread` clues have no audience either (clue has "characters who discovered", nothing else).
- Hidden items, exits and traps are engine-gated (`HiddenContent.cs`), which is a different mechanism (checks, not knowledge).

So the inverse index (character -> what they know) exists and is enforced at write time. Fact -> who may know it does not exist, and nothing enforces that an NPC-facing payload excludes facts the NPC has no memory of. That second part is **unverified**, hence Phase K0.

**Release CI.** `.github/workflows/release.yml` already builds the Unity client for Windows, Linux and macOS through GameCI, with the server pre-staged by `tools/embed-server.sh`. It is **not working**:
- All 4 recent Release runs failed (latest `37063289075`, 2026-10-02). Unit and integration jobs inside it pass; all three Unity build jobs fail at `game-ci/unity-builder`.
- Logs show `[Licensing::Client] Error: Code 400 ... Machine bindings don't match` and `Access token is unavailable`, i.e. the `UNITY_LICENSE` secret/activation is the likely root cause. The Linux log shows the build continuing into UnityLinker/Player steps (`ExitCode: 4` on the first incremental player build, then `0`), so the true terminal error is not yet identified. Needs a full log read, not the tail.
- Release is `workflow_dispatch` only. Nothing builds the Unity player on PRs or pushes, and `ci.yml` runs no Unity tests, though `UnityClient/Assets/CampaignVault/Tests/EditMode` and `PlayMode` exist.

**Embedded server.** Already built: `ServerHostManager` + `ServerEmbedBuildProcessor` + `tools/embed-server.sh`.
- Autostart defaults **on** (`PrefKeys.EmbeddedAutostart`, default 1) with a toggle in `ConnectionPages.cs`; only auto-starts when `ServerUrl` is loopback (`VaultController.Server.cs:199`). Desktop only; WebGL and mobile return "not available".
- Known weak spot: the server is self-contained, but embedded RavenDB shells out to `dotnet`, so the host machine needs a .NET runtime on PATH or the server dies at startup (`ServerHostManager.cs` ~165-170 shows an install-from-URL error). That undercuts "runs by default" for non-developers.

## Workstream A: knownBy

- **K0. Audit (read-only).** Trace every payload that reaches an NPC or the DM-for-NPC path (`NpcViews`, `MutationTools.Briefing.cs`, scene presence, recall) and list which can contain a fact the NPC has no memory of. Output: a short table. Decides whether K1+ are a leak fix or a new feature.
- **K1. Audience on facts.** Add `KnownBy` (character ids) plus a visibility scope (public / location-present / private) to `Event`, `WorldEvent`, rumors and plot-thread clues. Default derives from existing data: `Event.Involved` + characters present at `LocationId`; clue discoverers. Backfill by migration default, not by rewriting documents.
- **K2. Query-layer filtering.** NPC-facing reads filter by audience in the query/index (per project convention), not via `[JsonIgnore]` or in-memory strips. RavenDB index on audience + campaign.
- **K3. Propagation.** `Heard`/`Told` memories add the listener to `KnownBy` (rumor spread, conversation). Keep the existing rule that Witnessed/Experienced cite `sourceEventIds`.
- **K4. Prompts + schema.** Expose `knownBy` on the relevant change types in the commit schema and DM help text; update the commit cheat sheet.
- **K5. Tests.** Unit: audience derivation, filter excludes unwitnessed event, propagation on Told. Integration: an NPC briefing for a scene never contains a secret event the NPC did not witness.

Data-oriented note: audience defaults and scope rules should be data (YAML) where reasonable; say why if any stays code.

## Workstream B: Unity release CI that actually works

- **C0. Diagnose. DONE 2026-10-24.** Root cause: the Unity builds *succeed*; GameCI's post-build check rejects them.
  - `unity-builder@v6` is a thin wrapper that downloads the separate `game-ci/cli` binary (`cliVersion` defaults to `latest`, unpinned). The CLI's `UnityBuildValidation.validateBuild` (`src/model/unity/build-validation/unity-build-validation.ts`) passes only if the container output contains the literal `Build succeeded!` (with the exclamation mark), or a `# Build results #` block with `Errors: 0` and a `Size:` line. Otherwise it throws "There was an error building the project", and the action reports `Build failed with exit code 1`.
  - `BuildTools.Build` (`UnityClient/Assets/CampaignVault/Editor/BuildTools.cs`) logs `[Vault] Build ... result=Succeeded`, and GameCI's `build.sh` prints `Build succeeded` (no bang), so neither marker ever appears. Evidence: run `37063289075` Windows log has `Build Finished, Result: Success.`, `[Vault] Build StandaloneWindows64 result=Succeeded size=1268010060B errors=0`, a 1.2 GB output dir, then the error; run `36786775173` shows the identical sequence for macOS.
  - The `Machine bindings don't match` license error is **not** the cause: the `.ulf` secret is rejected, but GameCI falls back to `UNITY_EMAIL`/`UNITY_PASSWORD` and activation succeeds (`Seat ID ...-UnityPersonal`, `Status: [200] SameMachine`). Still worth cleaning up (see C1).
  - Linux/macOS logs in run `37063289075` are cut off mid-UnityLinker (GitHub log truncation, no error text), but the annotation is the same `Build failed with exit code 1`; assumed same cause, confirm after the fix.
  - Older run `36786775173` also had a real compile error (`error CS0234 ... 'OSXStandalone'` in `BuildTools.cs:56` and `ServerEmbedBuildProcessor.cs:128`) on one platform; later runs no longer show it, but check it is gone on current master.
  - Fix (small): have `BuildTools.Build` print `Build succeeded!` after a successful `BuildPlayer`; also pin `cliVersion` in `release.yml` so a new CLI release cannot change the check under us. **Applied 2026-10-24 (uncommitted, untested in CI):** `BuildTools.Build` now logs `Build succeeded!`; `release.yml` pins `cliVersion: v0.1.70` (the version the failing runs used). Verification is a pre-release dispatch (C1).
- **C1. Make a manual Release run go green** for all three targets: apply the C0 fix, pin `cliVersion`, and (user step, needs Unity account) regenerate the `UNITY_LICENSE` secret or drop it and rely on `UNITY_EMAIL`/`UNITY_PASSWORD` so the "Machine bindings" warning goes away. Test with a **pre-release tag** (dispatching Release publishes a GitHub Release on success). Confirm the zips contain the embedded server and boot (`/health`).
- **C2. Unity tests in CI.** Add a job running EditMode (and PlayMode if stable) via `game-ci/unity-test-runner` on PRs touching `UnityClient/**`, path-filtered so .NET-only PRs stay fast.
- **C3. Build smoke on PRs.** Cheapest option: Linux-only player build (or compile-only) on `UnityClient/**` changes so a broken client is caught before release day.
- **C4. Keep one build at a time** per the repo rule: concurrency groups so Unity jobs never overlap.
- **C5. Verification.** A green full run for each of: CI on PR, Release dispatch.

## Workstream C: embedded server on by default, user can turn it off

Already true in code; the work is making it dependable.
- **E1. Confirm the default end to end** on a clean profile: fresh prefs -> server starts on first launch, health check passes, toggle off persists and stops the server, remote URL skips autostart. `EmbeddedServerTests.cs` exists; check what it covers.
- **E2. Remove the `dotnet` prerequisite.** Options: ship a private .NET runtime beside the server (RavenDB embedded points at it), or use RavenDB's server mode with a bundled runtime. Largest item; decide after a spike. Fallback: first-run dialog with the download link and a one-click "use remote server instead".
- **E3. First-run UX.** Surface start failure as a clear card with Retry / Use remote / Open log, not a silent error.
- **E4. Release builds include the server for every shipped RID** (depends on B/C1); add a check in the release job that the staged server directory is non-empty before the Unity build.
- **E5. Mobile.** Out of scope here: the embedded server stays desktop-only; mobile uses the remote option.

## Suggested order

1. C0 (diagnose release failure; unblocks everything shipping) and K0 (audit) in parallel.
2. C1, then E1/E4.
3. K1-K5.
4. C2/C3, E2/E3.

## Open questions

- Is the .NET runtime requirement acceptable for the first public build, or must E2 land first?
- Should Unity tests gate every PR, or only PRs that touch `UnityClient/**`?
