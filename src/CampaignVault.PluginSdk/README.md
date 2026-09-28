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

## 0.12.0

- **Piercings** (`piercing`, `PiercingChange`, `Character.Piercings`): SFW body adornment (earrings, septum, navel, …).
  Open `site`/`kind` strings (`PiercingSites` / `PiercingKinds` suggest; plugins may namespace kinds and use intimate
  sites). Several marks may share one site (and the same kind) — e.g. three rings on `labia.left` plus a `clitoris`
  ring. Each mark has a stable `id` (auto `1`,`2`,…); pass `id` to update/remove one of a stack. Default `add` stacks;
  `replace:true` upserts a single site+kind. `load` none|light|heavy; tags (`locked`, `bell`, `leash_ring`, `fresh`, …).
  Locked marks refuse `remove` without `force:true`. Cap 32 per character. Publishes `core.pierced.v1` (includes
  `piercingId`). Cards show a compact summary (stacks as `3× …`). Mutate only via the verb (or return `PiercingChange`
  from an event handler).
- **Full rulesets out of tree.** `IRulesetModule`, `IActionResolution`, `ICombatRuleset`
  (`CampaignVault.Rulesets`) and the character-bootstrap contracts (`IBootstrapStep`,
  `ILevelGainStep`, `ICharacterBootstrapPipeline` + in-box implementations,
  `BootstrapContext`, `BootstrapStepResult`, `BootstrapReport`,
  `IBootstrapEquipmentAccess` in `CampaignVault.Rulesets.Bootstrap`) moved here from
  the host (same namespaces, so host code is untouched). A plugin referencing only
  this package can now author a complete ruleset: dice resolution, action economy,
  and HP/defense/proficiency derivation.
- **Deliberately not moved:** session-bound pressures. `IPressureContributor` and
  `IRulesetPressureContributor` stay host-side (first-party resolvers implement the
  host's `IHostRulesetModule` for those); out-of-tree rulesets contribute pressure
  via `IPluginGuidanceContributor` / `IPluginContextContributor`, which the host
  surfaces on the same read paths.
- **Bootstrap stays Raven-free.** Steps that derived stats from worn gear previously
  took a live session; they now read `BootstrapContext.EquipmentAccess`
  (`GetEquippedItemsAsync`), which the host backs with its session. Null means no
  equipment data — degrade to unarmored defaults.

## 0.11.1

- **Dirt phrase leaf.** Namespaced kinds narrate the leaf after the last dot: `myplugin.ichor` → "ichor-stained hem"
  (`SoilHelpers.DisplayKind` / `Phrase`). Stored `Kind` stays fully qualified.
- **`DirtMark.AppliedBy` / `SoilChange.AppliedBy`.** Optional provenance (character id, verb id, `pluginId:cause`). Audit
  only; does not split stacks. On worsen, a new non-null value replaces the previous. Included on `core.soiled.v1` as
  `appliedBy`.
- **`DirtSpots`.** Suggested spot constants (`face`, `hair`, `hands`, `chest`, `back`, `clothes`, `boots`, `cloak`, `hem`,
  `blade`, `hilt`) — freeform spots still work.
- **Docs.** Plugins mutate dirt by returning `SoilChange` from an `IDomainEventHandler` follow-up. Observers cannot return
  WorldChanges and must not call `SoilHelpers.Apply` on tracked hosts.

## 0.11.0

- **Roll modifiers.** Implement `IRollModifierProvider.Modifiers(RollQuery)` to change what a roll is: a numeric bonus, advantage
  or disadvantage, and a reason the player reads. Core folds every provider (its own status-effect layer, willpower, and yours)
  into each attack, damage, AC, check, save, initiative and speed, cancels advantage against disadvantage, and adds each reason to
  the roll's narrative. Providers are registered by convention, gated by your `systems`, must be pure and synchronous, and are
  skipped if they throw. `RollQuery` has `Kind` (`RollKinds.*`), the normalized `Subject` (skill or save), `Tags` (what the roll is
  against: `charm`, `fear`, `compulsion`, `mental`), `Actor`, `Other`, `System` and the campaign's `Options`.
  Rule: numbers that expire live on a `StatusEffect` (core folds those); providers add situational advantage and tag-specific
  bonuses. Never stamp an effect and also return the same number.
- **Plugin rolls.** `IChangeContext.ResolveRollModifiers(query, baseBonus, explicitAdvantage)` runs your own rolls through the same
  pipeline. It is a default interface member (returns the bonus unchanged), so existing contexts and test doubles keep compiling.
- **Willpower matters.** Saves tagged `charm`, `fear`, `compulsion` or `mental` (a Wisdom, Intelligence or Charisma save counts as
  mental) move with `Willpower`: 90+ +1, 60-89 none, 30-59 -1, 10-29 -2, under 10 -3 and disadvantage. The default (75) changes
  nothing. `SystemExtension.WillpowerDrained` records what was worn down by something recoverable (a negative `attribute willpower`
  delta, or your own drain); each 4-hour rest step gives back up to 5. A willpower value set outright is a new baseline.
- **Spell tags.** `SpellDefinition.tags` (charm, fear, compulsion, mental...) and an action's `saveTags` parameter feed `RollQuery.Tags`.
- **Speed.** `Speed` status modifiers are now real: they slow travel (the group moves at its slowest member's pace, at most 3x, gear
  excluded), show as `speed` on character and NPC cards, and a context line compares everyone's speed when someone is off their
  normal pace, so a chase can be adjudicated.
- **Dirt** (`soil`, `SoilChange`, `IHasDirt.Dirt`): dust, blood, mud... on a character, an item or a location (scenery via
  `fixture`: `north wall`, `floor`; props are items whose `HolderId` is the location). Each host keeps at most 8 `DirtMark`s
  (`Kind`, `Severity` 1-3, `Spot`, `Fixture`, `AppliedDay`, `Note`, `AppliedBy`); identity is `(kind, spot, fixture)`,
  case-insensitive. `amount` +1 adds or worsens, -1 washes (spot and fixture then act as filters), `clear: true` removes
  every match. Past the cap the least severe, oldest mark fades. The engine never soils anything on its own: the DM
  commits `soil` in the same batch as the fight or the road. **Kinds are open strings** (`DirtKinds` only suggests
  `blood`, `mud`, `dust`, `soot`...): invent `ectoplasm`, or `myplugin.ichor` if you want a namespace; summaries phrase the
  leaf (`ichor-stained`). To read dirt, use `ctx.Characters/Items/Locations[id].Dirt` (or
  `SoilHelpers.HasDirt/SeverityOf/Summarize`) from any handler, observer or event handler. To change it, return a
  `SoilChange` as an `IDomainEventHandler` follow-up; there is no need for a new `$type`. Every changed mark publishes
  `core.soiled.v1` (`targetId`, `kind`, `severity` (0 once gone), `spot`, `fixture`, `action`, `appliedBy`), which is where
  scent tracking, infection or cleaning rituals belong. On the wire, lists carry one short `soil` line ("muddy boots,
  heavily bloodied") or nothing; the full `dirt` array appears only in `get_entity` for characters and items, take_turn's
  `fullDetailCharacterId`, and a location's `fullDescription` view.

## 0.10.0

- **Time hook.** Implement `IWorldTimeObserver.OnTimeAdvancedAsync(TimeAdvance, IChangeContext, ct)`. It runs after the
  clock moved, once per bucket (travel 6h, rest 4h, other activity and `advance_world` 6h; a long skip is split into at
  most 16 coarse steps), oldest first. `TimeAdvance` carries `Source` (`travel`, `rest`, `activity`, `advance_world`),
  `Hours`, `TotalHoursSoFar`, `CharacterIds`, `LocationId` and `Terrain` (travel: the exit's terrain). Like
  `IWorldChangeObserver` it cannot fail the commit (exceptions are logged), is gated by your plugin's `systems`, and can only
  cause effects by dispatching new `WorldChange`s; whatever it dispatches does not re-trigger the hook.
- **`apply_effect`** (`ApplyEffectChange`): a clamped layer over statuses. `tier` light (±1, ≤1h), moderate (±2, Speed ±10,
  ≤8h), serious (±3, Speed ±20, ≤24h, needs `recoveryHint`) or `persistent` (curses, auras: needs `imposedBy` and
  `removal`, never expires). Modifiers are a whitelist (`AllChecks`, `AllSaves`, `AttackRoll`, `AllRolls`, `Initiative`,
  `Speed`, skills), at most two per effect; a buff must be positive and a debuff negative. `key` is unique per
  character: reapplying refreshes to the stronger value. Each character keeps at most two non-persistent buffs and two
  debuffs; a third is reported as not applied. Non-persistent effects need `durationHours` and are swept when their hour
  passes. `StatusEffect` gained `EffectKey` and `EffectTier`. `EffectTiers` (core) holds the numbers.
- **Consequence beats** (core, uses the time hook): each step on the road or in camp may signal a `good`, `bad` or `mixed`
  beat of `light`, `moderate` or `serious` size as a hint in the commit result; the DM invents it and resolves it,
  usually with `apply_effect`. Campaign options: `consequences` (`off`, `light` (default), `full`),
  `consequenceCooldownHours` (default 8; bad beats 24), `consequenceMaxPerDay` (default 2). Cooldown and count are kept per
  character in `SystemStats.Traits` (`consequences.*`). It never spawns creatures.
- **`tether`** (`TetherChange`, `Character.SystemStats.Tethers`): `attach` a subject to an anchor (character, item, or
  `fixture:<name>`) with `breakDc`, optional `slackFeet`, `holderId`, `label`; `detach`; `strain` (a d20 check vs the DC).
  A tethered subject cannot travel unless the anchor or its holder travels in the same batch. A tether ends when its
  anchor item is archived, its anchor or holder character is gone, or its holder is incapacitated.
- **Ammunition.** A weapon item with the property `ammoType` fires real rounds: ranged `ruleset_action` attacks find a held
  item whose `ammoFor` (or `ammoType`) names the weapon's key, name or ammo type (or the explicit `ammoItemId`), spend
  `attackCount × ammoPerShot` from its charges (or quantity), clamp to what is left, and fail with `[NoAmmo]` when
  there is none. `fireModes: "single:1, burst:3, auto:10"` plus `mode=burst` sets the count (an explicit `attackCount`
  wins). A loaded item with `damage`/`damageType` adds that damage as a rider on each hit (dnd5e). Weapons without
  `ammoType` behave as before.
- Weapon attacks with an `attackCount` above the number of targets now fan out round-robin (a burst at one target, a
  machine gun across a horde) instead of being capped at the target count.

## 0.9.0

- `[ActorAction]` on a `WorldChange` marks it as an action its actor takes (`ActorId`, else `CharacterId`). The host
  refuses it at top level while `ActionBlock.IsBlocked(actor)`: the core conditions `incapacitated`, `paralyzed`,
  `petrified`, `stunned`, `unconscious`, or any status whose `StatModifiers` carry `ActionBlock.Tag`
  (`BlocksAllActions`). Verbs without the attribute (saves, recovery, effects aimed at someone) are never blocked,
  and neither are engine follow-ups. Give every blocking status an exit: `ExpiresAtDay` (compared with
  `TotalDaysElapsed + Hour / 24.0`; the host stops honouring an expired block even if nothing removed it), or an
  owner that removes it in and out of encounters. Core `attack`/`spell`/`use item` ruleset actions are gated the same way.

## 0.8.0

- `EngineOnlyAttribute` is public: put it on a `WorldChange` your plugin only emits (for example from an
  `IDomainEventHandler` reacting to `core.rested.v1`) so it stays out of the model's schema.
- `plugin.json` `systems` (string list): the `ActiveSystem` values the plugin applies to; empty means all.
- `IModeStateMachine.TryAddParticipant` / `TryRemoveParticipant` (default implementations) and the
  `core.mode_joined.v1` / `core.mode_left.v1` events back `mode_transition` `join` / `leave`.

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
