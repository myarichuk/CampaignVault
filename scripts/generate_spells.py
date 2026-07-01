#!/usr/bin/env python3
"""Generate spell YAML from SRD sources (dnd5eapi + PF2e Player Core)."""

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
    "# Source: Pathfinder 2e material released under the Open RPG Creative (ORC) License by Paizo Inc.\n"
)

# Pinned commit of fyjham-ts/Pathfinder-2E-Spell-DB (Nethys scrape). Bump when intentionally refreshing PF2e corpus.
PF2E_SPELL_DB_COMMIT = "0ac8f4ac4d233a60f17e83badb43ad66a14da15d"
PF2E_SPELL_DB_URL = (
    f"https://raw.githubusercontent.com/fyjham-ts/Pathfinder-2E-Spell-DB/"
    f"{PF2E_SPELL_DB_COMMIT}/NethysScrape/spells.json"
)

TRADITION_TO_CLASSES = {
    "arcane": ["wizard", "witch"],
    "divine": ["cleric"],
    "primal": ["druid"],
    "occult": ["bard"],
}

CLASS_TRAITS = {"bard", "witch", "cleric", "druid", "wizard"}


def kebab_to_snake(name: str) -> str:
    return name.replace("-", "_")


def yaml_quote(value: str) -> str:
    if re.search(r'[:#\[\]{}&*!|>\'"%@`]', value) or value.strip() != value:
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
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


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


def generate_dnd5e() -> int:
    index = fetch_json("https://www.dnd5eapi.co/api/spells")
    spells = index["results"]
    DND5E_DIR.mkdir(parents=True, exist_ok=True)

    def load_spell(entry: dict) -> tuple[str, dict]:
        detail = fetch_json(f"https://www.dnd5eapi.co{entry['url']}")
        slug = kebab_to_snake(detail["index"])
        classes = sorted({c["index"] for c in detail.get("classes", [])})
        return slug, {
            "name": slug,
            "system": "dnd5e",
            "level": detail["level"],
            "classes": classes,
            "concentration": bool(detail.get("concentration")),
            "castingTime": detail.get("casting_time") or "1 action",
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
    for tradition in spell.get("traditions") or []:
        key = tradition.lower()
        if key in TRADITION_TO_CLASSES:
            classes.update(TRADITION_TO_CLASSES[key])
    for trait in spell.get("traits") or []:
        t = trait.lower()
        if t in CLASS_TRAITS:
            classes.add(t)
    return sorted(classes)


def pf2e_casting_time(spell: dict) -> str:
    action = str(spell.get("action", "2")).strip()
    if action.lower() == "reaction":
        return "1 reaction"
    if action == "1":
        return "1 action"
    if action == "2":
        return "2 actions"
    if action == "3":
        return "3 actions"
    return f"{action} actions"


def pf2e_level(spell: dict) -> int:
    if (spell.get("type") or "").lower() == "cantrip":
        return 0
    return int(spell.get("level", 1))


def pf2e_concentration(spell: dict) -> bool:
    traits = {t.lower() for t in spell.get("traits") or []}
    return "concentrate" in traits


def is_player_core(spell: dict) -> bool:
    source = (spell.get("source") or "").strip()
    return source.startswith("Player Core")


def is_common(spell: dict) -> bool:
    traits = {t.lower() for t in spell.get("traits") or []}
    return "uncommon" not in traits and "rare" not in traits


def generate_pf2e() -> int:
    print(f"  pf2e source: {PF2E_SPELL_DB_URL}")
    raw = fetch_json(PF2E_SPELL_DB_URL)
    selected = [
        s for s in raw
        if is_player_core(s) and is_common(s) and pf2e_classes(s)
    ]

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

        generated[slug] = {
            "display": spell["name"],
            "name": slug,
            "system": "pf2e",
            "level": pf2e_level(spell),
            "classes": pf2e_classes(spell),
            "concentration": pf2e_concentration(spell),
            "castingTime": pf2e_casting_time(spell),
        }

    for path in PF2E_DIR.glob("*.yaml"):
        path.unlink()

    for slug in sorted(generated):
        write_spell(PF2E_DIR / f"{slug}.yaml", PF2E_HEADER, generated[slug])

    print(f"Generated {len(generated)} pf2e spells (Player Core, common)")
    return len(generated)


def main() -> None:
    print("Generating D&D 5e SRD spells...")
    dnd_count = generate_dnd5e()
    print("Generating PF2e ORC spells...")
    pf2_count = generate_pf2e()
    print(f"Done: {dnd_count} dnd5e + {pf2_count} pf2e")


if __name__ == "__main__":
    main()