# Narration Voice, Grok Kit, Distribution, Plugins and UI Pass

Follow-up to `UNITY_UI_PLAN.md` (all phases done). Scope: why the DM writes terse "telegram"
prose on every model we tried, and how to fix it for both the Unity client and Grok Web; a Grok
packaging script that makes re-uploading painless; making the Unity build shippable to another
machine; a plugin manager; and a pass over the screens that still look like "enterprise WPF in
Unity".

Work phase by phase; every phase ends with its gate. Check phases off here as they land.

---

## Findings (evidence for the decisions below)

### F1. The terse prose comes from shared material, not the Unity driver

Nemotron Ultra (Unity client) and Grok Web (MCP connector, no Unity code at all) both write
fragment-stacked "telegram" prose. The only things they share are the system prompt, the skills
and the server's tool results, so the cause lives there.

- **The bad style is quoted verbatim, many times.** `claude_skills/dnd-narration/SKILL.md` and
  `recommended-system-prompt.md` paste the banned fragments as examples: `Hut up. Rest broken.
  Goblin outside.`, `Trail bread. Water. Twelve hours.`, `Coin in the purse. Alarm still yours.`,
  `You climb back on. Wheels moving again.`, `Lyra takes a short rest.` Models imitate examples
  even under "banned". The failing output ("Kit on. Dome already dead — eight hours gone before
  you packed.") is the same pattern. There is no positive example anywhere.
- **The instructions are written in the style we don't want.** "Then stop. The player acts."
  "Stop when the change has landed." "If you wrote a sunset three times, cut two." Instruction
  register leaks into output register.
- **Brevity pressure outweighs the floor.** Against one "3–5 paragraphs" line: "do not pad",
  "three short paragraphs … better than eight", "stop", "concrete, not purple", "cut", plus the
  system prompt's "Written to be cheap per turn", "TOOL HYGIENE (tokens)", "request.narrative:
  one sentence". The two files contradict each other on quiet beats (prompt: 3–5 paragraphs even
  when quiet; skill: "place + body + one new fact. Do not pad them to a paragraph count"). Models
  resolve conflicts toward the shorter reading.
- **The last thing read before the prose is log-voiced.** Tool JSON with one-line `summary`,
  `guidance`, `narrativeReminder`, and the model's own one-sentence `narrative` log line.
- **Grok's self-diagnosis is not evidence.** A model explaining its own behavior is guessing. It
  claimed to have restored `dnd-narration/references/pc-voice.md`; that file doesn't exist.

### F2. The good sample (Grok Web, no MCP, ~100k of prose markdown as RAG)

What works: long multi-clause sentences with several senses; each roll's effect shown in full
(the Investigation roll becomes the found items, each with texture; the Recall roll becomes the
road-lore paragraph, which is earned exposition); rolls in a ROLL SUMMARY block above the prose,
so the prose never stops for numbers. Its context was almost all prose: no JSON, no rule spec.

What it got wrong, and the vault fixes: it decided for the PC ("You will travel south."); rolls
and DCs were model-invented (a convenient 19 on the one roll that unlocks the lore dump); nothing
is persisted.

Our rules push against what the user likes: finding items is "a status dump", dense paragraphs
are "purple", the lore paragraph is "a litany". The fix is to separate **new** facts (describe
lovingly) from **unchanged** state (don't restate).

### F3. Unity-specific amplifiers

- `SystemPromptProvider` sends all of `system-prompt.md`: the installer preamble ("Written to be
  cheap per turn…", "Fill in `<slug>`…", `TOKEN_SURFACE_PLAN.md`), the code fence, and the
  template line `CAMPAIGN: campaignName="<slug>"…` before the real CAMPAIGN line it appends. It
  also ignores the ruleset: a `narrative` campaign gets the 5e/PF2e prompt
  (`recommended-system-prompt.narrative.md` exists but isn't staged).
- `CompactFinishedTurns` replaces earlier `load_skill` results with "[earlier skill load,
  dropped…]", so `dnd-narration` is only in context if the model reloads it every turn.
- History (48k chars) keeps past narration verbatim: once one telegram reply lands, it is the
  most recent style example and reinforces itself.
- Temperature is not the lever: at 1.0 nothing is sent (provider default); `max_tokens` is unset
  unless configured. Neither explains paragraph length.

### F4. Grok packaging today

`pack-skills-for-grok.sh` (repo root) copies each `claude_skills/*/SKILL.md` to
`grok-skills-<timestamp>/<timestamp>_<name>.md` plus a JSON index. Timestamped names mean every
upload is a new file set, so old copies must be found and deleted by hand in Grok's project UI.
It doesn't include the system prompt or a style anchor, and doesn't say what changed since the
last upload.

### F5. Distribution of the Unity build to another machine (current state)

- **Does start its own MCP server.** `ServerHostManager` launches the self-contained .NET server
  staged under `StreamingAssets/CampaignVault/Server/<rid>/`, copied to
  `persistentDataPath/Server/<rid>` on first run (re-synced by timestamp), bound to 127.0.0.1:5275,
  DB at `persistentDataPath/CampaignData`, `/health` probe, killed on quit.
- **Only `osx-arm64` is staged** (894 MB: RavenDB server 633 MB, embedding model 87 MB,
  onnxruntime). `ServerEmbedBuildProcessor` publishes the matching RID at build time; Windows and
  Linux have never been built or tested.
- Ships **`ASPNETCORE_ENVIRONMENT=Development`**, `appsettings.Development.json` and `.pdb`s.
- **RavenDB license:** `COMMERCIAL.md` says every deployment needs a RavenDB license key
  (Community is free, annual). The client has no way to enter or import one.
- **macOS:** the app and the server/RavenDB binaries are unsigned and not notarized; on another
  Mac, Gatekeeper quarantine blocks or flags them ("damaged").
- **Fixed port 5275:** if it's taken, start fails. An orphaned server (client crash) is not
  cleaned up on next launch.
- First run copies ~900 MB into `persistentDataPath` (double disk use, silent, no progress).
- A clone without `git-lfs` stages a pointer stub as `model.onnx`; nothing stops the build.
- **Plugins:** the server loads `Plugins/*` next to its own exe
  (`CampaignVaultModule`, `AppContext.BaseDirectory/Plugins`); five plugins ship in the bundle.
  There is no plugin UI: install means unzipping into the deployed server folder by hand, and a
  re-sync can overwrite it. Settings → Tools toggles MCP tools, not plugins.

### F6. Screens that read as "enterprise WPF in Unity" (from `Library/VaultSnapshots`)

- **Character sheet (`p5-sheet.png`)** is a key/value grid of raw engine internals (WILLPOWER,
  WILLPOWER DRAINED, MORALE, TEMPERATURE, WARMTH RATING, MOVEMENT MODIFIER) under a monogram tile
  and a "YOUR CHARACTER" footer. No ability scores, AC, HP, saves, skills, slots, features or gear.
- **Party frames:** monogram tiles; an unknown HP shows a bare "–"; companions have no stat block.
- **Settings → Tools (`p6-settings-tools.png`)** is an admin console: raw tool ids (`world_build`,
  `get_entity`) with their model-facing developer descriptions and toggles.
- **Top-bar status pills** ("server", "offline-dm" in monospace) read as a dev dashboard.
- **Roll cards** are fine but boxy, with mono "17 vs DC 14".
- **Not yet photographed:** Scene, Pack and Journal tabs, campaign events, a fully filled 5e/PF2e
  sheet, companion detail. These need snapshots before they can be judged.

---

## Phases

### N0: Baseline measurement

- [x] Capture provider `usage` (prompt, completion, cached tokens) per request in the driver and
  show it per turn in the F12 inspector; keep a per-session total.
- [x] `scripts/measure/prose_stats.py`: words per beat, paragraphs, mean sentence length, share of
  sentences under 5 words ("fragment ratio"), inline roll lines. Reads a transcript export.
- [x] Transcript export from the client (Inspector → "Export session transcript").
- [ ] **(user run)** Baseline on a **fresh SFW scratch campaign on an empty DB** (never read existing campaign
  data): a fixed 8-beat script (quiet travel, a search, a failed check, a chase with a miss, an
  NPC exchange, a rest interruption, combat start, a lore recall), same provider and model.

*Gate:* baseline tokens-per-turn and prose stats recorded in this file.

### N1: Unity prompt fixes

- [x] Stage only the fenced ```` ```text ```` block (in `DmContentStager`, with a runtime fallback
  in `SystemPromptProvider`), and drop the template `CAMPAIGN: … <slug>` line so only the real
  one is sent.
- [x] Stage per-ruleset prompts (`system-prompt.md`, `system-prompt.narrative.md`) and pick by the
  campaign's system.
- [x] Tests: the built prompt contains no preamble, no `<slug>`, exactly one CAMPAIGN line, and the
  narrative variant for a narrative campaign.

*Gate:* EditMode green; the inspector shows the clean prompt.

### N2: Separate narration pass (Unity)

Split each turn into **bookkeeping** (the existing tool loop) and **storytelling** (one call, no
tools, prose-only context), so the prose is never written right after tool JSON and the
narration rules are always present.

- [x] **Loop contract.** The loop's prompt says: do the tool work; when finished, reply with the
  single word `DONE` (or a short OOC answer when the player asked out of character). The client
  treats `DONE` as end of loop. Content sent alongside tool calls stays a muted aside.
- [x] **TurnBrief (client-side, deterministic, no model call).** Built from this turn's tool
  results: the player's words; each commit's `narrative` log line; roll outcomes (label, total,
  target, verdict) from `SegmentSplitter.ExtractRolls`; new or departed NPCs; items gained or
  lost; location/time change; `narrativeReminder` and ENGINE WARNINGs. Plain sentences, no JSON.
  Hard cap (~1.2k tokens).
- [x] **Narration request.** Tools off. Order, most stable first (so providers cache the prefix):
  narration system prompt (built from the rewritten `dnd-narration` skill, N3, plus a short
  Unity header) → PC voice card (personality + voice memory, fetched once per session) → the last
  2–3 scene passages as prior assistant turns → a final user message with the player's line and
  the TurnBrief. Streams into the log as today.
- [x] **Rolls in Unity** are cards, so the narration prompt says: the rolls are already shown;
  describe what they did, never the numbers.
- [x] **Loop history shrinks.** The loop keeps the one-line log entries and the latest passage
  only; older narration isn't replayed to the loop (it lives in the narration context instead).
- [x] **When the loop made no tool calls** (pure dialogue), still route through the narration
  pass with a brief of just the player's line. An OOC reply is shown as is and skips the pass.
- [x] ~~**Optional narration model**~~ (removed in session 5 at the user's request: one profile, one model).
- [x] **Fallback:** a "Single-pass (legacy)" switch in Settings → Dungeon Master.
- [x] Cancel, stream fallback, dangling-message cleanup and the empty-reply guard all cover the
  new call.
- [x] Tests: TurnBrief builder (rolls, items, NPC moves, caps, no JSON); fake-provider test that
  the narration request has no `tools`, contains the brief and no raw tool JSON; `DONE` ends the
  loop; cancel during the narration call; OOC skips the pass.

*Gate:* EditMode and PlayMode green; the N0 script re-run shows tokens per turn at or below
baseline and a clear improvement in prose stats; the user reads the transcript and agrees.

### N3: Rewrite the narration guidance (shared: Unity, Claude Code, opencode, Grok)

- [x] `dnd-narration/SKILL.md` built around **positive examples**: the Grok RAG passage with
  proper nouns swapped for neutral stand-ins (models copy names from examples) and the "You will
  travel south" decision cut; plus one new example of a *missed* roll in a chase.
- [x] Remove every quoted bad fragment; describe the failure instead ("stacked sentence
  fragments, captions, log lines").
- [x] One length rule, no brevity wording; drop "Stop", "cut", "short", "not purple" phrasing.
- [x] Replace "no status dump" with "new things get full detail; unchanged state gets nothing".
- [x] Keep: resolve before describing, PC agency (never decide the PC's next move), NPC
  knowledge limits, 2nd person, no option menus.
- [x] Rolls: inline italics only for clients without roll cards; with cards, describe the effect.
- [x] Same clean-up in the NARRATION blocks of `recommended-system-prompt*.md` (remove the quoted
  captions; the prompt keeps a short floor and points to the skill).
- [x] Write instructions in the voice we want: full sentences, not clipped imperatives.
- [x] Unity's narration prompt (N2) is generated from the skill, so there is one source.

*Gate:* N0 script re-run on Unity (with and without the N2 pass, to separate the two effects);
user review of the transcripts.

### N4: Grok Web kit and packaging script

- [x] `grok/` in the repo, with:
  - `system-prompt.md`: the Grok project prompt, written as prose, with a fixed reply shape: all
    tool calls first → a **ROLL SUMMARY** block → a divider → the scene. The summary block gives
    log-voiced text a place to go, so the scene starts clean.
  - Grok skill variants where they differ (narration built around the examples); the mechanical
    skills (bundling, combat, world-change…) come straight from `claude_skills/` so the two sets
    can't drift.
  - `style-anchor.md`: 2–3k tokens of narration the user likes (replaces what the 100k of RAG
    prose did implicitly).
- [x] `scripts/pack-grok.sh` (replaces root `pack-skills-for-grok.sh`):
  - Output to `dist/grok/` (gitignored) with **stable file names** (`skill-dnd-narration.md`,
    `00-style-anchor.md`, …), so it's obvious which Grok file each one replaces.
  - `dist/grok/grok-kit.zip` of the whole set for one drag-and-drop.
  - `MANIFEST.json` with content hashes; the script compares with the previous pack and prints
    **only the files that changed** ("delete and re-upload these 3").
  - Fills the project system prompt with `--campaign <slug> --pcs "<id — Name>" --ruleset <x>`
    and copies it to the clipboard (`--copy`, `pbcopy`/`clip`/`xclip`).
  - `--dry-run`, and a printed step-by-step upload checklist.
- [x] Investigate (time-boxed, stop and report if not): whether Grok projects are backed by the
  xAI Files/Collections API, which would allow scripted upload. No automation built on guesses.
- [x] Update `scripts/README.md` and `INSTALLATION.md` (Grok section).

*Gate:* running the script twice with one skill edited in between reports exactly that file;
the user uploads the kit once and confirms the flow.

### N5: Distribution readiness (Unity client)

- [x] Ship as Production: `ASPNETCORE_ENVIRONMENT=Production`; exclude `.pdb` and
  `appsettings.Development.json` from the staged server.
- [x] Build guards in `ServerEmbedBuildProcessor`: fail if `model.onnx` is a git-lfs pointer;
  stage only the target RID (no stale `osx-arm64` payload in a Windows build).
- [x] RavenDB license: Settings → Embedded gets "License key" (paste or import file), stored in
  the user's data folder and passed to the embedded server; first-run setup explains the free
  Community key. Investigate first how the server configures the Raven license.
- [x] Port: if 5275 is busy, pick a free port and use it; a pidfile in the data folder lets the
  next launch kill an orphaned server from a crash.
- [x] First-run server copy: show progress and a disk-space check (or run in place where the
  platform allows, e.g. Windows/Linux, and copy only on macOS).
- [x] Version handshake: `/health` returns the server version; the client warns on mismatch.
- [x] macOS: document Gatekeeper for local sharing (ad-hoc `codesign`, removing quarantine);
  list Developer ID signing + notarization as the proper route (needs the user's Apple account;
  not done by us).
- [ ] Build and smoke-run a Windows x64 build *(open: no Windows machine, and this Unity install has no Windows build support; only the `win-x64` server publish was confirmed)* (the user runs it on a Windows machine, or we only
  confirm it builds and stages if none is available; say which).
- [x] `UnityClient/README.md` → "Distributing": what's in the bundle, sizes, data location,
  backup of campaigns, uninstall.

*Gate:* a clean-profile run (fresh `persistentDataPath`) on macOS starts the server, onboards a
scratch campaign and plays a turn; results listed here per platform.

### N6: Plugin manager

- [x] Server: plugin search path gains a user directory via env var (e.g.
  `CAMPAIGN_PLUGIN_DIRS`), so user plugins live in `persistentDataPath/Plugins` and survive
  client updates and server re-syncs; bundled plugins stay in the server folder.
- [x] Server: disabled plugins via config/env list of ids (no moving folders).
- [x] Server: plain HTTP `GET /plugins` (not an MCP tool, so it costs the model nothing): id,
  name, version, author, kind (code/data-only), enabled, bundled/user, campaign options, load
  errors/faults.
- [x] Client: **Plugins** page (Settings or its own overlay) as cards: name, version, what it
  adds, enabled toggle, "needs restart" banner with a Restart server button.
- [x] Install from `.zip`: path field plus "Open plugins folder" (runtime Unity has no native
  file picker); validate `plugin.json`, reject zip-slip paths and id clashes; code plugins get the
  PLUGINS.md trust warning and an explicit confirm.
- [x] Uninstall (confirm; bundled plugins can only be disabled).
- [x] Tests: server (extra dir, disabled list, `/plugins` payload); client (zip validation incl.
  zip-slip, id clash, data-only vs code detection).

*Gate:* on the embedded server, install a data-only plugin from a zip, restart, see it listed,
disable it, restart, see it gone; full suites green. **Met** (session 4: `PluginHostTests` does exactly
this on the staged server; the user's own click-through in the UI is still worth a look).

### N7: UI pass against the "enterprise" look

- [x] Snapshot fixtures with a **full** character (5e and PF2e) and a companion, plus the Scene,
  Pack, Journal tabs and campaign events; review them before redesigning. (Done: `SheetFixtureTests`;
  campaign events show only when there is no handoff, and that case isn't snapped yet.)
- [x] **Character sheet** redesign: hero header (portrait/crest, name, ancestry · class · level,
  condition badges with icons); vital strip (big HP bar with numbers, AC shield, speed,
  initiative, proficiency); six ability medallions (score + modifier); saves and skills with
  proficiency pips (PF2e: T/E/M/L ranks); resources as pips (spell slots, hit dice, focus);
  features and traits collapsible; equipment slots and pack. Engine internals (temperature,
  warmth, willpower) become a small "Condition & needs" section of labelled gauges ("Cold",
  "Weary"); raw values only in the F12 inspector. Narrative ruleset variant.
- [x] **Companions:** click or hover opens a classic parchment **stat block** (name, type line,
  AC/HP/Speed, ability row, traits, actions), not a key/value grid.
- [x] **Party frames:** HP numbers, hide the bar when HP is unknown (no bare "–").
- [x] **Settings → Tools** moves under Advanced/Developer with human names and player-facing
  one-liners, grouped; raw ids and model descriptions only as a tooltip.
- [x] **Top bar:** replace the monospace pills with status sigils and tooltips.
- [x] **Roll cards:** die face with the total large, verdict as a ribbon, no monospace.
- [x] Codex tabs (quests, scene, journal, campaign events) as ledger/journal styling rather than
  plain lists, per what the new snapshots show.

*Gate:* new snapshot set reviewed; PlayMode green; the user signs off on the look. **PlayMode green
(9/9, session 5); waiting on the user's review of `Library/VaultSnapshots/n7-*.png`.**

### N8: Guidance rewrite: compress, dedupe, narration-first (skills + prompts)

Audit (session 5): ~14.7k words of skills plus 3 prompt variants (1.3k/1.6k/1.8k). The same rules sit in 3–5
places (one beat per call, no chaining, seed before you name, auto-apply pairs, DB wins, 2-beat rule).
There is drift: `dnd-world-change`'s change-type table is broken up by paragraphs, `dnd-combat` numbering
skips 5, the opencode prompt's rules run "8b, 9, 8", and some JSON examples look stale (`status` with
`statusId`/`newState`; `character_update` `newMood`/`updateAppearance`: verify). Unity's storyteller gets
the whole `dnd-narration`, bookkeeping included, and its header has to tell it to skip the tool parts.

- [x] `dnd-narration` becomes craft only: voice, examples, new vs unchanged, roll display, beat shape,
  dialogue, several NPCs, NPC knowledge, player agency, scene vs log, checklist. Bookkeeping sections
  ("Read before you narrate" mechanics, seed before you name, ENGINE WARNING handling) go to their owners
  (world-change, npc-interaction, campaign-events), which mostly already hold them.
- [x] Mechanical skills: each rule stated once, in its owner (ownership table in `dnd-world-change`); others
  name the owner in a few words. Remove the prose/style lines and "narrate sensory outcome" checklist
  items (narration owns them). Written in plain full sentences (F1: instruction register leaks), but
  shorter through less repetition, not clipped phrasing.
- [x] Fix drift: the table, numbering, and every JSON example checked against `WorldChanges.cs`.
- [x] New test: every `$type` JSON example in `claude_skills/**` parses through `CommitChangesParser`
  (guards against stale examples for good).
- [x] Prompts: one shared core; ruleset blocks for 5e/PF2e vs narrative; the opencode variant only adds the
  plugin specifics. The NARRATION block keeps a short floor and points to the skill. Check the Grok
  project prompt against it.
- [x] Unity: drop the storyteller header's "the guide mentions tools" paragraph; update StorytellerTests
  and the stager tests.
- [x] Measure words/tokens before and after (target about a third less), and the full suites
  (GuidanceCorpusTests, the prompt size test, pack-grok's changed-file report).

*Gate:* suites green; every rule in the old text has exactly one home (spot-checked list in the log);
the user reads the new `dnd-narration` and the prompt. **Suites green (session 6); waiting on the
user's read.** Note: the N0 prose A/B hasn't run yet, so this
mostly changes loop instructions; narration craft stays close to N3.

---

## Progress log (resume here)

**2026-09-30, session 7 (release workflow built, untested on GitHub).** Nothing committed. The user
decided: the repo is public (free runner minutes, macOS included), Unity license is Personal, signed
macOS builds don't matter yet (no users).
- `.github/workflows/ci.yml`: on push to master, PRs, manual, and `workflow_call`. Job `unit`
  (build slnx, xunit executable) and job `integration` (docker build `campaignvault:latest`,
  integration executable). The LFS model is cached by object id. No Unity license needed.
- `.github/workflows/release.yml`: `workflow_dispatch` (tag, prerelease). Runs CI, then a GameCI
  `unity-builder@v6` matrix on ubuntu runners: Windows x64, Linux x64, macOS arm64 (unsigned,
  built from Linux with the mac-mono image), each zipped; then `gh release create`. GameCI images
  for 6000.6.3f1 (windows-mono, mac-mono, base) exist.
- GameCI's container has no dotnet SDK, so the workflow runs `tools/embed-server.sh <rid>` on the
  host, and `ServerEmbedBuildProcessor` accepts `-vaultPrestagedServer` (checks the staged binary,
  drops other RIDs, writes version.txt, skips the publish). `BuildTools.BuildStandaloneOSX` pins
  arm64 so the build matches the osx-arm64 server.
- Verified: actionlint clean; Unity EditMode 128/128; a local macOS build with
  `-vaultPrestagedServer` succeeded (log: "Using the pre-staged server osx-arm64"), with no
  scene/settings changes. Not verified: any run on GitHub, and the integration tests (never run
  here, since Docker isn't available locally).
- User setup: the local license is entitlement-only (no `Unity_lic.ulf`). Hub → Preferences →
  Licenses → Add → "Get a free personal license" should write
  `/Library/Application Support/Unity/Unity_lic.ulf`; then secrets `UNITY_LICENSE` (file contents),
  `UNITY_EMAIL`, `UNITY_PASSWORD`. If Hub doesn't write the file, GameCI's Discord documents the
  workarounds.

**2026-09-30, session 6 (N8 built, gate open).** Nothing committed. Verified: .NET unit **2068 total,
0 failed**, 2 skipped; integration 4/4 skipped (Docker); Unity EditMode **128/128**, PlayMode **10/10**;
server re-staged; DM prompt + skills re-staged into StreamingAssets (`DmContentStager.Stage` in
batchmode); `pack_grok.py --dry-run` builds (instructions 8,898 chars).

Measured (words / chars; prompts are the ```text body):

| | before | after |
|---|---|---|
| 11 skills | 14,737 / 101,199 | 11,227 / 72,411 (−24% / −28%; narration examples untouched, ~5.6k chars) |
| recommended-system-prompt.md | 1,087 / 7,426 | 1,080 / 7,324 |
| …narrative.md | 1,382 / 9,306 | 1,338 / 8,800 |
| …opencode.md | 1,697 / 13,337 | 1,237 / 8,305 (−38%) |

Guards added (`tests/CampaignVault.UnitTests/GuidanceExampleTests.cs`):
- every `$type` example (fenced ```json or inline) in skills and prompts parses through the real
  change types with unknown fields rejected; every `world_build` batch example parses into
  `WorldBuildBatch` the same way;
- every `topic=` reference is a real `lookup kind=help` topic;
- NARRATION, SESSIONS and TOOL HYGIENE are identical in all three prompt files.
- `LlmVisibleSurfaces_DoNotReferenceRetiredToolNames` now scans all three prompts.

Drift the guards and the rewrite fixed: `event` categories `Social`/`Narrative` (don't exist; the batch
fails), `character_update.newMood/updateAppearance`, `status.statusId/newState`,
`spatial_position.location`, `...` placeholders in examples, `topic=patterns` and `topic=world-pressure`
(not help topics), `personality` (no such field: it is `psychology.traits/wants/fears`), the missing
"Speaking NPC floor" npc-interaction pointed to (now in world-building with an example), the grapple
example that contradicted "manual engagement only for non-grapple", `upsert_feat` (retired),
conversation time (10–30 vs 60–180 for an interrogation; unified), conversation "every line" vs
"batch related lines" (now one event per exchange), the neutral relationship band (−39..39),
`EntitySeedingAdvisor` pointing the model at "SACRED RULES rule 4" (a prompt section that no longer
existed), the opencode rules "8b, 9, 8".

Rule homes (spot check): one beat per call, never chain, the A/B split → bundling · auto-apply,
auto-log, persistent physical state, seed before you name, required fields, time/minutesElapsed,
DB clock wins, refresh opt-ins → world-change · two-beat rule, no telepathy, voice sources (PC and NPC),
knowledge valence, nudges → npc-interaction · method-not-job, relationship bands → social · Conversation
events, Trivial → conversation · ENGINE WARNING fix + verify, advance_world → campaign-events ·
interrupted/guarded rests, encounter cleanup, FOUND/NOTICED, dmOnly → exploration · speaking-NPC floor,
secrets, clues as items → world-building · prose, rolls display, new vs unchanged, dialogue → narration.
The prompts restate the floors on purpose (Grok Web and bare API loops have no reliable skills).

Prompts: one shared core (the three sections above, test-enforced); the opencode variant is the main
prompt plus a PLUGIN section (status bar, toasts, dice guard, re-inject); its old SPELLS/COMBAT/
BOOTSTRAP quick references were dropped because skills load reliably there. Its CAMPAIGN line is now the
plain `CAMPAIGN:` form; `setup-opencode.sh`/`.ps1` match either form (checked with a scratch run).
Unity: the storyteller header lost the "the guide mentions tools" paragraph and its duplicate
new-vs-unchanged/agency lines (the skill has them).

**2026-09-30, session 5 (N7 built, gate open).** Nothing committed. Verified: .NET unit **2028 total,
0 failed**, 2 skipped; integration 4/4 skipped (Docker); Unity EditMode **127/127**, PlayMode **9/9**;
staged server re-published.

Client:
- `Tests/PlayMode/TableHarness.cs` (shared host: start, snap, call, wait) and `SheetFixtureTests.cs`,
  which seed a full 5e and a full PF2e table from `Tests/PlayMode/Fixtures/n7-*.json` (PC, companion,
  NPC, gear, quests, damage + Poisoned, checkpoint), dump the raw payloads next to the snapshots,
  snap every codex tab and sheet, then delete the campaigns. `TableTests` moved onto the harness.
- `Model/CharacterSheet.cs` (reads get_entity's full payload; 5e/PF2e skills and saves, ranks, pools,
  needs as words) and `UI/CharacterSheetOverlay.cs` (full sheet for PCs, parchment stat block for
  companions and NPCs; `UI/Theme/sheet.uss`). `VaultController.GetCharacterDetail`.
- `Model/ToolCatalog.cs`: Settings tools list moved under Advanced as "What the Dungeon Master can
  do", grouped, with raw ids in the tooltip. Settings tabs: PLUGINS=4, ADVANCED=5.
- Top bar status sigils with live tooltips; roll cards (die face, "against DC x", verdict ribbon,
  fixed width); codex quests as a ledger, Scene as prose with clickable allies, Journal as story so
  far / threads / party plan with the handoff form behind a button; toasts bottom-left; party frames
  hide HP when unknown. Tests: `CharacterSheetTests`, `TableWordsTests`.

Server bugs the fixtures exposed (fixed, tested in `WorldBuildToolsTests`):
- `RulesetSystem.Canonicalize`: create_campaign, onboarding finalize and set-active-system stored
  `Dnd5e`/`Pathfinder2e`, so engine checks against `dnd5e`/`pf2e` failed until a restart migrated them.
- world_build characters now get class resource pools at once (spent amounts kept), and armor
  equipped in the same batch recomputes AC.

Follow-up fixes (same session, after the user's answers). Verified again: .NET unit **2037, 0 failed**,
2 skipped; integration 4 skipped; PlayMode **9/9** on the re-staged server:
- Race/ancestry: speed, size and traits now apply on world_build upserts too; ability bonuses only on
  Create. world_build creates new characters with Create and re-upserts with Upsert, and
  character_create on an existing id no longer re-adds race bonuses (it used Create before).
- PF2e slot pools now have Player Core tables (rank r: 2 slots at level 2r−1, 3 from 2r; rank 10: 1 at
  19). They were `defaultMax: 1` for ranks 1–4 and 1 slot for 5–10.
- PF2e spell DC only for casters: the ability comes from explicit stats or the new class YAML field
  `spellcastingAbility` (bard, cleric, druid, witch, wizard); the `?? "Wisdom"` default and the
  hard-coded class-name guess are gone. Characters stored before the fix keep their stray DC.
- `RulesetTemplateLoader`: an extracted default the DM never edited is now refreshed when the shipped
  file changes (manifest hash check). Before, a data fix never reached an install that had extracted
  the old file; the unit tests' temp extraction was 17 days stale.

Session 5, third round (verified: .NET unit 2037/0 failed/2 skipped; Unity EditMode **128/128**,
PlayMode **10/10**; server re-staged):
- Plugin changes restart the embedded server by themselves (`VaultController.MarkPluginsChanged` →
  `RestartWhenQuiet`: 1.5 s debounce, waits for a turn in flight), then `/plugins`, the MCP health check,
  and a check that each changed plugin is loaded or gone (`PluginExpectationProblems`). Page: "RESTART NOW",
  switch "Enabled". Tests: `PluginHostTests.Controller_RestartsServerByItself_…` (install, then 3 quick
  flips = exactly 2 restarts), `AppLayerTests.PluginExpectations_…`.
- One model: the per-profile narration model is removed; the storyteller uses the profile's model.
- Plain-string statuses get category "Condition", not "Legacy"; event text omits an empty category.
- `.gitignore`: Unity `InitTestScene*` leftovers. Deleting `UNITY_UI_PLAN.md` (all ticked, untracked),
  the stray `InitTestScene*.unity` and the stale `summoning_todo.md` (summoning is implemented) was
  blocked by the permission check: the user decides.

Still open: wizard curriculum slots and sorcerer 4-slot tables need
per-class slot tables (pools are per system today).

**2026-09-30, session 4 (N6 done).** Nothing committed. Verified: .NET unit **2020 total, 0 failed**,
2 skipped (the rate-limit pair); integration 4/4 skipped (Docker); Unity EditMode **117/117**, PlayMode
**8/8** (editor binary directly); staged server re-published with `tools/embed-server.sh osx-arm64`.

N6 findings and changes:
- Found: a data-only package (`plugin.json` + `RulesetData`, no DLL) under `Plugins/` was never
  loaded (the loader only enumerated DLLs). `PluginAssemblyLoader.LoadPlugins(folders, disabled)` now
  groups packages per folder, loads data-only ones (YAML roots, campaign-option defaults, id for
  `requires.plugin`), skips `.`-prefixed folders, and reports every package in a catalog
  (`Plugins/PluginCatalog.cs`). `LoadPluginsFromDirectory` is a wrapper, so old callers are unchanged.
- Server env: `CAMPAIGN_PLUGIN_DIRS` (user folders, scanned after the bundled one; only a *loaded*
  package claims its id, so a user copy can't shadow a bundled id unless that id is disabled, and the
  disabled list is per id, so it disables every copy) and `CAMPAIGN_PLUGINS_DISABLED`. Banner line
  `Plugins: N loaded (x user), y disabled, z with errors`.
- `GET /plugins` (plain HTTP, not MCP): engineVersion, folders, and per plugin id, name, version,
  author, description, kind, source, enabled, loaded, directory, minEngineVersion, systems, modeIds,
  campaignOptions, errors (load errors only: runtime faults are per commit, not tracked globally).
  `plugin.json` gained optional `author` and `description`.
- Client: `Server/PluginPackages.cs` (listing parse, zip inspect with zip-slip/absolute/drive-letter
  refusal, one-package rule, safe id, 512 MB / 20k-entry caps, code = ships a DLL, PluginSdk copy
  and `__MACOSX` skipped, staged extraction into `Plugins/<id>`, scan, uninstall, disabled file).
  `ServerHostManager` passes `<DataRoot>/Plugins` and `plugins-disabled.txt` to the server.
  Settings → **Plugins** tab: restart banner + RESTART SERVER, install card (path + Open plugins
  folder; code plugins get the trust warning and an explicit confirm), cards per plugin (kind,
  source, systems, modes, options, errors, loaded state, "load on next start" switch, uninstall for
  user plugins), plus packages installed since the last start. Listing works for any connected
  server; managing only for the built-in one (`CanManagePlugins`).
- Found in tests: `TableTests` never set `Server.DataRoot`, so the Setup/Plugins pages read the
  real app-data folder; now a temp root. Scratch servers in tests (`ScratchServer`, the smoke test)
  used a plain Kill (RavenDB lingered briefly); now the client's graceful stop.
- Seen but not touched: the dev `src/CampaignVault/bin/Debug/.../Plugins` holds stale builds of the
  user's own GoblinPonyriders and LewdHandbook plugins declaring `minEngineVersion` 0.12.0, which a
  0.11.0 server now lists with an error (they were skipped silently before).
- Snapshot: `Library/VaultSnapshots/n6-settings-plugins.png`.

**2026-09-30, session 3 (N5 done except the Windows run).** Nothing committed. Verified: .NET unit
**2013 total, 0 failed**, 2 skipped (the rate-limit pair); integration 4/4 skipped (Docker);
Unity EditMode **108/108**, PlayMode **7/7**; macOS player build via
`BuildTools.BuildStandaloneOSX` succeeded (bundle ~1.0 GB, only `osx-arm64`, no `.pdb`/Development
settings, `version.txt` present). Note: the `unity test` CLI wrapper now exits 2 before Unity starts;
run the editor binary directly instead:
`/Applications/Unity/Hub/Editor/6000.6.3f1/Unity.app/Contents/MacOS/Unity -batchmode -runTests -projectPath UnityClient -testResults <scratch>/x.xml -testPlatform EditMode|PlayMode -logFile <scratch>/u.log`.

N5 findings and changes:
- License (the open question): the server never passed one. Now `CAMPAIGN_RAVEN_LICENSE` (JSON) or
  `CAMPAIGN_RAVEN_LICENSE_PATH` → `ServerOptions.Licensing` (`Hosting/HostSwitches.cs`,
  `RavenStartup.Initialize(dbPath, licensing)`); banner prints the source, never the key. A malformed
  license doesn't stop startup (RavenDB ignores it). Client: Settings → Embedded license card (paste
  JSON or a file path, "Get a free license", Remove), stored as `raven-license.json` in the data
  folder; first-run Setup mentions it.
- Production: any non-Development env forced 0.0.0.0 + HTTPS + mandatory token. `MCP_BIND_ANY` and
  `HTTPS_ENABLED` are now tri-state (`HostSwitches.Resolve`: explicit `0` wins). The embedded server
  runs `Production` + `MCP_BIND_ANY=0` + `HTTPS_ENABLED=0` (checked: listens on 127.0.0.1/::1 only).
- `/health` → `{status, version}` from `EngineVersion.Current` (also the MCP server info and banner,
  which printed stale 0.11.0/0.2.0 before); banner prints `Environment:`. Build/stage write
  `StreamingAssets/CampaignVault/Server/version.txt`; `CheckConnection` warns (status + toast) on mismatch.
- Found and fixed: RavenDB's child process outlived a killed server and held `system.lock`, so a
  Stop→Start (or crash→restart) couldn't open the DB. Server disposes `EmbeddedServer` on
  `ApplicationStopped`; client stops with SIGTERM (Windows `taskkill /T`), waits on a background
  thread, then SIGKILLs children before the parent (graceful exit takes ~8 s, so nothing blocks the UI).
- Found and fixed: `server.log` was closed as soon as /health answered (later output lost, and a
  write after dispose could throw); now open until stop, shared, capped at 5000 lines.
- `ServerHostManager`: `DataRoot` (tests use temp), orphan kill via `server.pid` (matched by binary
  name, since /var vs /private/var paths differ), payload copy on a background thread with % progress
  and a free-space check (kept copying on every platform; run-in-place not done), preferred port or a
  free one (`ActivePort`), gRPC on a free port. Pure helpers in `Server/EmbeddedServerSupport.cs`.
- `ServerEmbedBuildProcessor`: fails on a missing/LFS-pointer model, deletes all staged RID folders
  first, strips `.pdb` + `appsettings.Development.json`, macOS RID from the build architecture (x64 →
  `osx-x64`, else arm64). `tools/embed-server.sh` matches (`ALLOW_NO_EMBEDDINGS=1` escape).
- Tests: `HostSwitchesTests` (.NET), `EmbeddedServerTests` (EditMode, temp dirs),
  `EmbeddedHostTests` (PlayMode: fresh data root, busy port, Production banner, version, orphan
  stopped by a second session, graceful stop removes the pidfile). `ScratchServer` gets a free gRPC port.
- Docs: client README "Embedded server" rewritten + new "Distributing" (bundle, data paths, backup,
  uninstall, license, Gatekeeper `xattr`/ad-hoc `codesign`, Developer ID + notarization as the proper
  route); INSTALLATION env table (`MCP_BIND_ANY`, `HTTPS_ENABLED`, `CAMPAIGN_RAVEN_LICENSE*`).
- N5 gate: not run on a clean profile of the *built app* (that would mean moving the user's real
  data folder). Covered instead by `EmbeddedHostTests` (fresh temp data folder) + `TableTests`
  (onboard + turn on the staged server). The user can run the built app on another Mac/account.
- Idea from the user (not started): a `workflow_dispatch` GitHub Actions release building all
  platforms; see the discussion in the session-3 report.

**2026-09-30, session 2 (checkpoint before compaction).** N3 and N4 are in; nothing committed.
Verified: .NET unit suite **2002 total, 0 failed**, 2 skipped as always (the two rate-limit tests
skipped in source), Unity EditMode **98/98**, PlayMode **6/6**. Gates still waiting on the user:
the 8-beat runs (N0/N2/N3) and one real Grok upload (N4).

N3 (shared narration guidance):
- `claude_skills/dnd-narration/SKILL.md` rewritten: two worked examples up front (the de-named
  Grok safe-house/road passage without the "you will travel south" decision; a new chase with a
  missed hatchet throw), "new things get full detail; unchanged state gets nothing", a Rolls
  section (cards: no numbers; otherwise an italic roll block above the scene), all mechanics kept
  (read-before-narrate, seed-before-name, NPC knowledge, agency, multi-NPC, warnings), written in
  full sentences, no quoted bad fragments, no brevity wording.
- `recommended-system-prompt.md` / `.narrative.md`: NARRATION blocks rewritten the same way; the
  "Show rolls inline" line became the roll block; "cheap per turn" preamble line and quoted
  captions gone; `request.narrative` is called a log sentence. `.opencode.md` rule 6 now uses the
  roll block too. `dnd-conversation` pointer says "sensory-detail rules" (was "compression").
- Unity `Storyteller.cs`: `NarrationHeader` tells the storyteller the guide's tool/commit parts
  are done elsewhere; new `SinglePassNote` (cards, so no roll block) is appended in legacy
  single-pass mode (`OpenAiChatDriver` system prompt line).
- Tests: `LlmToolingRegressionTests.NarrationSurfaces_QuoteNoTelegramFragments` (.NET; the four
  prompt/skill files never quote the old fragments); `StorytellerTests.NarrationPrompt_FromRealSkill_CarriesTheExamples`.
- The Unity build reads skills from StreamingAssets: run *CampaignVault → Stage DM Prompt +
  Skills* (or build) before a play test so the new skill and prompts are staged.

N4 (Grok kit):
- `grok/project-prompt.md` (Grok addendum: consult narration skill + style anchor before each
  scene; reply shape tool calls → ROLL SUMMARY → `---` → scene; OOC; no self-rolled dice),
  `grok/style-anchor.md` (four new passages: quiet road, failed persuasion with the ferryman,
  interrupted rest, first moments of a fight; **written by me, the user may replace them with
  prose they like**), `grok/skills/README.md` (override folder, empty on purpose).
- `scripts/pack_grok.py` + `scripts/pack-grok.sh` wrapper; root `pack-skills-for-grok.sh`
  deleted; `.gitignore` ignores `/dist/` and old `grok-skills-*` output. Gate run in scratch:
  first pack → all 12 files; repack → "nothing changed"; one skill edited → exactly that file in
  the report and in `changed/`. Instructions are ~9k chars.
- Investigation: grok.com projects are not the xAI Collections API (that is the developer
  platform, billed separately); no scripted upload built.
- Docs: `INSTALLATION.md` → "Grok Web" section; `scripts/README.md` → pack-grok + measure.

Earlier (session 1): N0 tooling, N1, N2. Files: `Scripts/AI/TurnLedger.cs`, `Storyteller.cs`
(TurnBrief, contract, PC card), `OpenAiChatDriver.cs` (usage capture + stream_options fallback,
two-pass turn, `SinglePass`/`NarrationModel`), `SystemPromptProvider.cs` (fence extraction,
per-ruleset prompts), `ByokSettings.cs`, `ProviderForm.cs`, `SettingsOverlays.cs` (Inspector
tokens, copy prompt, export transcript), `VaultController.cs` (`[Table action]` instead of OOC,
transcript export), `DmContentStager.cs`, `VaultSmokeScenario.cs`, tests
(`PromptAndLedgerTests`, `StorytellerTests`, `DriverFakeProviderTests`, smoke/table mocks),
`scripts/measure/*`.

**Next, in order:**
1. User: stage prompt + skills in Unity, then the 8-beat script on a fresh scratch DB, same model:
   storyteller pass off vs on (and optionally before/after N3 via git stash of the skill);
   `python3 scripts/measure/prose_stats.py before.md after.md`; record under the N0/N2/N3 gates.
2. User: `scripts/pack-grok.sh --campaign … --pcs … --copy`, upload once, confirm the flow (N4 gate).
3. N5 leftovers: a Windows run (user's machine, or the Windows zip from the release workflow).
   Release workflow: the user adds the three Unity secrets, commits and pushes, then runs
   Actions → Release once with a pre-release tag (session 7).
4. User: click through Settings → Plugins on the built app (install a zip, restart, toggle).
5. User: review the `n7-*` snapshots (N7 gate); then any look changes and a campaign-events snapshot.
6. User: read the new `dnd-narration` and `recommended-system-prompt.md` (N8 gate), then re-upload the
   Grok kit (every skill file changed).

Open follow-ups: the storyteller reuses the profile's temperature/max_tokens/reasoning effort;
the Inspector's "last request" shows whichever call ran last; a stray
`UnityClient/Assets/InitTestScene<guid>.unity` (+ .meta) from a test run is untracked and can be
deleted with the user's OK.

---

## Verification (every phase)

- Unity EditMode/PlayMode in batchmode, one run at a time (CLAUDE.md). Integration tests use the
  staged server on a scratch temp DB; nothing reads the user's campaigns, prefs or provider file.
- Server changes (N5, N6): full .NET suite via the xunit executables (`dotnet test` exits 5 here).
  Report every failure, including pre-existing ones.
- Prose changes (N2, N3): the N0 script on a scratch campaign, before and after, plus the user's
  read of the transcripts. Metrics support the judgement; they don't replace it.

## Open questions

- ~~RavenDB license: how the embedded server receives it~~ (answered in session 3: it didn't; now env vars).
- Whether Grok Web project files can be uploaded by API (N4 investigation).
