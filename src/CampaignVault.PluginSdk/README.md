# CampaignVault.PluginSdk

Contracts and models for writing out-of-tree **CampaignVault** plugins: `IWorldChangeHandler`,
`IChangeContext`, `IInteractionMode`, `PluginManifest`, and the shared domain models
(`Character`, `Location`, `Item`, `WorldChange`, ...) plugins mutate.

This package has no dependency on the CampaignVault host — it's the assembly boundary plugin
DLLs compile against so they never need (or get) access to host internals.

## Usage

```bash
dotnet add package CampaignVault.PluginSdk
```

Implement `IRulesetModule` (data/mechanics plugin) or `IInteractionMode` (turn-based scene
activity), drop the built DLL + a `plugin.json` manifest under `Plugins/<YourPlugin>/` in a
CampaignVault install, and restart the host.

See [PLUGINS.md](https://github.com/myarichuk/CampaignVault/blob/master/PLUGINS.md) in
the main repository for the full plugin architecture, trust model, and quick-start guide.

## Guidance and mode-scoped verbs (0.3.0)

- Implement `IPluginGuidanceContributor` to append a short hint to take_turn responses. It receives a
  Raven-free `IGuidanceContext` (campaign, time, config, surfaced character IDs, and the changes this
  turn committed). The host namespaces your hint keys, admits at most one plugin hint per response, and
  delivers each key once per session (again after `RepeatAfterDays`, or in a new session); still, fire on
  an edge (a `ModeTransitionChange` entering your mode just landed), not on a level.
- (0.4.0) Implement `IPluginContextContributor` to push one-line facts the next prose needs on the beat that uses
  them (recipe state on your crafting verb, a meter on your mode's action). It receives a Raven-free
  `IContextTurn` (campaign, committed changes, involved entity IDs, party IDs, the party's location) after
  every committed take_turn. Each `PluginContextItem` key is namespaced and delivered once per session, so
  put the value in the key when a changed fact should go out again (`"meter:3"`). Core and plugin lines
  share one ~800-char budget per response, ranked by `Priority`.
- Set `[PluginWorldChange("my_verb", ModeId = "my_mode")]` on verbs that only make sense inside your
  interaction mode. They drop out of the `lookup kind=commit_schema` index but still resolve with `type=`;
  pair that with a guidance hint on mode entry so the model gets the schema when it needs it.

- Several modes can be active at once. Look up your own encounter with
  `context.ActiveModes.TryGetValue("my_mode", out var enc)` rather than `context.ActiveMode`, which is only
  the most recently entered one.
- Declare `ParticipantClaim` on your `IInteractionMode`: `Independent` (default), `Shared` (mode actions cost
  the character's combat action), or `Exclusive` (the character acts only in your mode, e.g. astral
  projection). The host rejects entering a mode when a participant is already held by an exclusive one.
  Combat skips an `Exclusive` participant's turn; charging `Shared` mode actions against the combat action
  budget is not enforced yet.

- **Domain events** (`CampaignVault.Events`): string-topic pub/sub, so plugins integrate without ever
  referencing each other. Publish with `context.Publish("myplugin.thing_happened.v1", new { ... })` from a
  handler; subscribe with `IDomainEventHandler` (`Topics` + `HandleAsync`), read fields with
  `e.TryGet<T>(key, out var v)`. Rules: you may only publish under your manifest id + `.` (the host stamps
  `Source` and rejects anything else), payloads are JSON objects of plain values, delivery is synchronous
  inside the same commit, and you react by returning follow-up `WorldChange`s, not by mutating entities.
  Follow-ups can reference any entity (the host loads what the batch didn't), are seen by observers, and
  count as this turn's applied changes for guidance. Chains stop at depth 3.
- **Faults** don't block play. If your handler throws or a follow-up is rejected, the rest of that reaction
  is skipped, the turn is saved, and the turn summary gets a `PLUGIN FAULT` line: what broke, which follow-ups
  had already landed (there is no rollback), and a fix hint. Throw `PluginFaultException(message, fixHint)` to
  write that hint yourself. If your steps only make sense together, return one follow-up or set
  `FailurePolicy => ReactionFailurePolicy.FailCommit`, and a fault then rejects the whole turn. Every fault
  is also published as `core.plugin_faulted.v1` after all other reactions finish, so a diagnostics plugin can
  subscribe to it.
- **Core topics** (`CoreEvents.*`, use the constants; `CoreEvents.All` lists them): `ModeEntered`,
  `ModeExited`, `CharacterDamaged`, `CharacterDowned`, `Traveled`, `Rested`, `EncounterInterrupted`
  (travel, rest, ambient and crowd ambushes), `EventLogged` (every event beat; conversations are category
  `Conversation`), `CombatStarted`, `CombatTurnStarted`, `CombatEnded`, and `PluginFaulted`. The
  doc comment on each constant lists its fields. List what you publish under `"publishes"` in `plugin.json`
  so the host can warn about subscriptions to topics nobody publishes.
- A combatant held by an `Exclusive` mode no longer gets a combat turn; it can still be targeted, and
  `CharacterDamaged` tells your mode about it.

All of this is additive; plugins built against 0.2.0 keep working.

## Hidden content and hazards (0.4.0)

- `LocationExit`, `Item` and `ItemDetail` gain `Hidden` and `DiscoverDc`; exits also gain `Intent`. Hidden
  entries stay off the wire until a Perception/Investigation check (or passive Perception on arrival)
  meets the DC, or the party uses or takes them.
- New `Hazard` (name, trigger enter/take, detect/disarm DC, effect, save DC/ability, intent) on exits,
  items and `Location.Hazards`; the host sets detected/disarmed/spent. New world-change fields:
  `addHazard` on a location update, `hazard` and `hidden`/`discoverDc` on item and upsert changes.
- `IPluginContextContributor` (see above) is new in this version.

## Breaking Changes

**0.4.0** — `CampaignConfig.PointOfInterestDetailCharCap` is removed: points of interest are now ordinary
fixture items (the host migrates existing data on startup). Drop any reference to it.

**0.2.0** — `ItemCategory`, `EquipZone`, and `EquipLayer` are no longer enums. They're now open
string-constants classes (`ItemCategories`, `EquipZones`, `EquipLayers`) so item packs can define
their own categories/zones via YAML alone. `Item.CoreCategory`/`EquipLayer` are now `string`/
`string?`, and `Item.EquipZones` is `List<string>`. Update any code referencing the old enum
members (e.g. `ItemCategory.Weapon` → `ItemCategories.Weapon`).

## Traits migration (0.5.0)

- New `IPluginTraitsUpgrader`: migrate your own `SystemStats.Traits` keys (rename, reshape, retire) when
  your trait schema changes. The host runs it once per character on load; keep `TryUpgrade` idempotent
  and return `true` only when you changed something.

## License

PolyForm Noncommercial 1.0.0 — see the bundled `LICENSE` file.
