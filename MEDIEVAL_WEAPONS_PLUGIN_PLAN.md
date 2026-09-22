# Medieval Weapons Plugin Implementation Plan

## Overview
Create a historically-grounded medieval weapons plugin for CampaignVault with realistic special effects/attributes that reflect each weapon's historical role and design. Ensure all plugins (CraftingMode, MedievalWeapons, etc.) are built and deployed to `bin/Release/net10.0/Plugins/` folder.

## Phases

### Phase 1: Deploy Wiring ✓
**Goal:** Modify CampaignVault.csproj to build all plugins and copy them to bin folder.

**Tasks:**
- [ ] Modify `src/CampaignVault/CampaignVault.csproj`:
  - Add ProjectReference to each plugin with `ReferenceOutputAssembly="false"` 
  - Add AfterBuild target to copy plugin DLLs to `$(OutDir)Plugins\<Name>\`
  - Copy plugin.json, RulesetData/**, and skills/** to plugin folder
  - Never delete Plugins/ folder (LewdHandbook is maintained externally)

**Verification:** After build, `bin/Release/net10.0/Plugins/` contains:
- `CraftingMode/CraftingMode.dll` + `plugin.json`
- `MedievalWeapons/MedievalWeapons.dll` + `plugin.json` + `RulesetData/dnd5e/items/*.yaml`

---

### Phase 2: Create Medieval Weapons Plugin ✓
**Goal:** Set up plugin project structure with minimal C# code.

**Tasks:**
- [ ] Create `plugins/MedievalWeapons/` directory
- [ ] Create `plugin.json` with metadata:
  - id: `com.campaignvault.medieval-weapons`
  - version: `1.0.0`
  - minEngineVersion: `0.2.0`
  - rulesetDataRoots: `["./RulesetData"]`
- [ ] Create `MedievalWeapons.csproj` (minimal, only .dll needed)
- [ ] Create single minimal C# file (empty class or interface impl) to generate DLL
- [ ] Ensure project builds to `bin/Debug/net10.0/MedievalWeapons.dll`

**Verification:** `plugins/MedievalWeapons/bin/Debug/net10.0/MedievalWeapons.dll` exists

---

### Phase 3: Medieval Weapons Content ✓
**Goal:** Define historically accurate weapons with realistic special effects.

**Tasks:**
- [ ] Create `plugins/MedievalWeapons/RulesetData/dnd5e/items/` directory
- [ ] Write YAML definitions for weapons using prefix `medieval_` (e.g., `medieval_longsword.yaml`)

**Weapon Categories (organized by historical period & role):**

#### Early Medieval (8th-11th century)
- Spatha (Viking/Frankish longsword)
- Francisca (throwing axe)
- Seax (single-edge blade)

#### High Medieval (11th-13th century)  
- Arming Sword (cruciform hand-and-a-half)
- Bastard Sword (transitional, two-handed capable)
- Greatsword (two-handed, anti-plate)
- Pike/Lance (polearm, set-vs-charge mechanics)
- Kite Shield
- Mace (crushing vs mail)
- Bill Hook (peasant/Hussite, anti-armor)

#### Late Medieval/Renaissance (14th-16th century)
- Longsword (High Medieval continuation)
- Poleaxe (multipurpose: spike/axe/hammer)
- Partisan (spear + side blades)
- Warhammer (anti-plate, percussive)
- Halbert/Halberd (axe + pike)

**Special Attributes by Historical Role:**

| Weapon | Role | Special Mechanic | D&D Translation |
|--------|------|------------------|-----------------|
| Pike/Lance | Set vs cavalry charge | Set-defense bonus vs mounted | `bonus` only vs mounted foes |
| Poleaxe | Anti-plate versatile | Effective vs heavy armor | 1d8/1d10, bonus vs AC 16+ |
| Greatsword | Armor penetrator | Ignores dex-based AC | 2d6, extra damage vs plate |
| Warhammer | Blunt vs mail | Effective armor piercer | 1d8 + bonus vs armor |
| Halberd | Reach + hooking | Reach + dismount attempt | Reach, can disarm |
| Spear (one-hand) | Versatile thruster | 1H or 2H, set-defense | 1d6/1d8, defensive stance bonus |
| Mace | Armor bypass | No edge bonus but solid impact | 1d6, bonus vs AC 15+ |
| Bill Hook | Peasant polearm | Reach + anti-mounted | Reach, 1d8, vs mounted +2 |

**Constraints (Historical Realism):**
- **Weights**: arming sword 2-3 lb, longsword 3-4 lb, two-hander 5-7 lb, pike 8-12 lb
- **Period**: Avoid post-16th century firearms/rapiers in "medieval" pack
- **Armor interaction**: Weapons designed specifically against plate armor (poleaxes, warhammers) get +bonus vs AC 16+
- **Reach**: Pikes/polearms get reach + vulnerability at close range
- **Setting**: Assume typical D&D 5e campaign with plate/mail/leather

---

### Phase 4: Verification ✓
**Goal:** Verify build, deployment, and content loading.

**Tasks:**
- [ ] Run: `dotnet build src/CampaignVault/CampaignVault.csproj -c Release`
- [ ] Check `bin/Release/net10.0/Plugins/`:
  - CraftingMode folder contains `.dll` + `plugin.json` (no .dll duplication in root)
  - MedievalWeapons folder contains `.dll` + `plugin.json` + `RulesetData/dnd5e/items/*.yaml`
- [ ] Run full test suite with `verify` skill:
  - Report any pre-existing failures
  - Ensure no new test failures
- [ ] Spot-check: Create small test that ItemDefinitionProvider loads medieval weapons
  - Assert weapon count ≥ expected minimum
  - Verify sample weapon has `damage`, `damageType` properties

---

## Implementation Notes

### YAML Schema for Weapons
```yaml
name: medieval_longsword
system: dnd5e
category: Weapon
tags: [martial, melee, versatile, medieval-period-name]
description: Historical description of role and design
properties:
  damage: 1d8              # One-hand damage
  damageVersatile: 1d10    # Two-hand damage (if applicable)
  damageType: slashing|piercing|bludgeoning
  weight: 3                # in pounds
  costGp: 15               # D&D gold cost
  historicalPeriod: "High Medieval"  # Contextual info (not engine-read)
  historicalRole: "Anti-infantry infantry sword"  # Contextual
  # Special mechanics as rules-text (DM-interpreted, not engine-read):
  specialMechanics: "Effective vs plate armor foes"
equipZones: [MainHand, OffHand]
equipLayer: Held
twoHanded: false  # true if requires both hands; weapons with damageVersatile stay false
```

### Build Process
1. `CampaignVault.csproj` references `plugins/CraftingMode/*.csproj` and `plugins/MedievalWeapons/*.csproj`
2. AfterBuild target invokes `msbuild plugins/*/plugin.csproj /t:GetTargetPath` to get each DLL path
3. Copies only the main `.dll` (never `CampaignVault.PluginSdk.dll`) to `bin/Release/net10.0/Plugins/<PluginName>/`
4. Copies `plugin.json`, `RulesetData/**/*.yaml`, and `skills/**` from plugin source to plugin folder in bin

### Known Constraints
- `EnumeratePluginDlls` only loads `.dll` files (no data-only support today)
- Duplicate item names use last-wins merge (prefix names with `medieval_` to avoid SRD collision)
- Test suite won't auto-exercise YAML (no UI), so manual spot-check via tool call needed
- Plugins cannot hotload; MCP restart required after DLL/manifest changes

---

## Expected Outcome
1. ✅ Build succeeds with all plugins copied to bin folder
2. ✅ `get_rules_reference(kind:'items')` includes all `medieval_*` weapons
3. ✅ Full test suite passes (or pre-existing failures documented)
4. ✅ Combat engine can reference medieval weapons in attacks
5. ✅ New weapons follow historical design principles with realistic special effects
