# CampaignVault full review — findings and remediation plan

## Context

Michael asked for a whole-project review covering: engine inconsistencies and missing functionality, rule-implementation errors (5e SRD 5.1 / PF2e Player Core), licensing exposure (SRD/ORC only), MCP token efficiency, Unity client bugs/flows/design, and an evaluation of the "stub schema in tools/list + schema lives in skills, loaded on demand" idea. Token efficiency is the top priority.

This file is the review output and the remediation plan. Sections: (A) Unity client, (B) engine + rules, (C) licensing, (D) MCP surface / prompts / skills / token use, (E) schema-on-demand assessment, (F) phased remediation plan, (G) verification.

---

## A. Unity client (UnityClient/Assets/CampaignVault/Scripts/)

Plans status: UNITY_UI_PLAN P0–P7 verified done (B1, B2, B9, B10, B12, B13 hold). LLM_COST_AND_ERRORS_PLAN done. NARRATION_AND_CLIENT_PLAN done except N0 baseline and N5 Windows smoke. CHARACTER_ART_PLAN not started. Staged prompts/skills are byte-identical to repo copies. No `[Ignore]`d tests; integration tests `Assert.Ignore` without a staged server.

### High
- **A1. `start_session` after every world-changing turn** — `App/VaultController.cs:558-561` → `RefreshTable()` → `start_session` (`:363-366`). Server `PrimeTurnCursorAsync` (`src/CampaignVault/Tools/SessionTools.cs:282-298`) sets `ForcedFullReseedPending` and clears the guidance ledger, so every next `take_turn` is a full (non-delta) turn and one-time hints are re-taught. Biggest single token leak in the client. The tool description itself says "never per turn".
- **A2. partyFingerprint never refreshed from take_turn** — `AI/SystemPromptProvider.cs:78-81` bakes it into the system prompt, set only from start_session (`VaultController.cs:210`). take_turn returns a fresh one every call (`Models/TurnModels.cs`); compaction trims the result to 280 chars (`OpenAiChatDriver.cs:1041-1058`) so the stale value is all the model sees. Hidden by A1; fixing A1 alone exposes forced resyncs (`MutationTools.cs:602-615`).
- **A3. RETRY re-sends a partially committed turn** — `CanRetryLastTurn` checks only `LastError` (`VaultController.cs:481-484`); `Fail()` fires for tool errors / step cap even after other tools committed (`OpenAiChatDriver.cs:461,498,515`); `LlmFailure.Other` is retryable (`AI/LlmFailure.cs:37,115`). Double commits.
- **A4. warnings / worldPressure / ENGINE WARNING lost at compaction** — `CompactResult` keeps only `summary` (`OpenAiChatDriver.cs:1048-1056`); `Storyteller.cs:258-307` drops `warnings` too. The prompt's "fix the ENGINE WARNING in your next take_turn" cannot work.
- **A5. Malformed tool args sent as `{}`** — `OpenAiChatDriver.cs:439-440`; `finish_reason:"length"` never checked (`AI/ChatStream.cs:117,184`). Should return a parse-error tool message.

### Medium
- A6. Phantom roll cards: `SegmentSplitter.ExtractRolls` runs on every tool result incl. `recall_history`/`get_entity` (`OpenAiChatDriver.cs:476`, `Storyteller.cs:278`). Limit to take_turn/combat.
- A7. Identical rolls deduped into one card (`SegmentSplitter.cs:182`, damage not in key).
- A8. `TurnBrief` ignores `fullScene`, `partyDelta`, `worldStateDelta` (`Storyteller.cs:298-306`) — misses the room description on travel turns.
- A9. `TrimToBudget` runs once before the loop (`OpenAiChatDriver.cs:278`); 8 iterations × 20k-char results can blow `MaxHistoryChars`.
- A10. take_turn timeout (180 s) → model resends → double commit; no idempotency key.
- A11. Whole-request 120 s timeout kills slow local-model streams mid-scene (`OpenAiChatDriver.cs:1144`); needs inactivity timeout.
- A12. Errors inside a 200 SSE stream retried as transient (`:761`).
- A13. `McpClient.AbortAll` (`:91-95`) kills unrelated in-flight requests (RefreshTable, codex search).
- A14. API key survives preset/host change (`AI/ByokSettings.cs:135-141`, `VaultController.cs:1887`) → key sent to wrong provider.
- A15. Keys plaintext in `vault-providers.json`, no chmod 600, corrupt file silently overwritten (`ByokSettings.cs:174-178,197`).
- A16. `TestConnection` only GETs `/models`; wrong key passes on providers with a public models list (`ByokSettings.cs:222-252`).
- A17. Zip-installed plugin skills never loaded; disabled plugins' skills stay indexed; index cached for process life (`SystemPromptProvider.cs:200-209`).
- A18. Level-up applied client-side with no transcript line / prompt update, no `IsBusy` guard (`VaultController.LevelUp.cs:46-79`).
- A19. Mid-campaign built PC not added to `PartyLine` (`VaultController.Builder.cs:644-685`).
- A20. History trimming is silent, so `forceFullReseed` after compaction never triggers (`OpenAiChatDriver.cs:1066`).
- A21. Retry removes failed segments from screen but chronicle file already has them (`VaultController.cs:493-501` vs `:548-552`).
- A22. `StartEmbedded` overwrites `Config.ServerUrl` before health (`Server/ServerHostManager.cs:176`).
- A23. Esc closes onboarding/builder overlays with no discard guard (`UI/VaultClientUI.cs:273-277`, `UI/Layers.cs`).

### Low
- A24. Two-pass mode: loop prompt still carries NARRATION + LAST STEP blocks (~2.6k chars) and "Load dnd-narration" — wasted loop prose and an 18k skill load that's thrown away (`Storyteller.cs:74-84` vs system-prompt).
- A25. `reasoning_details` fragments stored unmerged (`ChatStream.cs:124`).
- A26. Non-SSE 200 reply >64 KB truncated (`ChatStream.cs:201`).
- A27. `CheckConnection` resets MCP sessions each call (`VaultController.cs:1434`).
- A28. IPv6 loopback `[::1]` rejected (`Net/TextSanitizer.cs:87-92`, `ServerHostManager.cs:89-96`).
- A29. Truncation can split surrogate pairs (`TextSanitizer.cs:30-33`).
- A30. SSE fallback takes the last event without matching JSON-RPC id (`McpClient.cs:381-417`).
- A31. MiniJson: non-ASCII digits accepted; all numbers double (`Json/MiniJson.cs:306-319`).
- A32. Client guesses proficiency from listed modifiers (`Model/CharacterSheet.cs:306-318`); server should send ranks.
- A33. Misplaced `<summary>` docs (`VaultController.cs:368,1874`).
- A34. Tool allowlist not persisted, mixes /play and /build (`VaultController.cs:1390-1401`).
- A35. No periodic health check; "Healthy" sticks after server death.

### Flows & UX gaps
- Onboarding: rejection is a toast, not inline; no Back (only START OVER); seeding progress is just a status line; `SetupPending` forces OOC with no visible banner.
- Builder: 3 sequential calls per choice (steps/options/preview, `Builder.cs:176-178`), no debounce; ~20 controls + Ask-DM + DM-fill on one screen.
- Play loop missing: edit/resend last message, undo last turn, copy transcript (only F12 export), scroll past 160 segments (`TranscriptStore.LoadLimit`).
- Level-up only surfaced via start_session toast (depends on A1).
- End session: 6 hand-typed fields incl. "id | stance" needing internal NPC ids; no DM-drafted handoff; no confirm on END.
- Plugins: typed path, no file picker. Campaign delete: two-click, no disarm timeout.
- Unrepresented states: server down mid-play, provider problem (toast only), campaign with no PCs in party frame.

### Design
- MVVM mostly consistent; leaks: `TopBarViewModel.cs:93,159`, `CommandBarViewModel.cs:158`, `ProviderFormViewModel.cs:188-252`, `SettingsOverlay.cs:80`, `MeasurePage.cs:86`.
- `VaultController` god object (~3.2k lines). Seams: SessionService, TurnService, OnboardingService (932-1349), BuilderService, EmbeddedServerService + PluginService (1491-1847), ProviderService (1853-1948), CampaignService. `VaultAppState` is 568 lines of public mutable fields with one bit-flag event.
- Driver mixes transport/retry/tool loop/compaction/storyteller/usage/brainstorm; brainstorm shares `IsBusy` (`OpenAiChatDriver.cs:657-700`).
- Client/server duplication: roll parsing of English resolver text, `Dnd5eMod`/proficiency guessing, `Slugify`, onboarding answerType guessing, Pascal/camel key fallbacks → no shared DTOs. Fix: additive structured `rolls[]` + `ranks` in take_turn/get_entity output.
- Magic numbers scattered (MaxToolIterations 8, MaxHistoryChars 48000, 400/280 compaction, MaxResultChars 20000 clipped twice, 3×6000 passages, 4800 brief, 2400 scene, 160 load, 400 segments).
- Prompt-cache unfriendly: `CompactFinishedTurns` rewrites the previous turn every turn (`:1002-1039`) so the cacheable prefix ends after system prompt + tools. Compact only turns older than K, in batches.
- Token cost per turn (chars/4): loop call ≈6.5k fixed (prompt 8.1k + skill index 2.5k + contract 1.3k chars ≈3k tokens; /play tools ≤13.2k chars ≈3.3k) + history ≤12k; narration call 7–12k; typical turn 20–35k input, more on reseed turns.
- English literals everywhere, no localization table. Persistence scattered over PlayerPrefs + 5 files, no version field.

---

## B. Engine + rules (src/CampaignVault/)

Items already listed as deferred in RULES_GAPS.md / MECHANICS_REMEDIATION_PLAN.md are excluded. B1 and B3 verified by hand.

### High
- **B1. 5e spell-slot tables wrong above level 5** — `RulesetData/dnd5e/pools/spell_slots_3..7.yaml`: 3rd-level stays at 2 through 20 (SRD: 3 from level 6); 4th `9:2` (SRD 3 from 9); 5th never reaches 3 (SRD: 3 at 18); 6th lacks `19:2`, 7th lacks `20:2`. Every full caster level 6+ is short of slots. No test covers these levels.
- **B2. Pact Magic at warlock 2** — `pools/pact_magic.yaml:16-17` `1:1, 3:2`; SRD gives 2 slots at level 2. Test only checks 3 and 11 (`ResourcePoolInitializerTests.cs:309-345`).
- **B3. Death-save tally never reset on stabilising** — `Data/ChangeHandlers/DeathSaveChangeHandler.cs:117-121` sets `Stable=true` leaving `Successes=3`; `Data/DamageApplication.cs:169-171` flips `Stable=false` and adds failures to the old tally, so the next single success re-stabilises. SRD: both counts reset.
- **B4. Sorcery points cap at 9** — `pools/font_of_magic.yaml:10-19`; SRD = sorcerer level up to 20.
- **B5. PF2e spells all cost 1 action** — `Rulesets/Pf2eRulesetResolver.cs:725` defaults `cost = 1`; nothing maps `castingTime` → `actionCost`. 5e bonus-action/reaction spells consume the main action unless caller passes `bonusAction`.

### Medium
- B6. Channel Divinity `pools/channel_divinity.yaml:10` `1:1` shared by cleric/paladin; cleric gets it at 2, paladin at 3; paladin 5.1 stays at 1 use.
- B7. Wild Shape `pools/wildshape_uses.yaml:11-15` scales 3→6; SRD 5.1 is 2 always.
- B8. Missing SRD pools: Rage (`classes/barbarian.yaml:8` `pools: []`), Lay on Hands, Arcane Recovery, Indomitable.
- B9. PF2e untrained weapons add level: `Rulesets/AttackDerivation.cs:112-113` `Level + rank` even at rank 0 (armor and proficiency step handle it right: `ArmorParameterResolver.cs:73`, `Pf2eDeriveProficiencyStep.cs:124`).
- B10. PF2e thrown weapons get no Str damage: `AttackDerivation.cs:131` `if (!weapon.Ranged)`; javelin tagged `[ranged, simple, thrown]`.
- B11. Melee spell attacks treated as ranged: `Rulesets/RulesetResolverBase.cs:939` returns "ranged" for any spell; Shocking Grasp vs prone gets disadvantage, never auto-crits paralyzed.
- B12. Ammo rider dice not doubled on crit: `Dnd5eRulesetResolver.cs:350-353` (extra/sneak dice are doubled at 342-343, 438-441).
- B13. PF2e shield AC always on: `ArmorParameterResolver.cs:123` `contributesAc = … || isShield`; Player Core only when raised (the YAML says so itself).
- B14. Never-read item fields: `strMinimum`, `stealthDisadvantage`, `checkPenalty`, PF2e `strength`, `hardness`, `damageVersatile` (grep 0 hits) → heavy-armor speed, stealth disadvantage, PF2e check penalty, versatile damage all absent.
- B15. Reach weapons mapped to `Near` (31–80 ft) in `scripts/generate_items.py:109-110`, `generate_pf2e_items.py:111`; should be Close.
- B16. No shipped item has `longRange` (grep 0) → longbow refuses a Distant target rather than rolling with disadvantage.
- B17. PF2e Grapple/Escape: crit success = success (`Pf2eRulesetResolver.cs:454-458`, should restrain), crit fail no effect, Escape DC uses escaper's skill against the grappler's same skill (`:470`), neither counts for MAP (`:742`).
- B18. 5e grapple defender always Athletics (`Dnd5eRulesetResolver.cs:1301-1303`); SRD allows Acrobatics; no size check.
- B19. PF2e conditions text-only: enfeebled only hits Athletics (`pf2e/conditions/enfeebled.yaml`); drained no HP loss; stupefied no spell DC/flat check; frightened never decrements end of turn.
- B20. 5e petrified lacks resistance/immunities (`dnd5e/conditions/petrified.yaml`).
- B21. 5e unconscious `suppresses: [incapacitated, prone]` → ranged attacker gets advantage instead of straight roll.
- B22. Long rest: `Data/ChangeHandlers/RestChangeHandler.cs:105` explicit `LongRest` accepted at any `intendedHours`, no once-per-24h; `:216-226` heals a stable 0-HP PC to full (SRD requires ≥1 HP).
- B23. Spell pairs with a lower slot: `Data/SpellBookkeeping.cs:43-46` any negative spend on any slot pool counts.
- B24. PF2e concentration hint fires for 357/373 spells: `generate_spells.py:740-742` maps `concentrate` trait → `concentration:true`; `SpellSlotValidator.cs:49-52` nags.
- B25. PF2e summons bound to concentration (`RulesetResolverBase.cs:393,539`).
- B26. PF2e spells all `verbal:true` (`generate_spells.py:798`); remaster has no components.
- B27. PF2e spell YAML has no damage/save/area/range/heightening (grep 0 of 373) — all mechanics up to the caller.
- B28. PF2e spellcasting proficiency stops at Master (`Pf2eDeriveProficiencyStep.cs:76-78`); Legendary at 19.
- B29. Range disadvantage folded before pipeline: `RulesetActionHandler.cs:200` collapses caller Adv + range Disadv to None, then a later condition adv yields Advantage (RAW: straight roll). Also applies 5e-style disadvantage to PF2e long range.

### Low
- B30. Ranged-in-melee ignores incapacitated hostile; thrown never gets it (`RangeValidationHelper.cs:131-150`). B31. `autoCrit range: melee` also crits reach from 10 ft. B32. PF2e Sneak Attack doesn't require agile/finesse/ranged (`Pf2eRulesetResolver.cs:331-339`). B33. PF2e save fallback = ability mod only (`:111-131`), should be trained. B34. PF2e attack folds target AC without `other: actorChar` (`:276`). B35. PF2e feat resistance halves (`DamageResolver.cs:174`), PF2e is flat. B36. Deprivation fixed 3 days (`SurvivalDeprivationRule.cs:63`; SRD 3+Con); `Order => 38` shared with `ResourceRecoveryRule`. B37. Stale comment `ConditionExpiryEvaluator.cs:66-69` (invisible.yaml does use Concentration). B38. No stabilise path without death saves (Spare the Dying / Medicine DC 10). B39. ~200 PF2e feats for ancestries with no file (catfolk, hobgoblin, kholo, kobold, lizardfolk, ratfolk, tengu, tripkee) and hundreds for classes with no file (PC2: alchemist, barbarian, champion, monk, sorcerer, swashbuckler, investigator, oracle; non-core: animist, commander, exemplar, gunslinger, magus, necromancer, psychic, summoner, thaumaturge); `generate_pf2e_classes.py:36` queries only "Player Core". B40. AoN queries never check `hits.total` vs `size` (`generate_pf2e_feats.py:79`). B41. 5e races have no subraces; dragonborn no resistance. B42. RULES_GAPS.md lists PF2e versatile heritages as both built and skipped.

### Tests
- 2,216 `[Fact]`/`[Theory]` in UnitTests; ~4 Docker-gated integration tests. Skipped: `CampaignToolsTests.cs:116` (exhaustion timing nondeterministic in parallel buckets), `:144` (rate-limit boundary).
- No tests for: 5e slot counts 6–20, warlock 2, Channel Divinity/Wild Shape counts, sorcerer >9, stabilise→damage→save, PF2e action cost / thrown Str / untrained / grapple crit.

---

## C. Licensing

### High
- **C1. No attribution notice.** No NOTICE file; LICENSING.md/README say only "Attribution: Wizards of the Coast LLC" / "Paizo Inc.". CC-BY 4.0 needs the SRD 5.1 statement ("This work includes material taken from the System Reference Document 5.1 ('SRD 5.1') by Wizards of the Coast LLC… licensed under CC-BY-4.0, https://creativecommons.org/licenses/by/4.0/legalcode"). ORC needs the ORC Notice (attribution + Reserved Material declaration). Neither server nor Unity client surfaces either (grep `SRD|Paizo|ORC` in `UnityClient/Assets`, `Tools/*.cs`: 0).
- **C2. PolyForm appears to cover the game data.** Root `LICENSE` has no carve-out for `src/CampaignVault/RulesetData/**`; CC-BY §2(a)(5)(B) and ORC forbid adding restrictions. Needs an explicit exception.

### Medium
- C3. Product Identity in shipped strings: `Data/ChangeHandlers/RestChangeHandler.cs:112` "Leomund's Tiny Hut" (SRD: "Tiny Hut"); `Tools/CampaignManagementTools.cs:96` example `"Waterdeep: Dragon Heist"`; `claude_skills/dnd-exploration/SKILL.md:25` `locations/neverwinter`.
- C4. PF2e feats outside Player Core/PC2 ship (e.g. `pf2e/feats/echoing_channel.yaml` prereq "[Animist] …" from War of Immortals; ~30 feats naming non-core classes). ORC material but outside the scope LICENSING.md claims.
- C5. PF2e creatures (`pf2e/creatures/goblin_warrior`, `ogre`, `skeleton_guard`, `wolf`) are Monster Core, no source/license header, stats don't match the book.

### Low
- C6. PI in tests/docs: `LazyLlmScenarios.cs:89` "Drizzt Do'Urden", `CampaignSlugTests.cs:13` "curse-of-strahd", `NameSearchIndexTests.cs:11-37` + `Services/NameSearchIndex.cs:137` "Leomund's", `docs/superpowers/specs/2026-06-11-avalonia-authoring-app-design.md` (Waterdeep, Harpers, Undermountain).
- C7. SWADE: `RulesetData/swade/*` only `.gitkeep` (csproj embeds `RulesetData/**/*.yaml` so anything added ships); `plugins/ShadowAndSteel` ships 15 `swade/items/*.yaml` in SWADE notation and names "swade" in README. Savage Worlds is not open-licensed; mechanics aren't copyrightable but the name is a Pinnacle trademark.
- C8. Clean: no named-wizard spells, 32 invocations SRD, subclasses/Grappler/Acolyte match; no legacy PF2e terms (flat-footed/alignment/drow 0 hits); `guards_and_wards.yaml` "umber hulk blood" is verbatim SRD text (fine).

## D. MCP surface, prompts, skills, token use

Numbers are chars counted from source and the repo's own budget tests (`tests/CampaignVault.UnitTests/ToolListBudgetTests.cs:15-29`); tokens ≈ chars/4. Nothing was run live.

### tools/list
| Route | Stub mode | Full mode |
|---|---|---|
| `/` (17 listed; ARCHITECTURE says 15) | ~19.5k chars ≈4.9k tok | +~7k tok of take_turn `$defs` |
| `/play` (10 tools) | ~12.5k ≈3.1k tok | ~10k tok |
| `/build` (11 listed) | ~11.4k ≈2.9k tok | same |

- D1. `take_turn` description (`Tools/MutationTools.cs:288-296`, 1025 chars) restates the prompt's EVERY-take_turn section (includeParty rule, fullDetailLocationId-on-travel, ruleset_action auto-apply, fingerprint echo, refresh list given twice). Its 388 chars of `[Description]` on the parameter are dead (schema is replaced).
- D2. `lookup` has 14 params with reflection noise: nullable params emit `"type":["string","null"],"default":null` (~25 chars each, ~325 on lookup alone). `combat`, `advance_world`, `create_campaign` similar. `campaignName` + description repeats on 10 /play tools (~700 chars). `lookup` requires `campaignName` even for `commit_schema`/`help` (`CampaignManagementTools.cs:302-305`).
- D3. /build descriptions carry inline examples (`create_campaign`, `submit_onboarding_answer`, `delete_campaign`, `finalize_campaign_onboarding`, `start_campaign_onboarding`) that belong in `lookup kind=help topic=onboarding` (`DmHelpManual.cs:174-187`). `list_campaigns` wordy.
- D4. Output schemas already collapsed to `{"type":"object"}` and stripped from tools/list (`McpSchemaInstaller.cs:61-70`, `McpResponseCleaner.cs:69-78`): good. Serializer compact, nulls/empties stripped (`McpResponseCleaner.cs:50-54,160-209`): good. StructuredContent off by default: good.
- D5. Fragility: `McpSchemaInstaller` relies on `TryGetPrimitive("take_turn")`; if it misses, the SDK ships `TakeTurnRequest`'s reflection schema (~4k chars of dead descriptions). Only the budget test catches it.

### Per-call response weight
- D6. **Deserialization error ≈4.2k chars (~1k tok)**: STJ message + 2,507-char `DeserializationHint` (emoji/caps, combat+summons tips, `Tools/ToolCallExamples.cs:463-480`) + 639-char retry template twice (in summary and as `retryExample`, `:373-387`). Never lists valid `$type`s or the failing type's fields. world_build same pattern (961-char hint). Error results also skip the cleaner (`McpResponseCleaner.cs:108`).
- D7. **Unknown fields silently dropped**: `CommitChangesParser.CreateOptions` (`:37-42`) and `WorldChangeArrayJsonConverter` set no `UnmappedMemberHandling`. Under Stub mode a misnamed field (`target` vs `targetId`) vanishes without a word. Main coherence risk of the stub approach.
- D8. Wrong enum: `ModelEnumErrorHints.Enrich` called with `source: null` (`McpToolErrorFilter.cs:43`) so the bad value / "did you mean" is often missing while the 2.5k hint is still appended. Unknown `$type`: raw STJ message + hint, no valid list (`WorldChangeTypeRegistry.cs:146`). Missing `$type` → `InvalidOperationException`, not `JsonException`, may bypass the friendly filter (unverified).
- D9. `sceneCue` (152 chars, `Tools/SceneCue.cs:10-11`) on every committed call; a quiet beat is ~344 chars before it (`ResponseBudgetTests.cs:22-29`) so ≈30% of a quiet beat; restates prompt + narration skill; drops the 600–900 tense range; Unity two-pass has to be told to ignore it (`Storyteller.cs:82`).
- D10. Always-on noise: `worldSequence` (documented "not used", `Models/TurnModels.cs:226-228`), envelope `summary` "Committed N changes." duplicating `committed`+`changesProcessed`, `committed:false`/`changesProcessed:0` on pure queries, confirmation-only `summary[]` lines (`TurnModels.cs:132-136`), `partyDelta[].changes` echoing the model's own changes (`:282-283`). Validation failure returns `new TurnResult()` adding the same noise.
- D11. Duplicates: `suggestedCommitExamples` repeats each pressure's `SuggestedCommitJson` already appended to the WorldPressure string (`PressureManager.cs:241-247` vs `SuggestedCommitExampleBuilder.cs:38-42`, `MutationTools.cs:2564`), incl. placeholder ids. `start_session`: `campaign.pcs`/`companions` duplicate `party[]` (`SessionStartView.cs:53-71`); constant `isSystemLocked:true`, `resumed:false`, `levelUpReady:false`; `entryHint` enum has no string converter (may serialize numeric, unverified).
- D12. `get_entity chars/` (`ExplorationTools.cs:240-256`, `NpcViews.cs:21-127`): `character.needs` AND `knownNeeds`+`needDescriptors`; ALL `psychology.memories` AND `relevantMemories`; `tensionComponents` debug breakdown; full `classFeatures` rule text; full `systemStats` always. `get_entity factions/` returns the raw document (`DeepDiveTools.cs`).
- D13. Guidance repetition: `GuidanceLedger` does suppress repeats (`GuidanceOrchestrator.cs:62-66`, 2 hints/600 chars, cleared at start_session). Advisory "Hint:" lines dedup once per session (`MutationTools.cs:1147-1170`) but novelty/departure/nudge reminders don't. WorldPressure suppressed on delta when unchanged; full on Full turns.

### Prompts and skills
| File | chars | ≈tok |
|---|---|---|
| recommended-system-prompt.md (fence) | 8.4k | 2.1k |
| dnd-narration | 18.5k | 4.6k |
| dnd-world-change | 13.5k | 3.4k |
| dnd-combat | 10.5k | 2.6k |
| dnd-exploration | 7.9k | 2.0k |
| dnd-world-building | 6.5k | 1.6k |
| dnd-npc-interaction | 5.2k | 1.3k |
| dnd-bundling / pf2e-combat | 4.6k / 4.5k | 1.2k / 1.1k |
| campaign-events / social / conversation | 3.3k / 2.3k / 2.3k | 0.8 / 0.6 / 0.6k |
| all 11 skills | 79k | 19.8k |

- D14. Rules stated in ≥3 places (prompt / skill / tool desc or response): 350–600 words; fullDetailLocationId on travel (7 places); includeParty rule; ruleset_action auto-apply (5); utility spell status+resource; atomic batch; fingerprint echo; sparse no nulls; ENGINE WARNING → next take_turn; one beat per call; commit_schema only on failure.
- D15. Contradictions: (a) dnd-world-change L31/L129 "every take_turn commits… never call it to look" vs prompt L49 + start_session summary ordering a pure-refresh take_turn; code: pure queries don't commit. (b) dnd-combat Fire Bolt example sends `bonus:5`/Fireball `dc` while cheat sheet + hint say omit (passing replaces derived). (c) actionType list: prompt 5 values, narrative prompt 8, dnd-combat 5, code 8 (incl. Recovery used by Second Wind). (d) roll display: base prompt italic block vs `grok/project-prompt.md` ROLL SUMMARY header. (e) `opencode-plugin/src/campaignReinject.ts:41-49` tells the model to re-verify via get_entity/start_session; prompt says start_session never mid-play. (f) sceneCue omits the 600–900 range. (g) `MetaTools.cs` help says guidance arrives under `guidance`; field is `guidanceHints`, `ToolResult.Guidance` never filled.
- D16. Stale refs: `ToolCallExamples.cs:465` + `WorldChangeConverter.cs` "see WorldChange's own description" (never advertised); `ToolCallExamples.cs:516` "upsert_* tool"; `DmHelpManual.cs:246` "inspect world_build's input schema" (now id/name only) while dnd-world-building L10 points back to help (circular); FAQ `DmHelpManual.cs:116-120` mentions "creates" and "scene fetch". Prompt `$type VOCABULARY` + dnd-world-change table omit 5 authorable types: `grant_temp_hp` (used by dnd-combat L121), `apply_effect`, `tether`, `soil`, `piercing`. `HpChange.critical` "set by engine" but exposed (`WorldChanges.cs:199-202`).
- D17. Skill overlap: dnd-conversation + dnd-social mostly point to dnd-npc-interaction (a talk beat loads all three, ~2.4k tok). pf2e-combat says "flow in dnd-combat is the same" so PF2e loads both (~3.7k) incl. 5e-only material. dnd-narration demands loading "every scene"; Unity drops loads each turn (`SystemPromptProvider.cs:219-220`) → **4.6k tok per beat**.
- D18. Test gap: `GuidanceCorpusTests.SkillFiles_ReferenceOnlyLiveTools` (`:56-62`) never runs (path resolves to `tests/claude_skills`, returns early). `GuidanceExampleTests` (`:40-95`) does parse every JSON example with `UnmappedMemberHandling.Disallow` — good, but it doesn't check skill coverage of fields.

### Loop inefficiencies
- D19. Opening every session is two model calls: start_session → "Load the scene: take_turn fullDetailLocationId" (`SessionTools.cs:405`, by design per `SessionStartView.cs:9-10`).
- D20. First contact with an NPC = two commits (prompt L22) even when the card already arrived with the scene.
- D21. Verifying an ENGINE WARNING fix requires `includeWorldState:true` (full rebuild).
- D22. Predictable first-attempt failures under Stub: nested shapes never advertised (`status.effect`, `ruleset_action.parameters`, `location_update.addExit`, `item_update.upsertItemDetail`, `scene_setup.engagement/spatial`, `character_update.minionBinding`); knowledge_update `sourceEventIds` when Witnessed/Experienced; Conversation needs `involved`. Each failure ≈1k tok + a commit_schema call that still lacks types.
- D23. world_build on /play: stub gives id/name only; help topic=world-building has one example and points back at the schema.
- D24. opencode re-injection after every idle (`index.ts:93-108`), not only compactions.

---

## E. Schema-on-demand assessment (stub in tools/list, schema in skills)

**Verdict: the idea holds, and it is already half-built. The tools/list half works; the on-demand half does not, which is why it currently costs coherence instead of saving tokens.**

What exists today (own reading):
- `ToolSchemaMode.Stub` is the default (`Schema/McpSchemaInstaller.cs:75-78`). Stub advertises the request envelope with field descriptions plus `changes.items = {$type:string, additionalProperties:true}` and a pointer to skills / `lookup kind=commit_schema` (`Schema/TakeTurnSchemaBuilder.cs:148-166`). Constant by design so client caches never go stale. `end_session` and `world_build` are also hand-built. OutputSchemas collapsed. This is the right shape for the .NET SDK: a `PostConfigure<McpServerOptions>` swap of `ProtocolTool.InputSchema`, cached per mode; the SDK does not regenerate on `list_changed` or per-route filtering (`ToolProfiles.Filter` reuses instances; `SlimTool` wraps world_build on /play).
- The "schema in skills" half is **not** true: dnd-world-change carries an area table plus required-field notes for 7 of 46 authorable types (~300 fields), no types, no nested shapes. `lookup kind=commit_schema type=X` (`Tools/CommitSchemaRegistry.cs:88-97`) emits field **names** with enum values and required hints only; no JSON type, no description, no nested object shape. Yet ≥9 `WorldChanges.cs` field descriptions say "See lookup kind=commit_schema…" (`:240,561,597,1161,1387,1419,1465,1481,1537`), and under Stub those descriptions are never sent anywhere. So the model's only source for nested fields is the examples.
- Failure path is expensive and uninformative (D6–D8), and wrong field names are silently dropped (D7). That combination is the real cost: not tokens on tools/list, but wasted commits and retries the model can't diagnose.

Why it can hold water:
- Field descriptions in a schema are paid on every model call by hosts that don't cache tools (and the Unity client re-sends all tool schemas every request). Skills are paid only when loaded. A 46-type `$defs` is ~7k tok per call; the stub is ~0.7k. The saving is real.
- MCP hosts with skill support (Claude Code, opencode, the Unity client's `load_skill`) already load `dnd-world-change` before the first take_turn, so a compact field reference inside it is "free" relative to today.
- Hosts without skills (Grok web, bare API) fall back to `lookup kind=commit_schema`, which only needs to become complete.

What has to change for it to work (the plan's Phase 2):
1. **Single source of truth**: generate a compact FIELDS reference from `CommitSchemaModel` (per type: `name:type` list, enum values, `[required]`/conditional hints, nested object shapes one level down, one-line description capped ~60 chars). Emit it (a) as the body of `lookup kind=commit_schema type=X`, (b) as an index `lookup kind=commit_schema` (no type) listing all 46 authorable types with 60-char summaries, (c) as a generated section appended to `claude_skills/dnd-world-change/SKILL.md` (and the grok/opencode copies via `scripts/deploy-skills.sh`), guarded by a golden-file test that fails when the generated section drifts from `CommitSchemaModel`. Budget the skill section to ≤4k chars for hot-tier types inline; cold-tier types only in the index with a pointer. Token cost: dnd-world-change grows ~1k tok once per session; saves 1–3 failed round trips (~1–3k tok each) per unfamiliar type.
2. **Make Stub safe**: `UnmappedMemberHandling.Disallow` on the WorldChange converter, with the error naming the unknown field, the `$type`, and that type's compact field list (from the same generator). Replace the 2.5k `DeserializationHint` and the doubled retry template with: failing path + compact field list for the failing `$type` (or the valid `$type` list when `$type` is unknown/missing) + one retry example. Pass the real source to `ModelEnumErrorHints.Enrich`. Target ≤600 chars per error.
3. **Prune prompt/skill redundancy around it**: the stub description, the system prompt TOOL HYGIENE line, and the skill all say "commit_schema only on failure"; keep one.
4. Keep `Full` mode available for hosts with tool caching where a one-time 7k is cheaper than skill loads; document when to pick which (README/INSTALLATION).

What does not hold: putting the *whole* schema into skills and loading them "every scene" (dnd-narration's current instruction) — in the Unity client that is 4.6k tok per beat, more than Full mode would cost. On-demand only wins when the load is rare (once per session per topic) and the loaded text is compact. So the skill split should be: a small always-loaded core (bundling + world-change core + narration core ≈3k tok) and rarely-loaded references (fields, combat per system, world-building).

## F. Remediation plan (phased)

**Decisions (Michael, 2026-10-15):** implement all phases 0–5 in order; PF2e feats filtered to shipped classes/ancestries by the generator (C4/B39); sceneCue shortened to ≤60 chars with a `CampaignConfig` opt-out (D9); skills merged/split as in 3f including the dnd-narration core/examples split (A/B with prose_stats).

Every phase ends with a green full suite (`/verify`; `dotnet test` exits 5 here, so build once then run the xunit v3 executables; the two `LocalEmbeddingServiceTests` failures are environmental without git-lfs). Each phase is checked off here as it lands. Groomed per-ticket plans go in the repo root per Michael's convention.

### Phase 0 — Licensing hygiene (DONE 2026-10-15; suite 2602/0 failed/2 skipped)
- [x] Add `NOTICE.md` with the exact SRD 5.1 CC-BY-4.0 statement and an ORC Notice (attribution to Paizo; Reserved Material = none of ours; Licensed Material = `RulesetData/pf2e/**`). Link it from README.md and LICENSING.md.
- [x] `LICENSE`: add a carve-out paragraph stating `src/CampaignVault/RulesetData/dnd5e/**` is CC-BY-4.0 and `RulesetData/pf2e/**` is ORC, not PolyForm. Mirror in LICENSING.md summary table.
- [x] Surface the notice: `lookup kind=help topic=licensing` (new entry in `Tools/DmHelpManual.cs`) and a Settings → About line in the Unity client (`UI/Settings`), reading NOTICE.md from StreamingAssets (stage it via `Editor/DmContentStager.cs`).
- [x] Strip PI strings: `RestChangeHandler.cs:112` → "Tiny Hut"; `CampaignManagementTools.cs:96` → a generic example; `dnd-exploration/SKILL.md:25` → `locations/harrowgate` (then `scripts/deploy-skills.sh` + restage); test fixtures renamed (docs/superpowers spec left as-is: not shipped).
- [x] PF2e scope: add `# Source:` headers to `pf2e/creatures/*.yaml` and declare them in LICENSING.md as original stat blocks (or Monster Core ORC); C4 resolved: generator filter on shipped classes/ancestries (+ versatile heritages); 590 orphan feats pruned, 37 class lists trimmed; `ContentLicensingTests.Pf2eFeats_ReferenceOnlyShippedClassesAndAncestries` pins it.
- [x] SWADE: README note in `plugins/ShadowAndSteel` that notation is original; trademark disclaimer added to the plugin README.
- [x] Test: `ContentLicensingTests.ShippedContent_NamesNoProductIdentity` greps shipped `RulesetData/**`, `claude_skills/**`, `Tools/**` for a PI blocklist (Waterdeep, Neverwinter, Faerûn, Golarion, Absalom, deity names, iconic names, "Leomund's" etc.).

### Phase 1 — Rule data fixes (DONE 2026-10-15; suite 2647/0 failed/2 skipped)
- [x] `pools/spell_slots_1..9.yaml`: rewrite `levelToMaxMap` from the SRD full-caster table; `pact_magic.yaml` (`2:2`); `font_of_magic.yaml` (= level to 20); `channel_divinity.yaml` split per class (cleric 2:1, 6:2, 18:3; paladin 3:1); `wildshape_uses.yaml` (2 flat).
- [x] Add pools: rage (barbarian table), lay_on_hands (5×level), arcane_recovery, indomitable. Wire into `classes/*.yaml` `pools:`.
- [x] `conditions/petrified.yaml` resistance all + immunities poison/disease; `unconscious.yaml` drop `prone` from `suppresses` (keep incapacitated).
- [x] Generators: `generate_items.py` / `generate_pf2e_items.py` reach → Close; add `longRange` for ranged weapons (5e `range: 80/320` style parse; `scripts/item_overlay.yaml` if the API lacks it); `generate_spells.py`: PF2e `concentrate` trait ≠ concentration (map to a `sustain`/trait field), drop hardcoded `verbal:true` for PF2e, emit `actionCost` from `castingTime`; add `hits.total` vs `size` assertions in all AoN scripts. Regenerate (needs network) and commit.
- [x] Tests: `ResourcePoolInitializerTests` table-driven across levels 1–20 for wizard, cleric, warlock, sorcerer, paladin, druid; `ConditionDefinitionTests` for petrified/unconscious; item range band tests.
- Notes: `channel_divinity` uses a new `classLevelToMaxMap` (PluginSdk `ResourcePoolTemplate` + `ResourcePoolInitializer.TryResolveClassTable`). Conditions gained a `resistance` effect kind (`ConditionEffectKinds.Resistance`, `ConditionEffect.DamageType`, fed into feat-effect resistance by `RulesetActionHandler.AddConditionResistances`; `FeatEffectRules.NamesType` accepts `all`). Spell YAML now carries `castKind` (action|bonus|reaction|long), 5e `attackType` (melee|ranged) and `ritual`, PF2e `actionCost`, `saveType`, `basicSave`; PF2e `concentration`/`verbal` are false. Items carry `longRange`; reach is `Close`. Generator keeps hand-kept 5e spell `tags`. Tests: `SrdPoolTableTests`, condition data tests; five old tests updated to SRD/Remaster expectations.

### Phase 2 — Rule code fixes (DONE 2026-10-15; suite 2677/0 failed/2 skipped)
- [x] B3: `DeathSaveChangeHandler` reset `Successes`/`Failures` to 0 when `Stable` becomes true; `DamageApplication` resets successes when unstabilising. Test stabilise→damage→save.
- [x] B5: `Pf2eRulesetResolver.cs:725` read `actionCost` from the spell definition; 5e: casting time `bonus action` → bonus-action budget, `reaction` → reaction. Tests.
- [x] B9/B10: `AttackDerivation.cs:112-113` untrained → 0 (no level); `:131` thrown adds Str (propulsive half). Tests.
- [x] B11: spell YAML `attackType: melee|ranged` (generator from `range: Touch` for 5e; PF2e `range: touch`); `RulesetResolverBase.cs:939` reads it.
- [x] B12: double rider dice on crit (`Dnd5eRulesetResolver.cs:350-353`).
- [x] B13: PF2e shield contributes only when a `raised_shield` status is present (status set by a `ruleset_action` "Raise a Shield", cleared at turn start in `CombatTools next`).
- [x] B14: read `strMinimum` (speed −10), `stealthDisadvantage` (Stealth checks), PF2e `checkPenalty`, `damageVersatile` (parameters.twoHanded). Keep `hardness` deferred (note in RULES_GAPS).
- [x] B17/B18: PF2e grapple degrees (crit success → restrained, crit fail → grappler off-guard/falls), Escape DC = grappler's Athletics DC, MAP for attack-trait actions; 5e defender choice via `parameters.defenderSkill`.
- [x] B22: long rest requires `intendedHours ≥ 8` (warn + treat as short otherwise) and ≥1 HP for 5e; B23: pairing requires slot level ≥ spell level; B24/B25: PF2e skip concentration hint/binding; B28: Legendary at 19; B29: run range disadvantage through `RollModifierPipeline` instead of pre-folding; PF2e long range = per-increment −2.
- [x] Low items B30–B38 as a batch where ≤10 lines each; B36 fix `Order` collision; B37 comment; B38 add `stabilize:true` on `death_save` (Spare the Dying / Medicine).
- [x] Update RULES_GAPS.md (fix B42 contradiction; list what remains).
- Notes: new `Rulesets/SpellActionDefaults.cs` (stamps `actionCost`/`bonusAction`/`IsReaction`/`attackType` from `SpellDefinition.CastKind|ActionCost|AttackType`; `[CastingTimeTooLong]` refusal in combat for `castKind: long`), `Rulesets/RangeRollModifier.cs` (handler stamps `rangeDisadvantage`/`rangePenalty`; `FoldAsync` folds them as one more source), `RulesetData/dnd5e/conditions/noisy_armor.yaml` (engine-managed via `ArmorParameterResolver`, as is the PF2e `engine:armor-check` status), `Pf2eRulesetResolver.TryResolveClassFeatureAsync` "Raise a Shield" (`pf2e:raised-shield` status, `ExpiresAtOwnTurnStart`, AC via `StatModifiers`), `EngagementMutationHelper.RestrainedCondition`, `DeathSaveChange.Stabilize`. B24/B25 are moot since Phase 1 made PF2e `concentration: false`. B31 (reach auto-crit), B35 (PF2e flat feat resistance), B18 size check and B19 stay deferred (RULES_GAPS). Numeric item properties parse invariant-first (the machine runs a Hebrew locale where `int.TryParse("-4")` fails). The long-rest-at-0-HP test was reversed to the SRD rule (1 HP, no other benefit). B22 changed a Mechanics-phase-8 decision: a stable 0-HP PC no longer heals to full on a long rest. pf2e-combat and dnd-combat skills (+ staged copies) describe the new behaviour; the rest of the skill work is Phase 3f.
- Tests: `Phase2RuleCodeTests.cs` (29 tests), `MechanicsPhase4Tests.Stabilise_ResetsTheTally_AndDamageStartsAFreshSet`, `MechanicsPhase8Tests.LongRest_StableCharacterAtZero_ComesRoundAtOneHpWithoutTheRestsBenefit`.

### Phase 3 — MCP token efficiency and coherence (server)
Order chosen so each step is independently shippable and measurable with `scripts/measure/take_turn_breakdown.py` on a scratch campaign (never real campaign data).
- [x] **3a Errors** (2026-10-15): `CommitChangesParser.CreateOptions` + `WorldChangeArrayJsonConverter.Inner` use `UnmappedMemberHandling.Disallow`; new `Tools/CommitBindingError.Describe` (≤600 chars: path, `$type`, nearest-name suggestion, that type's compact field list, or the valid `$type` list; enum/number hints via `ModelEnumErrorHints` with the real `request.changes` source); the normalizer's missing-`$type` case is a `JsonException` so both commit paths report it the same way; `actorId`→`characterId` alias now applies to every change type (strict mode would otherwise reject the known slip); `DeserializationHint` blobs deleted, retry template only in `retryExample`; error results go through `McpResponseCleaner.Clean` (no nulls). Tests: `CommitBindingErrorTests` (10).
- [x] **3b Schema generator** (2026-10-15): `Schema/CommitFieldReference.cs` (`name:type [req] (enum) {shape} — note`, nested shapes one level, shared `Clip`); `CommitSchemaRegistry.FormatField` and the 3a errors use it; generated `## Fields` section between `<!-- fields:begin/end -->` in `claude_skills/dnd-world-change/SKILL.md` (10 hot-tier types with names/types/enums, cold tier as an index; 3.6k chars, budget 4k) pinned by `GuidanceCorpusTests.WorldChangeSkill_FieldsSection_MatchesServer` (regenerate with `CV_WRITE_SKILL_FIELDS=1`; also checks the staged Unity copy); `SkillFiles_ReferenceOnlyLiveTools` now resolves the real repo root (`tests/RepoPaths.cs`); `lookup.campaignName` is optional (`McpToolMetadataTests` exempts `Lookup`). The skill's hand-written "Required fields" prose is deduped against the generated section in 3f.
- [x] **3c Response noise** (2026-10-15): `worldSequence` removed; `committed`/`changesProcessed` omitted when default (query calls); envelope summary only names warnings; `partyDelta[].changes` self-echo removed (`NeedsMoved` kept); `suggestedCommitExamples` no longer repeats pressure items' inline JSON (travel-stuck example kept); `sceneCue` = "Now the scene: 350–600 words (600–900 tense), sensory prose." (60 chars) with `CampaignConfig.SceneCueEnabled` + `campaign_update.sceneCueEnabled` opt-out; `get_entity chars/` drops `tensionComponents`, `relevantMemories`, `knownNeeds`, legend-only `needDescriptors`, `classFeatures` names-only unless new `includeCombatDetail` (Unity sheet passes true); `get_entity factions/` → `FactionDetailView`; `start_session` drops `campaign.pcs/companions` (Unity digest now built from `party[]`), `isSystemLocked`, default-omits `resumed`/`levelUpReady`, `CampaignEntryHint` serializes as a string. Confirmation-only summary lines were kept: every hot handler's line carries a post-state value (HP now X/Y, relationship now N). `ResponseBudgetTests`: repeat beat 410 chars (ceiling 450); arrival 2872, first contact 1610, after rest 1767.
- [x] **3d Opening scene** (2026-10-15): `start_session includeScene` (default true) returns `scene` via the new shared `Tools/FullSceneDetail.BuildAsync` (also used by take_turn `fullDetailLocationId`); summary, help topic=sessions, `SessionStartView` docs and the three prompt variants now say "narrate from scene, then take_turn"; Unity's per-turn `start_session` passes `includeScene:false` until Phase 4 removes that call. Test: `SessionHandoffTests` (scene present, no PCs in it, `includeScene:false` falls back to the old hint).
- [x] **3e tools/list** (2026-10-15): `McpSchemaInstaller.HandWrittenSchemas` for end_session, lookup, combat, advance_world, create_campaign, start_session, get_entity; take_turn description ≤400 chars (dead param descriptions reduced to stubs; a metadata test requires some text); /build tool descriptions one line each, call examples moved to help topic=onboarding "## Calls" (help cap 15k→16k, on-demand); measured tools/list 21,364→17,332, /play 13,482→11,123, /build 12,455→10,099, budgets set to 17,400/11,200/10,200; tests `TakeTurn_InstalledSchema_IsTheHandBuiltStub` (D5) and `HandWrittenSchemas_MatchTheirMethods` (every advertised property is a parameter, required ⇔ no default).
- [x] **3f Prompts/skills** (2026-10-15): D15 resolved (dnd-world-change says a changeless take_turn commits nothing; Fire Bolt/Fireball examples omit `bonus`/`dc`, the sheet supplies them; one actionType list of 8 in the prompt and the shared combat skill, with ContestedCheck/OpposedCheck, Recovery and UseItem documented from the resolver; `guidanceHints` in MetaTools and the prompt; the grok addendum now names the prompt's italic roll block; the opencode plugin re-injects only on `session.compacted` (D24) and tells the model to send `forceFullReseed`, never `start_session`). D16: help FAQ "creates"/"scene fetch" wording, world-building help points at `lookup kind=commit_schema type=<$type>` instead of the stub schema, its example lost the Forgotten Realms names (now in the PI blocklist), `HpChange.critical` says ruleset_action sets it; `grant_temp_hp`, `apply_effect`, `tether`, `soil`, `piercing` added to the prompt vocabulary and the skill table. D14: the prompt keeps one-line floors, the skills own the detail (take_turn's auto-apply line halved; hand-written "Required fields" deduped against the generated Fields section). Skills: `dnd-conversation` + `dnd-social` merged into `dnd-npc-interaction` (9,779 → 7,792 chars for a talk beat); `dnd-combat` split into the shared flow (6,186) + `dnd5e-combat` (6,456), so a PF2e fight loads 11,351 instead of 16,078; `dnd-narration` split into a core (7,405 chars ≈ 1.85k tokens: rules + the guarded-answer exemplar; the ~1k target would have cut the rules themselves, which Michael's narration feedback says matter) + `dnd-narration-examples` (7,828, on demand), so the Unity loop's per-turn load drops from 18,461 to 7,405; the Unity narration pass loads both (`Storyteller.NarrationSystemPrompt` 4-arg overload, EditMode test updated). Unity StreamingAssets restaged (new folders with .meta, merged ones removed); grok files updated (`pack_grok.py` reads `claude_skills/`, so the kit follows); `scripts/deploy-skills.sh` targets a user's campaign dir, so run it with `--prune` on your own deployments. Suite 2690/0 failed/2 skipped; opencode plugin 32/32.
- [ ] Measure before/after with `scripts/measure/take_turn_breakdown.py` and `prose_stats.py` on a scratch campaign (needs the Unity MEASURE panel against a staged server, so Michael runs it); record here. Already captured by tests: tools/list 21,364 → 17,332 chars, /play 13,482 → 11,123, /build 12,455 → 10,099 (`ToolListBudgetTests`); take_turn repeat beat 410 chars incl. the 60-char cue, arrival 2,872, first contact 1,610, after rest 1,767 (`ResponseBudgetTests`); deserialization error ≤600 chars (was ≈4,200); skills loaded per Unity beat 18,461 → 7,405 chars.

### Phase 4 — Unity client correctness (DONE 2026-10-15; EditMode 382/0 failed; server suite 2691/0 failed/2 skipped)
- [x] A1: the table follows the DM's own `take_turn` (`VaultController.OnDriverTool` → `SessionDigest.ApplyTurn`): HP and location from `partyFingerprint`, members from `party[]`, the clock from the "It is now …" summary line, conditions/quests/deaths from the arguments the DM itself sent (free), quest titles via one `get_entity` on demand; no `start_session` per turn (manual Refresh and the post-level-up refresh remain). Server: additive `TurnResult.LevelUpReady` (ids; sent when `xp_grant`/`level_up` applied or on full turns; `TurnLevelUpReadyTests`).
- [x] A2: `Prompts.PartyFingerprint` follows every successful take_turn; A4: `CompactResult` keeps `partyFingerprint`, `warnings` and `worldPressure` (3 lines, 200 chars each); A8: the brief carries `fullScene` (room, people, threads), `partyDelta` (needs moved, memory hints), `questChanges` and `warnings` (world pressure stays out: pinned as the loop's business).
- [x] A3: `CanRetryLastTurn` is false once a world-changing tool succeeded (`TurnRecord.Succeeded`, `CommittedTheWorld`); A21: a retryable failed turn is not appended to the chronicle file.
- [x] A5: malformed/cut-off tool arguments become a `[BadArguments]` tool message (never `{}` to the server); `finish_reason=length` is surfaced as a system line.
- [x] A6/A7: roll cards only from `take_turn`/`combat` results (`CarriesRolls`), damage in the dedupe key. **Deferred:** the server-side structured `rolls[]`: every host would pay ~60 chars per roll on every take_turn for a client-only benefit, and the client's patterns are pinned to the resolver wording (`ParsingPinTests`); revisit if the wording drifts.
- [x] A9: `TrimToBudget` per loop iteration; A11: streamed replies time out on inactivity (`SseDownloadHandler.SecondsSinceLastData`, no fixed total); A12: an error event inside a 200 stream is retried only when it reads as transient (`LooksTransient`).
- [x] A13: Stop aborts only the turn's MCP calls (`McpClient.Abort(owner)`); A18: level-up, plugin toggles and server stop wait for the DM's turn (`DmBusy`); A19: a PC built mid-campaign joins the CAMPAIGN roster and the chronicle, a level-up writes a chronicle line; A20: after history trimming the next user message carries `TrimmedNote` (send `forceFullReseed:true`).
- [x] A14: a preset or host change clears the key; A15: provider file written via temp + swap, chmod 600 on Unix, a corrupt file is backed up before it is overwritten; A16: `TestConnection` is a one-token chat completion (falls back to `/models` only on 404/405/400).
- [x] A17: zip-installed plugins' skills are indexed (`SystemPromptProvider.UserPluginsDir`, manifest `skillsPath`), disabled plugins' skills are skipped, the index rebuilds on plugin changes (`Reload`).
- [x] A24: the two-pass loop prompt drops the NARRATION and LAST STEP sections and the narration skills (`BuildSystemPrompt(extra, loopOnly)`, `SkillNames(loopOnly)`); dnd-narration + dnd-narration-examples go to the storyteller pass only. The stable-prefix compaction (compact turns older than K in batches) is left as is: `CompactFinishedTurns` already rewrites only the previous turn.
- [x] Low batch: A22 (`Config.ServerUrl` set after /health), A23 (Esc/X/outside-click on a mid-flow onboarding or an unsaved builder asks first: `Overlay.DismissWarning`, `OverlayHost.RequestClose`), A25 (reasoning_details fragments merged by index/id), A26 (a plain-JSON 200 is kept whole, 4 MB), A27 (MCP sessions kept across a healthy re-check), A28 (`[::1]`), A29 (no surrogate split), A30 (SSE fallback matches the JSON-RPC id), A31 (ASCII digits only), A33 (doc comments), A34 (tool allowlist persisted in `vault.tools`), A35 (30 s `/health` heartbeat → Down + toast).
- [x] Tests (EditMode): `TableFromTurnTests` (fingerprint, party[], clock, levelUpReady, quests/conditions from arguments, retry guard, controller fingerprint), `Phase4PinTests` (loop prompt sections, transient stream errors, reasoning merge, SSE detection, RPC id match, surrogates, digits, IPv6), `ParsingPinTests` additions (compaction keeps fingerprint/warnings/pressure, roll sources); `DriverFakeProviderTests` updated for the 3-arg `ToolSucceeded`.

### Phase 5 — Unity UX and structure (DONE 2026-10-15; EditMode 391/0 failed; server suite 2693/0 failed/2 skipped)
- [x] Journal: LET THE DM WRITE IT drafts every handoff field through one tool-free `Brainstorm` call (`Flows/HandoffDrafter`: this session's chronicle since the last recap, the previous handoff, the people seen; JSON reply clipped to the server's caps, people limited to NPCs the session showed, a name resolves to its id); the session now remembers every NPC it showed (`SessionDigest.SeenNpcs` from `npcs[]`, `cards[]` with the card's stance, scene rosters, last session's handoff), so People in play is a picker (chips to add, a stance line per person; `HandoffDraft.People` replaces the typed "id | stance" field); END asks first (`VaultAppState.RequestConfirm` → `ConfirmOverlay`; tests run the action at once). `JournalHandoffTests`.
- [x] Command bar: UNDO quick action when the last turn is still on screen, the DM is idle and nothing it did reached the world (`VaultController.CanUndoLastTurn`/`UndoLastTurn`: bubbles leave the log, `TranscriptStore.DropLastTurn` rewrites the chronicle to before the player line unless a recap followed, `OpenAiChatDriver.DropLastTurn` forgets the turn's history and passage); the line comes back into the box to edit or resend (`LastSentLine`). RETRY stays for failed turns. `UndoTurnTests`, `DriverFakeProviderTests.DropLastTurn_ForgetsTheTurn`.
- [x] Story log: muted event cards from the DM's own tool calls (`⚔` combat start/end from `combat`, `☠` a PC the fingerprint took to 0 HP, `⚙` ENGINE WARNING lines from worldPressure with the JSON cut off; `✦` level reached already existed), deduped when repeated; COPY STORY (plain text of player lines, narration, voices, rolls, recaps → clipboard, toast with the word count); EARLIER loads the chronicle's previous page above the story (`TranscriptStore.Load(slug, limit, skipFromEnd)` + `LineCount`, `VaultTranscript.Prepend`/`Inserted`, `VaultAppState.RequestEarlierHistory`). `StoryEventsTests`.
- [x] Onboarding: a rejected answer stays on the same question with the reason under it (`QuestionPageViewModel.Error`, what was typed stays); BACK from the third question on (server: `start_campaign_onboarding back=true` withdraws the previous answer, replays branching/path/flags from what remains, returns it as `priorAnswer`; the campaign name is never revisited; `OnboardingBackTests`). Builder: one `character_builder action=choose` (steps + the shown step's options + preview) per choice after a 0.25 s debounce (`BuilderSync`; a server without `choose` falls back to the three calls; `CharacterBuilderToolsTests.Choose_…`). tools/list budgets raised 100 for the bool and the action (17,400→17,500, /build 10,200→10,300; measured 17,466 / 10,233). Party frame: the "still in setup" notice + BUILD button were already there (`PartyViewModel.ShowBuild`), nothing added.
- [x] Decomposition, as partial classes along the §A seams rather than separate service objects: `VaultController` → `.Session` (select/restore/start/end/handoff/table-from-turn), `.Turn` (send/retry/undo/chronicle/export), `.Pc`, `.Campaign`, `.Onboarding`, `.Server` (tools allowlist, connection, embedded server, plugins), `.Provider`, with `.Builder`/`.Party`/`.LevelUp` as before and a 131-line core (ctor, preferences); `OpenAiChatDriver` → `.History` (compaction, budget, trim note, passages, undo), `.Transport` (requests, PostChat, retries, usage, failure context), `.Brainstorm`. **Deviation:** separate service classes would have doubled the public surface the ~50 view-model and test call sites use (`_c.X`) for no behaviour change; the seams are now files that can become classes one at a time. Brainstorm still shares `IsBusy` with turns on purpose: a BYOK endpoint gets one call in flight, and both callers (onboarding, journal) show the "DM is busy" reason.

Not planned (report only): CHARACTER_ART_PLAN (untouched), localization, full persistence consolidation.

## G. Verification
- Server: build once, run `tests/CampaignVault.UnitTests` xunit v3 executable; expect green except the two environmental LFS failures. Phase-specific: new table-driven pool tests, death-save sequence test, PF2e attack math tests, `ToolListBudgetTests` with lowered budgets, `ResponseBudgetTests` updated, golden-file test for the generated `## Fields` section, PI blocklist test.
- Token measurement: `scripts/measure/take_turn_breakdown.py` + `prose_stats.py` against a SFW scratch campaign on an empty DB (never existing campaign data), before Phase 3 and after each of 3a–3f; record per-call chars for a quiet beat, a travel beat, a combat beat, start_session, one deliberate bad-field error.
- Unity: EditMode suite via the Unity editor binary directly (`-batchmode -runTests`, the `unity test` wrapper exits 2); PlayMode smoke with a staged embedded server; manual: one full session (create → onboard → build → play 10 beats incl. a bad-field error → level up → end) watching the F12 inspector for per-turn `start_session` absence, fingerprint refresh, and ENGINE WARNING surviving compaction.
- Licensing: `grep -ri` PI blocklist over `src/CampaignVault/RulesetData claude_skills src/CampaignVault/Tools UnityClient/Assets/StreamingAssets` returns nothing; NOTICE visible via `lookup kind=help topic=licensing` and the Unity About tab.
