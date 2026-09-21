# CampaignVault Plugin System — Roadmap Plan

Groomed plan. This is the top-level plan for where the plugin system goes from here. It does not
restate what already works — see `PLUGINS.md` for that (current, shipped: `IRulesetModule` code
plugins, YAML data plugins, `/Plugins/` DLL drop-in via `PluginAssemblyLoader` + `AssemblyLoadContext`,
Autofac convention discovery). This doc sequences everything discussed beyond that baseline:
new extensibility surfaces (interaction modes, cross-cutting hooks), trust/sandboxing tiers, and
the eventual community marketplace. `INTERACTION_MODES_PLAN.md` is a child of this plan — the
detailed design for exactly one workstream (Track B below).

## Where things stand today (baseline, already shipped)

- **Trust model:** full-trust, in-process. Plugins are `.dll`s dropped in `/Plugins/`, loaded via
  `AssemblyLoadContext` (non-collectible), discovered by Autofac convention scanning. There is no
  security boundary — a plugin is assumed to be code the operator chose to run. This is fine for
  the current audience (yourself, and eventually vetted/trusted authors) and should **not** be
  "hardened" via reflection/IL tricks — see the "Sandboxing" track below for the real answer when
  untrusted authors enter the picture.
- **Extension points that exist:** `IRulesetModule` (whole stat systems: dnd5e/pf2e/homebrew),
  YAML data providers (spells/races/classes/...), `IWorldChangeHandler` (react to/own a mutation
  type), `ISimulationRule`, `IPressureContributor`, `IGuidanceContributor`, `IMcpServerTool`.
- **Known gaps** (from `PLUGINS.md` Future Work, restated here because this plan resolves some of
  them): closed `RulesetActionType` enum, no turn/state-machine primitive below `ICombatRuleset`,
  no cross-cutting post-commit observer hook, no plugin enablement scoping narrower than "is the
  DLL present," no marketplace, no hotloading.

## Tracks

### Track A — Enablement scoping (global / per-campaign / per-system) — **shipped**

Today a plugin is either loaded (DLL present) or not — there's no notion of "loaded but off for
this campaign." Needed once Track B (interaction modes) ships, since a GM shouldn't be forced into
every installed mode.

- [x] `CampaignConfig.EnabledModeIds` — per-campaign opt-in list, narrowest-wins against global DLL
  presence. (`EnabledSystemPlugins` not added — no concrete need surfaced yet; `IRulesetModule`
  selection is still one-per-campaign via `ActiveSystem`.)
- [x] A mode declares `IInteractionMode.CompatibleSystems` (empty = system-agnostic) so e.g. an
  Astral-Combat mode that needs Dnd5e plane stats refuses to activate under a Narrative-system
  campaign without the GM having to know that up front. Enforced in `ModeTransitionChangeHandler`.
- No new loading mechanism — this is pure config/validation on top of the existing discovery.

### Track B — Interaction modes & cross-cutting hooks — **core wiring shipped, reference plugin deferred**

Full design lives in **`INTERACTION_MODES_PLAN.md`**. Summary: generalizes the existing
`CombatEncounter`/`ICombatRuleset` turn-machine into a reusable `IInteractionMode`/
`IModeStateMachine`/`ModeEncounter` shape so plugins can define whole new turn-based activities
(crafting, hairstyling, astral combat) without touching the closed `RulesetActionType` enum or
`IRulesetModule`; adds an `IWorldChangeObserver` tier (post-commit, non-failing, cross-cutting —
needed for things like a trauma-triggered "inner voice" reactor); and defines trigger mechanics
(soft = `IGuidanceContributor` hint to the LLM, hard = observer-driven deterministic gate reusing
the `EncounterResolver` roll shape) and how modes borrow existing character stats
(`SystemExtension.Attributes`/`ResourcePools`, and delegating actual rolls to the campaign's active
`IRulesetModule.Actions` rather than reinventing dice math).

Shipped: `IInteractionMode`/`IModeStateMachine`/`ModeEncounter`/`IInteractionModeSelector`,
`ModeTransitionChange`/`ModeTransitionHandler`, the `IWorldChangeObserver` post-commit tier, and a
`WorldChangeDispatcher.FindHandler` bug fix (it only ever indexed core-assembly `WorldChange`
subtypes, so a plugin-defined change type would never actually dispatch — see
`INTERACTION_MODES_PLAN.md`'s "Bonus fix" note). Validated by
`tests/CampaignVault.UnitTests/InteractionModesTests.cs`. Deferred: a real reference mode plugin
(Crafting) and the corresponding `PLUGINS.md` section — see `INTERACTION_MODES_PLAN.md` Phases 4–5.

Depends on: nothing new from this doc except Track A for enablement. Can ship independently of
Tracks C/D.

### Track C — Sandboxing tiers (prerequisite for untrusted/community plugins)

Vanilla `AssemblyLoadContext` is **not** a security boundary (confirmed: .NET dropped CAS-style
sandboxing; ALC only isolates versioning/unload, not capabilities) — this has to be solved with a
different execution model before any plugin can come from someone other than a trusted author.
Two real tiers, not one:

1. **Trusted native DLL (current, Track 0)** — keep as-is for first-party/vetted plugins. No change.
2. **Lightweight scripted hooks — Jint.** For small, low-risk logic a mode/observer plugin wants to
   customize without a full DLL (a trigger condition, a narrative template, a simple state
   transition rule): embed Jint, expose only an explicit, narrow set of bound .NET
   objects/delegates to the script (no ambient file/process/network access unless deliberately
   granted), enforce statement/recursion/time limits. Good fit for "one function's worth of logic,"
   not a full mode implementation.
3. **Sandboxed native-speed plugins — WebAssembly (Extism / wasmtime-dotnet).** This is the real
   answer for untrusted, marketplace-sourced plugins: WASI capability-based isolation (no syscalls,
   no host access except explicit imports), enforceable CPU/memory limits, and language-agnostic
   authoring (Rust/Go/AssemblyScript/... compiled to `.wasm`). Extism specifically is built around
   exactly this "shareable plugin, host SDK, manifest" model. This is the tier a "community
   marketplace" requires — don't build the marketplace on tier 1 or 2.

Sequencing: build nothing here until Track D (marketplace) is actually being scoped. Track C is a
prerequisite for Track D, not for Track B — interaction modes ship fine as trusted DLLs first.

### Track D — Community marketplace (deferred, not yet scoped)

Explicitly deferred. Real prerequisites before this is worth designing in detail:
- Track C tier 3 (Wasm sandbox) shipped and load-bearing in production for at least one real plugin.
- A distribution/versioning story (manifest format, compatibility checks against CampaignVault's
  own version — both already flagged as unresolved in `PLUGINS.md`'s Future Work).
- A trust/moderation story for what gets listed (even sandboxed code can be abusive — spam,
  scraping via the exposed host API surface, IP issues on shared content).

Nothing to build yet; this section exists so the sequencing above ("don't sandbox before you need
to, don't marketplace before you can sandbox") stays visible as a single decision record.

## Sequencing summary

```
Track A (enablement scoping) ──┐
                                ├──> Track B (interaction modes + hooks) ships first, trusted-DLL only
Track B design doc ─────────────┘

Track C (sandboxing: Jint now-ish, Wasm later) ──> Track D (marketplace), only once C tier 3 is proven
```

Track B is the near-term deliverable. Tracks C/D are directional — revisit when a concrete plugin
author other than yourself is actually on the table.
