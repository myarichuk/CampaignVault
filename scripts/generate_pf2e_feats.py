#!/usr/bin/env python3
"""Generate pf2e feat YAML from Archives of Nethys (Player Core / Player Core 2, ORC-licensed).

Mirrors generate_spells.py's pf2e sourcing: pulls directly from the official
Archives of Nethys Elasticsearch endpoint, filtered to Remastered core rulebooks
and common rarity, matching LICENSING.md's stated scope.
"""

from __future__ import annotations

import json
import re
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PF2E_FEATS_DIR = ROOT / "src/CampaignVault/RulesetData/pf2e/feats"

HEADER = (
    "# Source: Pathfinder 2e Remastered (Player Core / Player Core 2) by Paizo Inc.,\n"
    "# released under the Open RPG Creative (ORC) License. Pulled directly from the\n"
    "# official Archives of Nethys database (elasticsearch.aonprd.com).\n"
)

AON_SEARCH_URL = "https://elasticsearch.aonprd.com/aon/_search"
AON_REMASTER_SOURCES = ["Player Core", "Player Core 2"]

# Class trait names observed on Player Core / Player Core 2 class feats (aggregated from
# trait_group:Class documents). Anything else in `trait` is a non-class trait (Concentrate,
# Manipulate, Flourish, damage types, etc.) and is ignored for class-eligibility purposes.
KNOWN_CLASSES = {
    "fighter", "barbarian", "rogue", "monk", "cleric", "bard", "druid", "ranger",
    "swashbuckler", "champion", "sorcerer", "alchemist", "investigator", "wizard",
    "oracle", "witch", "magus", "commander", "thaumaturge", "necromancer", "animist",
    "psychic", "exemplar", "gunslinger", "summoner",
}

PREREQ_RE = re.compile(
    r"Prerequisites?\s+(.*?)\s*(?:Trigger|Frequency|Cost|Requirements?|Range|Area|Effect|Special|---|$)",
    re.IGNORECASE,
)


def kebab_to_snake(name: str) -> str:
    return name.replace("-", "_")


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


def fetch_aon_feats() -> list[dict]:
    query = {
        "size": 2000,
        "query": {
            "bool": {
                "must": [
                    {"term": {"category": "feat"}},
                    {"terms": {"primary_source.keyword": AON_REMASTER_SOURCES}},
                    {"term": {"rarity": "common"}},
                ]
            }
        },
        "_source": ["name", "level", "trait", "summary", "text"],
    }
    result = fetch_json(AON_SEARCH_URL, json.dumps(query).encode("utf-8"))
    return [hit["_source"] for hit in result["hits"]["hits"]]


def feat_classes(feat: dict) -> list[str]:
    traits = {t.lower() for t in feat.get("trait") or []}
    return sorted(traits & KNOWN_CLASSES)


def feat_prerequisite(feat: dict) -> str | None:
    text = feat.get("text") or ""
    match = PREREQ_RE.search(text)
    if not match:
        return None
    prereq = match.group(1).strip().rstrip(".")
    return prereq or None


def write_feat(path: Path, body: dict) -> None:
    lines = [HEADER.rstrip()]
    lines.append(f"name: {body['name']}")
    lines.append("system: pf2e")
    if body.get("prerequisite"):
        lines.append(f"prerequisite: {yaml_quote(body['prerequisite'])}")
    if body.get("mechanicalSummary"):
        lines.append(f"mechanicalSummary: {yaml_quote(body['mechanicalSummary'])}")
    lines.append("extraPools: []")
    if body.get("classes"):
        classes = ", ".join(body["classes"])
        lines.append(f"classes: [{classes}]")
    if body.get("level") is not None:
        lines.append(f"level: {body['level']}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def generate() -> int:
    print(f"  pf2e feats source: {AON_SEARCH_URL} (Archives of Nethys, Player Core / Player Core 2)")
    raw = fetch_aon_feats()

    PF2E_FEATS_DIR.mkdir(parents=True, exist_ok=True)
    generated: dict[str, dict] = {}

    for feat in raw:
        name = (feat.get("name") or "").strip()
        if not name:
            continue
        slug = kebab_to_snake(name.lower())
        slug = re.sub(r"[^a-z0-9_]+", "_", slug).strip("_")
        slug = re.sub(r"_+", "_", slug)
        if not slug:
            continue
        base = slug
        n = 2
        while slug in generated and generated[slug]["display"] != name:
            slug = f"{base}_{n}"
            n += 1

        generated[slug] = {
            "display": name,
            "name": slug,
            "prerequisite": feat_prerequisite(feat),
            "mechanicalSummary": feat.get("summary"),
            "classes": feat_classes(feat),
            "level": feat.get("level"),
        }

    for path in PF2E_FEATS_DIR.glob("*.yaml"):
        path.unlink()

    for slug in sorted(generated):
        write_feat(PF2E_FEATS_DIR / f"{slug}.yaml", generated[slug])

    print(f"Generated {len(generated)} pf2e feats (Player Core / Player Core 2, common)")
    return len(generated)


if __name__ == "__main__":
    generate()
