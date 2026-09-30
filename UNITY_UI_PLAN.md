# Unity Client: UI Toolkit rewrite + chat/DM-driver fixes

## Context

`UnityClient/` (Unity 6000.6.3f1, URP) builds its whole UI in C# at runtime: legacy uGUI `Text` with built-in Arial (so "Display" and "Mono" fonts are also Arial), nested `ContentSizeFitter`s, and per-frame layout patches (`CapWidth`, `ChatAutoScroll`, `Canvas.ForceUpdateCanvases`). That's where the jumpy scrolling, rebuild stalls and flat look come from. The panels also hold the business logic: `WorldPanels`, `SessionPanel`, `PartyPanels` and `OnboardWizard` call MCP tools directly from button handlers. The smoke runner drives everything through GameObject lookups (`PanelFor`, `row.Find("Delete")`).

**Decisions made:** UI Toolkit (UXML/USS), a **clean cut** (delete the uGUI layer and keep Net/AI/Model/Flows/Json/Server), and a **refined dark fantasy** look (ink and gold, real serif type, ornate 9-slice frames; the BG3/Pillars register).

This file is the working plan (groomed-plan convention); check phases off here as they land.

---

## Bugs found (chat/DM path first). Every one gets fixed during the rewrite

| # | Where | Problem |
|---|---|---|
| B1 | `AI/OpenAiChatDriver.cs:77`, `:525-561` | **Stop doesn't stop.** `Cancel()` only sets a flag, which is checked between model calls. The in-flight `UnityWebRequest` keeps running for up to 120 s+. Fix: hold the request and `Abort()` it, and make tool calls abortable too. |
| B2 | `SegmentSplitter.cs:23-25,65-86` + driver `:255` | **Roll chips are wrong.** The regex runs over raw tool JSON. Server text is `"X (Perception): Success. Rolled 17 vs DC 14."` (`Dnd5eRulesetResolver.cs:1256`), so the chip label becomes **"Rolled"**. The outcome is recomputed client-side as `roll >= dc`, which ignores PF2e degrees (nat-20/nat-1 step shifts, `Pf2eRulesetResolver.cs:164-197`) and shows FAIL for a crit success. The same roll can double-match when it appears in both summary and data. Fix: parse the server's own verdict word (Success/Failure/CriticalSuccess/…), or better, structured roll fields from the envelope (see Open items). |
| B3 | `SegmentSplitter.cs:19-20` | NPC voice regex handles ASCII quotes only, but models emit `“ ”`. It also gives false positives (`Note: "…"`) and splits one paragraph into fragments. |
| B4 | `VaultTheme.MakeText` (rich text on by default), `Typewriter.Begin` | **Rich-text injection and raw markdown.** Model/server text like `<size=300>` or a stray `<` gets interpreted, and any narration containing `<` skips the typewriter. `**bold**` shows literally. |
| B5 | `VaultModels.cs:42-53` + `VaultClientUI.cs:727-737` | **Perf cliff at 400 segments.** Once the cap trims the head, the reference-prefix check fails on every add. `ClearChildren` then rebuilds all ~400 GameObjects several times per DM turn. `Destroy` is deferred, so old and new coexist for a frame and the scroll jumps. |
| B6 | `VaultFx.cs` `ChatAutoScroll` | **Auto-follow unpins itself.** `onValueChanged` recomputes `_pinned` when content grows during layout rebuild, not only on user scroll, so the chat stops following long replies. |
| B7 | `VaultClientUI.cs:574-580`, `VaultTheme.MakeInput` | Single-line input: no multi-line and no Shift+Enter, and long messages scroll sideways. Esc in a legacy `InputField` reverts the typed text, and Esc also closes pages (`:518`). Enter detection uses the legacy `Input.GetKeyDown` inside `onEndEdit`. |
| B8 | driver `:191-196` | "Preamble" text sent alongside `tool_calls` ("Let me check the rules…") renders as story narration, and narration arrives fragmented across tool iterations. |
| B9 | driver `:140`, `:166-169` | A failure on the first model call leaves a dangling user message. The next send then has two consecutive `user` messages, which strict gateways reject. |
| B10 | driver `PruneMessage :590-614` | Replay drops content-block arrays (`GetString` on an array returns empty) and provider reasoning fields (OpenRouter `reasoning_details` / Gemini thought signatures). That can 400 multi-step tool loops on those providers. |
| B11 | driver `:143`, `:267` | The empty-reply guard counts segments. A panel `Note()` fired mid-turn masks a genuinely empty reply. |
| B12 | driver `:112-279` | `IsBusy` is reset only in an iterator `finally`. Unity doesn't dispose iterators on `StopCoroutine` or when the object is disabled, so busy state can stick. |
| B13 | `McpClient.Send` / `VaultClientConfig.cs:24` | `take_turn` shares the 60 s generic timeout. `ExtractJsonPayload` keeps only the last `data:` line and doesn't join multi-line SSE frames. |
| B14 | uGUI-wide | `Mask` instead of `RectMask2D`, an `Outline` on every panel (doubles vertices), nested fitters, a per-frame `CapWidth`. These go away with the rewrite. |

---

## Target layout ("the table")

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ ✦ crest  THE SUNKEN CROWN · Session 4 · Day 12, Dusk · Harrowgate › Old Mill  │  top bar 56px
│                                                   ● server  ◆ gpt-x  ⚙  ☰     │
├────────────┬───────────────────────────────────────────────┬─────────────────┤
│ PARTY      │               STORY LOG (max 760px)            │ CODEX (drawer)  │
│ ┌────────┐ │  Ｔhe mill wheel groans… (drop-cap narration)   │ [Quests][Scene] │
│ │ AR  ▰▰▱│ │  ┃ MIRELLE  “You're late.”   (NPC nameplate)    │ [Pack][Journal] │
│ │ ◈poison│ │  ╔ d20 ╗ Perception 17 vs DC 14  ✔ SUCCESS      │                 │
│ └────────┘ │  ⋯ The DM consults the ledger (3) ▸  (collapsed)│  scene NPCs,    │
│ companion  │  » I check the wheel for tampering.  (player)   │  active quests, │
│ frames…    │                                               │  items w/ use   │
│ 232px      ├───────────────────────────────────────────────┤  340px, toggle  │
│            │ [Look around][Talk][Search][Rest][OOC]  chips  │                 │
│            │ ┌ multi-line command bar ─────────────┐ [SEND]│                 │
│            │ └ Enter send · Shift+Enter newline · ↑ recall┘ │                 │
└────────────┴───────────────────────────────────────────────┴─────────────────┘
 toasts (top-right) · full-screen overlays: Campaigns · Character sheet · Settings ·
 Plugins · Onboarding · First-run setup · F12 dev inspector
```

- **Background:** dim painted-texture layer, plus embers as a real URP `ParticleSystem` behind the UI document (replaces the `EmberField` CPU hack) and a USS vignette.
- **Story log:** a `ScrollView` whose children are appended and head-trimmed incrementally (no full rebuilds). Typography: EB Garamond body at 18px/1.5, Cinzel nameplates and headings, JetBrains Mono for dice and dev text. Roll cards animate the dice tumble with USS transitions. Tool activity collapses into a single expandable "consulting" strip per turn. Markdown-lite renders safely.
- **Party frames:** monogram portrait, HP bar with damage-lag ghost, condition icons with tooltips. Click opens the Character sheet overlay.
- **Codex drawer:** absorbs today's Dashboard/Inventory/Companions/Session rail. Session start/end/advance-days lives under Journal.
- **Notes become toasts.** Only errors (⚠) and DM-relevant system lines stay in the log.
- **Responsive:** under 1440px the Codex becomes an overlay drawer; under 1100px party frames collapse to icon pips. `PanelSettings`: scale with screen size, ref 1920×1080, match 0.5.
- **Motion:** USS transitions (150–250 ms ease-out) on hover, focus and overlay enter/exit; the text reveal comes from streaming. Settings keeps the "Animations off" switch as a root `.reduced-motion` class.

---

## Architecture

**Keep and fix:** `Net/`, `AI/`, `Model/`, `Flows/`, `Json/`, `Server/`, `Editor/` (update `VaultClientMenu`).

**New `Scripts/App/`** (UI-agnostic, plain C#):
- `VaultAppState`: observable state (active campaign and system, `SessionDigest`, PC and companion ids, transcript, driver status/error, connection health) exposed through `event Action<…> Changed`. It replaces `VaultUiContext`.
- `VaultController`: every command, extracted from `VaultClientUI` and the panel handlers. That covers `SelectCampaign`, `ApplySession`, `SendPlayerText`, `Cancel`, `StartSession`, `EndSession`/checkpoint, `AdvanceDays`, `ListCampaigns`, `DeleteCampaign`, `LoadPc` (get_entity), `SearchWorld`, onboarding start/submit/finalize, `WorldBuild`, plugin toggles, embedded server start/stop, and PlayerPrefs persistence. It reuses the existing `McpClient.CallToolData`, `SessionDigest.FromResult`, `HandoffBuilder`, `RosterParser` and `ServerHostManager`.
- `VaultBootstrap` (MonoBehaviour): wires config, MCP, BYOK, prompts, driver, server, state and controller, and owns autostart (from `VaultClientUI.Awake/Start`).

**New `Scripts/UI/` (UI Toolkit)**, with the old `UI/*.cs` deleted:
- Assets under `Assets/CampaignVault/UI/`: `VaultPanelSettings.asset`, `Theme/VaultTheme.tss`, `Theme/tokens.uss` (color/spacing/type variables), `Theme/components.uss`, `Shell.uxml`, and one `.uxml` per screen/overlay. Fonts go in `Fonts/` (OFL TTFs converted to TextCore `FontAsset`s), icons in `Icons/` (game-icons.net PNG @2x, CC BY 3.0), frames in `Frames/` (9-slice sprites).
- One controller class per view, each binding to `VaultAppState` and calling `VaultController`: `ShellView`, `StoryLogView`, `CommandBar`, `PartyFramesView`, `CodexDrawer` (tabs), `CampaignsScreen`, `CharacterSheetOverlay`, `SettingsScreen` (Provider/Server/Embedded/Audio-Video/Advanced tabs, reusing `ByokSettings` presets), `PluginsScreen`, `OnboardingModal`, `SetupModal`, `DevInspector` (F12: last request, raw response, tool log, copy buttons), `ToastHost`.
- Custom controls (`[UxmlElement]`): `HpBar`, `RollCard`, `Nameplate`, `ConditionIcon`.
- Keep `VaultSfx` as a pure audio helper.
- Input: switch to `InputSystemUIInputModule` (UITK uses the Input System natively). Set `activeInputHandler` to Input System only once nothing calls `UnityEngine.Input`.

**`Model/`** gains:
- `MarkdownLite`: escapes `<` via `<noparse>`, then maps `**`, `*`, `#`, `-` lists and `>` quotes to rich-text tags.
- A smarter `SegmentSplitter`: curly quotes, stricter speaker rule, and roll parsing from the server verdict (B2/B3).
- `VaultTranscript.Removed`/`Added` events so the view trims incrementally (B5).

---

## Phases (each ends with its gate)

- [x] **P0: Test harness + plan file.** Copy the plan to `UNITY_UI_PLAN.md`. Add asmdefs: `CampaignVault.Client` (runtime), `CampaignVault.Client.Editor`, and `CampaignVault.Client.Tests` (EditMode, NUnit). Write tests that pin current behavior of `SegmentSplitter`, `OpenAiChatDriver.CompactResult`/`ExtractContent`, `McpClient.ExtractJsonPayload`, and `SessionDigest.FromResult`. *Done when:* EditMode tests run green in batchmode via the unity-cli skill.
- [x] **P1: Core/driver fixes (UI-independent):** B1, B2, B3, B8, B9, B10, B11, B12, B13, plus `MarkdownLite` and transcript events. For B8, the driver tags content sent with tool_calls as `SegmentKind.Aside`. Add **SSE streaming** to the driver (`stream:true`, a `DownloadHandlerScript` delta parser that assembles `tool_calls` deltas). It falls back to non-streamed on a 400 or on a per-profile toggle in `ByokSettings`. It emits `NarrationDelta` so the log reveals text as it arrives, which replaces `Typewriter`. *Done when:* new unit tests cover each bug, including a fake-provider test for streaming and cancel.
- [x] **P2: App layer.** Build `VaultAppState`, `VaultController` and `VaultBootstrap`, extracting logic from `VaultClientUI`, `SessionPanel`, `DashboardPanel`, `PartyPanels`, `WorldPanels`, `OnboardWizard`, `SetupModal` and `ProviderPanels`. Rewrite `VaultSmokeRunner` against the controller and state instead of GameObjects. *Done when:* the smoke runner passes headless against the embedded server with no UI in the scene.
- [x] **P3: Theme and assets.** Fonts converted to FontAssets, tokens.uss, components.uss (button variants primary/ghost/danger, tabs, cards, inputs, scrollbars, tooltips, toasts), 9-slice frames, icon atlas. Add third-party attributions to `LICENSING.md` (OFL fonts, CC BY icons). *Done when:* a style-guide UXML shows every component in UI Builder.
- [x] **P4: Shell + story log + command bar** (the chat, end to end): top bar, background/particles, StoryLogView (incremental, markdown, nameplates, roll cards, collapsed tool strip, asides muted), CommandBar (multi-line, Enter/Shift+Enter, ↑ history, quick-action chips, Send↔Stop swap, busy status), ToastHost, DevInspector. Delete `VaultClientUI`, `VaultTheme`, `VaultFx`, `Tooltip`, and the old panels. The **clean-cut point**: the scene now runs on the new shell. *Done when:* you can play a turn against the embedded server, and Stop aborts within 1 s.
- [x] **P5: Party frames + Codex drawer + Character sheet** (Quests / Scene / Pack with use-equip actions / Journal with session controls). *Done when:* the smoke runner's dashboard/character/inventory steps pass.
- [x] **P6: Overlays:** Campaigns (card grid, delete confirm without a native dialog), Settings (tabbed), Plugins, Onboarding modal, First-run Setup. *Done when:* the smoke runner covers onboarding, campaign select and delete.
- [x] **P7: Polish + responsive + reduced motion.** Breakpoints, focus/keyboard navigation (Tab order, Esc closes the top overlay only), hover/press states, SFX hooks, and a perf check: 1000-segment transcript, no GC spikes per frame. Update `UnityClient/README.md`. *Done when:* a manual pass at 1280×720, 1920×1080 and 2560×1440 looks clean.

## Verification

- Unity EditMode tests (new asmdef) via the `unity-cli` skill in batchmode, one run at a time per CLAUDE.md.
- `VaultSmokeRunner` headless run (existing `RequestedServerUrl` hook) against the embedded server, after P2 and after every later phase.
- Manual play-mode pass per phase using a **fresh SFW scratch campaign** on an empty DB (memory rule: never read existing campaign data). Check a 5e skill check with nat 20, a PF2e crit, a long reply (auto-follow), Stop mid-turn, and a 400+ segment session.
- Full .NET suite (`/verify`) before declaring done, even though the server is untouched. Report any failures, including the known git-lfs `LocalEmbeddingServiceTests` pair.

## Decisions made during P1

- **Rolls (B2):** the server envelope has no structured roll data (`ResolverResult` carries only `Narrative`, `RollTotal`, `Skill`). A `rolls[]` array would grow every tool result the LLM reads too, so the client parses the resolvers' own fixed narrative formats instead, taking the **server's verdict word** (Success / CriticalSuccess / Hit / Saved …), never `total >= dc`. The formats come from `Dnd5eRulesetResolver` (checks, saves, targeted saves, attacks) and `Pf2eRulesetResolver` (degrees, maneuvers, attacks). Anything else (hidden-content disarms, tether strains) makes no card rather than a guessed one. If a resolver's wording changes, `ChatCoreTests.B2_*` is the canary. Revisit a structured field if the token-surface work ever adds a client-only channel.
- **Streaming** is on by default; `ProviderProfile.DisableStreaming` opts out, and an endpoint that 400s `stream:true` is retried once non-streamed and remembered for the session. A 200 that isn't SSE is parsed as a plain reply.
- **Text reveal:** streaming replaces the typewriter. Non-streamed replies appear whole.
- The old uGUI shell was patched only enough to keep compiling (`Aside` rendering, verdict text, streaming segments shown once complete). It's deleted in P4.

## Outcome and deviations (P2–P7)

- **Gates met.** EditMode: 62 tests, including `SmokeScenarioTests` (all 17 smoke checks headless against the embedded server on a scratch DB). PlayMode: 6 tests, including `TableTests` (a real Enter keypress plays a turn against the scratch server and a scripted DM; STOP aborts in under 1 s; every screen photographed to `Library/VaultSnapshots`) and `ShellLayoutTests` (narrow/compact breakpoints; 1,000 appends past the 400 cap in about 30 ms, one element per segment).
- **UI root name kept.** The UI root kept the name `VaultClientUI` (same script GUID), so `SampleScene`, `CampaignVault > Create Client UI` and `BuildTools` needed no scene edits.
- **No EventSystem or input module.** UI Toolkit reads input itself. No code uses `UnityEngine.Input` any more, but Active Input Handling stays **Both** (switching gains nothing and touches ProjectSettings).
- **Backdrop embers not done.** The ember `ParticleSystem` backdrop was not built. The vignette carries a warm top glow instead, and the particle layer is listed as a gap in the README.
- **Snapshot bugs fixed in this pass:**
  - The crit note after an attack swallowed the next roll (regression tests added).
  - A crit hit read "HIT".
  - A toast covered the codex tabs.
  - Elastic scrolling left a gap above the story.
  - Focus went nowhere after a page closed; it now returns to the command box.
- **The deleted uGUI files had uncommitted edits.** Those edits (and the old `VaultClientUI.cs`) are gone from the working tree as planned. Rider's Local History has them if anything is wanted back.
