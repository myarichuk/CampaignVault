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
4. Settings tab: server URL, connector, optional bearer token, BYOK base
   URL + model + API key. Health check first, then Campaigns tab.

If you ever start from a truly empty scene, use
**CampaignVault > Create Client UI** (adds `VaultClient` to the open scene
without hand-editing YAML) or **CampaignVault > Bootstrap Main Scene** (does
the same to `SampleScene.unity` specifically and registers it in Build
Settings — this is also what every build target below runs automatically
before building).

## Build a player

Menu items (`CampaignVault > Build > …`) or CLI, either way they call into
`Assets/CampaignVault/Editor/BuildTools.cs`, which bootstraps the scene,
builds, and writes to `UnityClient/Builds/<Target>/`:

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

## Smoke-test a build

The player has a built-in end-to-end test that drives the real UI (Onboard →
Campaigns → Session → Dashboard → Character → Inventory → Events → two chat
turns → delete) and exits 0/1. Point it at a **scratch** server: it creates and
deletes a campaign.

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

Without `-vault-smoke-llm` the chat checks are skipped. The chat mock used
during development scripts `load_skill` → `search_world` → prose per turn and
asserts earlier turns arrive compacted (see `VaultSmokeRunner.Chat`).

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
  `VaultClientUI`-bearing GameObject — a fresh checkout had neither, so a
  player build shipped zero scenes. `BuildTools.EnsureMainSceneBootstrapped`
  fixes both and runs before every build method above.
- `ProjectSettings` `Active Input Handling` must be **Both** (not
  "Input System Package (New)" only) — `VaultClientUI` wires up the legacy
  `StandaloneInputModule` and calls `Input.GetKeyDown` directly for
  Enter-to-send, both of which throw under Input System-only.
- Unity UI (`UnityEngine.UI`) allows exactly one `Graphic`-derived component
  (`Image`, `Text`, `RawImage`, …) per GameObject. A few `VaultTheme`/panel
  helpers used to call `.AddComponent<RectTransform>()` on a GameObject that
  `VaultTheme.Row`/`Column` (via their required-component layout group) had
  already given one, or add a `Text` onto the same object as an `Image` —
  both throw and abort `BuildShell` mid-construction, which is why the whole
  client rendered blank. Fixed at every call site; if you add a new panel
  helper, use `GetComponent<RectTransform>()` on anything that already came
  from `Row`/`Column`/`PanelBox`, and give a button/card's label its own
  child GameObject rather than sharing the background's.

## Layout / design notes

The UI is entirely code-built `UnityEngine.UI` (legacy Text/Image, no
TextMeshPro, no UI Toolkit) — see `Assets/CampaignVault/Scripts/UI/VaultTheme.cs`
for the palette and primitives.

**Table feel** (`VaultFx.cs`, `VaultSfx.cs`; asset-free, toggle both in
Settings > Table feel): narration and NPC lines typewrite in (click a line,
or send, to finish instantly; layout-stable, so the scroll never jumps), new
transcript lines fade/scale in, roll chips tumble through d20 faces and land
on SUCCESS (gold flash, chime) or FAIL (shudder, thud), buttons swell on hover
and tick on click, health bars fill up, panels cross-fade on tab switch with
a gold underline on the active tab, a "The Dungeon Master weaves the tale…"
indicator pulses while a turn resolves, and dim embers drift behind the page
under a procedural vignette. Sounds are synthesized at startup (no audio
assets). Effects only animate scale/alpha/color/rotation/text — never
layout-driven positions — and always settle to the exact final state.

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

- **11 equal-width tabs in one row** is cramped at the 1280 reference width.
  Chat is the heart of the app; Dashboard/Character would work better as a
  side rail next to it than as separate tabs, and Plugins belongs under
  Settings > Advanced.
- Legacy `Text` renders softer than TextMeshPro, especially at the header's
  26px. A TMP migration is the biggest remaining visual-quality lever
  (`VaultTheme.EnsureFonts()` is the choke point) but it's its own project.
- The transcript isn't persisted: restarting the app loses the chat view
  (campaign state itself lives on the server).
- Replies don't stream; long narration arrives in one piece (then typewrites).
