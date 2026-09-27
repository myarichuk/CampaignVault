# Shadow & Steel Pack

Sidearms, supplemental armor, and rogue-kit gear for **dnd5e**, **pf2e**,
and **swade**. All entries use the `ss_` prefix so they never shadow core
(regenerated) or MedievalWeapons (`medieval_`) definitions — plugin
templates merge last-wins on `name:` *per system*.

## Contents (61 blueprints)

| System | Weapons (5) | Armor (5) | Gear |
|--------|-------------|-----------|------|
| dnd5e | estoc, francisca, seax, katar, sap | buckler, pavise, brigandine, gambeson, great helm | caltrops, smoke bomb, ball bearings, thieves' tools, barding + 5 trick arrows (water, fire, gas, noise, rope) |
| pf2e | estoc, katar, garrote, boar spear, bec de corbin | brigandine, lamellar, pavise, great helm, barding | caltrops, smoke bomb, thieves' tools + 5 trick arrows (water, fire, gas, noise, rope) |
| swade | dagger, short sword, hand axe, short bow, garrote | leather, chain, plate, heater shield, open helm | caltrops, smoke bomb + 3 trick arrows (water, fire, noise) |

Trick arrows are Thief-inspired single-use `Consumable` ammo: they carry an
`ammoFor` hint (bow required, enforced narratively) plus a `damage` seed for
GM resolution. The water arrow exists to douse torches and wash away
bloodstains, not to deal damage.

## Mechanics honesty

- `specialMechanic` is **prose-only**: the engine copies weapon `Properties`
  verbatim into attack parameters but nothing computes armor interaction, so
  conditional bonuses (e.g. estoc vs heavy armor) resolve narratively / by GM.
  Descriptions say so where relevant.
- Consumables (`smoke_bomb`) seed as `Category: Consumable`; charges/doses
  are instance-level (`MaxCharges`/`ChargeUnit` via `item_use`), so the
  blueprint carries a `uses:` hint plus prose, not engine state.
- Mount barding seeds as `Tool` with a `mountAcBonus` hint — it is fitted to
  a mount and has no effect worn by a humanoid.
- SWADE has no armor-class resolver in the engine yet; its `acBonus` values
  are seed data for future/GM use.

## Licensing

All stats and descriptions are original homebrew. No SRD/ORC stat blocks or
flavor text were copied; generic historical names (estoc, katar, brigandine,
…) carry newly authored numbers, and nothing duplicates a proprietary
non-SRD/non-ORC entry.
