# Trail & Hearth Pack

Wilderness and survival gear for **dnd5e** and **pf2e**. All entries use the
`th_` prefix so they never shadow core (regenerated) content or other packs
(`medieval_`, `ss_`, `ng_`) — plugin templates merge last-wins on `name:`
*per system*.

## Contents (25 blueprints per system)

| Group | Items |
|-------|-------|
| Shelter & camp | trail tent, waxed bedroll, camp cookpot, pack frame |
| Fire & light | fire steel, storm tinder (10 pinches), pitch torch bundle (6) |
| Navigation & signals | trail compass, trail chalk (10 sticks), signal whistle, ward chimes |
| Hunting & fishing | fishing tackle, snare wire (3 loops), skinning knife, game calls |
| Foraging & water | forager's pouch, herbal satchel, filter cloth, trail rations (5 days) |
| Cold & country | waxed cloak, snowshoes, camp hatchet |
| Alchemical & herbal | bitterleaf poultice, glowmoss vial, bearbane salts |

The skinning knife is a real `Weapon` (1d4 slashing, light/finesse); everything
else is `Tool` or `Consumable`. Consumable charges live on the item instance
(`item_use`), so blueprints carry a `uses:` hint plus prose, not engine state.

## Mechanics honesty

- `specialMechanic` is **prose-only**: the engine copies no survival rule for
  these, so shelter quality, cold, foraging yields, alarm chimes, and repellent
  salts resolve narratively / by GM arbitration. Descriptions say so.
- `grantsAdvantageOn` follows the core `climbers_kit` convention (fire-starting,
  navigation, fishing, foraging, herbalism, woodcutting, skinning checks) — read
  by the GM/agent when adjudicating, not computed by the engine.
- No SRD/ORC stat blocks or flavor text were copied; all numbers and
  descriptions are original homebrew, and nothing duplicates a proprietary
  non-SRD/non-ORC entry.

## Installation

1. Extract this plugin folder to your CampaignVault installation:
   ```
   CampaignVault/Plugins/TrailAndHearth/
   ```
2. Restart the CampaignVault MCP host.
3. New gear appears in `lookup(kind:'items')` with prefix `th_`.

## Version

**Trail & Hearth Pack v1.0.0** — Engine version 0.11.0+
