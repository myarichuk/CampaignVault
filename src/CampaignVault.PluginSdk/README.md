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
  does not yet deduplicate across turns, so fire on an edge (a `ModeTransitionChange` entering your mode
  just landed), not on a level.
- Set `[PluginWorldChange("my_verb", ModeId = "my_mode")]` on verbs that only make sense inside your
  interaction mode. They drop out of the `get_commit_schema` index but still resolve with `type=`;
  pair that with a guidance hint on mode entry so the model gets the schema when it needs it.

- Several modes can be active at once. Look up your own encounter with
  `context.ActiveModes.TryGetValue("my_mode", out var enc)` rather than `context.ActiveMode`, which is only
  the most recently entered one.
- Declare `ParticipantClaim` on your `IInteractionMode`: `Independent` (default), `Shared` (mode actions cost
  the character's combat action), or `Exclusive` (the character acts only in your mode, e.g. astral
  projection). The host rejects entering a mode when a participant is already held by an exclusive one.
  The combat side (charging `Shared` actions, skipping an `Exclusive` body's turn) is not enforced yet;
  until then, mark an exclusive participant's body with a condition on entry.

- **Domain events** (`CampaignVault.Events`): string-topic pub/sub, so plugins integrate without ever
  referencing each other. Publish with `context.Publish("myplugin.thing_happened.v1", new { ... })` from a
  handler; subscribe with `IDomainEventHandler` (`Topics` + `HandleAsync`), read fields with
  `e.TryGet<T>(key, out var v)`. Rules: you may only publish under your manifest id + `.` (the host stamps
  `Source` and rejects anything else), payloads are JSON objects of plain values, delivery is synchronous
  inside the same commit, and you react by returning follow-up `WorldChange`s, not by mutating entities.
  A failing follow-up fails the commit; a throwing handler is logged and skipped; chains stop at depth 3.
  Core publishes `CoreEvents.ModeEntered`, `ModeExited`, and `CharacterDamaged` (use the constants). List
  what you publish under `"publishes"` in `plugin.json` so the host can warn about subscriptions to topics
  nobody publishes.

All of this is additive; plugins built against 0.2.0 keep working.

## Breaking Changes

**0.2.0** — `ItemCategory`, `EquipZone`, and `EquipLayer` are no longer enums. They're now open
string-constants classes (`ItemCategories`, `EquipZones`, `EquipLayers`) so item packs can define
their own categories/zones via YAML alone. `Item.CoreCategory`/`EquipLayer` are now `string`/
`string?`, and `Item.EquipZones` is `List<string>`. Update any code referencing the old enum
members (e.g. `ItemCategory.Weapon` → `ItemCategories.Weapon`).

## License

PolyForm Noncommercial 1.0.0 — see the bundled `LICENSE` file.
