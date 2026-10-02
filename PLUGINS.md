# CampaignVault Plugin Architecture

CampaignVault supports three plugin types: **data-only plugins** (YAML), **code plugins** (DLL + C#, `IRulesetModule`), and **interaction-mode plugins** (whole new turn-based activities like crafting or astral combat — see [Type 3](#type-3-interaction-mode-plugin)). Mix and match to extend the platform without modifying core code.

## Trust Model

**Every code plugin today runs full-trust, in-process, exactly like first-party code.** A `.dll` dropped in `/Plugins/` is loaded via `AssemblyLoadContext` and given the same access to the process as `IRulesetModule` implementations shipped with CampaignVault itself — there is no sandbox, no capability restriction, and no permission model. `AssemblyLoadContext` alone is **not** a security boundary in .NET (it isolates assembly versioning/unload, not what code is allowed to do); the vanilla plugin-loading mechanism described in this document assumes the operator trusts the author, the same way they'd trust code they wrote themselves.

**In practice this means:** only load plugin DLLs from authors you trust (yourself, your table, or a vetted source), the same discipline you'd apply to any other native code you run. A malicious or buggy plugin DLL can do anything the CampaignVault process can do — read/write the database, the filesystem, the network.

This is a deliberate, current-stage tradeoff, not an oversight: it's the same tradeoff the "first-party, trusted" model always makes, and it keeps plugin authoring simple (plain C#, no scripting sandbox, no serialization boundary) for the audience this exists for today. **Data-only (YAML) plugins carry no code-execution risk** — they're parsed data, not loaded assemblies — so the trust model above only applies to code plugins (`IRulesetModule` and interaction-mode plugins).

A real sandboxing story (for eventually running plugins from authors you *don't* personally vet — a community marketplace) is planned but not built: see `PLUGIN_SYSTEM_PLAN.md`'s Track C for the two real tiers under consideration (Jint for lightweight scripted hooks, WebAssembly/Extism for genuine capability-isolated native-speed plugins). Until Track C ships, treat every code plugin as equivalent to first-party code.

## Installing a Plugin (For Operators)

This section is for someone installing a plugin *someone else wrote*. If you're authoring your own, skip to [Quick Start](#quick-start).

**Before you install — trust checklist:**
- Does the package contain a `Plugins/*.dll`? If so, it's a **code plugin** — see [Trust Model](#trust-model) above. Only install code plugins from authors you'd trust to run any other native code on this machine.
- `RulesetData/`-only packages (no `.dll`) are **data-only** — parsed YAML, no code execution risk — safe to try even from a less-vetted source.
- A `plugin.json` with a `campaignOptions` block only ever *declares string/number/bool defaults* the host may merge into a campaign's house-rule settings — it is data, not code, regardless of whether the package also ships a DLL.

**Steps:**
1. Extract the package into your CampaignVault install directory, preserving its layout — a data-only pack drops files under `RulesetData/<system>/...`; a code plugin drops a folder under `Plugins/<PluginName>/` containing `plugin.json` + the `.dll` (see [Directory Structure](#directory-structure)).
2. Restart the MCP host process (or the Docker container) so it re-scans `Plugins/` and `RulesetData/`.
3. Verify it loaded: check startup logs for `Loaded plugin assembly: ...` (code plugins) or call `lookup(kind: 'items' | 'spells' | ...)` / `get_config` and confirm the new system, content, or `campaignOptions`-declared keys show up.
4. If nothing shows up, see [Troubleshooting](#troubleshooting) below (`"Failed to load plugin assembly"`, `"DLL loads but module is not registered"`, `"YAML data not loading"`).

**From the Unity client (built-in server):** Settings → Plugins lists every package the server found (`GET /plugins`, a plain HTTP endpoint that costs the model nothing), installs a `.zip` into your app data's `Plugins/` folder (a code plugin asks for an explicit trust confirmation first), uninstalls user-installed plugins, and enables/disables any plugin; changes apply after **Restart server**. The zip holds one package: `plugin.json` at the top or in one folder, plus its `RulesetData/`, `skills/` and DLLs. Zips with `..`/absolute paths, more than one `plugin.json`, or an id that's already installed are refused.

**Server switches:** `CAMPAIGN_PLUGIN_DIRS` adds folders scanned after the bundled `Plugins/` (a package there can't take a bundled plugin's id); `CAMPAIGN_PLUGINS_DISABLED` lists ids not to load. `GET /plugins` reports each package's id, name, version, author, description, kind (`code`/`data`), source (`bundled`/`user`), enabled/loaded state, systems, modes, campaign options and load errors. `plugin.json` accepts optional `author` and `description` fields for that listing.

**Data-only packages:** a `Plugins/<Name>/` folder with a `plugin.json` and a `RulesetData/` but no DLL is loaded as data: its YAML joins the ruleset roots, and its `campaignOptions` defaults apply, with no code run.

Uninstalling: delete the plugin's folder/files and restart. Data-only content simply stops resolving; any campaign `SystemOptions` keys the plugin had defaulted are left as-is on existing campaigns (they were copied into the campaign's config, not referenced live).

## Quick Start

### Data-Only Plugin (5 minutes)

Create `/RulesetData/mysystem/` with YAML files:

```
RulesetData/
└── mysystem/
    ├── spells/
    │   └── mystical_blast.yaml
    ├── races/
    │   └── custom_race.yaml
    ├── classes/
    │   └── custom_class.yaml
    └── ...
```

Restart MCP. Done — campaigns can now use `mysystem`.

### Code Plugin (30 minutes)

1. Create a C# class library project
2. Implement `IRulesetModule`
3. Build to DLL
4. Drop in `/Plugins/` directory
5. Restart MCP

```csharp
public class MyCustomSystem : IRulesetModule
{
    public string System => "my_ruleset";
    public IActionResolution Actions => this;
    public ICombatRuleset Combat => this;
    // ... implement required interfaces
}
```

Restart MCP. Campaigns can now use `my_ruleset` with custom rules.

### External plugin repo (PluginSdk) — preferred for out-of-tree modes

Out-of-tree plugins should **PackageReference `CampaignVault.PluginSdk` only** (public nuget.org when published; local feed for development). Do **not** ProjectReference the host, do **not** ship `CampaignVault.PluginSdk.dll` beside your plugin, and do **not** rely on `InternalsVisibleTo`.

```xml
<PackageReference Include="CampaignVault.PluginSdk" Version="0.5.0" />
```

Layout when installing into a host:

```
Plugins/
  YourMode/
    plugin.json
    YourMode.dll          # plugin assembly only — never CampaignVault.PluginSdk.dll
    RulesetData/          # optional YAML overlay (merged last-wins on system id)
    skills/               # optional LLM-client sidecar; host logs path only, does not load/inject
```

`plugin.json` fields: `id`, `displayName`, `version`, `minEngineVersion` (host skips on mismatch — no boot crash), optional `modeIds`, optional `playerOnlyModeIds` (modes only the player may enable or disable: a `campaign_update` that switches one must quote the player in `playerRequest` and be alone in its commit), `rulesetDataRoots` (default `./RulesetData`), `skillsPath` (default `./skills`, log-only), optional `campaignOptions` (declares house-rule config keys and their defaults — see [Campaign Option Defaults](#campaign-option-defaults) below).

**Custom `$type`:** annotate with `[PluginWorldChange("my_verb")]`. Handler **dispatch** already works via `WorldChangeDispatcher.FindHandler`'s `ShouldHandle` fallback. JSON wire deserialization and `lookup kind=commit_schema` require the type registry (seeded from core `[JsonDerivedType]` + plugin attributes at load). Discriminator collisions fail fast at registration.

**ALC / type identity:** the host resolves `CampaignVault.PluginSdk` from `AssemblyLoadContext.Default` for plugin ALCs. Dropping a second Sdk.dll in the plugin folder is skipped with a warning.

**Skills:** sidecar-only. Operators/clients install skill markdown; the MCP host never auto-discovers or serves them.

**Adult / optional content:** belongs in separate repos referencing PluginSdk; the main repo ships only neutral samples (e.g. `plugins/CraftingMode`).

**Compatibility:** host engine version is `0.14.0` (`EngineVersion.Current`). Set `minEngineVersion` accordingly.

In-tree reference: `plugins/CraftingMode` (mode id `crafting`, `$type` `crafting_step`).

See also `PLUGIN_SDK_PLAN.md` (platform track) and `INTERACTION_MODES_PLAN.md`.

---

## Architecture Overview

### Three-Layer System

```
┌─────────────────────────────────────┐
│  Campaign (uses system ID)          │
├─────────────────────────────────────┤
│  IRulesetModule (code)              │  ← Optional (code plugins only)
│  + YAML Data (spells/races/etc)     │
├─────────────────────────────────────┤
│  Base Calculation Engines           │
│  (HP, AC, checks, etc)              │
└─────────────────────────────────────┘
```

- **Campaign** stores system ID as a string (e.g., `"dnd5e"`, `"swade"`, `"my_ruleset"`)
- **IRulesetModule** (if present) provides custom calculation logic
- **YAML data** defines spells, races, classes, feats, pools, creatures, conditions, backgrounds, progressions
- **Base engine** applies core mechanics (work for any system)

### Discovery & Loading

**At Startup:**
1. Scan `/RulesetData/*/` for system directories → discover system IDs
2. Scan `/Plugins/` for `.dll` files → load assemblies
3. Autofac convention registration discovers:
   - `IRulesetModule` implementations (one per plugin)
   - `ISimulationRule`, `IPressureContributor`, `IGuidanceContributor` (optional)
   - `IWorldChangeHandler`, `IMcpServerTool` (optional)

**At Campaign Load:**
- Verify system exists (has IRulesetModule OR YAML data)
- Load appropriate module or degrade to base SystemExtension
- Load YAML data from disk/embedded resources

---

## Plugin Types

### Type 1: Data-Only Plugin

**Use case:** Add spells, races, items, etc. to an existing ruleset without custom rules.

**Example:** SWADE reskin using D&D 5e mechanics but SWADE spell names/descriptions.

**Directory structure:**
```
RulesetData/
└── swade/
    ├── spells/
    │   ├── fire_blast.yaml
    │   └── cure_light_wounds.yaml
    ├── races/
    │   ├── human.yaml
    │   └── dwarf.yaml
    ├── classes/
    │   └── warrior.yaml
    ├── feat s/
    │   └── fireball_mastery.yaml
    └── items/
        ├── longsword.yaml
        └── climbers_kit.yaml
```

**Files to create:** Just YAML files in appropriate subdirectories.

**Behavior:**
- No IRulesetModule needed
- System degrades to base `SystemExtension` (no custom calculations)
- All core rules (HP calculation, AC, checks, etc.) work with built-in formulas
- Data loads from disk, extracted from embedded resources, or both

**When to use:**
- Reskinning existing systems
- Adding homebrew content without custom rules
- Rapid prototyping before committing to code plugin

### Type 2: Code Plugin

**Use case:** Custom calculation logic, new action types, special mechanics.

**Example:** Actual SWADE with d10 action economy, wound thresholds, multiple actions per turn.

**Files to create:**
```
MyPlugin/
├── MyPlugin.csproj
└── MyRulesetModule.cs
    └── public class MyRulesetModule : IRulesetModule { ... }
```

**Build & deploy:**
1. Build to `.dll`
2. Copy to `/Plugins/` directory (created at startup if missing)
3. Restart MCP
4. Module is discovered and registered automatically

**Behavior:**
- Plugin DLL loaded via `AssemblyLoadContext` (isolated but shareable)
- Plugin's `IRulesetModule` implementation used for all custom logic
- Optional YAML data still loads and works alongside code
- Can provide additional `ISimulationRule`, `IPressureContributor`, etc.

**When to use:**
- Custom mechanics that differ from base rules
- New action types or resolution methods
- Complex calculations (XP thresholds, spell slots, etc.)
- System-specific pressure/guidance logic

### Type 3: Interaction Mode Plugin

**Use case:** A whole new turn-based *activity*, distinct from combat and from the campaign's ruleset — crafting, hairstyling-as-a-state-machine, astral combat, a heist minigame. Scene-scoped, not campaign-scoped: a D&D 5e campaign can drop into "Crafting" mode for one scene without touching `ActiveSystem`.

**Trust model:** same as any code plugin (see [Trust Model](#trust-model) above) — full-trust, in-process.

**Files to create:**
```
MyModePlugin/
├── MyModePlugin.csproj
└── CraftingMode.cs
    └── public class CraftingMode : IInteractionMode { ... }
```

```csharp
public class CraftingMode : IInteractionMode
{
    public string ModeId => "crafting";
    public string DisplayName => "Crafting";
    public IReadOnlyList<string> CompatibleSystems => []; // empty = works under any ruleset
    public IModeStateMachine StateMachine { get; } = new CraftingStateMachine();
}
```

Its actual verbs (e.g. a `CraftingStepChange` WorldChange + handler) are ordinary `IWorldChangeHandler` registrations — not part of `IInteractionMode` itself. See [Interaction Mode Plugin](#interaction-mode-plugin) below for the full walkthrough, and `INTERACTION_MODES_PLAN.md` for the design rationale.

**Behavior:**
- A mode must be explicitly enabled per campaign (`CampaignConfig.EnabledModeIds`, set via the `campaign_update` commit type) before it can be entered — being loaded (DLL present) is necessary but not sufficient.
- Entering/exiting is a single commit type, `mode_transition` (`ModeId`, `Action: "enter"|"exit"`, `LocationId`, `ParticipantIds`), validated against registration → enablement → `CompatibleSystems`, in that order.
- Actual dice rolls (skill checks, saves) should delegate to the campaign's already-active `IRulesetModule.Actions` via `RulesetActionType.SkillCheck`/`SavingThrow`/etc. rather than reimplementing them — a mode plugin never needs to know 5e math vs. PF2e math.
- **Action budget is enforced by the host.** For a verb whose `[PluginWorldChange(ModeId = "...")]` names your mode, while that mode has an active encounter, the dispatcher calls `TryConsumeActionSlot` on the actor's participant state (the change's `ActorId`, else `CharacterId`, else the encounter's `ActiveTurnId`) before the handler runs, and refunds the slot if the handler fails or throws. A refusal fails the commit with your `errorReason`. Refill slots in `AdvanceTurn`.
- Mode-specific character stats live in the existing `SystemExtension.Attributes`/`ResourcePools` dictionaries — no core model changes needed to add e.g. `Attributes["AstralAttunement"]`. A pool your plugin manages itself (a meter that starts empty, a max you compute) should set `ownerManaged: true` in its `pools/` template: core creates it once and never refills, resizes or re-derives it afterwards. `startsAt: zero` creates it at 0 instead of full.
- A Physical, Hard `EngagementRelation` (e.g. a prisoner chained to a guard) blocks the holder's `travel`, unless its target travels to the same destination in the same commit — so a chained group moves together.
- Mode-specific string facts go in `SystemExtension.Traits` (`Dictionary<string,string>`) — the same closed-set caveat as above applies: you cannot add your own `[JsonDerivedType]` to `SystemExtension`, only write keys into the base class's shared `Traits` dictionary. **Key convention: `"<modeId>.<name>"`** (e.g. `"crafting.tool_quality"`), for two reasons at once — it namespaces your keys against every other plugin writing into the same dictionary, and it is what gates the entry onto `NpcCard.SystemTraits`. A prefixed key only rides an NPC's card while that NPC is an active participant in your mode's `ModeEncounter` (checked at `BuildUndeliveredCardsAsync` time, not the point of write) — this keeps mode-only facts from costing tokens on every `take_turn` when nobody is in the mode. An unprefixed key (no `.`) always rides; use that only for facts genuinely relevant outside your mode.

**When to use:**
- A genuinely new kind of turn-based interaction, not a variant of combat or an existing ruleset action
- Content you want scoped to specific campaigns/systems rather than always-on

---

## Creating a Plugin: Step by Step

### Data-Only Plugin

**Step 1:** Create directory structure
```bash
mkdir -p RulesetData/homebrew_system/{spells,races,classes,feats}
```

**Step 2:** Write YAML files (inherit from existing definitions or start from scratch)

Example: `RulesetData/homebrew_system/races/my_race.yaml`
```yaml
name: MyRace
description: A custom race
size: Medium
speed: 30
ability_score_increases:
  strength: 2
  constitution: 1
```

**Step 3:** Restart MCP

**Step 4:** Create campaign with system ID `"homebrew_system"`

That's it! No code required.

---

### Code Plugin

**Step 1:** Create a C# class library (if you don't have one)
```bash
dotnet new classlib -n MyRulesetPlugin
cd MyRulesetPlugin
```

**Step 2:** Reference the PluginSdk package only (never ProjectReference the host):

```xml
<PackageReference Include="CampaignVault.PluginSdk" Version="0.14.0" />
```

`IRulesetModule`, `IActionResolution`, `ICombatRuleset`, and the character-bootstrap
contracts (`IBootstrapStep`, `ICharacterBootstrapPipeline`, `BootstrapContext`) all live
in the SDK, so a full ruleset authors out-of-tree. Example structure:

```csharp
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;

namespace MyRulesetPlugin
{
    public class MyCustomRuleset : IRulesetModule
    {
        public string System => "my_system";

        public IActionResolution Actions => this;
        public ICombatRuleset Combat => this;
        public ICharacterBootstrapPipeline Bootstrap =>
            new CharacterBootstrapPipeline([new MyDeriveHitPointsStep()]);

        // IActionResolution.ResolveAsync, ICombatRuleset members (RollInitiativeAsync,
        // GetTurnActionBudget, TryConsumeActionSlot, EnforcesRange), IBootstrapStep members.
    }
}
```

Notes:
- System-specific read-side pressure is *not* part of `IRulesetModule` (pressure contexts
  carry the host's live database session). Implement `IPluginGuidanceContributor` and/or
  `IPluginContextContributor` instead — the host surfaces those on the same read paths.
- Bootstrap steps needing worn gear read it via `BootstrapContext.EquipmentAccess`
  (null = no data, degrade to unarmored defaults). Never touch RavenDB from a plugin;
  the SDK stays Raven-free by design.

**Step 3:** Build
```bash
dotnet build -c Release
```

**Step 4:** Deploy
```bash
# Copy DLL to plugins directory
cp bin/Release/net10.0/MyRulesetPlugin.dll /path/to/CampaignVault/Plugins/
```

**Step 5:** Restart MCP

Your `IRulesetModule` is now registered and available.

---

### Interaction Mode Plugin

**Step 1:** Implement `IInteractionMode` and `IModeStateMachine`

```csharp
using CampaignVault.Rulesets.Modes;
using CampaignVault.Models;

public class CraftingMode : IInteractionMode
{
    public string ModeId => "crafting";
    public string DisplayName => "Crafting";
    public IReadOnlyList<string> CompatibleSystems => []; // system-agnostic
    public IModeStateMachine StateMachine { get; } = new CraftingStateMachine();
}

public class CraftingStateMachine : IModeStateMachine
{
    public ModeEncounter CreateEncounter(string locationId, IReadOnlyList<string> participantIds) =>
        new()
        {
            LocationId = locationId,
            Participants = participantIds.Select(id => new ModeParticipantState { CharacterId = id }).ToList()
        };

    public IReadOnlyDictionary<string, int> GetTurnActionBudget(Character participant) =>
        new Dictionary<string, int> { ["action"] = 1 };

    public bool TryConsumeActionSlot(ModeParticipantState state, WorldChange action, out string? errorReason)
    {
        errorReason = null;
        return true;
    }

    public bool AdvanceTurn(ModeEncounter encounter) => encounter.IsActive;

    public bool IsComplete(ModeEncounter encounter, out string? outcomeNarrative)
    {
        var stage = encounter.Participants.FirstOrDefault()?.State.GetValueOrDefault("stage") as string;
        outcomeNarrative = stage == "finish" ? "The item is complete." : null;
        return stage == "finish";
    }
}
```

**Step 2:** Define the mode's own verbs as ordinary `WorldChange` + `IWorldChangeHandler` pairs — these are *not* part of `IInteractionMode`, they're discovered the same way any other plugin handler is:

```csharp
public class CraftingStepChange : WorldChange
{
    public string CharacterId { get; set; } = null!;
    public string Step { get; set; } = null!; // "gather" | "shape" | "finish"
}

public class CraftingStepChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is CraftingStepChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var step = (CraftingStepChange)change;
        // Mutate ModeParticipantState.State["stage"] on the active ModeEncounter, or delegate
        // a skill check to context via the campaign's active IRulesetModule.Actions if this step
        // requires a roll. See INTERACTION_MODES_PLAN.md's "Supporting attributes & skills" section.
        return ChangeHandlerResult.Ok;
    }
}
```

**Step 3:** Build and deploy exactly like a code plugin — build to `.dll`, copy to `/Plugins/`, restart MCP. `IInteractionMode` and `IWorldChangeHandler` implementations are both discovered by the same Autofac convention scan.

**Step 4:** Enable the mode for a campaign via `take_turn`'s `campaign_update` commit:

```json
{ "$type": "campaign_update", "enabledModeIds": ["crafting"] }
```

**Step 5:** Enter the mode via `take_turn`'s `mode_transition` commit:

```json
{ "$type": "mode_transition", "modeId": "crafting", "action": "enter", "locationId": "locations/forge", "participantIds": ["chars/pc1"] }
```

`lookup kind=commit_schema` documents both `campaign_update` and `mode_transition` in full (required fields, examples) — call it if you need the exact shape.

---

## Directory Structure

### Full Plugin Layout

```
CampaignVault/
├── RulesetData/                    # YAML data for systems
│   ├── dnd5e/                      # Built-in system
│   │   ├── spells/
│   │   ├── races/
│   │   ├── classes/
│   │   └── ...
│   ├── pf2e/                       # Built-in system
│   │   └── ...
│   └── my_homebrew/                # Data-only plugin
│       ├── spells/
│       ├── ancestries/
│       └── ...
│
├── Plugins/                        # Code plugins
│   ├── MyRulesetPlugin.dll
│   ├── CustomPressurePlugin.dll
│   └── ...
│
└── PLUGINS.md                      # This file
```

### YAML Subdirectories

Standard subdirectories (must match provider names):
- `spells/` — Spell definitions (SpellDefinitionProvider)
- `races/` or `ancestries/` — Race/ancestry definitions (RaceDefinitionProvider)
- `classes/` — Class definitions (ClassDefinitionProvider)
- `feats/` — Feat definitions (FeatDefinitionProvider)
- `pools/` — Resource pool templates (ResourcePoolProvider)
- `creatures/` — Creature/NPC definitions (CreatureDefinitionProvider)
- `conditions/` — Condition definitions (ConditionDefinitionProvider)
- `backgrounds/` — Background definitions (BackgroundDefinitionProvider)
- `progressions/` — Class progression tables (ProgressionDefinitionProvider)
- `classOptions/` — One option of a class's choice per file: a subclass, a patron, a fighting style (see "Adding a subclass")
- `powers/` — Named powers: gods, patrons, bloodlines, one per file (NamedPowerProvider; see "Named powers"). None ship
- `items/` — Item/equipment definitions (ItemDefinitionProvider) — not restricted to weapons/armor; see below.
- `creation/` — Character builder recipes, one file per kind: `pc`, `companion` (CreationRecipeProvider); see below.
- `statblocks/` — Stat block schemas the builder edits (CreationRecipeProvider).

Not found: No error. Providers return empty if subfolder missing.

Multiple roots contributing the **same subfolder for the same system** (host + plugin "items" packs)
are **merged** by the definition providers: each root with at least one `*.yaml` is loaded in order
(host/embedded first, then plugin roots **sorted by plugin id**; the server logs the order at startup).
Duplicate `name:` entries **last-wins** (plugin overlays host), and every such replacement is logged as a
warning naming both sources. Empty stub folders (e.g. only `.gitkeep`) are ignored so they cannot shadow
host data. Prefer distinct, specific names (`kara_tur_wakizashi`, not `wakizashi`) when you intend
coexistence, and `patches:` (below) when you mean to change an existing entry.

---

## YAML Schema

### Spell Definition

```yaml
name: Magic Missile
description: Creates magical projectiles
level: 1
classes:
  - Wizard
  - Sorcerer
casting_time: 1 action
range: 120 feet
components:
  - V
  - S
duration: Instantaneous
```

### Race Definition

```yaml
name: dwarf
system: dnd5e
description: Bold and hardy dwarves
size: Medium
baseSpeed: 25
abilityBonuses:
  Constitution: 2
extraLanguages: [Dwarvish]
traits:
  - Darkvision
  - Dwarven Resilience
# 5e mechanics (each optional):
darkvision: 60                                   # shown with the traits as "Darkvision 60 ft."
effects:                                         # the effect vocabulary, applied to the character's rolls
  - { kind: resistance, damageType: poison }
  - { kind: advantage, on: save, assert: [againstPoison], when: "the saving throw is against poison" }
proficiencies: { weapons: [battleaxe, warhammer], armor: [], tools: [] }
skills: [Perception]                             # fixed skill proficiencies
spells: { 1: [light], 3: [faerie_fire] }         # by character level: cantrips to the cantrips, others to known
abilityChoice: { count: 2, exclude: [Charisma] } # +1 to that many different abilities of the player's choice
skillChoices: 2                                  # skills of the player's choice
bonusFeats: 1                                    # a feat of the player's choice at level 1
```

The 5e `pc` recipe asks for a race's choices in steps shown only for a race that has them: `raceAbilities` (an
`allocate` step from the `raceAbilities` source, each pick +1 to that score), `raceSkills` (a `pickN` with
`target: skills`, so its picks are skill proficiencies like the class's) and `raceFeat` (a `pickOne` of feats with
`target: feats`). A subrace is a race with `inherits: [dwarf]` and `effects+:` / `traits+:` for what it adds.
A feat taken this way doesn't ask its own choices (a half-feat's ability); the DM records those.

### Class Definition

```yaml
name: fighter
system: dnd5e
hitDie: d10
casterType: None            # None | Full | Half | HalfRoundUp | Third | Warlock
savingThrows: [Strength, Constitution]
pools: [action_surge, second_wind]
aliases: [fighter]
skillChoices: { count: 2, from: [Acrobatics, Athletics, Perception] }
proficiencies: { armor: [light, medium, heavy, shields], weapons: [simple, martial] }    # 5e, starting class
multiclassProficiencies: { armor: [light, medium, shields], weapons: [simple, martial] } # 5e, a later class
```

**5e proficiencies.** `proficiencies:` (here, on a class feature, a race or a feat) takes
`armor` (`light`, `medium`, `heavy`, `shields`), `weapons` (`simple`, `martial`, or item names such as
`crossbow_hand`) and `tools` (names such as `thieves_tools`). The sheet's `armorProficiencies`,
`weaponProficiencies` and `toolProficiencies` hold the union of everything the character has: the starting class's
`proficiencies`, each later class's `multiclassProficiencies`, its class features', race's and feats', and the
background's `toolProficiencies`. Derivation only adds, so an entry the DM writes stays. Once `weaponProficiencies` is set, an
attack with a weapon it doesn't cover (by the item's `simple`/`martial` tag or its name) gets no proficiency bonus,
and the roll note says why. A choice ("three musical instruments") stays in the description.

**Note:** Fields not matching any property are ignored.

### Inheritance, list edits, patches and `requires:` (every template kind)

These work the same in every folder (races, backgrounds, classes, feats, spells, items, creatures,
conditions, pools, progressions).

**`inherits: [parent]`** copies the parent's fields into the child; fields the child sets win. A plain
list in the child (`traits: [...]`) **replaces** the parent's list, as it always has.

**List edits** change a list without copying it. Add `+` to the key to append, `-` to remove:

```yaml
name: moon_elf
inherits: [elf]
traits+: [Moonlit Step]      # elf's traits, plus this one
traits-: [Trance]            # ...minus this one
extraLanguages: [Sylvan]     # plain key: replaces elf's list
```

- Edits apply on top of whatever the list resolved to (the template's own list, or the parent's).
  They work without `inherits:` too.
- Strings match case-insensitively. For lists of objects, an entry with the same `name` (or `id`) as
  one already there replaces it in place; remove matches by `name`/`id`.
- Appending a value that is already there is a no-op.
- An edit on a key that isn't a list is ignored, with a warning in the server log.

**`patches: <name>`** changes an existing template instead of replacing it or adding a new name:

```yaml
# RulesetData/dnd5e/races/elf_patch.yaml in your plugin
patches: elf
description: Elves of the Silver Marches.
traits+: [Starlight Sense]
```

- The patch merges by inheritance rules, with the patch as the child: fields it sets win, list edits
  apply, plain lists replace. The target keeps its name, parents and `requires:`.
- Patches apply after every root has loaded (host first, then plugins by id), so a patch can target
  core content or another plugin's. Templates that inherit from the target see the patched version.
- A patch whose target doesn't exist is skipped, with a warning in the server log.

**`requires:`** hides any template unless a plugin is loaded (and, optionally, a mode is running):

```yaml
name: shadow_operative
system: dnd5e
skillProficiencies: [Deception, Stealth]
requires: { plugin: shadow-and-steel }          # optional: mode: heist
```

Hidden means left out of lists (the system handbook, spell and creature lookups, the character
builder), not deleted: characters that already have it keep it, and it comes back when the plugin
does. Children inherit `requires:` unless they set their own.

### Character Builder Recipes (`creation/`, `statblocks/`)

The client's character builder (the `character_builder` tool) walks a recipe: an ordered list of steps, one
file per creation kind. A system without a `creation/pc.yaml` gets a single identity step (name, concept, look),
so the builder works for every system. Recipes are templates like any other, so plugin roots, `patches:` and
list edits apply. Steps match by `key:` in `steps+:` / `steps-:`.

```yaml
# RulesetData/dnd5e/creation/pc.yaml (shipped; shortened)
name: pc
system: dnd5e
maxLevel: 20                                # the highest level the builder builds at (absent: no limit)
steps:
  - { key: race, kind: pickOne, source: races }
  - { key: class, kind: pickOne, source: classes }
  - key: skills
    kind: pickN
    source: classSkills
    countFrom: class.skillChoices.count     # a path: the chosen class template's field
    exclude: background.skillProficiencies  # options to leave out
  - { key: levels, kind: levelChoices }    # subclass, fighting style, ability score improvements...
  - { key: spells, kind: spells, source: spells, when: "class.casterType != None" }
  - { key: identity, kind: identity }
```

A plugin adding a step:

```yaml
# MyPlugin/RulesetData/dnd5e/creation/deity.yaml
patches: pc
steps+:
  - { key: deity, kind: pickOne, after: background, prompt: Deity, validators: [mypack.deityMatchesAlignment] }
```

- **kind:** `pickOne`, `pickN`, `abilityScores`, `allocate`, `spells`, `feats`, `identity`, `levelChoices`.
  The client draws one widget per kind, so no client work is needed for a new step.
- **source:** `races`, `classes`, `backgrounds`, `classSkills`, `skills`, `untrainedSkills` (skills the chosen
  class and background don't already train), `spells`, `feats`, `creatures`, `abilities`, `startingEquipment`.
  PF2e: `heritages` (the chosen ancestry's), `backgroundSkills` (a background's "Nature or Occultism"),
  `ancestryBoosts` (abilities the ancestry doesn't boost already), `backgroundBoosts`, `keyAbilities` (the class
  progression's `keyAbility`, plus any a class feature picked allows), and `ancestryFeats` / `classFeats` / `skillFeats` / `generalFeats` (that `category`,
  at or below the draft's level, for its class or ancestry). Templates gated by `requires:` are never offered.
- **Constraints are data, not code:** `count`, `countFrom` and `exclude` (paths), `countPlus` (paths added to the
  count, joined by `+`: `modifier.intelligence`, the ability modifier the draft's choices give so far, and
  `classFeatures.extraSkills`, the extra skills a picked class feature gives), `when` (`path`,
  `path == value` or `path != value`), `optional`. A `feats` step with no count takes it from the class's
  progression (every feat of its category up to the draft's level) and isn't shown when that is 0. Anything else is
  a named validator.
- **`levelChoices`** needs no source: it asks for every choice the chosen class's progression
  (`progressions/<class>.yaml`, the `choices:` under a level's features) makes at levels 1 to the draft's, one slot
  per choice with id `<level>.<choice key>`, or only those of its `choiceTypes:` (PF2e asks for the class features,
  `[Enum, FeatSelection]`, before the skills, and for `[SkillIncrease, AttributeBoosts]` after them). Its choice is an
  object of slot → option id, or a list: `{"2.subclass": "evocation", "2.invocation": ["agonizingBlast",
  "repellingBlast"]}`. A choice's `count:` is how many different options it takes ("choose two invocations"); one
  picked at an earlier level of the same key can't be picked again. A choice with no options of its own (a later
  invocation) offers that key's options from another level; inside a subclass ("two more maneuvers" at 7), the
  subclass's own features are searched first. Choice types:
  - `AsiOrFeat` takes one ability (+2), two (+1 each) or one feat instead (its `prerequisites` checked), and no score
    may pass 20.
  - `SkillIncrease` (PF2e) raises one skill a rank: trained or expert at any level, master from 7, legendary from 15.
  - `AttributeBoosts` (PF2e, `count: 4`) raises that many different attribute modifiers by 1; at +4 or more a boost
    is partial and two make +1.
  - `SkillProficiency` (5e, with `count:`) makes that many skills proficient, ones the character isn't proficient in
    yet (a Lore bard's three). Use the key `skills` so they're derived like the class's skill picks.
  - An option may say what it gives: `skills:` it trains (a racket's Thievery: not offered by the skills step, trained
    on the sheet), `extraSkills:` (more skill picks; the skills step's `countPlus` reads it), `keyAbility:` (offered
    by the key attribute step), `effects:` (what it does to rolls, the feat effect vocabulary: Archery's
    `{ kind: attackBonus, value: 2, weapon: [ranged] }`) and `features:` (a subclass's, by class level).

  **Subclass features.** An option's `features:` is a map of class level to features, written like the class's own.
  Once the option is picked, its features count from their level, and their `choices:` become slots (a hunter's prey
  at 3; a choice with no options borrows the key's, like the champion's second fighting style). A feature can carry:

  ```yaml
  - id: draconic
    label: Draconic Bloodline
    features:
      1:
        - name: Draconic Resilience
          description: Your hit point maximum rises by 1 per sorcerer level. Without armor, your AC is 13 + Dexterity.
          hpPerLevel: 1                                          # added to max HP, per character level
          unarmoredArmorClass: { base: 13, abilities: [Dexterity] }   # the best formula wins when no armor is worn
          # proficiencies: { armor: [heavy] }                     # joins the sheet's lists from this level
      6:
        - name: Elemental Affinity
          description: ...                                       # text only: the DM applies it
  ```

  A feature's `effects:` take the same vocabulary as an option's (fixed values only; a bonus that scales with a
  modifier stays text). Features are never stored on the character: get_entity (`classFeatures`) and the
  builder preview read them from the progression and the recorded picks, so a fix to the data reaches every character.
  The engine applies `effects`, `hpPerLevel`, `unarmoredArmorClass`, `proficiencies` and `spells`; everything else is the description,
  which the DM reads. The class's own features take the same fields (Unarmored Defense is
  `unarmoredArmorClass: { base: 10, abilities: [Dexterity, Constitution] }`).

  **Spells from a feature.** `spells:` (class level → spell ids) are given outright: always prepared, they join the
  sheet's prepared list once the class level is reached (a domain's, an oath's, a circle's) and cost no pick.
  `spellOptions:` adds spells to the class's list *to choose from* instead (a patron's expanded list): the builder's
  spells step offers them beside the class's own, and each costs a pick like any other.

  **A subclass that casts (5e).** An option's `spellcasting:` gives a class with no casting of its own spells from another
  class's list:

  ```yaml
  spellcasting:
    casterType: Third              # counts toward spell slots (multiclass rules included)
    ability: Intelligence          # spell save DC and attack bonus
    list: wizard                   # the list it learns from
    schools: [evocation]           # leveled spells from these schools only...
    anySchoolAt: [8]               # ...until the first of these class levels (then any school, marked in the options)
    cantripsKnown: { 3: 2, 10: 3 } # by class level, the highest reached wins
    spellsKnown: { 3: 3, 4: 4 }
  ```

  Once it's picked, the builder's spells step appears (its `when: "spellcasting != None"` reads the class's caster type,
  else the picked subclass's), offers the list's spells up to the slot level, and counts from these tables. Spells carry
  `school:`.

  **Effect vocabulary** (`effects:` on a feat, feature or option; fixed values only, the DM supplies facts, never numbers):

  | kind | needs | does |
  |---|---|---|
  | `attackBonus`, `damageBonus`, `skillBonus`, `saveBonus`, `armorClassBonus` | `value` | adds to that roll (`subject` narrows a skill or save) |
  | `advantage`, `disadvantage` | `on: attack\|check\|save` | rolls with it (`subject` narrows); any of each cancels (5e) |
  | `extraDamage` | `dice: 1d8`, optional `damageType` | rolled on a hit and again on a critical hit (5e attacks) |
  | `critRange` | `value: 19` | a natural d20 at or above it is a critical hit (the lowest wins) |
  | `resistance` | `damageType` | damage of that type to the character is halved (5e attacks) |
  | `damageReduction` | `value`, optional `damageType` | damage to the character drops by that much, before resistance (5e attacks) |
  | `initiativeBonus` | `value` | adds to the character's initiative |
  | `speedBonus` | `value` (feet) | adds to the character's speed |
  | `passiveBonus` | `value`, optional `subject: Investigation` | adds to passive Perception (or passive Investigation) |

  `damageType` on `resistance` and `damageReduction` may list several, comma-separated. Initiative, speed and passive
  bonuses have no action to carry a toggle or an assertion, so only unconditional ones count.
  Any effect can add `weapon: [ranged, melee, oneHanded, finesse, twoHanded, heavy]` (checked from the weapon's tags),
  `toggle: name` (the player opts in with an action parameter), or `assert: [flag]` with a `when:` sentence (the DM
  claims the condition holds). Anything beyond this stays description text, for the DM.

  **Adding a subclass (or patron, or fighting style) from a plugin.** One file in `classOptions/` joins a class's
  choice; nothing of the class is restated, and it shows in the builder with a "homebrew" tag:

  ```yaml
  # RulesetData/dnd5e/classOptions/ember_knight.yaml
  name: emberKnight           # the option id, recorded on the character
  class: fighter              # the class's progression name or alias
  choice: subclass            # the choice's key; "subclass" when left out
  label: Ember Knight
  description: A knight of the kindled blade.
  features:
    3: [{ name: Kindled Blade, description: ..., effects: [{ kind: damageBonus, value: 1, weapon: [melee] }] }]
  ```

  It takes the option fields above (`skills`, `keyAbility`, `effects`, `features`, `inherits:`, `requires:`). A file
  naming a class with no progression is skipped with a warning; it never replaces a shipped option of the same id.

  **Switching off shipped content.** In a `patches:` file, `hidden: true` removes any shipped template (a background, a
  race, a feat...) from every list and lookup, and `hideOptions+: [champion]` on a progression removes shipped
  options from its choices. A character that already recorded one keeps the record, but the option gives nothing and
  isn't offered again. (`requires:` instead keeps the content and gates it on a plugin or mode.)

  **Homebrew tag.** Anything that didn't come from the host's own data (a plugin's template or option) is offered with a
  "homebrew" tag in the builder, because the shipped rules are the free-licensed ones.

  **The DM's own homebrew.** A campaign can hold its own subclasses (class options), ancestries and named powers, written
  as the same YAML a plugin file holds and saved with the campaign by `world_build`:
  `homebrew: [{ kind: classOption | ancestry | power, system: dnd5e, yaml: "name: ember_knight\nclass: fighter\n..." }]`.
  The YAML is read as that template and refused with the reason when it has no name, a class option names no class the
  ruleset has, or a power has no valid type. Saving the same kind, system and name again replaces it; `isArchived: true`
  stops offering it (characters that have it keep it). The campaign's builder lists it, tagged homebrew, and no other
  campaign sees it. It is the last layer, so a homebrew name replaces a shipped or plugin template of the same name, and
  a homebrew ancestry or power can `inherits:` from one. The campaign's own feats and spells (`upsert_feat`, `upsert_spell`) are offered in its builder too, tagged homebrew: a feat among a 5e improvement's feats, a spell in a caster's list when its `classes` name the class and its level fits. A PF2e feat has no category, so it isn't offered in the ancestry, class, skill or general feat steps.

  **Named powers (gods, patrons, bloodlines).** Nothing named ships, because every name is somebody's setting. A
  plugin adds them in `powers/`, and a recipe step lists them:

  ```yaml
  # RulesetData/dnd5e/powers/lantern_keeper.yaml
  name: lantern_keeper
  type: deity              # deity | patron | lineage
  label: The Lantern Keeper
  classes: [cleric]        # who may take it; empty means any class
  offers: [light, trickery]   # the class choice's options it joins (a cleric's domains, a warlock's patron kind)
  # choice: subclass       # which choice `offers` narrows; "subclass" when left out
  # narrows: { font: [healingFont] }   # more choices it narrows, by choice key (a PF2e deity: domain, font)

  ```

  The shipped `pc` recipes already hold three optional steps (`deity`, `patron`, `lineage`; sources `deities`,
  `patrons`, `lineages`), each shown only when a power fits the picked class, so a plugin adds only the power file. A
  power with `classes:` is only listed for those classes. Once
  one is picked, the class's choice (`choice:`, the subclass by default) offers only the options in `offers:`; if none
  of them exist for the class, the choice stays whole, so a god that offers nothing the class has is harmless. The pick
  is recorded in the character's `levelUpChoices` under the step's key.

  The PF2e cleric has no deity in the shipped data, so it picks two `domain`s and a divine `font` itself (a house rule,
  RULES_GAPS.md). A PF2e deity plugin narrows those: `narrows: { domain: [healing, sun, truth], font: [healingFont] }`.

  Picks are recorded as `levelUpChoices` at their level, as `level_up` records them; improvements and boosts raise
  the scores or modifiers, a feat joins `feats`, and skill increases set the skill ranks. The step isn't shown for a
  class with no such choices up to the level, and a step with `choiceTypes` waits for the class. Spells gained by
  level stay the `spells` step's (its counts follow the level). Hit points above level 1 are the hit die's average:
  the builder never rolls.
- **Feat prerequisites:** a feat's `prerequisite:` text is shown on its option; its `prerequisites:` list is checked
  (`feat.prerequisites`, run by every `feats` step and on a feat taken instead of an improvement). Each entry is one of
  `{ skill: Athletics, rank: trained }`, `{ ability: Strength, min: 13 }` (a 5e score, a PF2e modifier),
  `{ feat: shield_block }`, `{ classFeature: leaf }` (an option id picked in a levelChoices step), or
  `{ anyOf: [...] }`. `generate_pf2e_feats.py` writes them from the text it can read; 5e's are written by hand. A
  validator can read the same facts at the path `sheet` (`sheet.skillRanks`, `sheet.abilities`, `sheet.feats`,
  `sheet.classFeatures`).
- **Where the choice goes:** the stats field named by `target:` or the key (`race`, `background`, `feats`); an
  `allocate` step's picks are attribute boosts, +1 each to the stats field `<ability>Mod`; otherwise a level-1
  `levelUpChoices` record per value (5e class skills and PF2e trained skills are derived from these).
- **PF2e validators** (`RulesetData/pf2e/creation/pc.yaml`): `pf2e.boosts` (an ancestry's free boosts go where it
  doesn't boost already; one of a background's two goes to an attribute it names), `pf2e.featEligibility` (level,
  class, ancestry, category), `pf2e.classSkills` (the
  fighter's "Acrobatics or Athletics"). They read the steps keyed `ancestry`, `background` and `class`.
- **Validators** are C# classes implementing `IRecipeValidator` (PluginSdk, `CampaignVault.Rulesets.Creation`),
  found by scanning plugin assemblies. A recipe naming a step kind, source or validator that doesn't exist stops
  the server at startup with every problem listed.
- **A whole system** that a recipe can't express can implement `ICharacterCreation` instead. Preview and
  commit still run through the host's bootstrap pipeline and `world_build`.

**Stat block schemas (`statblocks/`).** An `identity` step with `schema:` edits a stat block (a companion's), one
field per stats field of the same name, or a line of the character's notes (`creatureType`, `challengeRating`,
`attacks`, `traits`, `stance`), or one of the character's psychology lists (`descriptors` → traits, `drives` → wants,
`fears` → fears: Narrative's `nature` schema). The schema's `title:` captions the editor ("Stat block" without one),
and any field's `hint:` is shown in the empty field. Field `type`:

- `int` (`min`, `max`), `text` (a bare number is fine), with `required` and `group` (fields of a group are drawn
  together); `compact: true` draws a short text field as a narrow box beside the numbers (numbers always are).
- `modifiers`: an object of name to whole number (`{"Perception": 4}`, or the text "Perception +4, Stealth +6"),
  names from `source: skills | abilities`, each within `min`..`max`.
- `choice`: one name from `source:` (`creatureTypes`: the SRD 5.1 types for dnd5e), picked from a searchable list.
- `rows`: a list of objects, one per row, with `columns:` (`key`, `label`, `type: text | int | dice`, `min`,
  `max`, `required`), `item:` (what one row is called, for the add button) and `max:` (the most rows). Text form:
  rows split by `;`, columns by `,` in column order, a whole-number second column may share the name's part:
  `"Bite +3, 1d6+1 piercing, reach 5 ft.; Claw +3, 1d4+1 slashing"`.
- `list`: short entries, as a list or one text split at commas, semicolons and line breaks; `min`..`max` is how
  many (none is fine unless `required`).

The server checks every value against its field (`statBlock.fields`) and refuses to start on a field that lands
nowhere, an unknown type, or a `modifiers`/`choice` field whose source has no names in that system.

### Resource Pool Definition

```yaml
# RulesetData/dnd5e/pools/gambit_dice.yaml
name: gambit_dice
applicableSystems: [dnd5e]
applicableClasses: [duelist]      # its level is this class's level; without it, the character's
grantedOnly: true                 # only from a feat's extraPools or a feature's pools, never by class
recovery: ShortRest               # LongRest | ShortRest | PerTurn | EncounterEnd
recoveryByLevel: { "11": PerTurn } # optional: from that level on
levelToMaxMap: { "3": 4, "7": 5 } # the highest reached level wins
# maxFrom: { ability: Charisma, proficiencyBonus: false, levelMultiplier: 0, plus: 0, min: 1 }   # instead of the table
die: d8                           # shown on the pool; one use rolls it
dieByLevel: { "10": d10, "18": d12 }
```

A pool is sized by `levelToMaxMap` (or `defaultMax`) or by `maxFrom`: the ability's modifier + the proficiency bonus
(when `proficiencyBonus: true`) + `levelMultiplier` × the pool's level + `plus`, at least `min`. A max of 0 means the
character doesn't have the pool. Something grants a `grantedOnly` pool by name: a feat's `extraPools`, or a class
feature's `pools: [gambit_dice]` (a subclass's, once picked and reached). `spell_slots_*` pools follow the caster level.

### Background Definition

```yaml
name: acolyte
system: dnd5e
skillProficiencies: [Insight, Religion]
toolProficiencies: []
languages: [Two of your choice]
feature: Shelter of the Faithful
equipment:                                # what a character built with it starts holding
  - { item: dagger }                      # an item template: its fields are copied in
  - { name: Prayer book }                 # a plain item
  - { name: Stick of incense, quantity: 5 }
gold: 15                                  # starts the gold pool
```

The character builder gives a new character its background's equipment on commit (not again when the same draft id is
committed), and the gold fills its `gold` pool, which otherwise starts empty.

### Feat Definition (5e)

```yaml
# RulesetData/dnd5e/feats/steadfast_training.yaml
name: steadfast_training
system: dnd5e
prerequisite: Strength 13 or higher          # shown; the checkable part is prerequisites:
prerequisites: [{ ability: Strength, min: 13 }]
mechanicalSummary: +1 Strength or Constitution and its saving throw...
abilityIncrease: { choose: [Strength, Constitution], amount: 1 }   # one entry: fixed; none: any ability
savingThrowOfIncrease: true                  # proficient in the raised ability's save
savingThrows: [Wisdom]                       # ...or fixed ones
proficiencies: { armor: [medium], weapons: [longbow], tools: [smiths_tools] }
skillChoices: 1                              # skills of the player's choice
spells: [light]                              # given outright
spellChoices: [{ level: 0, count: 2, lists: [wizard, sorcerer] }]   # chosen from those lists
hpPerLevel: 1                                # from the level it's taken at, for every level
effects: [{ kind: saveBonus, value: 1 }]     # roll effects (see the effect vocabulary)
extraPools: [lucky_points]                   # grantedOnly pools it gives
```

A feat taken at an ability score improvement asks its own choices in the same place, as slots after the improvement
(`4.steadfast_training.ability`, `.skills`, `.spells`): the builder lists them once the feat is picked, and a
`level_up` / `character_level_up` sends them with the feat's pick (`options` with the picks so far lists them). The
picks are recorded as `levelUpChoices` (`steadfast_training.ability`, `skills`, `steadfast_training.spells`), and the
sheet derives the rest: the raised score, the save, the proficiencies, the hit points, and the spells (cantrips to
the cantrips, others to the known list).

### Item Definition

Not restricted to weapons/armor — `category` plus the open `properties` bag cover outfits, tools,
consumables, and artifacts uniformly. This is a *template* ("what a Wakizashi is"); a campaign's
`world_build` tool creates an `Item` *instance* ("Bob's Wakizashi") by passing `definitionName`,
which copies the template's `category`/`tags`/`properties`/equip fields into the new item at
creation time — any of those fields also set explicitly on the same `world_build` entry override
the template's values. It is a one-time copy, not a live reference: the item keeps working even if
the defining pack is later removed.

```yaml
name: kara_tur_wakizashi
system: dnd5e
category: Weapon   # built-in: Weapon | Armor | Clothing | Container | Consumable | Tool | Material | Valuable | Document | Key | Other — or your own (see below)
tags: [martial, melee, exotic, kara-tur]
description: A curved short blade favored by Kara-Tur duelists.
properties:
  damage: 1d6
  damageType: slashing
  weight: 2
  costGp: 20
equipZones: [MainHand]        # built-in: Head | Face | Neck | Torso | Back | Waist | Hands | Wrists | Legs | Feet | MainHand | OffHand | Ring | Accessory — or your own
equipLayer: Held               # built-in: Base | Armor | Outer | Held
```

A "custom firearms pack" or "mountaineering equipment pack" is just more files under
`RulesetData/{system}/items/` — no code required (see `climbers_kit.yaml` in the directory
structure above for a non-weapon example).

**`category` and equip `zone`/`layer` values are open strings, not a fixed enum.** A pack can
introduce its own alongside the built-in set — e.g. a jewelry/piercings pack using
`category: Jewelry` and zones like `septum`, `anklet`, or `bellyButton`. Two different zone names
never conflict with each other regardless of whether either is built-in, so a new zone "just works"
for equip-slot purposes with no code change: it defaults to capacity 1 (one item at a time) and
contributes no AC/warmth/movement unless the item's own `properties` carry `acBonus`/`warmth`/
`speedModifier` directly. A handful of zone-specific behaviors (Ring/Accessory holding multiple
items at once, Torso/Armor governing dex-cap, OffHand+Held being detected as a shield) only apply to
the built-in names.

---

## Campaign Option Defaults

A plugin can declare custom **house-rule config keys** — plain string/number/bool/enum values, not
YAML content — via `campaignOptions` in `plugin.json`. This lets a plugin ship a sensible default for
a setting it cares about (e.g. an encumbrance variant, a firearms-availability flag) without every DM
having to know the key exists or set it by hand.

```json
{
  "id": "com.example.kara-tur-weapons",
  "displayName": "Kara-Tur Weapons Pack",
  "version": "1.0.0",
  "campaignOptions": [
    {
      "key": "exoticWeaponProficiencyCost",
      "type": "int",
      "default": "2",
      "description": "Feats required to gain proficiency with an exotic Kara-Tur weapon."
    },
    {
      "key": "firearmsEra",
      "type": "enum",
      "values": ["none", "early", "modern"],
      "default": "early",
      "description": "Which firearms tier is available in this campaign."
    }
  ]
}
```

Fields: `key` (required, the `SystemOptions` key), `type` (`string` | `enum` | `bool` | `int`, informational — all values are stored as strings), `values` (optional, allowed values for `enum`), `default` (optional; omit to declare a key with no default, e.g. for `get_config`/help-surface documentation only), `description` (optional, shown in config help surfaces).

**When defaults get applied:** the host merges every loaded plugin's declared defaults into a
campaign's `SystemOptions` when the campaign is created and whenever `set_active_system` runs — and
**only for keys not already present**. A DM's own `campaign_update` change or a prior `set_active_system`
call always wins; a plugin default can fill a gap but never overwrite a value someone already set.
Runtime values live in `CampaignConfig.SystemOptions`, mirrored to `Campaign.SystemOptions` for the
handlers that read house rules mid-turn — both copies are written from the same merged dictionary, so
they never drift apart.

---

## System Registration & Validation

### Valid System Configurations

| Has Module | Has YAML | Status | Behavior |
|-----------|----------|--------|----------|
| ✓ | ✓ | **Valid** | Full-featured ruleset |
| ✓ | ✗ | **Valid** | Code plugin, no predefined data |
| ✗ | ✓ | **Valid** | Data-only plugin, uses base rules |
| ✗ | ✗ | **Invalid** | Error at campaign load |

### Discovery Process

**At startup, for each system:**
1. Check if `IRulesetModule` is registered (code plugin loaded?)
2. Check if YAML data exists (spells/races/classes/etc?)
3. If neither: log error, system cannot be used
4. If module missing: log warning, will degrade to base SystemExtension
5. If YAML missing: log info, campaigns can still use system

**At campaign load:**
- Verify campaign's system ID is valid
- Load appropriate module or degrade gracefully
- Load YAML data (if available)

---

## Capabilities & Limitations

### What Plugins Can Do

✅ Add YAML-based data (spells, races, classes, etc.)
✅ Implement custom `IRulesetModule` for new rule systems
✅ Add custom `ISimulationRule` (simulation event handlers)
✅ Add custom `IPressureContributor` (world pressure sources)
✅ Add custom `IGuidanceContributor` (proactive guidance hints)
✅ Add `IPluginContextContributor` (one-line facts pushed on the take_turn beat that needs them, once per session; `IContextTurn` exposes `Config`, `Time` and `LoadCharacterAsync` for characters outside the party)
✅ Add `IPluginCampaignOptionsUpgrader` (migrate your own campaign option keys at host startup; see [Migrating Your Own Campaign Options](#migrating-your-own-campaign-options))
✅ Add `IPluginTraitsUpgrader` (migrate your own `SystemExtension.Traits` keys — rename, reshape, or retire — when you change your own trait schema; see [Migrating Your Own Traits Schema](#migrating-your-own-traits-schema) below)
✅ Add custom `IWorldChangeHandler` (react to player actions)
✅ Add custom `IWorldChangeObserver` (post-commit, non-failing, cross-cutting hooks — e.g. a trauma-triggered "inner voice" reactor that watches every mutation without owning any of them)
✅ Read and change dirt on characters, items and locations, with plugin-invented kinds (`SoilChange`, `core.soiled.v1`; see [Dirt](#dirt-reading-and-extending-it))
✅ Add custom `IMcpServerTool` (new MCP tools)
✅ Define a whole new turn-based interaction mode via `IInteractionMode`/`IModeStateMachine` (crafting, astral combat, ...) — see [Type 3: Interaction Mode Plugin](#type-3-interaction-mode-plugin)

### Dirt: Reading and Extending It

Every character, item and location carries `Dirt` (`IHasDirt`), a short list of `DirtMark`s. Kinds are open strings, so a
plugin can invent one with no host change and no new `$type`:

```csharp
public sealed class GhostSlime : IDomainEventHandler
{
    public IReadOnlyCollection<string> Topics { get; } = [CoreEvents.CharacterDowned];

    public Task<IReadOnlyList<WorldChange>> HandleAsync(DomainEvent e, IChangeContext ctx, CancellationToken ct = default)
    {
        if (!e.TryGet<string>(CoreEvents.Fields.CharacterId, out var id) || !ctx.Characters.TryGetValue(id, out var body))
            return Task.FromResult<IReadOnlyList<WorldChange>>([]);
        if (body.HasDirt("myplugin.ectoplasm")) // read: any host's Dirt is on the preloaded entity
            return Task.FromResult<IReadOnlyList<WorldChange>>([]);
        return Task.FromResult<IReadOnlyList<WorldChange>>([new SoilChange { TargetId = id, Kind = "myplugin.ectoplasm", Spot = "hands" }]);
    }
}
```

Subscribe to `core.soiled.v1` to react to dirt (scent tracking, infection, cleaning rituals). The engine never soils anything
itself; the DM commits `soil` alongside the fight or journey. See the SDK README (0.11.0) for the fields and caps.

### What Plugins Cannot Do (Yet)

❌ Hotload without restarting MCP
❌ Inject bootstrap steps into existing pipelines (hardcoded in resolvers)
❌ Contribute new action types to the core `RulesetActionType` enum — it remains closed by design. Define a new `WorldChange` subtype instead (fully open, no enum change needed) — this is exactly how interaction modes add new verbs; see `INTERACTION_MODES_PLAN.md`.
❌ Override core simulation rules (conflict resolution undefined)
❌ Run untrusted — every code plugin is full-trust, in-process (see [Trust Model](#trust-model))

### Known Constraints

- **System IDs are global.** If two plugins use the same system ID, last-registered wins (undefined order).
- **YAML only.** Plugin data must be YAML. JSON/XML not supported (use YAML anchors for reuse).
- **Lazy discovery.** Systems discovered at startup. Adding/removing plugins requires restart.
- **Autofac registration.** Plugin types registered via Autofac convention matching. If you need a non-standard interface, use [Autofac attributes](https://autofac.readthedocs.io/en/latest/).

---

## Troubleshooting

### "System 'X' is not supported or not registered"

**Cause:** Campaign uses system ID `"X"`, but no IRulesetModule and no YAML data found.

**Fix:**
1. Check `/Plugins/` for the DLL (if code plugin)
2. Check `/RulesetData/X/` for YAML files (if data-only)
3. Verify system ID matches exactly (case-insensitive, but must exist)
4. Check startup logs for load errors

### DLL loads but module is not registered

**Cause:** DLL is valid C# but doesn't implement `IRulesetModule`.

**Fix:**
1. Verify class implements `IRulesetModule` interface
2. Verify class is public (not internal)
3. Verify class is not abstract
4. Check MCP startup logs for convention registration details

### "Failed to load plugin assembly"

**Cause:** DLL loading failed (missing dependencies, architecture mismatch, etc.).

**Fix:**
1. Check MCP logs for the specific exception
2. Ensure DLL matches MCP architecture (net10.0)
3. Verify all dependencies (NuGet packages) are present
4. Test DLL in isolation: `dotnet /path/to/plugin.dll` (should fail gracefully)

### YAML data not loading

**Cause:** Subdirectory name mismatch or YAML parse error.

**Fix:**
1. Verify subdirectory name matches provider (e.g., `races/`, not `race/`)
2. Check YAML syntax (use YAML validator if unsure)
3. Verify file has `.yaml` extension (`.yml` not supported)
4. Check MCP logs for YAML parse errors

### Campaign created but system is marked as "data-only"

**Cause:** No IRulesetModule found. System is using base calculation rules.

**Action:** This is expected behavior. If you need custom rules, provide a DLL with `IRulesetModule`.

---

## Best Practices

### Data-Only Plugins

1. **Name by system, not by mod:** Use system ID (`swade`, `pathfinder2e`) not author name.
2. **YAML over DLL:** If pure data, don't write code. Keep plugins lightweight.
3. **Inherit when possible:** Use `>` in YAML to extend base definitions (reduces duplication).
4. **Version your data:** Include version in filename or top-level YAML property.
5. **Docs matter:** Include a README explaining what the plugin provides.

### Code Plugins

1. **One module per DLL:** One `IRulesetModule` per plugin (simplifies discovery).
2. **Log generously:** Use `ILogger` for startup warnings and calculation traces.
3. **Pair with YAML:** Even if minimal, include a `RulesetData/mysystem/` directory with sample data.
4. **Test without MCP:** Unit test your module in isolation (mock dependencies).
5. **Document the module:** Include XML docs on public methods.

### General

1. **Follow the slot:** If your system uses a standard mechanic (attack rolls, saves, HP), implement it consistently.
2. **Case-insensitive IDs:** System IDs are case-insensitive (`SWADE` = `swade`). Pick one and stick to it.
3. **Graceful errors:** If a feature isn't implemented, return empty collection, not null or throw.
4. **No breaking changes:** Once shipped, don't change YAML field names (data migration burden).

---

## Distributing Your Plugin

### Package Format

For distribution, use either:

**Option 1: Loose files** (simplest)
```
my-swade-plugin.zip
├── RulesetData/
│   └── swade/
│       ├── spells/
│       └── races/
└── README.md
```

Users extract to their CampaignVault directory. Restart MCP.

**Option 2: Code plugin package** (with DLL)
```
my-swade-plugin.zip
├── Plugins/
│   └── MySWADEPlugin.dll
├── RulesetData/
│   └── swade/
│       ├── spells/
│       └── races/
└── README.md
```

Same extraction. Users get both code and data.

**Option 3: Installer script** (advanced)
Provide shell/PowerShell script that copies files to correct locations and verifies installation.

### Checklist

- ✅ README.md with installation instructions
- ✅ System ID clearly documented
- ✅ Changelog for version updates
- ✅ Example campaign (if applicable)
- ✅ License (MIT/Apache/CC0 recommended for community mods)

---

## Examples

### Example 1: Pathfinder 2e Data-Only Expansion

**Plugin:** `pathfinder2e-expanded-spells`

```
RulesetData/
└── pf2e/
    ├── spells/
    │   ├── expanded_transmutation.yaml
    │   ├── expanded_evocation.yaml
    │   └── ...
    └── feats/
        └── expanded_feats.yaml
```

Users: Drop in `/RulesetData/`, restart, create campaign with system `"pf2e"`.

### Example 2: Custom Ruleset Code Plugin

**Plugin:** `faterpg-plugin`

```
FateRpgPlugin.csproj
└── FateRulesetModule.cs
```

```csharp
public class FateRulesetModule : IRulesetModule
{
    public string System => "fate";
    
    public IActionResolution Actions => this;
    public ICombatRuleset Combat => this;
    
    // Implement FATE-specific mechanics:
    // - Aspects and compels
    // - Stress tracks
    // - Fate points
    // - Recovery
}
```

Users: Copy built DLL to `/Plugins/`, optionally add `/RulesetData/fate/` with YAML data, restart.

---

## API Reference

### IRulesetModule Interface

All code plugins must implement this. Key members:

```csharp
public interface IRulesetModule
{
    string System { get; }  // System ID (e.g., "dnd5e")
    IActionResolution Actions { get; }
    ICombatRuleset Combat { get; }
}

public interface IActionResolution
{
    Task<ActionResolutionResult> ResolveActionAsync(
        Character actor,
        WorldChange action,
        SimulationContext context);
}

public interface ICombatRuleset
{
    int CalculateAC(Character character);
    int CalculateInitiative(Character character);
    Task<SaveResult> ResolveSaveAsync(
        Character character,
        SaveType type,
        int difficulty);
}
```

See `Rulesets/IRulesetModule.cs` for full interface definitions.

### Discovery Interfaces

Optional: Implement to provide additional functionality.

```csharp
public interface ISimulationRule
{
    int Order { get; }
    Task ApplyAsync(SimulationContext context, ...);
}

public interface IPressureContributor
{
    int Order { get; }
    Task<IEnumerable<PressureItem>> EvaluateAsync(PressureContext context);
}

public interface IGuidanceContributor
{
    int Order { get; }
    Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext context);
}
```

### Interaction Mode Interfaces

```csharp
public interface IInteractionMode
{
    string ModeId { get; }
    string DisplayName { get; }
    IReadOnlyList<string> CompatibleSystems { get; } // [] = system-agnostic
    IModeStateMachine StateMachine { get; }
}

public interface IModeStateMachine
{
    ModeEncounter CreateEncounter(string locationId, IReadOnlyList<string> participantIds);
    IReadOnlyDictionary<string, int> GetTurnActionBudget(Character participant);
    bool TryConsumeActionSlot(ModeParticipantState state, WorldChange action, out string? errorReason);
    bool AdvanceTurn(ModeEncounter encounter);
    bool IsComplete(ModeEncounter encounter, out string? outcomeNarrative);
}

public interface IWorldChangeObserver
{
    bool IsInterestedIn(WorldChange committed, ChangeContext context);
    Task OnCommittedAsync(WorldChange committed, ChangeContext context, CancellationToken ct = default);
}
```

See `Rulesets/Modes/IInteractionMode.cs` and `Data/ChangeHandlers/IWorldChangeObserver.cs` for full definitions, and `INTERACTION_MODES_PLAN.md` for the design rationale (why `IWorldChangeObserver` is a distinct, non-failing tier from `IWorldChangeHandler`).

---

## Migrating Your Own Traits Schema

`SystemExtension.Traits` (`Dictionary<string,string>`) is a closed dictionary shared by every plugin — you cannot add your own `[JsonDerivedType]` to `SystemExtension`, only write keys into this shared bag (see [Type 3](#type-3-interaction-mode-plugin)'s "Key convention"). That's a problem the moment you need to rename a key, change what a value means, or retire a field on data that already exists in someone's campaign: there's no code-level equivalent of a database migration for your own namespaced keys, short of asking every operator to hand-edit JSON.

`IPluginTraitsUpgrader` closes that gap:

```csharp
public interface IPluginTraitsUpgrader
{
    string PluginId { get; }
    bool TryUpgrade(IDictionary<string, string> traits);
}
```

Implement it, and it's discovered by the same Autofac convention scan as every other plugin type — no registration needed. The host calls `TryUpgrade` once per character, every time that character is loaded (alongside the existing `SystemStats` type-coercion pass), and passes the character's live `Traits` dictionary. By convention (not enforced by the host — every code plugin is full-trust, see [Trust Model](#trust-model)) only touch keys under your own `"<pluginId|modeId>."` prefix. Return `true` only when you actually changed something, so the host knows to persist the character; an already-current document should return `false` to avoid a write on every load. Make `TryUpgrade` idempotent — it may run again on a document your own prior run already upgraded.

```csharp
public class CraftingTraitsUpgrader : IPluginTraitsUpgrader
{
    public string PluginId => "crafting";

    public bool TryUpgrade(IDictionary<string, string> traits)
    {
        // v1 -> v2: "crafting.tool_quality" renamed to "crafting.toolQuality".
        if (traits.Remove("crafting.tool_quality", out var value))
        {
            traits["crafting.toolQuality"] = value;
            return true;
        }
        return false;
    }
}
```

**Exceptions are caught and logged, never fatal.** A throwing upgrader doesn't block character load or any other registered upgrader — but a partial mutation it made before throwing is not rolled back, so keep each rename/reshape as a single, cheap dictionary operation rather than a multi-step edit.

**Orphaned prefixes ("missing master").** If a plugin is later uninstalled, its trait keys don't get deleted — there's no upgrader instance to call, since none is loaded, so those keys simply sit inert in the data (same as a Skyrim plugin's records when its master `.esp` isn't loaded: present, unresolved, untouched). At startup the host scans every character's `Traits` keys and logs one warning per `"<prefix>."` that no currently loaded plugin's id or `modeIds` claims — a single summary line per orphaned prefix, not per character, so a retired plugin with hundreds of affected characters doesn't flood the log. Reinstalling the plugin resumes normal upgrades on the next load; the data was never lost.

## Migrating Your Own Campaign Options

The same idea for `SystemOptions` (house rules and player settings you declared under `campaignOptions`):

```csharp
public interface IPluginCampaignOptionsUpgrader
{
    string PluginId { get; }
    bool TryUpgrade(IDictionary<string, string> systemOptions);
}
```

The host runs every registered upgrader once per startup, as a data migration, over each campaign's `Campaign.SystemOptions` and `CampaignConfig.SystemOptions`, and saves only when one returns `true`. Rules match the traits upgrader: touch only your own keys, be idempotent, return `false` on current data. Never overwrite a key the player already set — a retired option should only fill in its replacement when that replacement is absent. A throwing upgrader is logged and skipped.

---

## FAQ

**Q: Can I have multiple systems in one plugin?**
A: No. One `IRulesetModule` per DLL. Use separate DLLs for separate systems.

**Q: Do I need to restart MCP when I add YAML files?**
A: Yes. Providers cache at startup. Restart to discover new YAML.

**Q: Can plugins talk to each other?**
A: Yes, via DI. Both get registered in the same Autofac container. Wire dependencies normally.

**Q: What if two plugins provide the same system ID?**
A: Last-registered wins (registration order undefined). Avoid collisions—use unique system IDs.

**Q: Can I modify built-in systems (dnd5e, pf2e)?**
A: Only via data (add YAML to `/RulesetData/dnd5e/`, never modify core). Code changes require a fork.

**Q: Is there a plugin marketplace?**
A: Not yet. Community plugins can be shared as GitHub releases or hosted on personal sites.

**Q: Can plugins break campaigns?**
A: Yes, if the plugin disappears. Always provide a fallback or backup campaigns before removing plugins.

**Q: Are plugins sandboxed? Can I safely run a plugin I didn't write?**
A: No. Every code plugin (both `IRulesetModule` and interaction-mode plugins) runs full-trust, in-process — the same access as first-party CampaignVault code. Only load DLLs from authors you trust. See [Trust Model](#trust-model). Real sandboxing (Jint/WebAssembly) is planned but not built — see `PLUGIN_SYSTEM_PLAN.md` Track C.

**Q: Is a data-only (YAML) plugin also full-trust?**
A: No code-execution risk applies to YAML — it's parsed data, not a loaded assembly. The trust-model caveat is specific to code plugins.

**Q: What's the difference between an `IRulesetModule` and an interaction mode?**
A: `IRulesetModule` is a whole stat system (dnd5e, pf2e, homebrew) — one per campaign, selected via `ActiveSystem`. An interaction mode is a scene-scoped *activity* (crafting, astral combat) that layers on top of whatever ruleset is active, independently enabled per campaign via `CampaignConfig.EnabledModeIds`. See [Type 3: Interaction Mode Plugin](#type-3-interaction-mode-plugin).

---

## Future Work

Deferred capabilities (not yet implemented):

- [ ] Hotloading plugins without restart
- [ ] Plugin dependency management
- [ ] Plugin marketplace / registry (see `PLUGIN_SYSTEM_PLAN.md` Track D — blocked on Track C sandboxing)
- [ ] Bootstrap step injection (currently hardcoded)
- [x] ~~Action type extension (currently closed enum)~~ — resolved indirectly: `RulesetActionType` itself stays closed, but interaction-mode plugins (and any plugin) can define new verbs via open `WorldChange` subtypes + `IWorldChangeHandler`, with no core enum change needed. See `INTERACTION_MODES_PLAN.md`.
- [ ] Async plugin discovery (currently happens at startup)
- [ ] Plugin versioning / compatibility checks
- [ ] Real plugin sandboxing tiers (Jint scripted hooks, WebAssembly/Extism) — see `PLUGIN_SYSTEM_PLAN.md` Track C. Until this ships, all code plugins are full-trust (see [Trust Model](#trust-model)).

---

## Support & Contribution

**Issues:** Report plugin problems in [GitHub Issues](https://github.com/yourrepo/issues).

**Contributing plugins:** We'd love to feature community plugins! Open an issue or PR to add yours to the registry.

**Questions?** Check PLUGINS.md, read the code in `Rulesets/` and `AutofacModules/`, or ask in [Discussions](https://github.com/yourrepo/discussions).

---

**Last updated:** engine 0.15.0 — effect kinds `initiativeBonus`, `speedBonus`, `passiveBonus` and `damageReduction`; engine 0.14.0 — `ProficiencyGrants` (5e armor, weapon and tool proficiencies on classes and features; weapon proficiency gates the attack bonus), pool `maxFrom`, `die`/`dieByLevel`, `recoveryByLevel`, `grantedOnly` and feature-granted `pools`, subclass `spellcasting` and spell `school`; engine 0.13.0 — character creation contracts (`ICharacterCreation`, `IRecipeValidator`, `CharacterDraft`, `CreationOption.Values`), `FeatEffect`/`RulesetTemplate.Requires`, `SpellRepertoire`, explicit death; engine 0.11.0 — `IRollModifierProvider`/`RollQuery`/`RollModifier` (what buffs, conditions, willpower and plugin rules do to rolls; `IChangeContext.ResolveRollModifiers` for plugin rolls), `SystemExtension.WillpowerDrained` and willpower that matters (charm, fear, compulsion and mental saves; rest restores what was drained), `SpellDefinition.tags`, effective speed (`Speed` modifiers now slow travel and show on cards, plus a context line for chases); engine 0.10.0 — `IWorldTimeObserver`/`TimeAdvance` (plugin time hook), `apply_effect` (clamped, expiring, non-stacking buffs/debuffs; `persistent` for curses and auras), the consequence beat (`consequences`, `consequenceCooldownHours`, `consequenceMaxPerDay` options), `tether` (subject → anchor with break DC), ammunition (`ammoType`, `ammoPerShot`, `fireModes`, `mode`), and weapon bursts that fan out round-robin over targets; engine 0.9.0 — `ActorActionAttribute` and `ActionBlock` (the host refuses a marked verb, and core attack/spell/item-use actions, from an actor who is incapacitated, stunned, paralyzed, petrified, unconscious or carries a `BlocksAllActions` status; whoever applies such a status must give it an exit); engine 0.8.0 — public `EngineOnlyAttribute`, `plugin.json` `systems`, `IModeStateMachine.TryAddParticipant`/`TryRemoveParticipant` and `core.mode_joined.v1`/`core.mode_left.v1` (host handling of `mode_transition` join/leave and `systems` lands after the SDK publish); engine 0.7.0 — `IPluginCampaignOptionsUpgrader`, `IContextTurn.Config`/`Time`/`LoadCharacterAsync`, `playerOnlyModeIds`, owner-managed pools, host-enforced mode action slots
**Plugin API version:** 1.4 (adds the 0.8.0 contracts above; 1.3 added `IPluginCampaignOptionsUpgrader` and the 0.7.0 hooks above; 1.2 added `IPluginTraitsUpgrader`; 1.1 added `IInteractionMode`/`IModeStateMachine`/`IWorldChangeObserver`; `IRulesetModule` surface unchanged from 1.0)
