# Extra Content

Original homebrew for CampaignVault, beyond the SRD. Every spell is written from scratch: no Player's Handbook
text, names or setting terms. Data only (YAML), so there is no code to trust.

## Install

Drop this folder into `Plugins/`, restart the host, and check `lookup(kind: 'spells')` for the `ec_` entries.

## Spells (dnd5e)

| Spell | Level | What it does |
| --- | --- | --- |
| `ec_entropy` | 2 | Ages one object by tiers: worn, damaged, ruined, spent. |
| `ec_flame_rune` | 3 | Invisible rune on any surface, 5d8 fire when something arrives or touches it. |
| `ec_frost_rune` | 3 | Same, 4d6 cold in 15 ft, slows and roots. |
| `ec_thunder_rune` | 2 | Same, 3d8 thunder, loud, wakes sleepers. |

## Known engine gaps

- Rune traps are `Hazard`s, which attach to a whole location, exit or item, not a spot on the floor. They fire on
  arrival (`enter`) or taking (`take`). No caster id, no ally exemption (prose only), no proximity trigger.
- Hazard effects are prose: the DM rolls the save with a `ruleset_action`.
- Items have no integrity stat, so Entropy's tiers live in item details and the DM applies penalties by hand.

When core gains item integrity and richer hazard triggers, these spells get real fields instead of DM guidance.
