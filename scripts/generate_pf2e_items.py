#!/usr/bin/env python3
"""Generate pf2e weapon/armor/shield ItemDefinition YAML from Archives of Nethys
(Player Core / Player Core 2, ORC-licensed).

Mirrors generate_pf2e_feats.py's AoN sourcing and scope filter (category term +
primary_source.keyword in [Player Core, Player Core 2] + rarity: common). Unlike
generate_items.py (dnd5e), pf2e's model needs its own translation, not a shared
lookup table:
  - AC: a pf2e item's own `ac` field IS already the flat AC-bonus contribution
    (target AC = 10 + dex(capped) + proficiency + acBonus) -- there's no dnd5e-style
    "10 + dex" total-AC-at-zero-dex shape to subtract 10 from, for body armor OR
    shields. acBonus = ac directly, always.
  - Dex cap: pf2e armor carries an explicit numeric `dex_cap` field (present only
    when not uncapped) -- this is exactly the "PF2e style" numeric convention
    ArmorParameterResolver.cs's own comment reserves dexCap for, so it's used as-is;
    the dnd5e-only `armorType` fallback key is never written here.
  - No versatile-damage concept... except there actually is one: the `Two-Hand`
    trait (e.g. Bastard Sword: base 1d8 one-handed, "Two-Hand 1d12" wielded in two)
    behaves exactly like dnd5e's Versatile and is captured the same way, as
    `damageVersatile`. This corrects an earlier assumption in this project's history
    that pf2e has no such mechanic -- confirmed live, not assumed.
  - Shields are their OWN AoN document category ("shield"), not a subtype of
    "armor" like dnd5e's armor_category:"Shield" -- earlier work on this pipeline
    never queried it since it only checked weapon/armor. Found live, by noticing
    the 4 real base shields (Buckler/Wooden/Steel/Tower) are absent from both the
    "armor" category (13 hits, all body armor, none named Buckler/Shield) and the
    "equipment" category's Shields subcategory (which holds precious-material
    variants and specific magic shields with no base AC/hardness/HP of their own).

Requires network access to elasticsearch.aonprd.com.
"""

from __future__ import annotations

import json
import re
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ITEMS_DIR = ROOT / "src/CampaignVault/RulesetData/pf2e/items"

HEADER = (
    "# Source: Pathfinder 2e Remastered (Player Core / Player Core 2) by Paizo Inc.,\n"
    "# released under the Open RPG Creative (ORC) License. Pulled directly from the\n"
    "# official Archives of Nethys database (elasticsearch.aonprd.com).\n"
)

# See generate_pf2e_feats.py's identical comment: Player Core / Player Core 2 are the
# two ORC-licensed Remaster core rulebooks. Don't widen this without re-checking
# LICENSING.md.
AON_SEARCH_URL = "https://elasticsearch.aonprd.com/aon/_search"
AON_REMASTER_SOURCES = ["Player Core", "Player Core 2"]

# SpatialDistanceBand.cs constants -- shared engine-level scale, not ruleset-specific,
# same bucket thresholds used by generate_items.py's dnd5e weapons (no source-provided
# feet -> band mapping exists for either ruleset; these are this script's own choice).
BAND_TOUCH, BAND_CLOSE, BAND_NEAR, BAND_FAR, BAND_DISTANT = (
    "Touch", "Close", "Near", "Far", "Distant",
)

TWO_HAND_RE = re.compile(r"Two-Hand\s+(\d*d\d+)", re.IGNORECASE)
THROWN_RE = re.compile(r"Thrown\s+(\d+)\s*ft", re.IGNORECASE)
PENALTY_FT_RE = re.compile(r"(-?\d+)")


def kebab_to_snake(name: str) -> str:
    slug = name.lower()
    slug = re.sub(r"[^a-z0-9]+", "_", slug)
    return slug.strip("_")


def yaml_quote(value: str) -> str:
    if re.search(r'[:#\[\]{}&*!|>\'"%@`\n\r]', value) or value.strip() != value:
        return json.dumps(value)
    return value


def fetch_json(url: str, data: bytes) -> dict:
    req = urllib.request.Request(
        url, headers={"User-Agent": "CampaignVault/1.0", "Content-Type": "application/json"}, data=data
    )
    with urllib.request.urlopen(req, timeout=60) as resp:
        return json.loads(resp.read().decode("utf-8"))


def search(category: str, source_fields: list[str]) -> list[dict]:
    query = {
        "size": 500,
        "query": {
            "bool": {
                "must": [
                    {"term": {"category": category}},
                    {"terms": {"primary_source.keyword": AON_REMASTER_SOURCES}},
                    {"term": {"rarity": "common"}},
                ]
            }
        },
        "_source": source_fields,
    }
    result = fetch_json(AON_SEARCH_URL, json.dumps(query).encode("utf-8"))
    return [hit["_source"] for hit in result["hits"]["hits"]]


def cost_to_gp(price_cp: int | None) -> float | int | None:
    """pf2e's `price` field is always plain copper pieces (confirmed live: Dagger
    price=20 == "2 sp" == 0.2gp, Crossbow price=300 == "3 gp" == 3.0gp)."""
    if price_cp is None:
        return None
    value = round(price_cp / 100, 4)
    return int(value) if value == int(value) else value


def weapon_range_band(detail: dict, traits: list[str], trait_raw: list[str]) -> str | None:
    if "Reach" in traits:
        return BAND_NEAR

    if "Thrown" in traits:
        normal = None
        for t in trait_raw:
            m = THROWN_RE.search(t)
            if m:
                normal = int(m.group(1))
                break
    elif detail.get("weapon_type") == "Ranged":
        normal = detail.get("range")
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


def weapon_description(detail: dict, dice: str | None, damage_type: str | None, two_hand_dice: str | None) -> str:
    parts = [f"{detail['weapon_category']} {detail['weapon_type'].lower()} weapon."]
    if dice:
        dmg = f"Deals {dice}"
        if damage_type:
            dmg += f" {damage_type}"
        dmg += " damage"
        if two_hand_dice:
            dmg += f" ({two_hand_dice} two-handed)"
        parts.append(dmg + ".")
    if detail.get("weapon_type") == "Ranged" and detail.get("range"):
        parts.append(f"Range {detail['range']} ft.")
    return " ".join(parts)


WEAPON_FIELDS = [
    "name", "hands", "bulk", "price", "damage", "damage_die", "damage_type",
    "weapon_category", "weapon_group", "weapon_type", "trait", "trait_raw",
    "range", "reload", "level",
]


def load_weapons() -> dict[str, dict]:
    items: dict[str, dict] = {}
    for detail in search("weapon", WEAPON_FIELDS):
        category = detail.get("weapon_category")
        # Unarmed (Fist): not a carried/equippable item, always innately available.
        # Ammunition (Arrows/Bolts/Sling Bullets/Blowgun Darts): consumable ammo
        # stacks, not held weapons -- out of scope, same as dnd5e's generate_items.py
        # never touching dnd5eapi.co's equipment-categories/ammunition.
        if category in ("Unarmed", "Ammunition"):
            continue
        # Alchemical Bomb is a generic weapon-group placeholder for the whole
        # "alchemical bombs" family (damage: "Varies", no damage_die at all) -- real
        # bombs are separate Equipment/Consumable documents elsewhere, not this entry.
        if "damage_die" not in detail:
            continue

        slug = kebab_to_snake(detail["name"])
        traits = detail.get("trait") or []
        trait_raw = detail.get("trait_raw") or []

        dice = detail["damage"].split()[0]
        damage_type = (detail.get("damage_type") or [None])[0]
        damage_type = damage_type.lower() if damage_type else None

        two_hand_dice = None
        for t in trait_raw:
            m = TWO_HAND_RE.search(t)
            if m:
                two_hand_dice = m.group(1)
                break

        props: dict[str, object] = {"damage": dice}
        if two_hand_dice:
            props["damageVersatile"] = two_hand_dice
        if damage_type:
            props["damageType"] = damage_type
        if detail.get("bulk") is not None:
            props["bulk"] = detail["bulk"]
        cost_gp = cost_to_gp(detail.get("price"))
        if cost_gp is not None:
            props["costGp"] = cost_gp
        if detail.get("reload") is not None:
            props["reload"] = detail["reload"]
        if detail.get("level"):
            props["level"] = detail["level"]
        range_band = weapon_range_band(detail, traits, trait_raw)
        if range_band:
            props["range"] = range_band

        tags = sorted({category.lower(), detail["weapon_type"].lower(), *(t.lower() for t in traits)})

        items[slug] = {
            "name": slug,
            "system": "pf2e",
            "category": "Weapon",
            "tags": tags,
            "description": weapon_description(detail, dice, damage_type, two_hand_dice),
            "properties": props,
            "equipZones": ["MainHand"],
            "equipLayer": "Held",
            "twoHanded": detail.get("hands") == "2",
        }
    return items


def armor_description(detail: dict, ac_bonus: int) -> str:
    parts = [f"{detail['armor_category']} armor. AC bonus +{ac_bonus}."]
    if "dex_cap" in detail:
        parts.append(f"Dex cap +{detail['dex_cap']}.")
    if detail.get("check_penalty"):
        parts.append(f"Check penalty {detail['check_penalty']}.")
    if detail.get("speed_penalty"):
        parts.append(f"Speed penalty {detail['speed_penalty'].rstrip('.')}.")
    return " ".join(parts)


ARMOR_FIELDS = [
    "name", "ac", "armor_category", "dex_cap", "check_penalty", "speed_penalty",
    "strength", "bulk", "price", "trait", "level",
]


def load_armor() -> dict[str, dict]:
    items: dict[str, dict] = {}
    for detail in search("armor", ARMOR_FIELDS):
        # "Unarmored" is a placeholder for the state of wearing nothing (ac:0, no
        # dex_cap key at all, no price) -- not real equipment. Every actual armor
        # piece, including the unarmored-proficiency "Explorer's Clothing", has a
        # price; this is the same "must have concrete stats to be real gear" signal
        # used below for weapons (damage_die) and shields (nothing extra needed,
        # AoN's "shield" category has no such placeholder).
        if "price" not in detail:
            continue

        slug = kebab_to_snake(detail["name"])
        ac_bonus = detail.get("ac", 0)

        props: dict[str, object] = {"acBonus": ac_bonus}
        if "dex_cap" in detail:
            props["dexCap"] = detail["dex_cap"]
        if detail.get("check_penalty"):
            props["checkPenalty"] = detail["check_penalty"]
        if detail.get("speed_penalty"):
            m = PENALTY_FT_RE.search(detail["speed_penalty"])
            if m:
                props["speedModifier"] = float(m.group(1))
        if "strength" in detail:
            props["strength"] = detail["strength"]
        if detail.get("bulk") is not None:
            props["bulk"] = detail["bulk"]
        cost_gp = cost_to_gp(detail.get("price"))
        if cost_gp is not None:
            props["costGp"] = cost_gp
        if detail.get("level"):
            props["level"] = detail["level"]

        items[slug] = {
            "name": slug,
            "system": "pf2e",
            "category": "Armor",
            "tags": [detail["armor_category"].lower()],
            "description": armor_description(detail, ac_bonus),
            "properties": props,
            "equipZones": ["Torso"],
            "equipLayer": "Armor",
        }
    return items


SHIELD_FIELDS = ["name", "ac", "bulk", "price", "hardness", "hp", "speed_penalty", "level"]


def load_shields() -> dict[str, dict]:
    items: dict[str, dict] = {}
    for detail in search("shield", SHIELD_FIELDS):
        slug = kebab_to_snake(detail["name"])
        ac_bonus = detail.get("ac", 0)

        props: dict[str, object] = {"acBonus": ac_bonus}
        if detail.get("hardness") is not None:
            props["hardness"] = detail["hardness"]
        if detail.get("hp") is not None:
            props["hp"] = detail["hp"]
        if detail.get("speed_penalty"):
            m = PENALTY_FT_RE.search(detail["speed_penalty"])
            if m:
                props["speedModifier"] = float(m.group(1))
        if detail.get("bulk") is not None:
            props["bulk"] = detail["bulk"]
        cost_gp = cost_to_gp(detail.get("price"))
        if cost_gp is not None:
            props["costGp"] = cost_gp
        if detail.get("level"):
            props["level"] = detail["level"]

        desc = f"Shield. +{ac_bonus} AC when raised."
        if detail.get("hardness") is not None and detail.get("hp") is not None:
            desc += f" Hardness {detail['hardness']}, HP {detail['hp']}."

        items[slug] = {
            "name": slug,
            "system": "pf2e",
            "category": "Armor",
            "tags": ["shield"],
            "description": desc,
            "properties": props,
            "equipZones": ["OffHand"],
            "equipLayer": "Held",
        }
    return items


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
    zones = ", ".join(body["equipZones"])
    lines.append(f"equipZones: [{zones}]")
    lines.append(f"equipLayer: {body['equipLayer']}")
    if "twoHanded" in body:
        lines.append(f"twoHanded: {'true' if body['twoHanded'] else 'false'}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def generate() -> int:
    print(f"  pf2e items source: {AON_SEARCH_URL} (Archives of Nethys, Player Core / Player Core 2)")

    generated: dict[str, dict] = {}
    generated.update(load_weapons())
    generated.update(load_armor())
    generated.update(load_shields())

    ITEMS_DIR.mkdir(parents=True, exist_ok=True)
    # Unlike generate_items.py (dnd5e), RulesetData/pf2e/items/ has no hand-authored
    # content at all (didn't exist before this script) -- safe to fully wipe and
    # rebuild every run, same as generate_spells.py / generate_pf2e_feats.py.
    for path in ITEMS_DIR.glob("*.yaml"):
        path.unlink()

    for slug in sorted(generated):
        write_item(ITEMS_DIR / f"{slug}.yaml", generated[slug])

    print(f"Generated {len(generated)} pf2e items (Player Core / Player Core 2, common)")
    return len(generated)


if __name__ == "__main__":
    generate()
