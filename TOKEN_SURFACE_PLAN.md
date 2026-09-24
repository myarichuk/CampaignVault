# Token surface plan

Replaces the token/guidance phases of `REFACTOR_PLAN.md` (obsolete). Goal: cut what the model pays to *see* the tool surface, without an LLM in the loop. Measured against the live server, 2026-09-24.

## Baseline (measured)

| Payload | Size |
|---|---|
| `tools/list` (Stub mode), 18 tools | 52,211 chars, ~13k tokens (input 39%, output 34%, descriptions 25%) |
| `get_commit_schema` index (54 types) | 12,910 bytes before this plan |
| Per-type lookup, 54 types combined | 35,304 bytes, ~450 B of each is envelope |

Field detail per `$type` is small (~11k chars total). The cost is in descriptions and output schemas, not the vocabulary itself.

## Lever A: shrink the vocabulary (done)

1. **`[EngineOnly]`** on `rest_recovery_ack`, `item_persistence_surfaced`, `memory_decay`, `ambient_encounter_check`. Hidden from the index and from lookups; still registered so JSON and handlers round-trip.
2. **Mode-scoped verbs**: `PluginWorldChangeAttribute.ModeId`. Verbs with a `ModeId` are left out of the index but resolve via `type=`. Only `crafting_step` is tagged in-repo. **The external plugin that owns the ten `lewd_*` verbs must set `ModeId` to benefit.** True per-campaign scoping (list a mode's verbs only when it is in `EnabledModeIds`) needs `MetaTools` to get a campaign session; not done.
3. **Terse index**: summaries clipped to the first sentence, max 60 chars.

Tests: `CommitSchemaDriftTests` (engine-only hidden, mode verbs omitted from index but resolvable, clip behaviour).

## Plugin guidance (done, SDK 0.3.0)

`PressureContext` cannot go into the SDK: it carries a Raven `IAsyncDocumentSession` and the host-side `SceneView`, and the SDK is Raven-free. Instead, following the `IChangeContext` precedent:

- SDK: `IPluginGuidanceContributor`, `IGuidanceContext` (campaign, time, config, surfaced character IDs, `AppliedChanges`), `PluginGuidanceHint`. Additive; 0.2.0 plugins keep working.
- Host: an adapter in `GuidanceOrchestrator` stamps `GuidanceHint.Source` (plugin assembly name), namespaces keys as `plugin:{asm}:{key}`, uses `GuidanceTrigger.Plugin`, and admits at most one plugin hint per response, before the budget pass.
- `PressureContext.AppliedChanges` added. The take_turn guidance gate now also runs on commit turns that surface no characters.
- Tests: `PluginGuidanceTests`.

Known issues, not fixed:
- Nothing writes `GuidanceLedger`, so "once" and `RepeatAfterDays` do nothing and hints re-emit on every qualifying turn.
- take_turn passes `Scene: null`, so `CombatStartedGuidanceContributor` never fires.

## Additive modes (SDK 0.3.0, partly done)

Combat and modes were already independent. What was blocking two modes at once was `IChangeContext.ActiveMode` holding only one encounter.

Done:
- `IChangeContext.ActiveModes`, keyed by ModeId. `ActiveMode` is kept as the most-recently-entered view. The dispatcher preloads every active encounter, `ChangeContext.EnterMode`/`ExitMode` keep it in sync, and `crafting_step` now looks up its own mode.
- `IInteractionMode.ParticipantClaim` (`Independent` default, `Shared`, `Exclusive`) is a default interface member, so existing modes don't change. The host enforces the mode-vs-mode rule at `mode_transition enter`: a participant can't be held by two modes when either one claims it exclusively.
- Tests in `InteractionModesTests`: overlap is allowed, exclusive blocks in both directions, different participants are fine, and exiting one mode keeps the others.

Not done:
- **Combat side of the claim.** `Exclusive` should skip the body's combat turn, and `Shared` should charge the combat action budget. Neither is enforced yet. Check first whether combat already skips incapacitated combatants.
- **Combat side of the claim** can now use `core.character_damaged` / a future `core.combat_turn_started`.

## Domain events (done, SDK 0.3.0)

String topics with `JsonElement` payloads (`DomainEvent`, `IDomainEventHandler`, `IChangeContext.Publish`, `CoreEvents`). Plugins never reference each other: a missing publisher is a topic that never fires.

- Delivery: synchronous, inside `WorldChangeDispatcher`. Events are delivered after each successful top-level change, and once more after the post-loop engine steps. Events from a failed change are discarded. Reactions are follow-up changes via `DispatchMutationAsync`. Depth is capped at 3, and a throwing handler is logged and skipped.
- Ownership: the dispatcher stamps `Source` from the running handler's, observer's or subscriber's assembly (`PluginEventSources`: host/SDK → `core`, plugin → manifest id; a plugin can never be `core`). `Publish` rejects topics outside that prefix.
- Core topics: `core.mode_entered.v1` and `core.mode_exited.v1` (`ModeTransitionChangeHandler`), and `core.character_damaged.v1` (`HpChangeHandler`, the single point every ruleset's damage goes through). Damage is published on requested damage, so a body at 0 HP still reports hits. `actorId` is set only for a depth-0 `ruleset_action` that targets the character.
- Startup: a warning for subscriptions to topics nobody declares in `plugin.json` `publishes`, and for plugin ids that are a dotted prefix of another id.
- Tests: `DomainEventTests`.

Known limits: a failing follow-up fails the whole take_turn. Follow-ups skip `IWorldChangeObserver` and are not in `AppliedChanges`, so plugin guidance contributors don't see them. Only three core topics exist so far.

## Domain events, round 2 (in progress)

Faults become visible instead of fatal. Reactions become visible downstream. Core gets more topics.

- [ ] P1. Pending-event marks: events published by a throwing `HandleAsync` or a failed follow-up are dropped, not delivered.
- [ ] P2. Fault isolation: a failed reaction stops its remaining follow-ups and keeps the commit, unless it opts into `ReactionFailurePolicy.FailCommit`. There is no rollback: follow-ups that already applied stay applied, and the fault says so. It publishes `core.plugin_faulted.v1`, and `PluginFaultException(message, fixHint)` lets plugins supply the fix. Faults land on `CommitResult.PluginFaults` and on take_turn (only when non-empty).
- [ ] P3. Reaction visibility: follow-ups run observers and are returned on `CommitResult`, so guidance `AppliedChanges` sees them.
- [ ] P4. `CoreEvents.All` feeds the declared-topic set (no hand-maintained list).
- [ ] P5. Dispatcher topics: `character_downed`, `traveled`, `rested`, `encounter_interrupted` (EncounterResolver, all 5 callers apply the deltas), `event_logged` (EventOccurred: conversation, discovery, ...).
- [ ] P6. Combat topics via an out-of-band publish path (combat lives in `CombatTools`, outside the dispatcher): `combat_started`, `combat_turn_started`, `combat_ended`. Plus the `Exclusive` skip in `NextTurn`.
- [ ] P7. Tests.
- [ ] P8. Docs: SDK README, `IDomainEventHandler` doc, this file.
- [ ] P9. Full suite green.

## Open questions

- How often does the model call the no-args index? Unknown. Step zero of anything further: count `get_commit_schema` calls per session from telemetry / RavenDB.
- Which `$type`s does the model actually author, and which fields are ever populated? Same audit; feeds the always-on core and lever B.
- Verb-family merges (`item*`, `rumor*`) are low value: the measured per-verb floor is ~490 B.

## Next (not started)

- Lever C, predictive schema injection: edge-triggered contributors (e.g. combat starts → next `take_turn` carries the combat schema), a session-scoped ledger, and a separate never-truncated schema channel (the 600-char hint budget and `TruncateAtSentence` must not touch JSON).
- `ToolSchemaMode.Minimal`: names, one-line descriptions, tiny shapes, with a probe-based budget test.
- Stub the remaining output schemas and `world_build` input.
- Validate with a played session (Stub vs Minimal); tests alone cannot prove guidance quality.

## Session-1 audit (Grok playtest, 2026-09-24)

Source: Grok's report after Session 1 (courtyard → Dock Ward → Harband Counting-House), plus live measurement against `localhost:5275` (Stub mode).

### What the report got right vs. stale

| Grok claim | Status on current tree |
|---|---|
| take_turn schema reprint 8k–15k tokens with every `$type` incl. plugin verbs | **Stale / Full-mode.** Stub `take_turn` input is 3.7k chars. Full-mode `$defs` still pull the *unfiltered* variant list (engine-only + mode verbs) → T4. |
| `$defs` required arrays wrong (event, location_update, character_update) | **Fixed in WIP** (`FullSchema_RequiredArrays_OnlyListRealRequirements`). |
| Mage Armor + `status` on the caster → whole batch rolled back | **Real, but Grok's diagnosis is wrong.** The core rulesets never auto-apply a status from a spell (Dnd5e emits only `HpChange` + grapple engagement). `SideEffectDuplicationGuard` rejected *any* `status` on a character a `ruleset_action` touches, so the legitimate Mage Armor status could never ride with the cast → T3. Prompts now say: commit utility-spell statuses yourself, same batch. |
| `get_entity` on a room right before `take_turn` into it | **Real, and we tell it to.** System prompts step 2 and `dnd-exploration` L107/L166 say "get_entity on arrival" → T6. |
| Defensive `includeParty` on conversation beats | **Real, and we tell it to.** "PC STATE WARNING" / opencode rule 8b → T6. |
| `rateLimitTokensRemaining` stuck at 49 = vault token budget | **Misread.** It is the request rate-limiter permit count (`MutationTools.cs` ~L2634), not a token budget. No action. |
| `search_connected_tools` every beat, image search | Client-side habit. Prompt-only fix → T6. |

### Measured fixed cost: `tools/list` (18 tools, Stub)

51,853 chars (compact JSON) ≈ 13k tokens, paid every session. Output schemas ≈ 17.7k chars (`get_config` 4.8k, onboarding ×3 ≈ 4.9k, `list_campaigns` 1.6k, `create_campaign` 1.6k, `recall_history` 1.3k, `advance_world` 1.2k). Descriptions ≈ 12k chars (`take_turn` 2.3k, `get_rules_reference` 1.9k, `get_entity` 1.2k, `world_build` 1.1k). The `campaignName` param description (~120 chars) repeats on ~15 tools.

### Tasks

- [x] **T0. Baseline.** Build compiles. Suite: 1534 total / 197 failed / 1333 passed / 4 skipped, but the failures are environmental: the Docker daemon is off, so the embedded Raven fallback is used; a concurrent `dotnet test` from the main checkout ran at the same time and `RavenDbTestEnvironment.CleanupOldTestDirectories` (deletes *every* `RavenDBTest*` dir in the shared temp) wiped the live DB → 185 fixture-ctor failures + CatastrophicFailure. Re-run alone.
- [x] **T1. Stub every output schema.** (`OutputSchemaTrimmer` deleted: dead once everything is stubbed.) `McpSchemaInstaller`: extend the `take_turn`/`start_session` `{"type":"object"}` loop to all tools. Safe: the SDK only needs non-null for StructuredContent; `opencode-plugin` never reads `outputSchema`. Done when: `tools/list` loses ≈17k chars.
- [x] **T2. Trim descriptions.** Result: `tools/list` 51,853 → 21,973 chars compact JSON (−58%, ~13k → ~5.5k tokens), pinned by `ToolListBudgetTests` (ceiling 24,000). `take_turn` FullScene now also carries `associatedPlotThreads` and `scenePressure` (scene-scope ENGINE WARNINGs; visit stamping already matched via `StampPartyPresentLocationsAsync`), so the travel turn replaces the arrival `get_entity`. `ToolParameterDescriptions` constants → short; `take_turn` description → ~800 chars (fingerprint/delta mechanics live in `get_help topic=take-turn-modes`); verbose `take_turn` request params; `get_rules_reference`, `get_entity`, `world_build`, `combat`. Keep the rollback-preventing lines (`$type` required; world_build HARD CONSTRAINTS). Done when: `take_turn` input+description < 4.5k chars (test).
- [x] **T3. Status collapse.** Name-only dedupe would break deliberate stacking (`StatusChangeHandler_DuplicateStructuredEffects_AreAllowed`), so it is origin-aware instead: `RulesetActionHandler` raises `ChangeContext.AutoApplyDepth` while dispatching its own mutations, and `HandleAdd` collapses a same-name effect arriving from the *other* origin in the same batch (engine copy wins), before the concentration-break loop. Same-origin stacking is unchanged. `SideEffectDuplicationGuard`: `StatusChange` case no longer rejects (hp / engagement cases unchanged — damage isn't idempotent). Tests: `StatusOriginCollapseTests`.
- [x] **T4. Full-mode `$defs` filter.** `TakeTurnSchemaBuilder.BuildDefs` uses the same `!IsEngineOnly && ModeId is null` filter as the index. Update counts in `ToolSchemaBudgetTests`.
- [x] **T5. Compact `get_commit_schema` index.** Drop `hasSideEffects` when false and the `Uncategorized` category from index entries (query-layer, not `[JsonIgnore]`): `CommitTypeSchema.Category`/`HasSideEffects` are nullable and the index projection passes null; `McpResponseCleaner` drops nulls. The constant `hasSideEffects:false` was also wrong for `ruleset_action`.
- [x] **T6. Guidance text.** Generic prompt rewritten as a lean card (39 core `$type`s from the live index); opencode prompt rules 2/8b/9, arrivals, utility spells; skills `dnd-exploration`, `dnd-narration`, `dnd-world-change`. Both system prompts, `dnd-exploration`, `DmHelpManual`, `ToolCallExamples`: stop "get_entity on arrival" (use `fullDetailLocationId` on the travel turn if it returns the same scene data, verify first); includeParty only when HP/slots/gold/needs/AC/gear changed; don't rediscover tools; no image search; spell utility auto-applies status; one beat = one call with the single approved roll → `knowledge_update` split. Verb card built from the live index (39 core types), not Grok's list (it includes the engine-only `ambient_encounter_check`).
- [x] **T7. Suite.** 1540 total / 1532 passed / 6 skipped / 2 failed:
  - `DomainEventTests.FailingFollowUp_FailsTheCommit`: pre-existing in the WIP. Round-2 P2 made `ReactionFailurePolicy.Isolate` the default (commit kept); the test still asserts the old fail-the-commit behaviour. Untouched here.
  - `SixFixVerificationTests.Item3_GetParty_SurfacesItemDetailSummariesForHeldItems`: flaky under the full suite (passed in the run before, and 12/12 twice in isolation).

### Deferred (needs a decision)

- **Tool consolidation**: onboarding ×3 → one tool, `get_config` → `get_rules_reference kind:config`, `end_session` → `start_session`. ~5–7k chars more, but it renames tools that prompts, skills and clients' cached tool lists reference.
- **`includeParty` projection**: a compact party view (hp/slots/gold/needs/AC/location) instead of full summaries. Needs a query-layer projection per CLAUDE.md.
- **Test infra**: `CleanupOldTestDirectories` should only delete dirs older than N hours, so parallel runs stop killing each other.
- Plugin verb scoping (`lewd_*`): out of scope, the plugin is out of date.

## Session-1 audit, round 2 (connectors + lookup)

Tool definitions ride along on **every model call** (not once per session), so each connector should expose only what its job needs. Live compact-JSON sizes of `tools/list`:

| Route | Tools | Chars | vs. 51,853 baseline |
|---|---|---|---|
| `/` (all) | 16 | 20,545 | −60% |
| `/play` | 10 | 15,953 | −69% |
| `/build` | 10 | 11,639 | −78% |

- [x] R1 `lookup(kind=...)` replaces `get_rules_reference` + `get_commit_schema` + `get_help` (3,626 → 1,956 chars).
- [x] R2 `/play` and `/build` routes (`ToolProfiles`, filtered per session via `ConfigureSessionOptions`).
- [x] R3 Stateful HTTP by default, `MCP_STATELESS=1` opts out. No token effect; buys session ids (clients can cache tools/list) and server push. Escaping middleware no longer buffers the GET event stream.
- [x] R4 `list_campaigns` projects to `CampaignSummaryView` in the query: was 35,221 chars for 7 campaigns (24k of `initiativeSurfaced`), now ~1k.
- [x] Schema cache: PostConfigure runs per session, so take_turn/world_build schemas are now built once.

Next candidates: stub `world_build` in `/play` (3.2k → ~1k); measure `start_session` / `take_turn` / `includeParty` responses on a real campaign.

## Round 3 (2026-09-24): /play stubs, tunnel

Live, compact JSON: `/` 19,528 · `/play` 12,497 (was 15,953) · `/build` 11,399.

- `/play` serves its own `world_build` definition (`ToolProfiles.SlimTool`, a `DelegatingMcpServerTool`): short description and a `batch` object stub, no `$defs` (was 13 `$defs`, 3.2k chars; now ~0.9k). Calls go to the same tool; `/` and `/build` keep the full schema. Field detail: `dnd-world-building` skill, `lookup kind=help topic=world-building`.
- Stub check (`PlayTools_AreRealStubs`): no `/play` tool has `$defs`; `take_turn` `changes[].items` is `{ $type }`, no anyOf/oneOf.
- `advance_world` description: dropped a spliced, stale tail (~550 chars). `get_entity` / `recall_history` parameter text tightened.
- Unknown tool on a connector: one line, e.g. `unknown tool 'create_campaign' on /play; it is on /build (campaign setup). Tell the user; don't search for tools. This connector: ...`. System prompt pins `/play` and says the same.
- `lookup kind=commit_schema` with no type returns a 4.4k-char index of `$type` names, not `$defs`; with `type=` ~0.6k.
- `scripts/tunnel.sh` + `ngrok/traffic-policy.yml`: one tunnel, only `/play`, `/build`, `/health` pass; refuses to expose a server without `BEARER_TOKEN`.

Why not 6–8k on `/play`: what's left is parameter schemas the model needs to call the tools correctly (take_turn's refresh flags are 2.4k and carry the fingerprint/includeParty rules). The next saving is the client caching tools/list (stateful sessions), not more stubbing.

## Round 4 (investigation, 2026-09-24): per-call response payloads

Tool definitions are cacheable; tool *results* stay in the conversation and compound. Measured live on a copy of the local DB (worktree server, `/play`, compact JSON chars). Nothing below is implemented yet.

### Measurements

| Call | maeves-quest (1 session) | lyras-journey (long) |
|---|---|---|
| `take_turn` commit beat (one `event`) | 1,222 | 1,794 |
| `take_turn` skill check | 792 | |
| `take_turn` + `fullDetailLocationId` (arrival) | 7,329 (1 NPC) | 14,040 (6 NPCs) |
| `take_turn` + `fullDetailCharacterId` | 7,120 | 8,095 |
| `take_turn` `includeParty` Full (1 PC) | 6,524 (party) | 20,034 |
| `start_session` | 12,352 | 31,163 (isekai-college 34,864) |
| `get_entity` PC | 12,751 | 23,921 |
| `get_entity` NPC / location | 7,890 / 5,511 | |
| `search_world "Quill"` (18 hits) | 4,700 | |
| `recall_history` | 2,546 | 2,598 |
| `advance_world` 8h | 1,738 | |
| `combat` start/next/end/status | 228–524 | |

`take_turn` definition: 3.6k (description 1.0k, `changes` 0.5k, 15 params ~1.6k). Already tight; ~300 more by dropping rules the system prompt repeats. Low priority.

### Where the chars go, ranked by (size × frequency), with accuracy risk

**Every turn**
1. **Guidance hint repeats forever** (lyras: 697 chars on *every* take_turn, pure queries included: the travel/rest hint). `GuidanceOrchestrator` reads `GuidanceLedger` but nothing writes it. Fix: record delivery after the response is built (key per session, so a new session re-teaches once). ~700/turn, ~28k per 40 turns. Risk: none; hints are designed to fire once.
2. **Commit echo duplicates** (~30% of a 1.2k beat):
   - novelty hint emitted twice: once in `summary[]` for the event, once as `narrativeReminder` for `narrative` (~230);
   - `"Event logged (id: …)"` repeats `committedIds` (~65);
   - `knownCharacterIds` repeats `npcs[].characterId` when nothing else surfaced (~40);
   - `rateLimitTokensRemaining` every turn (Grok misread it as a token budget): send only when low;
   - envelope `summary: "World updated with N changes and fresh state echoed."` + `tokensEst`;
   - unrounded floats (`behavioralTension: 13.65999984741211`, needs `25.284723`): round to int / 1 dp.
   - `narrativeReminder` "combat/status changes but no event" fires on every skill check (~150), contradicting the prompt's "pair an event if the beat matters".
   Risk: none; all duplicates or precision noise.

**Arrival / full-detail turns** (`fullScene`, get_entity location; scales with NPC count, ~1.4k per NPC)
3. Per NPC: `needDescriptors` (~256, static definitions of stress/fatigue, repeated per NPC *and* in a scene-level `needDescriptorLegend` ~400); `behavioralSummary` (~240, restates currentActivity + mood + last event); engine internals in `systemStats` (`willpower`, `morale`, `temperature`, `warmthRating`, `movementModifier`). `seededNpcIds` repeats `presentNPCs[].id`. Fix: descriptors once per session for custom needs only; drop the rest in the projection (query layer, per CLAUDE.md). ~40% of a scene. Risk: low; keep AC/level/HP in systemStats.
4. `scenePressure` templates: the POI "SUGGESTION" carries a ~800-char two-change example; the discovery "NARRATIVE PROMPT" suggests generic text ("disturbed terrain, overturned stones") for a counting-house. Fix: one line each, template via `lookup kind=help`. Risk: low.

**Session start / reseed / PC detail** (unbounded growth)
5. **PC `psychology.memories` shipped whole** in `start_session`, Full `includeParty` and `get_entity`: 12–14k on long campaigns and growing every session; keyed by topic with `topic` repeated inside. `get_entity` also sends `relevantMemories` (1.5k), a subset of the same data. Fix: top-N by salience + `memoryCount`, full set via `memoriesOnlyCharacterId`. ~10k+ per call. Risk: **medium**: the model loses low-salience PC memories from view; the count + pointer mitigates. Needs a played-session check.
6. **`start_session` ships the raw campaign doc**: `initiativeSurfaced` 2–11k, `pressureCooldowns`, `recentInitiativeSlotNpcIds` (engine bookkeeping, same leak `list_campaigns` had in R4). Fix: summary projection. Risk: none.
   Also: its hint says "call get_entity with that location ID" (contradicts the prompt; the PC's `currentLocationId` is already known). Say "take_turn fullDetailLocationId=<id>" with the id filled in.
7. `get_entity` NPC `recentInteractions` 3.7–4.2k (10 events with UUID ids and `involved` lists). Cap at 5, drop `involved` and default `category`. ~2k. Risk: low.

**Other tools**
8. `advance_world`: `worldPressure` repeats `simulatorEvents` (7 "memory fading" lines, ~700); `newTime` carries `id`, `lastUpdated`, `unsimulatedHours`. Risk: none.
9. `search_world`: 18 hits for "Quill", most unrelated (semantic noise). Cap ~8 or score threshold. Risk: low.
10. `recall_history`: per entry `timestamp`, `sessionId`, `noveltyScore`, `campaignName`. ~120/entry. Risk: none.

### Functional bugs found while measuring (not token-only)

- **F1 Fire Bolt did nothing.** `ruleset_action` `Spell` with `targetIds` and no `parameters`, in active combat → `SpellResolutionHelper.InferMode` falls to `Utility` → "Utility spell cast outside combat", no roll, action consumed. Fix: infer attack/save/damage from the SRD spell entry, or fail with a fix hint when `targetIds` is set and the mode is Utility.
- **F2 Component warning on every spell** (~290): `RulesetActionHandler` flags any status with no `ConditionName` (Mage Armor) as a possible component blocker, on every cast. Fix: warn once per status, or only for statuses whose name/tags look restrictive.
- **F3** Skill checks trigger the "combat/status changes but no event" reminder (see 2).

### Proposed order

- [ ] P1 Guidance ledger write (item 1) + commit echo dedup (item 2). Every turn, zero accuracy risk.
- [ ] P2 `start_session`: see `SESSION_HANDOFF_PLAN.md` (model-authored handoff at `end_session`; measured 13.5k → 2.6k after 3 sessions, flat thereafter). Covers items 5 and 6 for `start_session`.
- [ ] P3 Scene/NPC projection (items 3, 4, 7).
- [ ] P4 PC memory top-N (item 5): behind a played-session check.
- [ ] P5 advance_world / search_world / recall_history (items 8–10).
- [ ] P6 F1–F2.
- [ ] Each phase: response-size tests pinned like `ToolListBudgetTests`, full suite green.

Measurement policy from here on: scratch campaigns on an empty DB (see `SESSION_HANDOFF_PLAN.md`).

Open question: turn mix (commit beats vs arrivals vs full-detail) per session, from the event log, to confirm the ranking.
