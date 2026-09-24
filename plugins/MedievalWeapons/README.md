# Medieval Weapons Pack

A historically-grounded, lore-accurate collection of medieval weapons for **CampaignVault D&D 5e**.

## Overview

This plugin provides meticulously researched medieval weapon definitions spanning from the Early Medieval period (8th century) through the Late Medieval/Renaissance period (16th century). Each weapon is designed with:

- **Historical accuracy**: Realistic weights, materials, and periods
- **Lore fidelity**: Descriptions and roles match historical usage and D&D narrative conventions
- **Mechanical balance**: Special effects reflect each weapon's real-world design purpose within D&D 5e rules

## Weapons Included

### Early & High Medieval (8th–13th century)

| Weapon | Role | Mechanics |
|--------|------|-----------|
| **Medieval Arming Sword** | General-purpose sword, balanced for cuts and thrusts | 1d8/1d10 (versatile), slashing |
| **Medieval Spear** | Common soldier's polearm, effective with shield or two-handed | 1d6/1d8 (versatile), piercing, +2 AC vs mounted when braced |
| **Medieval Mace** | Armor-bypassing blunt weapon, popular with clergy | 1d6, bludgeoning, effective vs mail joints |
| **Medieval Hand Axe** | Utility and combat tool, throwable | 1d6, slashing, throwable (Close range) |

### Late Medieval & Anti-Armor (14th–16th century)

| Weapon | Role | Mechanics |
|--------|------|-----------|
| **Medieval Bastard Sword** | Transitional hand-and-a-half sword | 1d8/1d10 (versatile), slashing |
| **Medieval Greatsword** | Armor penetrator, two-handed anti-plate weapon | 2d6, slashing, +2 damage vs plate armor (AC 16+) |
| **Medieval Poleaxe** | Multipurpose anti-armor polearm | 1d8/1d10 (versatile), slashing, +1 damage vs plate, exploitable joints |
| **Medieval Warhammer** | Armor-piercing percussive weapon | 1d8, bludgeoning, +1 vs mail, +2 vs plate |

### Polearms & Anti-Cavalry

| Weapon | Role | Mechanics |
|--------|------|-----------|
| **Medieval Pike** | Formation anti-cavalry polearm | 1d6, piercing, reach (10 ft), +2 AC vs mounted when braced, disadvantage at close range |
| **Medieval Halberd** | Anti-cavalry with hooking capability | 1d8, slashing, reach (10 ft), disarm vs mounted foes |
| **Medieval Bill** | Peasant anti-cavalry hooked polearm | 1d8, slashing, reach (10 ft), +2 grapple, advantage to disarm mounted |

### Mounted Combat

| Weapon | Role | Mechanics |
|--------|------|-----------|
| **Medieval Lance** | Cavalry shock weapon, mounted-only | 1d12, piercing, advantage on mounted charge, disadvantage on foot, shatters after use |

## Design Philosophy

### Historical Authenticity

- **Weights**: All weapons reflect actual historical pieces (arming sword 2–3 lb, longsword 3–4 lb, two-hander 5–7 lb)
- **Periods**: Weapons are labeled with their historical era; anachronistic combinations are avoided
- **Materials**: Steel, wood, and iron reflect what was actually used
- **Armor interaction**: Weapons designed to defeat plate armor (poleaxes, warhammers, greatswords) have mechanical advantages vs AC 16+ (representing full plate)

### Game Balance

- **Damage ranges align with D&D 5e**: No weapon is overpowered
- **Special mechanics are situational**: Bonuses apply only when conditions match (e.g., "vs mounted," "vs plate armor")
- **Trade-offs are real**: Reach weapons excel at distance but suffer at close range; greatswords are powerful but require both hands

### Lore Cohesion

- **Descriptions are D&D-appropriate**: Medieval terminology is translated to fantasy tavern-speak
- **Roles and uses match their historical purpose**: Spears are common soldiers' weapons; poleaxes are knight's anti-armor specialists
- **No "magic" properties**: Special effects are mechanical, not mystical (e.g., +bonus vs AC, reach, disarm advantage)

## Installation

1. Extract this plugin folder to your CampaignVault installation:
   ```
   CampaignVault/Plugins/MedievalWeapons/
   ```

2. Restart the CampaignVault MCP host.

3. Create a campaign with system `dnd5e` (or update an existing one).

4. New weapons appear in `lookup(kind:'items')` with prefix `medieval_*`.

## Usage in Campaigns

Reference weapons by their exact name in `world_build` tool:

```json
{
  "action": "create",
  "entityType": "Item",
  "definitionName": "medieval_greatsword",
  "characterId": "chars/my_fighter"
}
```

All weapons automatically inherit D&D 5e properties (damage types, weight for encumbrance, cost for commerce) and can be equipped into weapon slots.

## Notes for Game Masters

### Flavor

Each weapon includes a `specialMechanic` description (e.g., "Against enemies in plate armor, effective against joints"). These are **not** automatically enforced by the engine; they're guidance for your rulings:

- A player wielding a poleaxe against a plate-armored foe might argue for advantage on attacks or bonus damage
- A pike used defensively could grant the braced AC bonus
- A lance used on foot incurs disadvantage

### Modification

YAML files are human-editable. To create house-rule variants:

1. Copy a weapon YAML file (e.g., `medieval_longsword.yaml`)
2. Rename it (e.g., `house_rule_longsword.yaml`)
3. Edit properties as desired
4. Restart MCP to reload

## Version

**Medieval Weapons Pack v1.0.0** — Engine version 0.2.0+

## License

CC0 1.0 Universal (Public Domain) — Use freely in your own campaigns and forks.

## References

- Oakeshott, Ewart (1960). *The Archaeology of Weapons*
- Gotham, Daniel (2016). *The Archaeology of Medieval Combat*
- Blair, Claude (1958). *European Armour*
- Turnbull, Stephen (ed., 2013). *Fighting Techniques of the Medieval World*
