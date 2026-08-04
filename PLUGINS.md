# CampaignVault Plugin Architecture

CampaignVault supports two plugin types: **data-only plugins** (YAML) and **code plugins** (DLL + C#). Mix and match to extend the platform without modifying core code.

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
    └── feat s/
        └── fireball_mastery.yaml
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

Not found: No error. Providers return empty if subfolder missing.

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
✅ Add custom `IWorldChangeHandler` (react to player actions)
✅ Add custom `IMcpServerTool` (new MCP tools)

### What Plugins Cannot Do (Yet)

❌ Hotload without restarting MCP
❌ Inject bootstrap steps into existing pipelines (hardcoded in resolvers)
❌ Contribute new action types to the core enum (baked into `RulesetActionType`)
❌ Override core simulation rules (conflict resolution undefined)

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

---

## Future Work

Deferred capabilities (not yet implemented):

- [ ] Hotloading plugins without restart
- [ ] Plugin dependency management
- [ ] Plugin marketplace / registry
- [ ] Bootstrap step injection (currently hardcoded)
- [ ] Action type extension (currently closed enum)
- [ ] Async plugin discovery (currently happens at startup)
- [ ] Plugin versioning / compatibility checks

---

## Support & Contribution

**Issues:** Report plugin problems in [GitHub Issues](https://github.com/yourrepo/issues).

**Contributing plugins:** We'd love to feature community plugins! Open an issue or PR to add yours to the registry.

**Questions?** Check PLUGINS.md, read the code in `Rulesets/` and `AutofacModules/`, or ask in [Discussions](https://github.com/yourrepo/discussions).

---

**Last updated:** Phase 4.7 completion
**Plugin API version:** 1.0 (stable)
