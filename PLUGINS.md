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

`plugin.json` fields: `id`, `displayName`, `version`, `minEngineVersion` (host skips on mismatch — no boot crash), optional `modeIds`, `rulesetDataRoots` (default `./RulesetData`), `skillsPath` (default `./skills`, log-only), optional `campaignOptions` (declares house-rule config keys and their defaults — see [Campaign Option Defaults](#campaign-option-defaults) below).

**Custom `$type`:** annotate with `[PluginWorldChange("my_verb")]`. Handler **dispatch** already works via `WorldChangeDispatcher.FindHandler`'s `ShouldHandle` fallback. JSON wire deserialization and `lookup kind=commit_schema` require the type registry (seeded from core `[JsonDerivedType]` + plugin attributes at load). Discriminator collisions fail fast at registration.

**ALC / type identity:** the host resolves `CampaignVault.PluginSdk` from `AssemblyLoadContext.Default` for plugin ALCs. Dropping a second Sdk.dll in the plugin folder is skipped with a warning.

**Skills:** sidecar-only. Operators/clients install skill markdown; the MCP host never auto-discovers or serves them.

**Adult / optional content:** belongs in separate repos referencing PluginSdk; the main repo ships only neutral samples (e.g. `plugins/CraftingMode`).

**Compatibility:** host engine version is `0.2.0` (`EngineVersion.Current`). Set `minEngineVersion` accordingly.

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
- Mode-specific character stats live in the existing `SystemExtension.Attributes`/`ResourcePools` dictionaries — no core model changes needed to add e.g. `Attributes["AstralAttunement"]`.
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

**Step 2:** Add CampaignVault NuGet package (future: publish CampaignVault.Core package for plugins)

For now, reference core types. Example structure:

```csharp
using CampaignVault.Rulesets;
using CampaignVault.Models;

namespace MyRulesetPlugin
{
    public class MyCustomRuleset : IRulesetModule
    {
        public string System => "my_system";
        
        public IActionResolution Actions => this;
        public ICombatRuleset Combat => this;
        
        // Implement required interface methods
        // - ResolveAction(...)
        // - ResolveSave(...)
        // - GetActionResult(...)
        // etc.
    }
}
```

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
- `items/` — Item/equipment definitions (ItemDefinitionProvider) — not restricted to weapons/armor; see below.

Not found: No error. Providers return empty if subfolder missing.

Multiple roots contributing the **same subfolder for the same system** (host + plugin "items" packs)
are **merged** by the definition providers: each root with at least one `*.yaml` is loaded in order
(host/embedded first, then plugin roots). Duplicate `name:` entries **last-wins** (plugin overlays
host). Empty stub folders (e.g. only `.gitkeep`) are ignored so they cannot shadow host data.
Prefer distinct, specific names (`kara_tur_wakizashi`, not `wakizashi`) when you intend coexistence
rather than override.

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
name: Dwarf
description: Bold and hardy dwarves
size: Medium
speed: 25
ability_score_increases:
  constitution: 2
  wisdom: 1
languages:
  - Common
  - Dwarvish
traits:
  - Darkvision 60 feet
  - Dwarven Resilience
```

### Class Definition

```yaml
name: Fighter
description: Martial warrior
hit_die: d10
proficiencies:
  armor:
    - All
  weapons:
    - Simple
    - Martial
  saves:
    - Strength
    - Constitution
```

**Note:** Schema is system-agnostic. Define what makes sense for your system. Fields not matching any property are ignored.

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
✅ Add `IPluginContextContributor` (one-line facts pushed on the take_turn beat that needs them, once per session)
✅ Add `IPluginTraitsUpgrader` (migrate your own `SystemExtension.Traits` keys — rename, reshape, or retire — when you change your own trait schema; see [Migrating Your Own Traits Schema](#migrating-your-own-traits-schema) below)
✅ Add custom `IWorldChangeHandler` (react to player actions)
✅ Add custom `IWorldChangeObserver` (post-commit, non-failing, cross-cutting hooks — e.g. a trauma-triggered "inner voice" reactor that watches every mutation without owning any of them)
✅ Add custom `IMcpServerTool` (new MCP tools)
✅ Define a whole new turn-based interaction mode via `IInteractionMode`/`IModeStateMachine` (crafting, astral combat, ...) — see [Type 3: Interaction Mode Plugin](#type-3-interaction-mode-plugin)

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

**Last updated:** `IPluginTraitsUpgrader` (SystemExtension.Traits schema migration)
**Plugin API version:** 1.2 (adds `IPluginTraitsUpgrader`; 1.1 added `IInteractionMode`/`IModeStateMachine`/`IWorldChangeObserver`; `IRulesetModule` surface unchanged from 1.0)
