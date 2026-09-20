#!/usr/bin/env python3
"""Generate spell YAML from SRD sources (dnd5eapi + Archives of Nethys)."""

from __future__ import annotations

import json
import re
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DND5E_DIR = ROOT / "src/CampaignVault/RulesetData/dnd5e/spells"
PF2E_DIR = ROOT / "src/CampaignVault/RulesetData/pf2e/spells"

DND5E_HEADER = (
    "# Source: SRD 5.1 by Wizards of the Coast LLC, CC BY 4.0\n"
)
PF2E_HEADER = (
    "# Source: Pathfinder 2e Remastered (Player Core / Player Core 2) by Paizo Inc.,\n"
    "# released under the Open RPG Creative (ORC) License. Pulled directly from the\n"
    "# official Archives of Nethys database (elasticsearch.aonprd.com).\n"
)

# Archives of Nethys public search endpoint (official Paizo-run Remastered database).
AON_SEARCH_URL = "https://elasticsearch.aonprd.com/aon/_search"
AON_REMASTER_SOURCES = ["Player Core", "Player Core 2"]

TRADITION_TO_CLASSES = {
    "arcane": ["wizard", "witch"],
    "divine": ["cleric"],
    "primal": ["druid"],
    "occult": ["bard"],
}

CLASS_TRAITS = {"bard", "witch", "cleric", "druid", "wizard"}

GP_COST_RE = re.compile(r"([\d,]+)\s*gp", re.IGNORECASE)


def kebab_to_snake(name: str) -> str:
    return name.replace("-", "_")


def yaml_quote(value: str) -> str:
    if re.search(r'[:#\[\]{}&*!|>\'"%@`\n\r]', value) or value.strip() != value:
        return json.dumps(value)
    return value


def write_spell(path: Path, header: str, body: dict) -> None:
    lines = [header.rstrip()]
    lines.append(f"name: {body['name']}")
    lines.append(f"system: {body['system']}")
    lines.append(f"level: {body['level']}")
    if body.get("classes"):
        classes = ", ".join(body["classes"])
        lines.append(f"classes: [{classes}]")
    lines.append(f"concentration: {'true' if body.get('concentration') else 'false'}")
    if body.get("castingTime"):
        lines.append(f"castingTime: {yaml_quote(body['castingTime'])}")
    if body.get("verbal") is not None:
        lines.append(f"verbal: {'true' if body['verbal'] else 'false'}")
    if body.get("somatic") is not None:
        lines.append(f"somatic: {'true' if body['somatic'] else 'false'}")
    if body.get("material") is not None:
        lines.append(f"material: {'true' if body['material'] else 'false'}")
    if body.get("materialText"):
        lines.append(f"materialText: {yaml_quote(body['materialText'])}")
    if body.get("materialCost") is not None:
        lines.append(f"materialCost: {body['materialCost']}")
    if body.get("materialConsumed") is not None:
        lines.append(f"materialConsumed: {'true' if body['materialConsumed'] else 'false'}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def fetch_json(url: str, retries: int = 3, data: bytes | None = None) -> dict:
    for attempt in range(retries):
        try:
            headers = {"User-Agent": "CampaignVault/1.0"}
            if data is not None:
                headers["Content-Type"] = "application/json"
            req = urllib.request.Request(url, headers=headers, data=data)
            with urllib.request.urlopen(req, timeout=30) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except Exception:
            if attempt == retries - 1:
                raise
            time.sleep(0.5 * (attempt + 1))
    raise RuntimeError(f"Failed to fetch {url}")


def parse_gp_cost(text: str) -> float | None:
    match = GP_COST_RE.search(text)
    if not match:
        return None
    try:
        return float(match.group(1).replace(",", ""))
    except ValueError:
        return None


def generate_dnd5e() -> int:
    index = fetch_json("https://www.dnd5eapi.co/api/spells")
    spells = index["results"]
    DND5E_DIR.mkdir(parents=True, exist_ok=True)

    def load_spell(entry: dict) -> tuple[str, dict]:
        detail = fetch_json(f"https://www.dnd5eapi.co{entry['url']}")
        slug = kebab_to_snake(detail["index"])
        classes = sorted({c["index"] for c in detail.get("classes", [])})
        components = set(detail.get("components") or [])
        material_text = detail.get("material")
        return slug, {
            "name": slug,
            "system": "dnd5e",
            "level": detail["level"],
            "classes": classes,
            "concentration": bool(detail.get("concentration")),
            "castingTime": detail.get("casting_time") or "1 action",
            "verbal": "V" in components,
            "somatic": "S" in components,
            "material": "M" in components,
            "materialText": material_text,
            "materialCost": parse_gp_cost(material_text) if material_text else None,
            "materialConsumed": bool(material_text and "consum" in material_text.lower()),
        }

    generated: dict[str, dict] = {}
    with ThreadPoolExecutor(max_workers=12) as pool:
        futures = {pool.submit(load_spell, e): e for e in spells}
        for i, future in enumerate(as_completed(futures), 1):
            slug, body = future.result()
            generated[slug] = body
            if i % 50 == 0:
                print(f"  dnd5e: {i}/{len(spells)}")

    for path in DND5E_DIR.glob("*.yaml"):
        path.unlink()

    for slug in sorted(generated):
        write_spell(DND5E_DIR / f"{slug}.yaml", DND5E_HEADER, generated[slug])

    print(f"Generated {len(generated)} dnd5e spells")
    return len(generated)


def pf2e_classes(spell: dict) -> list[str]:
    classes: set[str] = set()
    for tradition in spell.get("tradition") or []:
        key = tradition.lower()
        if key in TRADITION_TO_CLASSES:
            classes.update(TRADITION_TO_CLASSES[key])
    for trait in spell.get("trait") or []:
        t = trait.lower()
        if t in CLASS_TRAITS:
            classes.add(t)
    return sorted(classes)


def pf2e_casting_time(spell: dict) -> str:
    actions = (spell.get("actions") or "").strip().lower()
    mapping = {
        "single action": "1 action",
        "one action": "1 action",
        "two actions": "2 actions",
        "three actions": "3 actions",
        "reaction": "1 reaction",
        "free action": "free action",
    }
    return mapping.get(actions, spell.get("actions") or "2 actions")


def pf2e_level(spell: dict) -> int:
    traits = {t.lower() for t in spell.get("trait") or []}
    if "cantrip" in traits:
        return 0
    return int(spell.get("level") or 1)


def pf2e_concentration(spell: dict) -> bool:
    traits = {t.lower() for t in spell.get("trait") or []}
    return "concentrate" in traits


def pf2e_somatic(spell: dict) -> bool:
    traits = {t.lower() for t in spell.get("trait") or []}
    return "manipulate" in traits


def fetch_aon_spells() -> list[dict]:
    query = {
        "size": 1000,
        "query": {
            "bool": {
                "must": [
                    {"term": {"category": "spell"}},
                    {"terms": {"primary_source.keyword": AON_REMASTER_SOURCES}},
                    {"term": {"rarity": "common"}},
                ]
            }
        },
        "_source": ["name", "level", "trait", "tradition", "actions", "cost"],
    }
    result = fetch_json(AON_SEARCH_URL, data=json.dumps(query).encode("utf-8"))
    return [hit["_source"] for hit in result["hits"]["hits"]]


def generate_pf2e() -> int:
    print(f"  pf2e source: {AON_SEARCH_URL} (Archives of Nethys, Player Core / Player Core 2)")
    raw = fetch_aon_spells()
    selected = [s for s in raw if pf2e_classes(s)]

    PF2E_DIR.mkdir(parents=True, exist_ok=True)
    generated: dict[str, dict] = {}

    for spell in selected:
        slug = kebab_to_snake(spell["name"].lower())
        slug = re.sub(r"[^a-z0-9_]+", "_", slug).strip("_")
        slug = re.sub(r"_+", "_", slug)
        if not slug:
            continue
        base = slug
        n = 2
        while slug in generated and generated[slug]["display"] != spell["name"]:
            slug = f"{base}_{n}"
            n += 1

        cost_text = spell.get("cost")

        generated[slug] = {
            "display": spell["name"],
            "name": slug,
            "system": "pf2e",
            "level": pf2e_level(spell),
            "classes": pf2e_classes(spell),
            "concentration": pf2e_concentration(spell),
            "castingTime": pf2e_casting_time(spell),
            "verbal": True,
            "somatic": pf2e_somatic(spell),
            "material": bool(cost_text),
            "materialText": cost_text,
            "materialCost": parse_gp_cost(cost_text) if cost_text else None,
            "materialConsumed": bool(cost_text),
        }

    for path in PF2E_DIR.glob("*.yaml"):
        path.unlink()

    for slug in sorted(generated):
        write_spell(PF2E_DIR / f"{slug}.yaml", PF2E_HEADER, generated[slug])

    print(f"Generated {len(generated)} pf2e spells (Player Core / Player Core 2, common)")
    return len(generated)


def main() -> None:
    print("Generating D&D 5e SRD spells...")
    dnd_count = generate_dnd5e()
    print("Generating PF2e ORC (Remastered) spells...")
    pf2_count = generate_pf2e()
    print(f"Done: {dnd_count} dnd5e + {pf2_count} pf2e")


if __name__ == "__main__":
    main()
