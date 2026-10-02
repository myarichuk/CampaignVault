# CampaignVault Unity Client — build, run, package

Panel-by-panel usage, roleplay flows, and the security model live in
[`Assets/CampaignVault/README.md`](Assets/CampaignVault/README.md). This file
covers getting the project open, building it, and turning a build into
something you can hand someone else.

## Prerequisites

- Unity **6000.6.3f1** (exact version from `ProjectSettings/ProjectVersion.txt`).
  Install via Unity Hub, or headlessly with the [Unity CLI](https://on.unity.com/unity-cli):
  ```bash
  curl -fsSL https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.sh | UNITY_CLI_CHANNEL=beta bash
  unity install 6000.6.3f1 --module <target module, see below> --yes --accept-eula
  ```
- **.NET SDK** on PATH (`dotnet --version`) — the client embeds its own copy
  of the CampaignVault MCP server via `dotnet publish`, so every desktop
  build shells out to it. No SDK, no embedded-server desktop build.
- Only the **Web** module ships with this checkout's editor install. Building
  Windows or Linux players from this machine needs their modules too:
  ```bash
  unity install-modules 6000.6.3f1 --module windows-mono --yes
  unity install-modules 6000.6.3f1 --module linux-il2cpp --yes
  ```

## Run it in the Editor

1. Open `UnityClient/` in Unity 6000.6.3f1 (or `unity open UnityClient`).
2. Start the server: `dotnet run --project src/CampaignVault` (port 5275) —
   or skip this and use the in-client embedded server (Settings tab).
3. `Assets/Scenes/SampleScene.unity` already has a `VaultClient` GameObject
   (added by `CampaignVault > Bootstrap Main Scene`, see below). Press Play.
4. First run opens the **Setup** wizard (server check → AI provider →
   campaign questionnaire). Providers are profiles (OpenAI, Anthropic,
   OpenRouter, Ollama, LM Studio, or any OpenAI-compatible URL); one is
   active at a time, and campaigns are independent of which model runs them.
   Profiles, keys included, are saved as plain JSON in
   `Application.persistentDataPath/vault-providers.json`. Manage them later
   in Settings, along with server URL, connector and optional bearer token.

If you ever start from a truly empty scene, use
**CampaignVault > Create Client UI** (adds `VaultClient` to the open scene
without hand-editing YAML) or **CampaignVault > Bootstrap Main Scene** (does
the same to `SampleScene.unity` specifically and registers it in Build
Settings — this is also what every build target below runs automatically
before building).

## Build a player

Menu items (`CampaignVault > Build > …`) or CLI, either way they call into
`Assets/CampaignVault/Editor/BuildTools.cs`, which bootstraps the scene,
builds, and writes to `UnityClient/Builds/<Target>/`. Under game-ci
(CI passes `-customBuildPath`), the build honors that path instead, since
game-ci validates the file exists there after Unity exits:

```bash
unity run UnityClient --editor-version 6000.6.3f1 \
  -- -executeMethod CampaignVault.UnityClient.Editor.BuildTools.BuildStandaloneOSX
# or BuildStandaloneWindows64 / BuildStandaloneLinux64 / BuildWebGL
```

Desktop targets (`StandaloneOSX`/`Windows64`/`Linux64`) trigger
`ServerEmbedBuildProcessor` as a pre-build step: it runs
`dotnet publish src/CampaignVault -r <rid> --self-contained` and stages the
output plus the embedding model under
`Assets/StreamingAssets/CampaignVault/Server/<rid>/`, so **the first build
for a given RID is slow** (a full self-contained .NET publish) — this is
normal, not a hang. WebGL builds skip the embed (no subprocesses in-browser)
and only ever point at a hosted server.

Every build (all targets) also runs `DmContentStager` first, which copies the
DM content the chat driver reads into `Assets/StreamingAssets/CampaignVault/`
(all git-ignored; also available as **CampaignVault > Stage DM Prompt +
Skills**):

- `recommended-system-prompt.md` → `system-prompt.md`
- `claude_skills/<name>/SKILL.md` → `skills/<name>/SKILL.md`
- each plugin's sidecar skills, `plugins/<P>/<skillsPath>/<name>/SKILL.md`
  (`skillsPath` from `plugin.json`, default `./skills`) →
  `plugin-skills/<P>/<name>/SKILL.md`. The server never serves plugin skills
  (see `PLUGINS.md`), so the client ships them; the model sees them as
  `<P>:<name>` in its skill index.

Run **one build at a time in the foreground**; the CLI's `--timeout` flag
(not backgrounding) is the right way to bound a slow one, e.g.
`--timeout 600`. If `unity run` sits silently without ever launching an
Editor, check Unity Hub for a sign-in/license prompt — it blocks headless runs.

## Tests

One run at a time, in the foreground (the CLI's `--timeout` bounds a slow one):

```bash
unity test . --mode EditMode --timeout 580    # unit + integration (~70 tests)
unity test . --mode PlayMode --timeout 480    # the real UI, rendered to PNGs
```

- **EditMode** (`Assets/CampaignVault/Tests/EditMode`): parsers, markdown,
  roll verdicts, streaming/SSE, the DM driver against a scripted local
  provider (streamed tool loop, stream fallback, STOP, history hygiene), the
  app layer, and `SmokeScenarioTests`, the full smoke flow headless against
  the staged embedded server on a throwaway DB under the temp dir.
- **PlayMode** (`Assets/CampaignVault/Tests/PlayMode`): hosts the actual UI
  and photographs it into `Library/VaultSnapshots/*.png` (review them after
  UI changes). `TableTests` plays a turn with a real Enter keypress against
  the scratch server and a scripted DM, and STOP must abort in under a
  second. `ShellLayoutTests` covers the breakpoints and a 1,000-segment log.
  The style guide (`UI/Dev/StyleGuide.uxml`) shows every component.
- Integration tests skip themselves when no server is staged for the
  platform, and never touch the player's prefs, provider file or campaigns.

## Smoke-test a build

The player has a built-in end-to-end check (`-vault-smoke`) that runs the same
scenario as `SmokeScenarioTests`, headless through the app layer: MCP
handshake, onboarding, world seed, campaign list, session, sheet, pack,
search, prompt budget, two optional chat turns, delete. It exits 0/1. Point it
at a **scratch** server: it creates and deletes a campaign.

```bash
S=$(mktemp -d)
# a stateful server on a scratch DB (the default mode, which needs the MCP handshake)
(cd Assets/StreamingAssets/CampaignVault/Server/osx-arm64 && \
  ASPNETCORE_ENVIRONMENT=Development MCP_PORT=5399 BEARER_TOKEN= CAMPAIGN_DB_PATH=$S/db ./CampaignVault &)
# optional: any OpenAI-compatible endpoint for the chat turns (a scripted mock works)
Builds/StandaloneOSX/CampaignVaultClient.app/Contents/MacOS/UnityClient \
  -logFile $S/smoke.log -vault-smoke http://127.0.0.1:5399 \
  [-vault-smoke-llm http://127.0.0.1:5401/v1]
grep VaultSmoke $S/smoke.log     # one PASS/FAIL line per check, then RESULT pass=N fail=M
```

Without `-vault-smoke-llm` the chat checks are skipped. The mock scripts
`load_skill` → `search_world` → prose on turn one and reports on turn two
whether earlier tool results arrived compacted (see `SmokeScenarioTests`).
Smoke runs keep prefs in memory, so they never change the player's settings.

## Package for distribution

Unity player builds are not installers — there's no OS-native package format
that "installs itself" the way an MSI or PKG does, and for a small tool like
this you generally don't want one. Ship the build folder, zipped or in a
disk image, and let the user unzip/mount and run:

- **macOS** (`Builds/StandaloneOSX/CampaignVaultClient.app`):
  - Quick share: `ditto -c -k --keepParent CampaignVaultClient.app CampaignVaultClient-macOS.zip`.
  - Nicer: build a `.dmg` (`hdiutil create` or `create-dmg`) with the `.app`
    and an `Applications` symlink.
  - **.NET runtime**: the embedded server is published self-contained, but
    its embedded RavenDB launches through the system `dotnet` (observed:
    `dotnet --fx-version 10.0.x …/Raven.Server.dll`). A machine without the
    .NET 10 runtime can't start the embedded server, so either document that
    prerequisite or make the RavenDB child self-contained before shipping.
  - **Gatekeeper**: an unsigned/unnotarized `.app` gets quarantined, and the
    client copies its own embedded server binary out of the bundle and
    `exec`s it at runtime — a doubly quarantined jump. For anyone outside
    your own Mac, codesign with a Developer ID and notarize
    (`xcrun notarytool`), or expect users to right-click → Open and clear
    the embedded server binary's quarantine bit themselves.
- **Windows** (`Builds/StandaloneWindows64/CampaignVaultClient.exe` + its
  `_Data` folder): zip the whole output folder. If you want a real
  installer, wrap it with [Inno Setup](https://jrsoftware.org/isinfo.php) or
  NSIS — neither is required for personal/team use.
- **Linux** (`Builds/StandaloneLinux64/`): `tar czf` the output folder;
  `chmod +x CampaignVaultClient` on the receiving end if the archive tool
  didn't preserve the bit.
- **WebGL** (`Builds/WebGL/`): this is a static site (`index.html` +
  compressed WASM) — serve it from any HTTP host. It must talk to a
  CampaignVault server you run separately; it cannot embed one.

## Known gotchas already fixed here (context for future changes)

- `ProjectSettings/EditorBuildSettings.asset` must list
  `Assets/Scenes/SampleScene.unity`, and that scene must contain a
  `VaultClientUI`-bearing GameObject: a fresh checkout had neither, so a
  player build shipped zero scenes. `BuildTools.EnsureMainSceneBootstrapped`
  fixes both and runs before every build method above.
- Nothing calls the legacy `UnityEngine.Input` API any more (UI Toolkit reads
  input itself), so `Active Input Handling` could move to Input System only.
  It's still **Both**, which is safe.
- USS `var()` doesn't resolve font asset references: faces are referenced
  with `url()` directly in the sheets.
- `IVisualElementScheduler.Execute(delegate { … })` is ambiguous between
  `Action` and `Action<TimerState>`: use a lambda (`() => …`).
- Batchmode renders frames uncapped. Tests that wait for a transition must
  wait real time, not a frame count, and never `WaitForEndOfFrame` (it
  doesn't resume in batchmode).
- The embedded server writes into its own folder at runtime (`RulesetData/`
  extraction, RavenDB logs), which is why the player copies it to app data
  before launching it. Tests run it in place and remove whatever it adds.

## Layout / design notes

UI Toolkit, dark-fantasy theme. Where things live:

- `Scripts/App/`: the UI-free layer. `VaultAppState` (everything the client
  knows, with change and toast events), `VaultController` (every command, as
  IEnumerators), `VaultBootstrap` (wiring, prefs, autostart, smoke).
- `Scripts/UI/`: views over the app layer. `VaultClientUI` (the root, which
  keeps its old name so scenes still bind), `StoryLogView`, `CommandBar`,
  `PartyViews` (party frames, codex drawer, character sheet), `WorldOverlays`
  (campaign book, onboarding), `SettingsOverlays` (settings, first-run setup,
  F12 inspector), `Layers` (overlays, toasts, tooltips), `Ui` (element factory).
- `UI/Theme/*.uss`: tokens, components, the table, and generated icon classes.
  Every visual decision is in USS; C# only adds `cv-*` classes.
- `UI/Resources/VaultUI/`: `Shell.uxml` (static skeleton) and
  `VaultPanelSettings.asset` (scales from 1920x1080).
- `UI/Fonts`, `UI/Icons`, `UI/Frames`: OFL fonts (Cinzel, EB Garamond,
  JetBrains Mono) as SDF font assets, game-icons.net SVGs imported as vector
  images, and hand-made filigree ornaments. Attribution is in `LICENSING.md`.
  After adding a font, re-run **CampaignVault > Build UI Assets** (or
  `unity run . -- -executeMethod CampaignVault.UnityClient.Editor.VaultUiAssetBuilder.BuildAll`).

The table: top bar (campaign, session, date, place, server and model status),
party frames on the left, the story in the center with the command bar below
it, and the codex drawer on the right (quests, scene, pack, journal). The
campaign book, settings, character sheet, onboarding and setup open as pages
over the table; Esc closes the top one, and F12 opens the inspector. Replies
stream in. Tool activity folds into one strip per turn, and roll cards show
the server's own verdict, criticals included. On narrow windows the codex
floats closed and, narrower still, party frames shrink to crests. Settings >
Table feel turns motion and sound off.

**Token budget** (every model call resends everything, so the chat driver
keeps it small — measured on the first call of a turn):

| | system prompt | tool schemas | first call |
|---|---|---|---|
| before | 95 KB (prompt + all 10 skills) | 18 KB (/play + /build) | ~113 KB |
| now | 9.4 KB (prompt + skill index) | 13 KB (/play + `load_skill`) | ~22.5 KB |

Skills load on demand through a client-side `load_skill` tool; only the
active connector's tools are offered; finished turns are compacted (tool
results → their one-line summary, bulky tool-call arguments elided); history
is capped by size and trimmed a whole turn at a time; the prompt prefix is
byte-stable so providers with automatic prompt caching can reuse it.

Remaining gaps worth knowing about:

- The transcript isn't persisted: restarting the app loses the story view
  (campaign state itself lives on the server).
- Embers are the vignette's warm glow only. A particle layer behind the UI
  document is the obvious next step for motion in the backdrop.
