#!/usr/bin/env python3
"""Generate dnd5e weapon/armor ItemDefinition YAML from SRD 5.1 (www.dnd5eapi.co).

Only two categories are pulled: equipment-categories/weapon and
equipment-categories/armor. Both category
listings also contain magic-item entries (Vorpal Sword, Armor +1, Dragon Scale Mail,
...) whose `url` points at /api/2014/magic-items/... instead of /api/2014/equipment/...
those carry no clean SRD stat block (bonuses vary per item) and are filtered out by
url prefix, not by guessing at names.

Writes into the same src/CampaignVault/RulesetData/dnd5e/items/ directory as the two
hand-authored files (longsword.yaml, climbers_kit.yaml). Unlike generate_spells.py
(which wipes and fully regenerates a 100%-generated directory), this script only
overwrites the specific slugs it generates and never deletes anything — items/ is a
mixed hand+generated directory (climbers_kit.yaml is a Tool, never touched here since
Tool isn't a weapon/armor category). longsword.yaml *is* regenerated: its mechanical
stats (damage/damageType/weight/costGp) come out byte-identical to the hand-authored
version, plus equip metadata (equipZones/equipLayer/twoHanded) the hand-authored file
never had, which means today's core longsword cannot actually be equipped via
item_equip. Regenerating it is a fix, not a regression; the flavor description line is
replaced with the same generated factual description every other item gets, for
consistency across the 50 generated files.

Requires network access to www.dnd5eapi.co.
"""

from __future__ import annotations

import json
import re
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ITEMS_DIR = ROOT / "src/CampaignVault/RulesetData/dnd5e/items"
OVERLAY_PATH = ROOT / "scripts" / "item_overlay.yaml"

# SRD ammunition bundles the overlay's ammoType values point at: api index -> (ammoType, weapons it fits).
# Rounds live in the item's charges (`uses`/`chargeUnit`), which is what AmmoResolver spends.
AMMO = {
    "arrow": ("arrow", ["shortbow", "longbow"], "arrows"),
    "crossbow-bolt": ("bolt", ["crossbow_light", "crossbow_heavy", "crossbow_hand"], "bolts"),
    "sling-bullet": ("bullet", ["sling"], "bullets"),
    "blowgun-needle": ("needle", ["blowgun"], "needles"),
}

HEADER = "# Source: SRD 5.1 by Wizards of the Coast LLC, CC BY 4.0\n"

DND5E_API = "https://www.dnd5eapi.co"

COST_TO_GP = {"pp": 10.0, "gp": 1.0, "ep": 0.5, "sp": 0.1, "cp": 0.01}

# SpatialDistanceBand.cs constants, in ascending distance order.
BAND_TOUCH, BAND_CLOSE, BAND_NEAR, BAND_FAR, BAND_DISTANT = (
    "Touch", "Close", "Near", "Far", "Distant",
)


def kebab_to_snake(name: str) -> str:
    return name.replace("-", "_")


def yaml_quote(value: str) -> str:
    if re.search(r'[:#\[\]{}&*!|>\'"%@`\n\r]', value) or value.strip() != value:
        return json.dumps(value)
    return value


def fetch_json(url: str, retries: int = 3) -> dict:
    for attempt in range(retries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "CampaignVault/1.0"})
            with urllib.request.urlopen(req, timeout=30) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except Exception:
            if attempt == retries - 1:
                raise
            time.sleep(0.5 * (attempt + 1))
    raise RuntimeError(f"Failed to fetch {url}")


def cost_to_gp(cost: dict | None) -> float | int | None:
    if not cost or cost.get("quantity") is None:
        return None
    rate = COST_TO_GP.get(cost.get("unit"), 1.0)
    value = round(cost["quantity"] * rate, 4)
    return int(value) if value == int(value) else value


def weapon_range_band(detail: dict) -> str | None:
    """Maps dnd5eapi's range/throw_range/properties onto SpatialDistanceBand.

    Reach weapons (10 ft) -> Near, matching the existing convention already used by
    the MedievalWeapons plugin (medieval_bill/halberd/pike all use `range: Near #
    Reach weapon`). Thrown and ranged (ammunition) weapons bucket by their `normal`
    range in feet; the break points below are this script's own choice (there's no
    source-provided mapping to feet -> band), picked so real SRD weapons land where
    the plugin convention would expect (thrown daggers/handaxes at throw-normal 20ft
    -> Close, exactly like medieval_hand_axe.yaml's `range: Close # Throwable`).
    Standard melee weapons (5 ft, no reach/thrown) get no range key at all, same as
    the existing hand-authored longsword.yaml -- RangeValidationHelper is permissive
    when a weapon has no `range` parameter, so omitting it is the correct "untracked,
    don't gate" default, not an oversight.
    """
    properties = {p["index"] for p in detail.get("properties", [])}

    if "reach" in properties:
        return BAND_NEAR

    if "thrown" in properties:
        throw = detail.get("throw_range") or detail.get("range") or {}
        normal = throw.get("normal")
    elif detail.get("weapon_range") == "Ranged":
        normal = (detail.get("range") or {}).get("normal")
    else:
        return None

    if normal is None:
        return None
    if normal <= 5:
        return BAND_TOUCH
    if normal <= 30:
        return BAND_CLOSE
    if normal <= 80:
        return BAND_NEAR
    if normal <= 150:
        return BAND_FAR
    return BAND_DISTANT


def weapon_description(detail: dict, damage_dice: str | None, damage_type: str | None,
                        two_handed_dice: str | None) -> str:
    parts = [f"{detail['weapon_category']} {detail['weapon_range'].lower()} weapon."]
    if damage_dice:
        dmg = f"Deals {damage_dice}"
        if damage_type:
            dmg += f" {damage_type}"
        dmg += " damage"
        if two_handed_dice:
            dmg += f" ({two_handed_dice} two-handed)"
        parts.append(dmg + ".")
    else:
        parts.append("Deals no direct damage.")
    rng = detail.get("range") or {}
    if detail.get("weapon_range") == "Ranged" and rng.get("normal"):
        long_range = f"/{rng['long']}" if rng.get("long") else ""
        parts.append(f"Range {rng['normal']}{long_range} ft.")
    return " ".join(parts)


def load_overlay() -> dict[str, dict[str, str]]:
    """Parse scripts/item_overlay.yaml: top-level `slug:` then indented `key: value` lines, `#` comments."""
    overlay: dict[str, dict[str, str]] = {}
    if not OVERLAY_PATH.exists():
        return overlay
    slug = None
    for raw in OVERLAY_PATH.read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0].rstrip()
        if not line.strip():
            continue
        if not line.startswith(" "):
            slug = line.rstrip(":").strip()
            overlay[slug] = {}
        elif slug is not None and ":" in line:
            key, value = line.split(":", 1)
            overlay[slug][key.strip()] = value.strip()
        else:
            raise SystemExit(f"item_overlay.yaml: cannot parse line {raw!r}")
    return overlay


def load_ammo(index: str) -> tuple[str, dict]:
    detail = fetch_json(f"{DND5E_API}/api/2014/equipment/{index}")
    ammo_type, weapons, unit = AMMO[index]
    rounds = detail.get("quantity") or 20
    props: dict[str, object] = {}
    if detail.get("weight") is not None:
        props["weight"] = detail["weight"]
    cost_gp = cost_to_gp(detail.get("cost"))
    if cost_gp is not None:
        props["costGp"] = cost_gp
    props.update({"uses": rounds, "chargeUnit": unit, "ammoType": ammo_type, "ammoFor": ", ".join(weapons)})
    slug = f"{kebab_to_snake(index)}s_{rounds}"
    return slug, {
        "name": slug,
        "system": "dnd5e",
        "category": "Consumable",
        "tags": ["ammunition", "adventuring-gear"],
        "description": f"{detail['name']} bundle of {rounds}, for {', '.join(w.replace('_', ' ') for w in weapons)}. "
                       f"One round per shot; rounds are tracked in the item's charges.",
        "properties": props,
    }


def load_weapon(entry: dict) -> tuple[str, dict]:
    detail = fetch_json(DND5E_API + entry["url"])
    slug = kebab_to_snake(detail["index"])
    properties = {p["index"] for p in detail.get("properties", [])}

    damage = detail.get("damage") or {}
    damage_dice = damage.get("damage_dice")
    damage_type = (damage.get("damage_type") or {}).get("index")

    two_handed = detail.get("two_handed_damage") or {}
    two_handed_dice = two_handed.get("damage_dice") if "versatile" in properties else None

    props: dict[str, object] = {}
    if damage_dice:
        props["damage"] = damage_dice
    if two_handed_dice:
        props["damageVersatile"] = two_handed_dice
    if damage_type:
        props["damageType"] = damage_type
    if detail.get("weight") is not None:
        props["weight"] = detail["weight"]
    cost_gp = cost_to_gp(detail.get("cost"))
    if cost_gp is not None:
        props["costGp"] = cost_gp
    range_band = weapon_range_band(detail)
    if range_band:
        props["range"] = range_band

    props.update(load_overlay().get(slug, {}))

    tags = sorted({detail["weapon_category"].lower(), detail["weapon_range"].lower(), *properties})

    return slug, {
        "name": slug,
        "system": "dnd5e",
        "category": "Weapon",
        "tags": tags,
        "description": weapon_description(detail, damage_dice, damage_type, two_handed_dice),
        "properties": props,
        "equipZones": ["MainHand"],
        "equipLayer": "Held",
        "twoHanded": "two-handed" in properties,
    }


def armor_description(detail: dict, ac_bonus: int) -> str:
    category = detail["armor_category"]
    if category == "Shield":
        return f"Shield. +{ac_bonus} AC when wielded in the off hand."

    dex_note = {
        "Light": "full Dexterity bonus",
        "Medium": "Dexterity bonus (max 2)",
        "Heavy": "no Dexterity bonus",
    }.get(category, "Dexterity bonus")
    parts = [f"{category} armor. Base AC {detail['armor_class']['base']} + {dex_note}."]
    if detail.get("str_minimum"):
        parts.append(f"Requires {detail['str_minimum']} Strength.")
    if detail.get("stealth_disadvantage"):
        parts.append("Imposes disadvantage on Stealth checks.")
    return " ".join(parts)


def load_armor(entry: dict) -> tuple[str, dict]:
    detail = fetch_json(DND5E_API + entry["url"])
    slug = kebab_to_snake(detail["index"])
    category = detail["armor_category"]
    ac = detail.get("armor_class") or {}
    base = ac.get("base", 0)

    # Shield's armor_class.base (2) is already a flat AC bonus, not a "10 + dex" total
    # target AC like body armor -- confirmed live by generating and reading actual
    # shield output. Applying the body-armor "base - 10" formula to a shield
    # would produce acBonus = -8, which is wrong. Body armor's base *is* the "10 +
    # dex + acBonus" target AC at 0 effective dex, so acBonus = base - 10 there.
    ac_bonus = base if category == "Shield" else base - 10

    props: dict[str, object] = {"acBonus": ac_bonus}
    if category != "Shield":
        # ArmorParameterResolver's armorType fallback (medium=cap 2, heavy=cap 0,
        # else uncapped) already matches dnd5e's own dex_bonus/max_bonus rules exactly
        # (confirmed live: every Medium armor has max_bonus 2, every Light/Heavy has
        # none). Writing a redundant numeric `dexCap` here would just duplicate logic
        # the resolver already has; `dexCap` is reserved for pf2e's own numeric
        # convention (Step 5), per ArmorParameterResolver's own doc comment.
        props["armorType"] = category
    if detail.get("weight") is not None:
        props["weight"] = detail["weight"]
    cost_gp = cost_to_gp(detail.get("cost"))
    if cost_gp is not None:
        props["costGp"] = cost_gp
    if category != "Shield" and detail.get("str_minimum"):
        props["strMinimum"] = detail["str_minimum"]
    if detail.get("stealth_disadvantage"):
        props["stealthDisadvantage"] = True

    if category == "Shield":
        equip_zones, equip_layer = ["OffHand"], "Held"
    else:
        equip_zones, equip_layer = ["Torso"], "Armor"

    return slug, {
        "name": slug,
        "system": "dnd5e",
        "category": "Armor",
        "tags": [category.lower()],
        "description": armor_description(detail, ac_bonus),
        "properties": props,
        "equipZones": equip_zones,
        "equipLayer": equip_layer,
    }


def write_item(path: Path, body: dict) -> None:
    lines = [HEADER.rstrip()]
    lines.append(f"name: {body['name']}")
    lines.append(f"system: {body['system']}")
    lines.append(f"category: {body['category']}")
    tags = ", ".join(body["tags"])
    lines.append(f"tags: [{tags}]")
    lines.append(f"description: {yaml_quote(body['description'])}")
    lines.append("properties:")
    for key, value in body["properties"].items():
        if isinstance(value, str):
            lines.append(f"  {key}: {yaml_quote(value)}")
        elif isinstance(value, bool):
            lines.append(f"  {key}: {'true' if value else 'false'}")
        else:
            lines.append(f"  {key}: {value}")
    if "equipZones" in body:
        zones = ", ".join(body["equipZones"])
        lines.append(f"equipZones: [{zones}]")
        lines.append(f"equipLayer: {body['equipLayer']}")
    if "twoHanded" in body:
        lines.append(f"twoHanded: {'true' if body['twoHanded'] else 'false'}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def fetch_mundane_entries(category_index: str) -> list[dict]:
    index = fetch_json(f"{DND5E_API}/api/2014/equipment-categories/{category_index}")
    # Both category listings interleave magic items (url -> /api/2014/magic-items/...);
    # only plain-equipment entries have a fetchable SRD stat block via /api/2014/equipment/.
    return [e for e in index["equipment"] if e["url"].startswith("/api/2014/equipment/")]


def generate() -> int:
    ITEMS_DIR.mkdir(parents=True, exist_ok=True)

    weapon_entries = fetch_mundane_entries("weapon")
    armor_entries = fetch_mundane_entries("armor")

    generated: dict[str, dict] = {}
    with ThreadPoolExecutor(max_workers=12) as pool:
        for slug, body in pool.map(load_weapon, weapon_entries):
            generated[slug] = body
        for slug, body in pool.map(load_armor, armor_entries):
            generated[slug] = body

    with ThreadPoolExecutor(max_workers=4) as pool:
        for slug, body in pool.map(load_ammo, AMMO):
            generated[slug] = body

    unused = set(load_overlay()) - set(generated)
    if unused:
        raise SystemExit(f"item_overlay.yaml names items the SRD did not produce: {sorted(unused)}")

    for slug in sorted(generated):
        write_item(ITEMS_DIR / f"{slug}.yaml", generated[slug])

    print(f"Generated {len(generated)} dnd5e items "
          f"({len(weapon_entries)} weapons, {len(armor_entries)} armor/shields)")
    return len(generated)


def main() -> None:
    print("Generating D&D 5e SRD weapons/armor...")
    count = generate()
    print(f"Done: {count} items")


if __name__ == "__main__":
    main()
