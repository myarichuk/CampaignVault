#!/usr/bin/env python3
"""Generate pf2e feat YAML from Archives of Nethys (Player Core / Player Core 2, ORC-licensed).

Mirrors generate_spells.py's pf2e sourcing: pulls directly from the official
Archives of Nethys Elasticsearch endpoint, filtered to Remastered core rulebooks
and common rarity, matching LICENSING.md's stated scope.

Requires network access to elasticsearch.aonprd.com.
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

# Player Core / Player Core 2 are the two books Paizo rewrote during the 2023
# Remaster specifically to strip Golarion-specific Product Identity out of core
# mechanics so they could be released under ORC. Don't widen this to other AoN
# sourcebooks (Lost Omens, adventure paths, legacy pre-Remaster books) without
# re-checking LICENSING.md's scope rationale.
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


# Setting names the free-rules scope keeps out (LICENSING.md); a feat's summary naming one gets the generic phrase.
SETTING_PHRASES = {"the Boneyard": "the realm of the dead"}
SETTING_NAMES = ["Golarion", "Absalom", "Magnimar", "Boneyard", "Abadar"]


def scrub(text: str | None) -> str | None:
    for name, generic in SETTING_PHRASES.items():
        text = text.replace(name, generic) if text else text
    if text and (found := [n for n in SETTING_NAMES if n in text]):
        raise SystemExit(f"A feat summary names {found}: setting content (LICENSING.md): {text[:80]}")
    return text


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
        "_source": ["name", "level", "trait", "trait_group", "skill", "summary", "prerequisite"],
    }
    result = fetch_json(AON_SEARCH_URL, json.dumps(query).encode("utf-8"))
    return [hit["_source"] for hit in result["hits"]["hits"]]


def fetch_ancestry_names() -> set[str]:
    """Every ancestry and versatile heritage (any rarity): an ancestry feat names its ancestry as a trait."""
    query = {
        "size": 200,
        "query": {
            "bool": {
                "must": [
                    {"term": {"category": "ancestry"}},
                    {"terms": {"primary_source.keyword": AON_REMASTER_SOURCES}},
                ]
            }
        },
        "_source": ["name"],
    }
    result = fetch_json(AON_SEARCH_URL, json.dumps(query).encode("utf-8"))
    return {hit["_source"]["name"].lower() for hit in result["hits"]["hits"]}


def feat_category(feat: dict) -> str:
    """skill, general, class, ancestry or archetype, from the feat's traits (a skill feat is also General)."""
    traits = {t.lower() for t in feat.get("trait") or []}
    if "skill" in traits:
        return "skill"
    if "general" in traits:
        return "general"
    if traits & KNOWN_CLASSES:
        return "class"
    if "Ancestry" in (feat.get("trait_group") or []):
        return "ancestry"
    if "archetype" in traits:
        return "archetype"
    raise SystemExit(f"Can't tell the category of the feat {feat.get('name')} (traits: {sorted(traits)}).")


def feat_ancestries(feat: dict, ancestry_names: set[str]) -> list[str]:
    traits = {t.lower() for t in feat.get("trait") or []}
    return sorted(kebab_to_snake(t) for t in traits & ancestry_names)


def feat_skills(feat: dict) -> list[str]:
    return sorted(set(feat.get("skill") or []))


def feat_classes(feat: dict) -> list[str]:
    traits = {t.lower() for t in feat.get("trait") or []}
    return sorted(traits & KNOWN_CLASSES)


def feat_prerequisite(feat: dict) -> str | None:
    prereq = (feat.get("prerequisite") or "").strip().rstrip(".")
    prereq = re.sub(r"[_*](.+?)[_*]", r"\1", prereq)
    return prereq or None


SKILLS = [
    "Acrobatics", "Arcana", "Athletics", "Crafting", "Deception", "Diplomacy", "Intimidation", "Medicine",
    "Nature", "Occultism", "Performance", "Religion", "Society", "Stealth", "Survival", "Thievery",
]
ABILITIES = ["Strength", "Dexterity", "Constitution", "Intelligence", "Wisdom", "Charisma"]
RANKS = ["trained", "expert", "master", "legendary"]

# The class feature choices the builder asks for (generate_pf2e_classes.py's CLASS_CHOICES): a prerequisite naming one
# ("leaf order", "warrior muse") is checked against the option picked.
CLASS_FEATURE_CATEGORIES = ["muse", "doctrine", "druidic-order", "hunters-edge", "racket", "patron", "arcane-school", "arcane-thesis"]
CLASS_FEATURE_WORDS = r"order|muse|racket|doctrine|patron|edge|school|thesis"


def option_slug(name: str) -> str:
    """generate_pf2e_classes.py's option id: "Faith's Flamekeeper" -> "faithsFlamekeeper"."""
    words = re.sub(r"[^A-Za-z0-9 ]", "", name).split()
    return words[0].lower() + "".join(w[:1].upper() + w[1:] for w in words[1:])


def fetch_class_feature_options() -> set[str]:
    query = {
        "size": 200,
        "query": {"bool": {"must": [{"terms": {"category": CLASS_FEATURE_CATEGORIES}}, {"term": {"primary_source.keyword": "Player Core"}}]}},
        "_source": ["name"],
    }
    result = fetch_json(AON_SEARCH_URL, json.dumps(query).encode("utf-8"))
    return {option_slug(hit["_source"]["name"]) for hit in result["hits"]["hits"]}


def prerequisite_item(text: str, rank: str | None, feats: dict[str, str], options: set[str]) -> tuple[str, str | None] | None:
    """One checkable prerequisite as a YAML flow mapping (and the skill rank it named, for the items after it in an "or"
    list), or None when it isn't one the builder can check."""
    text = text.strip().strip('"').strip()
    if m := re.fullmatch(r"(?i)(trained|expert|master|legendary) in (\w+)", text):
        rank, text = m.group(1).lower(), m.group(2)
    if rank and text.capitalize() in SKILLS:
        return f"{{ skill: {text.capitalize()}, rank: {rank} }}", rank
    if m := re.fullmatch(r"(\w+) \+(\d)", text):
        if m.group(1) in ABILITIES:
            return f"{{ ability: {m.group(1)}, min: {m.group(2)} }}", None
    # A feat, by name ("Shield Block", "Dueling Parry (Fighter)").
    if (slug := feats.get(text.lower(), feats.get(re.sub(r"\s*\([^)]*\)$", "", text).lower()))) is not None:
        return f"{{ feat: {slug} }}", None
    # A class feature option: "leaf order", "warrior muse", "thief racket".
    if m := re.fullmatch(rf"(?i)([\w' ]+?) (?:{CLASS_FEATURE_WORDS})", text):
        if (slug := option_slug(m.group(1))) in options:
            return f"{{ classFeature: {slug} }}", None
    return None


def feat_prerequisites(text: str | None, feats: dict[str, str], options: set[str]) -> list[str]:
    """
    The prerequisites the builder can check, as YAML flow mappings: a skill rank, an attribute modifier, another feat, a
    class feature option, or any one of a list of those ("Trained in Arcana, Nature, Occultism, or Religion"). Parts it
    can't read (a Lore, a Perception rank, prose) stay only in the prerequisite text, which the option shows.
    """
    result = []
    for part in re.split(r";", text or ""):
        part = part.strip()
        if not part:
            continue
        items = [i for i in re.split(r",\s*(?:or\s+)?|\s+or\s+", part) if i.strip()]
        parsed, rank = [], None
        for item in items:
            got = prerequisite_item(item, rank, feats, options)
            if got is None:
                parsed = None
                break
            parsed.append(got[0])
            rank = got[1] or rank
        if not parsed:
            # "Trained in Athletics, Strength +2" lists two prerequisites with a comma: each must hold.
            continue
        if len(parsed) == 1:
            result.append(parsed[0])
        elif re.search(r"\bor\b", part):
            result.append(f"{{ anyOf: [{', '.join(parsed)}] }}")
        else:
            result.extend(parsed)
    return result


def write_feat(path: Path, body: dict) -> None:
    lines = [HEADER.rstrip()]
    lines.append(f"name: {body['name']}")
    lines.append("system: pf2e")
    if body.get("prerequisite"):
        lines.append(f"prerequisite: {yaml_quote(body['prerequisite'])}")
    if body.get("prerequisites"):
        lines.append("prerequisites:")
        lines += [f"  - {p}" for p in body["prerequisites"]]
    if body.get("mechanicalSummary"):
        lines.append(f"mechanicalSummary: {yaml_quote(body['mechanicalSummary'])}")
    lines.append("extraPools: []")
    lines.append(f"category: {body['category']}")
    if body.get("classes"):
        classes = ", ".join(body["classes"])
        lines.append(f"classes: [{classes}]")
    if body.get("ancestries"):
        lines.append(f"ancestries: [{', '.join(body['ancestries'])}]")
    if body.get("skills"):
        lines.append(f"skills: [{', '.join(body['skills'])}]")
    if body.get("level") is not None:
        lines.append(f"level: {body['level']}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def generate() -> int:
    print(f"  pf2e feats source: {AON_SEARCH_URL} (Archives of Nethys, Player Core / Player Core 2)")
    raw = fetch_aon_feats()
    ancestry_names = fetch_ancestry_names()

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
            "mechanicalSummary": scrub(feat.get("summary")),
            "category": feat_category(feat),
            "classes": feat_classes(feat),
            "ancestries": feat_ancestries(feat, ancestry_names),
            "skills": feat_skills(feat),
            "level": feat.get("level"),
        }

    # Prerequisites name feats by their display name; the builder checks them by file name.
    by_name = {body["display"].lower(): slug for slug, body in generated.items()}
    options = fetch_class_feature_options()
    checked = 0
    for body in generated.values():
        body["prerequisites"] = feat_prerequisites(body["prerequisite"], by_name, options)
        checked += bool(body["prerequisites"])

    for path in PF2E_FEATS_DIR.glob("*.yaml"):
        path.unlink()

    for slug in sorted(generated):
        write_feat(PF2E_FEATS_DIR / f"{slug}.yaml", generated[slug])

    print(f"Generated {len(generated)} pf2e feats (Player Core / Player Core 2, common); {checked} with checkable prerequisites")
    return len(generated)


if __name__ == "__main__":
    generate()
