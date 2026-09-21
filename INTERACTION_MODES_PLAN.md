# Interaction Modes & Cross-Cutting Hooks — Design Plan

Child plan of `PLUGIN_SYSTEM_PLAN.md` (Track B). See that doc for how this fits alongside
enablement scoping, sandboxing tiers, and the deferred marketplace.

Groomed plan. Goal: let plugins define whole new turn-based interaction modes (crafting,
hairstyling-as-state-machine, astral combat, ...) — not just new `IRulesetModule` stat systems —
and let cross-cutting plugins (an "inner voice" trauma reactor, an event-log narrator, ...) observe
*any* mode's mutations without coupling to it. Resolves two items already flagged in `PLUGINS.md`'s
Future Work: "Action type extension (currently closed enum)" and, implicitly, "Bootstrap step
injection" for non-ruleset state machines.

## Why this isn't a rewrite

Everything needed for mode-specific *mutations* already exists and is already plugin-open:
`WorldChange` subclassing + `IWorldChangeHandler` is unbounded — a plugin can define
`CraftingProgressChange`/`HairstyleStepChange` today, register a handler for it via Autofac
convention scanning (`PLUGINS.md` §"Discovery & Loading"), and it Just Works through
`WorldChangeDispatcher`, same as `TravelChangeHandler`/`SceneInterruptChangeHandler`. **The
`RulesetActionType` enum is a dead end for this — don't route new modes through
`RulesetAction`/`ActionType` at all.** New modes get their own `WorldChange` subtype with a
free-form verb, exactly like every non-combat handler already does.

What's actually missing is smaller than "new action types": (1) a reusable turn/state-machine
primitive so every mode plugin doesn't reinvent "whose turn, how many actions, is this over" from
scratch, (2) a registry + enablement scope for *modes* (orthogonal to `ActiveSystem`, which is one
ruleset per campaign, not a stack of optional modes), and (3) a way to react to changes across
modes without the modes knowing about each other.

## Design decisions

- **Modes are additive, not a replacement for `IRulesetModule`.** `ActiveSystem` keeps meaning
  "which stat system this campaign uses" (dnd5e/pf2e/narrative). A mode is a *scene-scoped*
  activity that layers on top — a Dnd5e campaign can drop into "Crafting" mode for a scene without
  touching `ActiveSystem`.
- **Generalize `CombatEncounter`/`ICombatRuleset`, don't migrate combat onto it yet.** The existing
  `CombatEncounter` doc (round/combatants/activeTurnId/actionBudget) and `ICombatRuleset`
  (`GetTurnActionBudget`/`TryConsumeActionSlot`) are *already* a generic turn-machine, just named
  and keyed for combat specifically. New modes get the generalized version
  (`ModeEncounter`/`IModeStateMachine`); combat is left alone (it's stable, tested, and reworking
  it into the generic path is a separate, riskier migration — not a blocker for shipping new
  modes).
- **Enablement is three-tier, narrowest wins**, same shape already implied by
  `RulesetSystem`/plugin discovery:
  1. Global — whether the mode DLL is even loaded (ops kill switch, `/Plugins/` presence).
  2. Per-campaign — `CampaignConfig.EnabledModeIds` opt-in list (GM decides "this campaign has
     Astral Combat content").
  3. Per-system-compatibility — a mode declares which `ActiveSystem`s it's valid under (empty =
     system-agnostic, so "Hairstyling" works everywhere but "Astral Combat" can require Dnd5e
     plane-of-existence stats).
- **Observers are a distinct hook tier from handlers.** A handler *owns* a `WorldChange` type and
  can fail the commit. An observer reacts *after* a commit succeeds and cannot fail it — this is
  the right shape for "inner voice on trauma trigger," "narrative log," "achievement tracker": they
  watch everything, own nothing, and a bug in one can't corrupt another mode's state.

## New interfaces

```csharp
// Rulesets/Modes/IInteractionMode.cs
public interface IInteractionMode
{
    string ModeId { get; }                       // "crafting", "hairstyling", "astral_combat"
    string DisplayName { get; }
    IReadOnlyList<string> CompatibleSystems { get; } // [] = system-agnostic
    IModeStateMachine StateMachine { get; }
    // Actions/mutations are just ordinary WorldChange + IWorldChangeHandler pairs registered
    // by the plugin via existing Autofac convention scanning — not part of this interface.
}

public interface IModeStateMachine
{
    ModeEncounter CreateEncounter(string locationId, IReadOnlyList<string> participantIds);
    IReadOnlyDictionary<string, int> GetTurnActionBudget(Character participant);
    bool TryConsumeActionSlot(ModeParticipantState state, WorldChange action, out string? errorReason);
    bool AdvanceTurn(ModeEncounter encounter);            // round/turn progression + wraparound
    bool IsComplete(ModeEncounter encounter, out string? outcomeNarrative);
}
```

```csharp
// Models/ModeEncounter.cs — generalized CombatEncounter/CombatantState
public class ModeParticipantState
{
    public string CharacterId { get; set; } = null!;
    public Dictionary<string, int> ActionBudget { get; set; } = [];
    public Dictionary<string, object> State { get; set; } = []; // mode-owned scratch (e.g. "stage": "cut")
}

public class ModeEncounter
{
    public string Id { get; set; } = null!;       // campaigns/{name}/mode/{modeId}/current
    public string ModeId { get; set; } = null!;
    public string LocationId { get; set; } = null!;
    public int Round { get; set; } = 1;
    public List<ModeParticipantState> Participants { get; set; } = [];
    public string? ActiveTurnId { get; set; }
    public bool IsActive { get; set; }
}
```

```csharp
// Rulesets/Modes/IInteractionModeSelector.cs — mirrors IRulesetModuleSelector exactly
public interface IInteractionModeSelector
{
    IInteractionMode? TryGetMode(string modeId);
    IReadOnlyCollection<string> RegisteredModeIds { get; }
}
```

```csharp
// Data/ChangeHandlers/IWorldChangeObserver.cs — new hook tier, fires after a handler commits
public interface IWorldChangeObserver
{
    bool IsInterestedIn(WorldChange committed, ChangeContext context); // cheap, no side effects
    Task OnCommittedAsync(WorldChange committed, ChangeContext context, CancellationToken ct = default);
}
```
`WorldChangeDispatcher` runs registered observers (DI `IEnumerable<IWorldChangeObserver>`) after
a handler's `ApplyAsync` returns success, in registration order, swallowing/logging individual
observer exceptions so one broken observer can't roll back or block an unrelated mutation. An
observer that wants to *cause* further mutations (e.g. "spawn an InnerVoiceChange") enqueues a new
`WorldChange` onto the same dispatch queue rather than mutating context directly — keeps it
auditable the same way every other mutation is.

## Entry/exit into a mode

A `ModeTransitionChange : WorldChange` + `ModeTransitionHandler`, same shape as combat-start
today: validates `modeId` is registered (`IInteractionModeSelector`) and enabled for the campaign
(`CampaignConfig.EnabledModeIds`) and compatible with `ActiveSystem`, then creates/tears down the
`ModeEncounter` doc via `StateMachine.CreateEncounter`.

## Triggering: how a mode actually starts

Two tiers, reusing hooks already in this plan — no third hook type needed. Default to the soft
tier; reach for the hard tier only for rules that must not be left to LLM discretion.

**Soft trigger (default): `IGuidanceContributor`.** Same shape as the existing
`CombatStartedGuidanceContributor` (`Data/Guidance/Contributors/`) — a scene-scoped contributor
inspects `PressureContext` (location tags, recent commits, participant state) and, when conditions
look right, emits a `GuidanceHint` nudging the LLM to call the mode-transition tool itself. E.g.
`AstralCombatSuggestedGuidanceContributor`: `Scope = Scene`, fires when the current location's
tags include `"astral-plane"` and no mode is currently active; hint text: "Consider offering an
Astral Combat encounter here — call mode_transition(modeId: astral_combat) if the narrative calls
for it." The LLM stays the author of pacing; this never mutates state on its own.

**Hard trigger: `IWorldChangeObserver`.** For rules that are deterministic by design (e.g.
"stepping onto the Astral Plane unwarded always risks an encounter"), a mode-supplied observer
watches for the qualifying committed change (a `TravelChange`/location change landing on an
astral-tagged `Location`) and runs a probabilistic gate — literally the same bucketed
roll-with-modifier shape `EncounterResolver` already uses for travel encounters, generalized so any
mode can plug its own base chance + modifier source (see attributes, below). On a hit, the observer
enqueues the `ModeTransitionChange` itself onto the dispatch queue, same as any other
observer-caused mutation.

## Supporting attributes & skills

Modes should not invent their own stat storage or dice math — two existing extension points
already cover this:

- **`SystemExtension.Attributes: Dictionary<string, float>`** is already an open, ruleset-agnostic
  bag. A mode reads/writes e.g. `Attributes["AstralAttunement"]` or `Attributes["Hairstyling"]`
  directly — no changes to `Character`/`SystemExtension`/`Dnd5eExtension` needed. This is the
  existing mechanism that answers "supporting attributes": it's already there, unused by any
  specific ruleset, free for mode plugins to claim a key in.
- **`SystemExtension.ResourcePools`** covers anything consumable a mode needs (astral stamina,
  crafting "focus"), same story.
- **Anything that's an actual roll** (skill check, save, contested check) should not be
  reimplemented per mode. Delegate to the campaign's *already-active*
  `IRulesetModule.Actions.ResolveAsync(...)` via the existing `RulesetActionType.SkillCheck` /
  `ContestedCheck` / `SavingThrow` — these are already system-agnostic ("roll a named
  attribute/skill against a DC"), which is exactly what a hairstyling Dexterity check or an
  astral-navigation check is. A mode's `WorldChange` handler becomes a thin orchestrator: it
  receives e.g. `HairstylingStepChange{Step="cut"}`, internally issues a sub-`RulesetAction` for
  the roll, reads the `ResolverOutput`, and folds success/fail into
  `ModeParticipantState.State["stage"]`. A Dnd5e character and a PF2e character both get correct
  proficiency/ability math automatically — the mode plugin never needs to know 5e modifiers from
  PF2e modifiers.

Net rule: only genuinely new, non-roll *verbs* (select a style template, phase-shift in astral
combat) get bespoke `WorldChange` types. Anything roll-shaped reuses the existing action pipeline —
which is also why `RulesetActionType` still doesn't need to grow to support new modes.

## Worked example: "Hairstyling" as a state machine

- `ModeId = "hairstyling"`, `CompatibleSystems = []` (works anywhere).
- Own `WorldChange` subtypes: `SelectStyleChange`, `HairstylingStepChange` (verb: "wash" / "cut" /
  "style" / "finish"), each with its own handler mutating `ModeParticipantState.State["stage"]`.
- `IModeStateMachine.IsComplete` returns true once `stage == "finish"`, with a narrative outcome
  string the GM/LLM can use to describe the result.
- No `RulesetActionType`, no `ICombatRuleset` involvement, no change to `dnd5e`/`pf2e` code at all.

## Phases

1. [x] `IInteractionMode` / `IModeStateMachine` / `ModeEncounter` / `IInteractionModeSelector` —
   additive types only, no changes to `CombatEncounter`/`ICombatRuleset`/`RulesetActionType`. Shipped
   in `Rulesets/Modes/IInteractionMode.cs` + `Models/ModeEncounter.cs`. `InteractionModeSelector` is
   registered explicitly in `ConventionRegistration.RegisterApplicationCore` (its namespace,
   `CampaignVault.Rulesets.Modes`, doesn't match `RegisterNameMatchedServices`' exact-namespace
   convention scan) and `IInteractionMode` implementations are collected via
   `RegisterCollection<IInteractionMode>` per assembly, so plugin-supplied modes are discovered the
   same way plugin `IRulesetModule`s are.
2. [x] `CampaignConfig.EnabledModeIds` + `ModeTransitionChange`/`ModeTransitionHandler`, wired through
   `WorldChangeDispatcher` the same way `SceneInterruptChangeHandler` is today. Validates mode
   registration → `EnabledModeIds` → `CompatibleSystems`, in that order; enter creates a `ModeEncounter`
   keyed by `CampaignDocumentKeys.ModeCurrent`, exit deactivates it.
3. [x] `IWorldChangeObserver` tier in `WorldChangeDispatcher` (post-commit, non-failing, DI-discovered
   like handlers) — this is the mechanism the earlier "inner voice / trauma trigger" idea needs.
   Wired as an optional trailing ctor param + `RegisterCollection<IWorldChangeObserver>`; fires only
   after a change's own handler reports success, and an observer exception is logged and swallowed,
   never propagated into `CommitResult`.
4. [ ] Deferred. Ship one reference mode plugin end-to-end (Crafting is the simplest — linear stages,
   no contested rolls) as an actual runnable `/Plugins/` DLL, not just the docs walkthrough below.
   Wiring itself (selector resolution, enablement gating, observer contract) is validated instead by
   `tests/CampaignVault.UnitTests/InteractionModesTests.cs` using a test-double `FakeMode`.
5. [x] Updated `PLUGINS.md` with a "Type 3: Interaction Mode Plugin" section, a full step-by-step
   authoring walkthrough (mirroring the `IRulesetModule` one), a `## Trust Model` section (code
   plugins — including interaction modes — are full-trust/in-process; no sandboxing yet, see
   `PLUGIN_SYSTEM_PLAN.md` Track C), and API reference entries for the new interfaces. Done ahead of
   (4) rather than blocked on it — the walkthrough's crafting example is illustrative code in the
   docs, not a shipped, tested reference plugin, which is what (4) still covers. Checked off the
   "Action type extension" Future Work item (resolved indirectly via open `WorldChange` subtypes, not
   by opening the `RulesetActionType` enum itself).

### Bonus fix #2: EnabledModeIds was write-only plumbing until wired to a tool

`CampaignConfig.EnabledModeIds` (Phase 2) had no LLM/operator-reachable write path — `ModeTransitionChangeHandler`'s "not enabled for this campaign" gate would have been permanently closed in production with no way to open it. Fixed by adding `CampaignUpdateChange.EnabledModeIds` (full-replacement list, same convention as the existing `NarrativeFocus` field) and wiring it in `CampaignUpdateChangeHandler` — reachable today via `take_turn`'s `campaign_update` commit type, discoverable via `get_commit_schema` (reflection-based over `[JsonDerivedType]`, so no separate schema registration was needed). Covered by `CampaignUpdateHandler_SetsEnabledModeIds_OnPreloadedConfig` / `_CreatesAndStoresConfig_WhenNoneExistsYet`.

### Bonus fix discovered during implementation

`WorldChangeDispatcher.FindHandler` (and `ExtractInvolvedIds`) resolved change→handler lookups purely
through `BuildHandlerDictionary`, which only enumerates `WorldChange` subtypes declared in
`typeof(WorldChange).Assembly` — i.e. the core assembly only. A plugin-defined `WorldChange` subtype
(the load-bearing claim in "Why this isn't a rewrite" above) was never a key in that dictionary, so it
would always report `"WARNING: Unhandled change type"` even with a correctly registered plugin handler.
Fixed with a linear `ShouldHandle` fallback scan, memoized into the same dictionary on first hit, so
repeat dispatches of the same plugin-defined type don't re-scan. `ValidateHandlerCoverage` at startup
is intentionally left core-assembly-only — a third-party DLL shipping an unclaimed change type should
not crash the whole server at boot. Covered by
`Dispatcher_FindHandler_FallsBackToLinearScan_ForChangeTypeOutsideCoreAssembly`.

Deliberately deferred: migrating `CombatEncounter` onto `ModeEncounter` (combat stays as-is —
revisit only if duplication between the two becomes a real maintenance cost), and the Wasm/Jint
untrusted-plugin sandbox tier discussed separately (this plan assumes trusted, drop-in DLL plugins
per the existing `PluginAssemblyLoader` model).
