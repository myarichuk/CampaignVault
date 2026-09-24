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
- **`Shared` claim in combat.** Charging mode actions against the combat action budget is not enforced. (`Exclusive` is: `CombatTools` skips the body's turn at start and on next.)

## Domain events (done, SDK 0.3.0)

String topics with `JsonElement` payloads (`DomainEvent`, `IDomainEventHandler`, `IChangeContext.Publish`, `CoreEvents`). Plugins never reference each other: a missing publisher is a topic that never fires.

- Delivery: synchronous, inside `WorldChangeDispatcher`. Events are delivered after each successful top-level change, and once more after the post-loop engine steps. Events from a failed change are discarded. Reactions are follow-up changes via `DispatchMutationAsync`. Depth is capped at 3, and a throwing handler is logged and skipped.
- Ownership: the dispatcher stamps `Source` from the running handler's, observer's or subscriber's assembly (`PluginEventSources`: host/SDK → `core`, plugin → manifest id; a plugin can never be `core`). `Publish` rejects topics outside that prefix.
- Core topics: `core.mode_entered.v1` and `core.mode_exited.v1` (`ModeTransitionChangeHandler`), and `core.character_damaged.v1` (`HpChangeHandler`, the single point every ruleset's damage goes through). Damage is published on requested damage, so a body at 0 HP still reports hits. `actorId` is set only for a depth-0 `ruleset_action` that targets the character.
- Startup: a warning for subscriptions to topics nobody declares in `plugin.json` `publishes`, and for plugin ids that are a dotted prefix of another id.
- Tests: `DomainEventTests`.

Round 2 below replaced the original limits (a failing follow-up failed the whole take_turn; follow-ups were invisible downstream; three core topics).

## Domain events, round 2 (done, SDK 0.3.0)

Faults become visible instead of fatal. Reactions become visible downstream. Core gets more topics.

- [x] P1. Pending-event marks: events published by a throwing `HandleAsync` or a failed follow-up are dropped, not delivered.
- [x] P2. Fault isolation: a failed reaction stops its remaining follow-ups and keeps the commit, unless it opts into `ReactionFailurePolicy.FailCommit`. There is no rollback: follow-ups that already applied stay applied, and the fault says so. It publishes `core.plugin_faulted.v1`, and `PluginFaultException(message, fixHint)` lets plugins supply the fix. Faults land on `CommitResult.PluginFaults` and on take_turn (only when non-empty).
- [x] P3. Reaction visibility: follow-ups run observers and are returned on `CommitResult`, so guidance `AppliedChanges` sees them.
- [x] P4. `CoreEvents.All` feeds the declared-topic set (no hand-maintained list).
- [x] P5. Dispatcher topics: `character_downed`, `traveled`, `rested`, `encounter_interrupted` (EncounterResolver, all 5 callers apply the deltas), `event_logged` (EventOccurred: conversation, discovery, ...).
- [x] P6. Combat topics via an out-of-band publish path (combat lives in `CombatTools`, outside the dispatcher): `combat_started`, `combat_turn_started`, `combat_ended`. Plus the `Exclusive` skip in `NextTurn`.
- [x] P7. Tests.
- [x] P8. Docs: SDK README, `IDomainEventHandler` doc, this file.

Other additions:
- Follow-ups preload the entities they reference (`WorldChangeDispatcher.PreloadAsync`), so a reaction can touch a character the batch never loaded.
- `ChangeContext` failures are now counted (`FailureCount`), so an isolated fault can undo only its own failures.

Limits:
- No rollback. An isolated fault keeps the follow-ups that already applied, and the fault lists them.
- Combat reactions on the all-dead `next` path are best-effort: the encounter ends regardless of a `FailCommit` fault.
- `event_logged` fires for engine-emitted beats too (travel, encounters). Subscribers filter by `category`.
- Nested-prefix spoofing is still only warned about at startup.
- [x] P9. Full suite green (1,551 total, 0 failed, 6 pre-existing skips). One earlier run had 93 transient "internal error" failures that did not reproduce in three reruns.

## Open questions

- How often does the model call the no-args index? Unknown. Step zero of anything further: count `get_commit_schema` calls per session from telemetry / RavenDB.
- Which `$type`s does the model actually author, and which fields are ever populated? Same audit; feeds the always-on core and lever B.
- Verb-family merges (`item*`, `rumor*`) are low value: the measured per-verb floor is ~490 B.

## Next (not started)

- Lever C, predictive schema injection: edge-triggered contributors (e.g. combat starts → next `take_turn` carries the combat schema), a session-scoped ledger, and a separate never-truncated schema channel (the 600-char hint budget and `TruncateAtSentence` must not touch JSON).
- `ToolSchemaMode.Minimal`: names, one-line descriptions, tiny shapes, with a probe-based budget test.
- Stub the remaining output schemas and `world_build` input.
- Validate with a played session (Stub vs Minimal); tests alone cannot prove guidance quality.
