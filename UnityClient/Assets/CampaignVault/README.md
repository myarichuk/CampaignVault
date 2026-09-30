# CampaignVault Unity Client

In-repo Unity 6 (6000.6.3f1) client. Unity drives roleplay: the BYOK chat
endpoint narrates, CampaignVault stays authoritative via MCP tools.

## Setup

1. Open `UnityClient/` in Unity 6000.6.3f1.
2. Start the server: `dotnet run --project src/CampaignVault` (port 5275).
3. `SampleScene` is already bootstrapped (or **CampaignVault > Create Client UI**
   in an empty scene); press Play.
4. First run opens **Setup** (server check, then your Dungeon Master's AI
   provider, then your first campaign). The server pill in the top bar goes
   green only when an MCP session opens and `/play` lists its tools.

## Screens

| Where | What it does | Source |
|---|---|---|
| Top bar | Campaign, session, time and place; server and Dungeon Master sigils (the gem shows the state, hover for details, click to re-check or change); notices appear bottom left | health check + provider profile |
| Story + command bar | Streamed narration, NPC voice lines, roll cards (die with the total, the server's verdict on a ribbon), folded tool activity. Enter acts, Shift+Enter breaks the line, Up recalls; quick actions; ACT turns into STOP mid-turn | BYOK model + MCP tool loop (max 8 steps/message), on-demand `load_skill`, compacted history |
| Party frames (left) | HP numbers and bar with a damage ghost (hidden when HP is unknown), conditions; click for the sheet | `start_session` party roster |
| Codex (right): Quests | A ledger of open quests: what's left and when, overdue in red | `start_session` digest |
| Codex: Scene | Place, time, what the PC is doing; tracked allies (click for their stat block); known faces and where they stand | digest + `get_entity` |
| Codex: Pack | Equipped and carried; Use/Equip go through the DM | digest roster |
| Codex: Journal | Open/refresh session; story so far, this/last session, loose threads, party intent (or the recent-events digest when there is no handoff); downtime; the end-of-session handoff behind WRITE THE HANDOFF (checkpoint or end, cap-counted); lore search | `start_session` / `end_session` / `search_world` |
| Character sheet | Player characters: hero line, HP/AC/speed/initiative/proficiency/spell DC, ability medallions, saves and skills (5e proficiency pips, PF2e T/E/M/L ranks), resources as pips, features, equipment, condition and needs in words, recent events. Companions and NPCs: a parchment stat block. "Play as" for any party member | `get_entity` (whole answer) |
| Campaign book | Cards per campaign: Play switches (resets the DM's conversation, PC, companions); two-tap delete | `list_campaigns` / `delete_campaign` on `/build` |
| New campaign | Name and rules up front, then the server's questions one at a time (roster helper on party questions), finalize, seed through the DM | onboarding tools on `/build` |
| Settings | Dungeon Master profiles, server + connector + token, embedded server, table feel, plugins, advanced (prompt status, what the Dungeon Master can do: grouped switches with plain names; raw tool ids in the tooltip) | local |
| Settings: Plugins | What the server loaded (kind, source, errors). On the built-in server: install from a `.zip` (code plugins need a trust confirmation), uninstall, enable/disable, restart | `GET /plugins` + files under app data |
| Inspector (F12) | Last request, raw model reply, tool log, copy buttons | driver |

## Roleplay flows

- **New table:** Campaign book, then New campaign (questions, finalize, Seed the world), or just talk to the DM.
- **Returning table:** the client reopens the last campaign and resumes its session; Campaign book, then Play, to switch.
- **Mid-session safety:** Journal, then Checkpoint, before a long pause or prompt compaction; the handoff is what survives into the next context.
- **Ending:** Journal, then End session, with the structured handoff (caps mirror the server: 800/600/6x120/8x80/200/120).
- **Downtime:** Journal, then Let time pass, or ask the DM.
- Hover any control for its tooltip. Esc closes the top page.

## Security model (adversarial review)

- Provider API keys live in the local providers file (plain JSON: the BYOK
  trade-off) and are only ever sent to that profile's endpoint. The server
  bearer token is memory-only. Neither goes to PlayerPrefs or logs.
- Server auth is header-only; the `?token=` fallback is never used (server logs it).
- Provider/server URLs: https required, http allowed for loopback only.
- All rendered text is C0/BiDi-stripped and length-capped, and every `<` in
  model or server text is neutralized before display: markdown renders, rich
  text can't be injected.
- Roll cards render ONLY from tool-result text (provenance), never narration,
  and show the server's verdict word; nothing is recomputed client-side.
- Tool loop bounded (8 steps), results truncated at 20k chars, transcript
  capped at 400 segments (tool data dropped first). STOP aborts in-flight
  requests; a tool call cut mid-flight is reported as possibly applied.
- Item Use/Equip go through the DM driver as prose, not hand-built commits.

## Layout

`Scripts/Json` MiniJson (strict parser, depth cap 64) · `Scripts/Net`
sanitizer, MCP client (session handshake, `{success,data,summary}`
unwrapping, abortable requests), connection config · `Scripts/AI` BYOK
settings, system prompt + skill index/loader, chat driver with SSE streaming
(`ChatStream.cs`) · `Scripts/Model` transcript (with change events), roll and
voice splitter, markdown subset, sheet models · `Scripts/App` state,
controller, bootstrap (no UI) · `Scripts/UI` UI Toolkit views (see the
project README's design notes) · `Scripts/Diagnostics` the `-vault-smoke`
scenario and runner. `UI/` holds the theme (USS), fonts, icons and the shell
UXML. `StreamingAssets/CampaignVault` ships the DM system prompt, the dnd-*
skills, plugin sidecar skills, and the embedded server (all copies of repo
files, git-ignored).

## Embedded server

Player builds carry their own CampaignVault MCP server:

- Staging runs automatically before every desktop player build
  (`ServerEmbedBuildProcessor`); for play-testing in the editor run
  `tools/embed-server.sh [rid]` (`osx-arm64` default; `osx-x64`, `win-x64`,
  `linux-x64`). Both publish self-contained, strip `.pdb` files and
  `appsettings.Development.json`, stage `models/embedding` next to the binary,
  and write `Server/version.txt` (the server version the client expects). The
  build fails if `models/embedding/model.onnx` is a git-lfs pointer (install
  git-lfs, `git lfs pull`); the script accepts `ALLOW_NO_EMBEDDINGS=1`. A
  player build removes other RIDs' payloads, so it ships only its own. A macOS
  build stages `osx-x64` when the build architecture is Intel, otherwise
  `osx-arm64` (a universal build still carries only the Apple Silicon server).
- At runtime the client copies the payload into its data folder on first use
  (and again when the staged build is newer), on a background thread with
  progress in Settings > Embedded and a free-space check. It launches the server
  as `ASPNETCORE_ENVIRONMENT=Production` with `MCP_BIND_ANY=0` and
  `HTTPS_ENABLED=0` (127.0.0.1 only, plain HTTP, no token), on the configured
  port or, when that is taken, any free port. `server.pid` in the data folder
  lets the next launch stop a server a crashed session left running. Stopping
  sends SIGTERM (Windows: `taskkill /T`) so the server shuts RavenDB down and
  frees the database lock, and forces it after 15 s. `server.log` beside the
  payload captures the first 5000 lines; campaign data lives in `CampaignData`.
- `/health` reports the server version. When it differs from `version.txt`, the
  connection check says so (the client may be talking to an older server, e.g.
  a dev server already on the port).
- Settings > Embedded server: port, autostart (loopback URLs only), start/stop,
  and the RavenDB license (below). WebGL builds skip the embed (no
  subprocesses): point them at a server.

## Distributing

**What's in the bundle.** The player, the self-contained server (~890 MB for
`osx-arm64`: the RavenDB server ~630 MB, the embedding model ~87 MB,
onnxruntime and .NET), the DM prompt and skills, and the plugins shipped with
the server. First launch copies the server into the data folder, so plan on
about twice that on disk.

**Where the data lives.** Everything the client writes is in Unity's
`persistentDataPath`, shown in Settings > Advanced ("App data"):
macOS `~/Library/Application Support/<company>/<product>/`, Windows
`%USERPROFILE%\AppData\LocalLow\<company>\<product>\`, Linux
`~/.config/unity3d/<company>/<product>/`. Inside it:

| Path | What |
|---|---|
| `CampaignData/` | the campaigns (RavenDB) |
| `Server/<rid>/` | the unpacked server and `server.log`; safe to delete, it is copied again |
| `Server/server.pid` | the running server, while it runs |
| `raven-license.json` | the RavenDB license, if one was saved |
| `Plugins/` | plugins you installed (the server reads it via `CAMPAIGN_PLUGIN_DIRS`); survives client updates |
| `plugins-disabled.txt` | plugin ids the server starts without (`CAMPAIGN_PLUGINS_DISABLED`) |
| `vault-providers.json` | Dungeon Master profiles, including API keys |
| `Exports/` | exported transcripts |

**Backup.** Quit the client (so the server stops and RavenDB closes its files),
then copy `CampaignData/` (and `Plugins/`, if campaigns rely on plugins you installed). Restore by putting it back while the client is closed.

**Uninstall.** Delete the app, then the data folder above. Delete
`vault-providers.json` last if you want to keep your keys elsewhere first.

**RavenDB license.** RavenDB asks every installation for a license key (see
[COMMERCIAL.md](../../../COMMERCIAL.md)). The Community license is free and renewed
yearly: request it at <https://ravendb.net/license/request/community>, then
paste the JSON (or the path to the file) under Settings > Embedded. It is saved
as `raven-license.json` and passed to the server as
`CAMPAIGN_RAVEN_LICENSE_PATH` on the next start. Without one the database runs
under RavenDB's AGPLv3 terms. The server banner in `server.log` says which applies.

**macOS Gatekeeper.** The app and the server binaries are not signed or
notarized. A copy downloaded or AirDropped to another Mac is quarantined, and
macOS reports it as damaged or from an unidentified developer.

- Sharing with your own Macs or friends: after copying, remove the quarantine
  flag and ad-hoc sign the bundle:
  ```sh
  xattr -dr com.apple.quarantine "/Applications/CampaignVault.app"
  codesign --force --deep --sign - "/Applications/CampaignVault.app"
  ```
  The first command alone is usually enough; the second helps when macOS still
  refuses the unsigned server inside the bundle.
- Proper distribution needs an Apple Developer ID: sign the app and every
  binary inside it (the server, RavenDB, the native libraries) with the
  hardened runtime, then notarize with `xcrun notarytool` and staple the
  ticket. That needs the publisher's Apple account and isn't automated here.

**Windows and Linux.** The build processor publishes `win-x64` and `linux-x64`
servers, but those builds have not been run yet: treat them as untested.

Prompt and skills (core `claude_skills/` plus every plugin's `skillsPath`)
are staged automatically before each build by `DmContentStager`; after
pulling prompt/skill updates mid-session, run **CampaignVault > Stage DM
Prompt + Skills**.
