# Short, flat narration: diagnosis, remedies and a built-in measurer

Follow-up to `NARRATION_AND_CLIENT_PLAN.md` (N0–N8). Models on every client still write short scenes that tell
instead of show. N3/N8 already removed the quoted bad fragments and the brevity words, so the cause is
elsewhere. This plan lists the causes found by reading what the model actually sees, the remedies, and a
debug-only measurer inside the Unity client so each remedy can be judged by numbers, not by feel.

Work phase by phase; every phase ends with its gate. Check phases off here as they land.

---

## Findings

### D1. Length is asked for far from where the prose is written
"3–5 paragraphs" lives in the skill and the system prompt. The last thing read before the scene is the
brief (Unity) or the `take_turn` result (web connectors), then "Write the scene." with no target. A
paragraph target is met by one-sentence paragraphs. Recency wins over the system prompt.

### D2. The log line is a template for the scene
`take_turn.narrative` is described as "One-sentence vault LOG entry" (and the validation error says "short
summary"). The model writes a one-sentence account of the beat right before the scene, and the scene comes
out as that sentence, slightly expanded. In the Unity client it is worse: `LoopContract` says the
storyteller knows only the tool results plus that one line, `TurnBrief` leads with it ("What happens (as
committed)", capped at 400 chars) and the header forbids adding outcomes. A thin brief gives the storyteller
nothing concrete to describe. Grok's own account (short `take_turn` summaries affect it) matches this
mechanism; a model's self-diagnosis is not evidence, so the measurer decides how large the effect is.

### D3. The last text read is log-voiced
The result's `Summary[]` lines ("HP adjusted by -5"), `narrativeReminder` and the brief's bullets are
clipped, confirmation-style sentences. Instruction and tool register leak into prose register.

### D4. The examples are only in the system prompt
Two blockquotes (a search, a chase). Models copy prior assistant turns far more than system text, and the
storyteller replays the last three real passages as assistant turns, so one terse reply becomes the style
anchor for the next (self-reinforcing). There is no exemplar for dialogue or a combat round, and
"show, don't tell" is implied by the examples but never stated.

### D5. Tool-mode models write short after tool calls
Models tuned for agent loops write short post-tool replies. The prompts end on vocabulary lists and
"TOOL HYGIENE (tokens)"; the narration block is about 1.2k of 8.7k characters.

### D6. Not measured, and provider knobs unchecked
The N0 baseline never ran. `max_tokens` and reasoning effort are the profile's values and the storyteller
reuses them; a low effort or small cap also shortens prose. The measurer reports both next to each run.

---

## Phases

### L0: This plan
- [x] Findings and phases written down.

### L1: Measurer core (pure C#, debug-only compile)
- [x] `ProseStats` (C# port of `scripts/measure/prose_stats.py`: words, paragraphs, sentences, mean sentence
  length, fragment ratio, inline roll lines) and `MeasureScript` (the eight beats of
  `scripts/measure/narration-beats.md`), `MeasureReport` (per-beat table, totals, two-mode comparison as
  markdown).
- [x] EditMode tests: stats agree with the python script on a fixed sample; report formatting.

*Gate:* EditMode green.

### L2: Measure rig (debug-only)
- [x] An isolated second composition (own bootstrap with in-memory prefs, no saved chronicle, no usage
  store), its own embedded server on a scratch data root and a free port, a deterministic seeded SFW
  campaign (river valley, mill town, ranger PC level 3, a companion, the miller). The player's campaigns,
  prefs and chronicle are never read or written. Profile copied in memory from the active provider.
- [x] Two modes over the same eight beats: **storyteller** (loop plus a separate narration call) and
  **connector** (one model writes tool calls and scene, with the web connectors' prompt: no client note, roll
  block above the scene). `OpenAiChatDriver` gets `PromptNoteOverride` for this.
- [x] Cleanup: scratch server stopped, rig destroyed, data root emptied.

*Gate:* EditMode tests with the scripted chat provider drive the rig through both modes.

### L3: Debug panel in Settings
- [x] Settings tab "MEASURE", compiled only under `UNITY_EDITOR || DEVELOPMENT_BUILD`: shows the provider,
  model, temperature, max tokens and effort in use; buttons Run storyteller, Run connector, Run both; a
  cost warning (real model calls); progress; the results table and the comparison; the exported markdown
  path (readable by `prose_stats.py`).
- [x] Release builds contain none of it: guarded by a source test (`ReleaseBuilds_ContainNoMeasurerCode`); **not verified by an actual release build** (the editor always defines `UNITY_EDITOR`).

*Gate:* EditMode and PlayMode green; a snapshot of the panel.

### L4: Server-side remedies (D2, D3)
- [x] `take_turn.narrative` description and validation text: a log entry of one or two plain sentences with
  the concrete content of the beat, "the record, not the scene".
- [x] A scene cue at the end of the `take_turn` result when changes were committed (last text read before
  the prose), kept out of pure queries.
- [x] `ToolCallExamples` and the prompts' wording about `narrative` match.

*Gate:* .NET unit suite green; size tests updated on purpose.

### L5: Unity storyteller remedies (D1–D3)
- [x] The closing line of the narration request carries a word target and the show-don't-tell reminder.
- [x] A pinned exemplar exchange at the head of the storyteller messages; replayed passages under a word
  floor are skipped so a terse reply can't become the anchor.
- [x] `LoopContract` asks for specifics in the log line; the brief's line cap matches the server's.
- [x] Header: the brief is material, not style.

*Gate:* EditMode green (StorytellerTests, DriverFakeProviderTests).

### L6: Skills and prompts (D1, D4, D5)
- [x] `dnd-narration`: word targets beside the paragraph targets; a "Show, don't tell" section of positive
  patterns; a dialogue exemplar and a combat-round exemplar.
- [x] The three `recommended-system-prompt*.md`: same NARRATION wording (identity test), a last "write the
  scene" line, and the `narrative` description.
- [x] Grok kit: project prompt reply shape and word target; style anchor unchanged.

*Gate:* GuidanceCorpusTests, LlmToolingRegressionTests and the prompt identity tests green.

### L7: Verify
- [x] Full builds, .NET unit suite, Unity EditMode (and PlayMode where it runs), staged prompts and skills
  re-staged. Integration suite not run (no Docker): say so.
- [ ] **User run (open):** Settings → MEASURE on the real provider. For a true baseline see the progress log: the
  switch removes only the Unity storyteller remedies.

---

## Progress log (resume here)

**2026-10-05 (all of L1–L6 built; the measurement itself is the user's run).** Nothing committed.

Verified: .NET build 0 errors; .NET unit suite **2600 total, 0 failed**, 2 skipped (the two rate-limit tests, as
before); Unity EditMode **362/362**, PlayMode **20/20** (one `BuilderTests.BuildA5eFighter…` timing flake on the
first full run, passing alone and on the full re-run); prompts and skills re-staged. Integration suite (Docker)
not run.

Built:
- Measurer (debug/editor only): `Scripts/Measure/{ProseStats,MeasureScript,MeasureReport,MeasureRig}.cs`,
  `UI/Settings/MeasurePage.cs` + `MeasurePage.uxml`, Settings tab MEASURE (always last: indices 4 and 5 hold).
  Isolation: a second composition on its own GameObject (in-memory prefs, no chronicle, no usage store, never
  saved), its own embedded server on `<app data>/MeasureRig` (first run unpacks a ~1 GB copy), a seeded campaign
  (`MeasureScript.SeedBatch`), the active profile copied in memory. Modes: **Storyteller** and **Connector**
  (`OpenAiChatDriver.SinglePassNoteOverride = ""`: the staged connector prompt with no client note). Repeats 1–3,
  rolls printed beside each beat, roll block stripped before counting. Reports and a `prose_stats.py`-readable
  transcript per run. Tests: `MeasureTests` (stats match the python script), `MeasureRigTests` (scratch server and a
  scripted model, three runs, remedies on and off).
- D1: the storyteller's brief ends on `Storyteller.SceneRequest` (350–600 words, 600–900 under tension, senses,
  bodies and speech); skill and prompts ask in words as well as paragraphs.
- D2: `take_turn.narrative` (model attribute, tool schema, validation text, the skill, the three prompts, the
  example) is a log entry of one or two plain sentences with the beat's content, "the record, not the scene"; the
  loop is told it may use up to ~500 chars; the brief keeps 500 (was 400).
- D3: `SceneCue` field, last in a committed `take_turn` result (never on pure queries): "Now write the scene: 350 to
  600 words…". The Unity loop is told to ignore it, and the brief never forwards it. Cost: ~150 chars per committed
  result; budgets raised on purpose (repeat beat 450→530; tools/list 21,000→21,100; /play 13,100→13,200).
- D4: pinned exemplar exchange (a guarded answer, no names to copy) at the head of the storyteller messages;
  replayed scenes under 120 words are skipped; the skill gained "Show, don't tell" (four positive patterns), a
  combat-round example and a guarded-answer example.
- D5: every prompt ends on `LAST STEP OF EVERY BEAT` (identical in all three, test-enforced); Grok project prompt
  states the word target. Grok kit: re-upload the changed files (`pack_grok.py` lists them).

Also true, said plainly:
- Everything Unity runs uses the **staged** server (`StreamingAssets/.../Server/osx-arm64`). It was re-staged
  (`tools/embed-server.sh osx-arm64`) after the server changes, and `VaultSmokeScenario` now asserts that a committing
  `take_turn` carries `sceneCue`, so a stale stage fails the smoke test. A Docker image or `dotnet run` server used by
  Grok Web or opencode must be rebuilt and restarted before it sends the cue.
- Release builds use `BuildOptions.None` (no Development flag anywhere in `BuildTools` or `release.yml`), so
  `DEVELOPMENT_BUILD` is undefined there. `MeasurePage.uxml` still ships in release builds (it lives in `Resources`)
  but nothing loads it; the measurer code itself is compiled out.
- Each run makes one extra tool-free call to the real provider: the onboarding party step's level suggestion
  (`SuggestPartyLevel`). It is small and not part of the measured turns.
- New files need their Unity `.meta` siblings committed with them.

Known limits, said plainly:
- **No baseline of the old text exists.** The panel's "remedies" switch turns off only the storyteller remedies
  (exemplar, thin-scene filter, word-target closing). The server text (the `narrative` wording, `sceneCue`), the
  skill and the prompts are static. A true baseline puts the *old content into the current client* (the old commit
  has no MEASURE tab): `python3 scripts/measure/stage_baseline.py --ref 555e22a1 --server` stages that commit's
  prompts, skills and server into this tree's `StreamingAssets` (checked: prompts and skills byte-identical to what
  the Unity stager writes for the same files; the old server built in a temporary worktree has no `sceneCue`), then
  run the panel with remedies off, then put the current content back with
  `python3 scripts/measure/stage_baseline.py --ref WORKTREE --server`. Restart the embedded server and quit the
  editor/app between (it reads the files at start).
- Connector mode approximates a web connector (same driver, history compaction of earlier turns, client tool
  loop cap). It is the same prompt and tools, not Grok Web itself.
- Dice differ between runs: use repeats and compare means; beat 3's "retry if it passes" cannot be automated.
- The release-build exclusion is source-checked only.
- The onboarding answers are generic, so the campaign's setting text is the smoke test's ("a windswept frontier
  town"), while the seeded world is the river valley.
