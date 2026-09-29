# CampaignVault Unity Client

In-repo Unity 6 (6000.6.3f1) client. Unity drives roleplay: the BYOK chat
endpoint narrates, CampaignVault stays authoritative via MCP tools.

## Setup

1. Open `UnityClient/` in Unity 6000.6.3f1.
2. Start the server: `dotnet run --project src/CampaignVault` (port 5275).
3. `SampleScene` is already bootstrapped (or **CampaignVault > Create Client UI**
   in an empty scene); press Play.
4. Settings tab: server URL, connector, optional bearer token, BYOK base
   URL + model + API key. "Check server health" goes green only when an MCP
   session opens and `/play` lists its tools. Then Campaigns tab.

## Panels

| Tab | Source |
|---|---|
| Chat | BYOK model + MCP tool loop (max 8 turns/message), on-demand `load_skill`, compacted history |
| Session | `start_session` / `end_session` with cap-counted handoff form, checkpointing, downtime via driver |
| Dashboard | `start_session` digest: party HP/status/needs, quests + deadlines, handoff, fingerprint |
| Onboard | `start_campaign_onboarding` (step one pre-answers name + system) → `submit_onboarding_answer` → `finalize_campaign_onboarding` (creates the campaign) → seed in Chat |
| Character | `get_entity` (`chars/…` id, filled from `start_session`): HP bar, AC, conditions, attributes |
| Inventory | Equipped/carried names from the `start_session` party roster; Use/Equip via the DM driver |
| Companions | Party companions (auto-tracked from `start_session`) + any id you add, via `get_entity` |
| Campaigns | `list_campaigns` on `/build`; Play switches campaign (resets chat context, PC, companions); two-tap `delete_campaign` |
| Events | `search_world` over the active campaign: one card per match |
| Plugins | Tool allowlist per connector (client-side DM capability filter) |
| Settings | Server + BYOK + embedded server + prompt/skills status, memory-only secrets |

## Roleplay flows

- **New table:** Onboard tab (create → guided questions with roster parsing → finalize → Seed in Chat) or Chat directly.
- **Returning table:** Campaigns → Play, then Session → Start (shows handoff digest, fills PC/ruleset/fingerprint for the DM) or Dashboard → Refresh.
- **Mid-session safety:** Session → Checkpoint before prompt compaction; the handoff is what survives into the next context.
- **Ending:** Session → End with the structured handoff (caps mirror the server: 800/600/6×120/8×80/200/120).
- **Downtime:** Session → Advance time, or ask in Chat.
- Hover any gold action for its tooltip; the DM prompt status (system-prompt.md + skill count) is shown in Settings and the Chat welcome line.

## Security model (adversarial review)

- API key and bearer token: memory-only, never PlayerPrefs/disk/logs.
- Server auth is header-only; the `?token=` fallback is never used (server logs it).
- Provider/server URLs: https required, http allowed for loopback only.
- All rendered text is C0/BiDi-stripped and length-capped.
- Roll chips render ONLY from tool-result text (provenance), never narration.
- Tool loop bounded (8 turns), results truncated at 20k chars, transcript
  capped at 400 segments (tool data dropped first).
- Item Use/Equip go through the DM driver as prose, not hand-built commits.

## Layout

`Scripts/Json` MiniJson (strict parser, depth cap 64) · `Scripts/Net`
sanitizer, MCP client (session handshake, `{success,data,summary}`
unwrapping), connection config · `Scripts/AI` BYOK settings, system prompt +
skill index/loader, chat driver · `Scripts/Model` transcript,
sheet/inventory/companion models, splitter · `Scripts/UI` theme, shell,
party panels, world panels, effects (`VaultFx`) and sounds (`VaultSfx`) ·
`Scripts/Diagnostics` the `-vault-smoke` end-to-end runner.
`StreamingAssets/CampaignVault` ships the DM system prompt, the dnd-* skills,
plugin sidecar skills, and the embedded server (all copies of repo files,
git-ignored).

## Embedded server

Player builds carry their own CampaignVault MCP server:

- Staging (also runs automatically before every desktop player build via
  `ServerEmbedBuildProcessor`): `tools/embed-server.sh [rid]`
  (`osx-arm64` default; `osx-x64`, `win-x64`, `linux-x64`). Publishes
  self-contained plus `models/embedding` next to the binary.
- At runtime the client copies the payload to local storage on first use
  (re-syncs when the staged build is newer), launches it on 127.0.0.1 with
  no token, waits for `/health`, and kills it on quit. Stdout lands in a
  capped `server.log` beside the payload; campaign data lives in
  `CampaignData` under local storage.
- Settings > Embedded server: port, autostart (loopback URLs only), start/stop.
  WebGL builds skip the embed (no subprocesses) — point them at a server.
Prompt and skills (core `claude_skills/` plus every plugin's `skillsPath`)
are staged automatically before each build by `DmContentStager`; after
pulling prompt/skill updates mid-session, run **CampaignVault > Stage DM
Prompt + Skills**.
