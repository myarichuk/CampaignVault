#!/usr/bin/env python3
"""Generate pf2e ancestries (with their heritages) and backgrounds from Archives of Nethys
(Player Core / Player Core 2, common rarity, ORC-licensed).

Same sourcing and scope as generate_pf2e_feats.py. Both folders are wiped and rebuilt.
  - ancestries/<name>.yaml: hp, size, speed, the fixed attribute boosts and flaw (abilityBonuses: +1 / -1), how many
    free boosts, the ancestry's features (traits), and its heritages. The versatile heritages (Aiuvarin,
    Changeling, Dhampir, ...) are listed under every ancestry, since any ancestry may take one instead of its own.
  - backgrounds/<name>.yaml: the two attributes one boost must go to (boosts; empty means both free), the trained
    skill (or skillOptions when it's one of two), the Lore, and the skill feat it grants.

Requires network access to elasticsearch.aonprd.com.
"""

from __future__ import annotations

import html
import json
import re
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PF2E_DIR = ROOT / "src/CampaignVault/RulesetData/pf2e"
ANCESTRIES_DIR = PF2E_DIR / "ancestries"
BACKGROUNDS_DIR = PF2E_DIR / "backgrounds"

HEADER = (
    "# Source: Pathfinder 2e Remastered (Player Core / Player Core 2) by Paizo Inc.,\n"
    "# released under the Open RPG Creative (ORC) License. Pulled directly from the\n"
    "# official Archives of Nethys database (elasticsearch.aonprd.com).\n"
)

AON_SEARCH_URL = "https://elasticsearch.aonprd.com/aon/_search"
AON_REMASTER_SOURCES = ["Player Core", "Player Core 2"]

SKILLS = {
    "Acrobatics", "Arcana", "Athletics", "Crafting", "Deception", "Diplomacy", "Intimidation", "Medicine",
    "Nature", "Occultism", "Performance", "Religion", "Society", "Stealth", "Survival", "Thievery",
}
ABILITIES = ["Strength", "Dexterity", "Constitution", "Intelligence", "Wisdom", "Charisma"]


def fetch_json(url: str, data: bytes) -> dict:
    req = urllib.request.Request(
        url, headers={"User-Agent": "CampaignVault/1.0", "Content-Type": "application/json"}, data=data
    )
    with urllib.request.urlopen(req, timeout=60) as resp:
        return json.loads(resp.read().decode("utf-8"))


def fetch(category: str) -> list[dict]:
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
    }
    result = fetch_json(AON_SEARCH_URL, json.dumps(query).encode("utf-8"))
    return [hit["_source"] for hit in result["hits"]["hits"]]


def fetch_versatile() -> list[dict]:
    query = {
        "size": 50,
        "query": {
            "bool": {
                "must": [
                    {"term": {"category": "heritage"}},
                    {"terms": {"name.keyword": VERSATILE_HERITAGES}},
                    {"terms": {"primary_source.keyword": AON_REMASTER_SOURCES}},
                ]
            }
        },
    }
    docs = sorted((hit["_source"] for hit in fetch_json(AON_SEARCH_URL, json.dumps(query).encode("utf-8"))["hits"]["hits"]), key=lambda d: d["name"])
    missing = set(VERSATILE_HERITAGES) - {d["name"] for d in docs}
    if missing:
        raise SystemExit(f"No Player Core / Player Core 2 versatile heritage named {sorted(missing)}.")
    return docs


def slug(name: str) -> str:
    s = re.sub(r"[^a-z0-9]+", "_", name.lower()).strip("_")
    return re.sub(r"_+", "_", s)


def yaml_quote(value: str) -> str:
    if re.search(r'[:#\[\]{}&*!|>\'"%@`,\n\r]', value) or value.strip() != value or value[:1] in "-?":
        return json.dumps(value, ensure_ascii=False)
    return value


def flow(values: list[str]) -> str:
    return "[" + ", ".join(yaml_quote(v) for v in values) + "]"


# Golarion names Player Core still uses as examples ("such as Absalom Lore"). LICENSING.md keeps setting content out, so
# an example in parentheses that names one is dropped. Extend the list when a regeneration brings a new one in.
SETTING_NAMES = ["Abadar", "Absalom", "Magnimar", "Golarion", "Boneyard"]

# The versatile heritages: a heritage of any ancestry, taken instead of the ancestry's own (Player Core and Player Core 2;
# uncommon ones included, since all but Aiuvarin and Dromaar are). Aasimar and Tiefling are from the Advanced Player's
# Guide, which this repository doesn't ship.
VERSATILE_HERITAGES = ["Aiuvarin", "Changeling", "Dhampir", "Dragonblood", "Dromaar", "Duskwalker", "Nephilim"]

# Phrases to cut from a text that would name setting content (a place where the heritage's sentence is otherwise rules).
SETTING_PHRASES = [" and the Boneyard"]


def clean(text: str) -> str:
    """Markdown links and tags out, setting-named examples out, whitespace collapsed."""
    text = re.sub(r"\[([^\]]+)\]\([^)]*\)", r"\1", text or "")
    for phrase in SETTING_PHRASES:
        text = text.replace(phrase, "")
    text = html.unescape(re.sub(r"<[^>]+>", "", text))
    text = re.sub(r"\s*\(([^()]*)\)", lambda m: "" if any(n in m.group(1) for n in SETTING_NAMES) else m.group(0), text)
    return re.sub(r"\s+", " ", text).strip()


# The stat lines every ancestry page has as titles too; the rest are the ancestry's own features.
STAT_TITLES = {"Hit Points", "Size", "Speed", "Attribute Boosts", "Attribute Flaws", "Languages"}


def features(markdown: str) -> list[str]:
    """The ancestry's own features: the level-3 titles on its page (Darkvision, Clan Dagger, ...)."""
    titles = [clean(t) for t in re.findall(r'<title level="3"[^>]*>(.*?)</title>', markdown, re.S)]
    return [t for t in titles if t not in STAT_TITLES]


def body(doc: dict) -> str:
    """The first paragraph after the source line: the summary in full (AoN's summary field is cut off with "…")."""
    markdown = doc.get("markdown") or ""
    after = re.split(r"\*\*Source\*\*[^\n]*\n", markdown, maxsplit=1)
    paragraphs = [clean(p) for p in re.split(r"\n\s*\n", after[-1]) if clean(p) and not p.strip().startswith(("<", "---"))]
    return paragraphs[0] if paragraphs else clean(doc.get("summary", ""))


def write_ancestry(doc: dict, heritages: list[dict], versatile: list[dict]) -> str:
    name = slug(doc["name"])
    boosts = doc.get("attribute") or []
    free = sum(1 for b in boosts if b == "Free") + (2 if boosts == ["Two free ability boosts"] else 0)
    fixed_boosts = [b for b in boosts if b in ABILITIES]
    flaws = [f for f in doc.get("attribute_flaw") or [] if f in ABILITIES]
    if not fixed_boosts and free == 0:
        raise SystemExit(f"{doc['name']}: no attribute boosts in {boosts}.")

    lines = [HEADER.rstrip(), f"name: {name}", "system: pf2e", f"description: {yaml_quote(clean(doc.get('summary', '')))}"]
    lines.append(f"hp: {doc['hp']}")
    lines.append(f"size: {(doc.get('size') or ['Medium'])[0]}")
    if (speed := (doc.get("speed") or {}).get("land")) is not None:
        lines.append(f"baseSpeed: {speed}")
    bonuses = [f"{b}: 1" for b in fixed_boosts] + [f"{f}: -1" for f in flaws]
    if bonuses:
        lines.append("abilityBonuses: { " + ", ".join(bonuses) + " }")
    lines.append(f"freeBoosts: {free}")
    lines.append(f"traits: {flow(features(doc.get('markdown', '')))}")
    lines.append("heritages:")
    for h in heritages:
        lines.append(f"  - name: {slug(h['name'])}")
        lines.append(f"    label: {yaml_quote(h['name'])}")
        lines.append(f"    description: {yaml_quote(body(h))}")
    # A versatile heritage is any ancestry's; none is the ancestry's own (a "Human Aiuvarin" has no human heritage).
    for h in versatile:
        lines.append(f"  - name: {slug(h['name'])}")
        lines.append(f"    label: {yaml_quote(h['name'])}")
        lines.append(f"    description: {yaml_quote('Versatile heritage. ' + body(h))}")
    (ANCESTRIES_DIR / f"{name}.yaml").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return name


def write_background(doc: dict) -> str:
    name = slug(doc["name"])
    boosts = [a for a in doc.get("attribute") or [] if a in ABILITIES]
    skills = [s for s in doc.get("skill") or [] if s in SKILLS]
    lore = [clean(s) for s in doc.get("skill") or [] if s not in SKILLS]
    feats = doc.get("feat") or []

    lines = [HEADER.rstrip(), f"name: {name}", "system: pf2e", f"description: {yaml_quote(body(doc))}"]
    lines.append(f"boosts: {flow(boosts)}")
    # A background trains one skill; two listed means one of the two (Hermit: Nature or Occultism).
    if len(skills) > 1:
        lines.append("skillProficiencies: []")
        lines.append(f"skillOptions: {flow(skills)}")
    else:
        lines.append(f"skillProficiencies: {flow(skills)}")
    if lore:
        lines.append(f"lore: {yaml_quote(' '.join(lore).rstrip('.'))}")
    if len(feats) == 1:
        lines.append(f"skillFeat: {slug(feats[0])}")
        lines.append(f"feature: {yaml_quote(feats[0])}")
    (BACKGROUNDS_DIR / f"{name}.yaml").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return name


def generate() -> None:
    print(f"  pf2e origins source: {AON_SEARCH_URL} (Archives of Nethys, Player Core / Player Core 2, common)")
    ancestries = [a for a in fetch("ancestry") if a.get("hp")]
    heritages = fetch("heritage")
    versatile = fetch_versatile()
    backgrounds = fetch("background")

    for folder in (ANCESTRIES_DIR, BACKGROUNDS_DIR):
        folder.mkdir(parents=True, exist_ok=True)
        for path in folder.glob("*.yaml"):
            path.unlink()

    placed = set()
    names = []
    for doc in sorted(ancestries, key=lambda d: d["name"]):
        # A heritage's name ends with its ancestry's ("Rock Dwarf"); versatile ones (Aiuvarin) match none.
        own = sorted((h for h in heritages if h["name"].endswith(" " + doc["name"])), key=lambda h: h["name"])
        placed.update(h["name"] for h in own)
        names.append(write_ancestry(doc, own, versatile))
    placed.update(h["name"] for h in versatile)
    unplaced = sorted(h["name"] for h in heritages if h["name"] not in placed)

    bg_names = [write_background(doc) for doc in sorted(backgrounds, key=lambda d: d["name"])]
    for folder in (ANCESTRIES_DIR, BACKGROUNDS_DIR):
        for path in folder.glob("*.yaml"):
            body_text = "\n".join(l for l in path.read_text(encoding="utf-8").splitlines() if not l.startswith("#"))
            if found := [n for n in SETTING_NAMES if n in body_text]:
                raise SystemExit(f"{path.name} still names {found}: setting content (LICENSING.md).")
    print(f"Generated {len(names)} pf2e ancestries ({', '.join(names)}), "
          f"{len(placed)} heritages, {len(bg_names)} backgrounds")
    print(f"  Not under an ancestry (versatile or another ancestry's): {', '.join(unplaced)}")


if __name__ == "__main__":
    generate()
