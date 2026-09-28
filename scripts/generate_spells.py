#!/usr/bin/env python3
"""Generate spell YAML from SRD sources (dnd5eapi + Archives of Nethys).

Requires network access to both www.dnd5eapi.co and elasticsearch.aonprd.com.
"""

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
OVERLAY_PATH = ROOT / "scripts" / "spell_damage_overlay.yaml"
SUMMON_OVERLAY_PATH = ROOT / "scripts" / "spell_summon_overlay.yaml"
SUMMON_PF2E_OVERLAY_PATH = ROOT / "scripts" / "spell_summon_overlay_pf2e.yaml"

DND5E_HEADER = (
    "# Source: SRD 5.1 by Wizards of the Coast LLC, CC BY 4.0\n"
)
PF2E_HEADER = (
    "# Source: Pathfinder 2e Remastered (Player Core / Player Core 2) by Paizo Inc.,\n"
    "# released under the Open RPG Creative (ORC) License. Pulled directly from the\n"
    "# official Archives of Nethys database (elasticsearch.aonprd.com).\n"
)

# Archives of Nethys public search endpoint (official Paizo-run Remastered database).
# Player Core / Player Core 2 are the two books Paizo rewrote during the 2023
# Remaster specifically to strip Golarion-specific Product Identity out of core
# mechanics so they could be released under ORC. Don't widen this to other AoN
# sourcebooks (Lost Omens, adventure paths, legacy pre-Remaster books) without
# re-checking LICENSING.md's scope rationale.
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

# Parseable single-pool dice (mirrors DefaultRollService's NdX±M shape). Gates
# API-derived damagePools: every pool of a multi-entry spell must match, or no
# pools are emitted and the spell stays overlay-supplied (flame-strike's
# "4d6 OR 5d6" upcast entries fail this gate by design).
DICE_VALUE_RE = re.compile(r"^\d+d\d+\s*([+-]\s*\d+)?$", re.IGNORECASE)


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
    if body.get("damageType"):
        lines.append(f"damageType: {yaml_quote(body['damageType'])}")
    for key in ("damageAtSlotLevel", "damageAtCharacterLevel", "healAtSlotLevel"):
        table = body.get(key)
        if table:
            lines.append(f"{key}:")
            for level in sorted(table):
                lines.append(f"  {level}: {yaml_quote(table[level])}")
    if body.get("saveType"):
        lines.append(f"saveType: {yaml_quote(body['saveType'])}")
    if body.get("saveSuccess"):
        lines.append(f"saveSuccess: {yaml_quote(body['saveSuccess'])}")
    if body.get("areaOfEffectType"):
        lines.append(f"areaOfEffectType: {yaml_quote(body['areaOfEffectType'])}")
    if body.get("areaOfEffectSize") is not None:
        lines.append(f"areaOfEffectSize: {body['areaOfEffectSize']}")
    for key in ("instanceCountAtSlotLevel", "instanceCountAtCharacterLevel"):
        table = body.get(key)
        if table:
            lines.append(f"{key}:")
            for level in sorted(table):
                lines.append(f"  {level}: {table[level]}")
    for key in ("perInstanceDamageAtSlotLevel", "perInstanceDamageAtCharacterLevel"):
        table = body.get(key)
        if table:
            lines.append(f"{key}:")
            for level in sorted(table):
                lines.append(f"  {level}: {yaml_quote(table[level])}")
    pools = body.get("damagePools")
    if pools:
        lines.append("damagePools:")
        for pool in pools:
            lines.append(f"  {yaml_quote(pool)}:")
            for level in sorted(pools[pool]):
                lines.append(f"    {level}: {yaml_quote(pools[pool][level])}")
    choice = body.get("upcastChoice")
    if choice:
        lines.append("upcastChoice:")
        if choice.get("bonusDicePerSlot"):
            lines.append(f"  bonusDicePerSlot: {yaml_quote(choice['bonusDicePerSlot'])}")
    if body.get("requiresAttackRoll") is not None:
        lines.append(f"requiresAttackRoll: {'true' if body['requiresAttackRoll'] else 'false'}")
    if body.get("damageIsPool") is not None:
        lines.append(f"damageIsPool: {'true' if body['damageIsPool'] else 'false'}")
    if body.get("onMiss"):
        lines.append(f"onMiss: {body['onMiss']}")
    tick = body.get("delayedTick")
    if tick:
        lines.append("delayedTick:")
        if tick.get("damageType"):
            lines.append(f"  damageType: {yaml_quote(tick['damageType'])}")
        if tick.get("triggerAt"):
            lines.append(f"  triggerAt: {tick['triggerAt']}")
        if tick.get("requiresInitialHit") is not None:
            lines.append(f"  requiresInitialHit: {'true' if tick['requiresInitialHit'] else 'false'}")
        dice_table = tick.get("diceExpressionAtSlotLevel")
        if dice_table:
            lines.append("  diceExpressionAtSlotLevel:")
            for level in sorted(dice_table):
                lines.append(f"    {level}: {yaml_quote(dice_table[level])}")
    lines.extend(render_summon_lines(body.get("summon")))
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def render_summon_lines(summon: dict | None) -> list[str]:
    """Render the `summon:` YAML block for write_spell (also reused to verify
    checked-in files carry exactly the generator's bytes)."""
    if not summon:
        return []
    lines = ["summon:"]
    if summon.get("creatures"):
        lines.append(f"  creatures: [{', '.join(summon['creatures'])}]")
    seed = summon.get("inlineSeed")
    if seed:
        lines.append("  inlineSeed:")
        if seed.get("hp") is not None:
            lines.append(f"    hp: {seed['hp']}")
        if seed.get("defense") is not None:
            lines.append(f"    defense: {seed['defense']}")
        if seed.get("attacks"):
            quoted = ", ".join(yaml_quote(a) for a in seed["attacks"])
            lines.append(f"    attacks: [{quoted}]")
    for key in ("countAtSlotLevel", "retainCountAtSlotLevel"):
        table = summon.get(key)
        if table:
            lines.append(f"  {key}:")
            for level in sorted(table):
                lines.append(f"    {level}: {table[level]}")
    choices = summon.get("countChoicesAtSlotLevel")
    if choices:
        lines.append("  countChoicesAtSlotLevel:")
        for level in sorted(choices):
            lines.append(f"    {level}: [{', '.join(str(v) for v in choices[level])}]")
    if summon.get("durationRounds") is not None:
        lines.append(f"  durationRounds: {summon['durationRounds']}")
    if summon.get("durationDays") is not None:
        lines.append(f"  durationDays: {summon['durationDays']}")
    cap = summon.get("controlCap")
    if cap:
        lines.append("  controlCap:")
        for key in ("maxCreatures", "maxHitDice", "hitDicePerCasterLevel"):
            if cap.get(key) is not None:
                lines.append(f"    {key}: {cap[key]}")
    if summon.get("disposition"):
        lines.append(f"  disposition: {summon['disposition']}")
    return lines


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


# Overlay schema (scripts/spell_damage_overlay.yaml). The overlay is hand-authored
# YAML but this script stays stdlib-only like every other generator here, so it
# parses the file's small controlled subset directly: 2-space-indented nested
# maps, scalar leaves, `#` comments. Anything outside this schema fails loudly.
OVERLAY_BOOL_KEYS = {"requiresAttackRoll", "damageIsPool"}
OVERLAY_STR_KEYS = {"onMiss"}
OVERLAY_INT_TABLE_KEYS = {"instanceCountAtSlotLevel", "instanceCountAtCharacterLevel"}
OVERLAY_STR_TABLE_KEYS = {"perInstanceDamageAtSlotLevel", "perInstanceDamageAtCharacterLevel"}
OVERLAY_TICK_SCALAR_TYPES = {"damageType": "str", "triggerAt": "str", "requiresInitialHit": "bool"}
OVERLAY_TICK_TABLE_KEYS = {"diceExpressionAtSlotLevel"}
OVERLAY_POOL_KEYS = {"damagePools"}
OVERLAY_CHOICE_KEYS = {"upcastChoice"}
OVERLAY_CHOICE_STR_KEYS = {"bonusDicePerSlot"}
VALID_ON_MISS = {"none", "half"}
VALID_TRIGGER_AT = {"endOfTargetNextTurn"}


def _strip_overlay_comment(line: str) -> str:
    in_single = in_double = False
    for i, ch in enumerate(line):
        if ch == "'" and not in_double:
            in_single = not in_single
        elif ch == '"' and not in_single:
            in_double = not in_double
        elif ch == "#" and not in_single and not in_double:
            return line[:i]
    return line


def _parse_overlay_scalar(text: str, kind: str, where: str):
    text = text.strip()
    if len(text) >= 2 and text[0] == text[-1] and text[0] in ("'", '"'):
        text = text[1:-1]
    if kind == "bool":
        if text == "true":
            return True
        if text == "false":
            return False
        raise ValueError(f"{where}: expected true/false, got {text!r}")
    if kind == "int":
        try:
            return int(text)
        except ValueError:
            raise ValueError(f"{where}: expected int, got {text!r}") from None
    return text


def load_damage_overlay() -> dict[str, dict]:
    """Parse scripts/spell_damage_overlay.yaml into {slug: {camelCaseKey: value}}."""
    if not OVERLAY_PATH.exists():
        return {}
    overlay: dict[str, dict] = {}
    slug: str | None = None
    section: str | None = None
    tick_table: str | None = None
    pool_name: str | None = None
    for lineno, raw in enumerate(OVERLAY_PATH.read_text(encoding="utf-8").splitlines(), 1):
        line = _strip_overlay_comment(raw).rstrip()
        if not line.strip():
            continue
        indent = len(line) - len(line.lstrip(" "))
        if indent % 2 or indent > 6:
            raise ValueError(f"{OVERLAY_PATH.name}:{lineno}: bad indent (use 2 spaces/level)")
        content = line.strip()
        where = f"{OVERLAY_PATH.name}:{lineno}"
        if indent == 0:
            if not content.endswith(":"):
                raise ValueError(f"{where}: expected 'slug:'")
            slug = content[:-1].strip()
            if not slug or slug in overlay:
                raise ValueError(f"{where}: bad or duplicate slug {slug!r}")
            overlay[slug] = {}
            section = tick_table = pool_name = None
        elif indent == 2:
            if slug is None:
                raise ValueError(f"{where}: entry before any slug")
            key, _, value = content.partition(":")
            key, value = key.strip(), value.strip()
            section = tick_table = pool_name = None
            if key in OVERLAY_BOOL_KEYS:
                overlay[slug][key] = _parse_overlay_scalar(value, "bool", where)
            elif key in OVERLAY_STR_KEYS:
                overlay[slug][key] = _parse_overlay_scalar(value, "str", where)
            elif key in OVERLAY_INT_TABLE_KEYS | OVERLAY_STR_TABLE_KEYS | {"delayedTick"} | OVERLAY_POOL_KEYS | OVERLAY_CHOICE_KEYS:
                if value:
                    raise ValueError(f"{where}: {key!r} takes nested entries, not an inline value")
                overlay[slug][key] = {}
                section = key
            else:
                raise ValueError(f"{where}: unknown overlay key {key!r}")
        elif indent == 4:
            if slug is None or section is None:
                raise ValueError(f"{where}: nested entry outside a mapping")
            key, _, value = content.partition(":")
            key, value = key.strip(), value.strip()
            tick_table = pool_name = None
            if section in OVERLAY_POOL_KEYS:
                if value:
                    raise ValueError(f"{where}: pool {key!r} takes nested slot entries, not an inline value")
                if not key or key in overlay[slug][section]:
                    raise ValueError(f"{where}: bad or duplicate pool {key!r}")
                overlay[slug][section][key] = {}
                pool_name = key
            elif section in OVERLAY_CHOICE_KEYS:
                if key not in OVERLAY_CHOICE_STR_KEYS:
                    raise ValueError(f"{where}: unknown upcastChoice key {key!r}")
                overlay[slug][section][key] = _parse_overlay_scalar(value, "str", where)
            elif section in OVERLAY_INT_TABLE_KEYS:
                overlay[slug][section][_parse_overlay_scalar(key, "int", where)] = (
                    _parse_overlay_scalar(value, "int", where))
            elif section in OVERLAY_STR_TABLE_KEYS:
                overlay[slug][section][_parse_overlay_scalar(key, "int", where)] = (
                    _parse_overlay_scalar(value, "str", where))
            elif section == "delayedTick":
                if key in OVERLAY_TICK_SCALAR_TYPES:
                    overlay[slug][section][key] = _parse_overlay_scalar(
                        value, OVERLAY_TICK_SCALAR_TYPES[key], where)
                elif key in OVERLAY_TICK_TABLE_KEYS:
                    if value:
                        raise ValueError(f"{where}: {key!r} takes nested entries, not an inline value")
                    overlay[slug][section][key] = {}
                    tick_table = key
                else:
                    raise ValueError(f"{where}: unknown delayedTick key {key!r}")
            else:
                raise ValueError(f"{where}: nested entry under scalar {section!r}")
        else:  # indent == 6
            key, _, value = content.partition(":")
            if slug is not None and section == "delayedTick" and tick_table is not None:
                overlay[slug][section][tick_table][_parse_overlay_scalar(key.strip(), "int", where)] = (
                    _parse_overlay_scalar(value, "str", where))
            elif slug is not None and section in OVERLAY_POOL_KEYS and pool_name is not None:
                overlay[slug][section][pool_name][_parse_overlay_scalar(key.strip(), "int", where)] = (
                    _parse_overlay_scalar(value, "str", where))
            else:
                raise ValueError(f"{where}: nested entry outside diceExpressionAtSlotLevel or damagePools")
    for name, entry in overlay.items():
        if "onMiss" in entry and entry["onMiss"] not in VALID_ON_MISS:
            raise ValueError(f"{name}: onMiss must be one of {sorted(VALID_ON_MISS)}")
        for count_key, dice_key in (("instanceCountAtSlotLevel", "perInstanceDamageAtSlotLevel"),
                                    ("instanceCountAtCharacterLevel", "perInstanceDamageAtCharacterLevel")):
            if count_key in entry:
                missing = set(entry[count_key]) - set(entry.get(dice_key, {}))
                if missing:
                    raise ValueError(f"{name}: {count_key} levels {sorted(missing)} lack {dice_key}")
                if any(v < 1 for v in entry[count_key].values()):
                    raise ValueError(f"{name}: {count_key} counts must be >= 1")
        tick = entry.get("delayedTick")
        if tick:
            if tick.get("triggerAt") not in VALID_TRIGGER_AT:
                raise ValueError(f"{name}: triggerAt must be one of {sorted(VALID_TRIGGER_AT)}")
            if not tick.get("diceExpressionAtSlotLevel"):
                raise ValueError(f"{name}: delayedTick needs diceExpressionAtSlotLevel")
        pools = entry.get("damagePools")
        if pools:
            if len(pools) < 2:
                raise ValueError(f"{name}: damagePools needs at least 2 pools")
            slot_sets = [set(slots) for slots in pools.values()]
            if not slot_sets[0] or any(s != slot_sets[0] for s in slot_sets):
                raise ValueError(f"{name}: damagePools pools must share identical non-empty slot levels")
        choice = entry.get("upcastChoice")
        if choice:
            if "damagePools" not in entry:
                raise ValueError(f"{name}: upcastChoice needs damagePools")
            bonus = choice.get("bonusDicePerSlot")
            if not bonus or not re.fullmatch(r"\d+d\d+", bonus, re.IGNORECASE):
                raise ValueError(f"{name}: bonusDicePerSlot must be NdX, got {bonus!r}")
    return overlay


def apply_damage_overlay(slug: str, body: dict, overlay: dict[str, dict]) -> None:
    entry = overlay.get(slug)
    if not entry:
        return
    for key, value in entry.items():
        if key in body:
            raise ValueError(f"overlay for {slug!r} collides with API-derived key {key!r}")
        body[key] = value


# Summon overlay schema (scripts/spell_summon_overlay.yaml). Same stdlib-only
# mini-YAML subset as the damage overlay (2-space indent, `#` comments), plus
# inline string lists (`creatures: [skeleton, zombie]`) for handbook references
# and seed attacks. Anything outside this schema fails loudly.
SUMMON_INT_KEYS = {"durationRounds", "durationDays"}
SUMMON_TABLE_KEYS = {"countAtSlotLevel", "retainCountAtSlotLevel"}
SUMMON_CHOICE_TABLE_KEYS = {"countChoicesAtSlotLevel"}
SUMMON_SEED_INT_KEYS = {"hp", "defense"}
SUMMON_CAP_INT_KEYS = {"maxCreatures", "maxHitDice", "hitDicePerCasterLevel"}
VALID_DISPOSITIONS = {"loyal", "neutral", "hostile"}


def _parse_inline_str_list(text: str, where: str) -> list[str]:
    text = text.strip()
    if not (text.startswith("[") and text.endswith("]")):
        raise ValueError(f"{where}: expected an inline list like [a, b], got {text!r}")
    items: list[str] = []
    current: list[str] = []
    in_single = in_double = False
    for ch in text[1:-1]:
        if ch == "'" and not in_double:
            in_single = not in_single
            current.append(ch)
        elif ch == '"' and not in_single:
            in_double = not in_double
            current.append(ch)
        elif ch == "," and not in_single and not in_double:
            items.append("".join(current).strip())
            current = []
        else:
            current.append(ch)
    tail = "".join(current).strip()
    if tail:
        items.append(tail)
    result = []
    for item in items:
        if len(item) >= 2 and item[0] == item[-1] and item[0] in ("'", '"'):
            item = item[1:-1]
        if not item:
            raise ValueError(f"{where}: empty entry in inline list")
        result.append(item)
    if not result:
        raise ValueError(f"{where}: inline list must not be empty")
    return result


def _parse_inline_int_list(text: str, where: str) -> list[int]:
    try:
        return [int(item) for item in _parse_inline_str_list(text, where)]
    except ValueError:
        raise ValueError(f"{where}: expected an inline int list like [1, 2], got {text!r}") from None


def load_summon_overlay() -> dict[str, dict]:
    """Parse scripts/spell_summon_overlay.yaml into {slug: {'summon': {...}}}."""
    return load_summon_overlay_file(SUMMON_OVERLAY_PATH, DND5E_DIR.parent / "creatures", 9)


def load_summon_overlay_file(path, creatures_dir, max_slot: int) -> dict[str, dict]:
    """Parse a summon overlay file into {slug: {'summon': {...}}}.

    path: overlay YAML; creatures_dir: handbook dir creature refs must exist in;
    max_slot: highest legal table level (9 for dnd5e slots, 10 for pf2e ranks).
    """
    if not path.exists():
        return {}
    overlay: dict[str, dict] = {}
    slug: str | None = None
    section: str | None = None
    name = path.name
    for lineno, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = _strip_overlay_comment(raw).rstrip()
        if not line.strip():
            continue
        indent = len(line) - len(line.lstrip(" "))
        if indent % 2 or indent > 4:
            raise ValueError(f"{name}:{lineno}: bad indent (use 2 spaces/level, max 3 levels)")
        content = line.strip()
        where = f"{name}:{lineno}"
        if indent == 0:
            if not content.endswith(":"):
                raise ValueError(f"{where}: expected 'slug:'")
            slug = content[:-1].strip()
            if not slug or slug in overlay:
                raise ValueError(f"{where}: bad or duplicate slug {slug!r}")
            overlay[slug] = {"summon": {}}
            section = None
        elif indent == 2:
            if slug is None:
                raise ValueError(f"{where}: entry before any slug")
            key, _, value = content.partition(":")
            key, value = key.strip(), value.strip()
            section = None
            summon = overlay[slug]["summon"]
            if key == "creatures":
                summon[key] = _parse_inline_str_list(value, where)
            elif key in SUMMON_INT_KEYS:
                summon[key] = _parse_overlay_scalar(value, "int", where)
            elif key == "disposition":
                summon[key] = _parse_overlay_scalar(value, "str", where)
            elif key in SUMMON_TABLE_KEYS | SUMMON_CHOICE_TABLE_KEYS | {"inlineSeed", "controlCap"}:
                if value:
                    raise ValueError(f"{where}: {key!r} takes nested entries, not an inline value")
                summon[key] = {}
                section = key
            else:
                raise ValueError(f"{where}: unknown summon key {key!r}")
        else:  # indent == 4
            if slug is None or section is None:
                raise ValueError(f"{where}: nested entry outside a mapping")
            key, _, value = content.partition(":")
            key, value = key.strip(), value.strip()
            summon = overlay[slug]["summon"]
            if section in SUMMON_TABLE_KEYS:
                summon[section][_parse_overlay_scalar(key, "int", where)] = (
                    _parse_overlay_scalar(value, "int", where))
            elif section in SUMMON_CHOICE_TABLE_KEYS:
                summon[section][_parse_overlay_scalar(key, "int", where)] = (
                    _parse_inline_int_list(value, where))
            elif section == "inlineSeed":
                if key in SUMMON_SEED_INT_KEYS:
                    summon[section][key] = _parse_overlay_scalar(value, "int", where)
                elif key == "attacks":
                    summon[section][key] = _parse_inline_str_list(value, where)
                else:
                    raise ValueError(f"{where}: unknown inlineSeed key {key!r}")
            elif section == "controlCap":
                if key not in SUMMON_CAP_INT_KEYS:
                    raise ValueError(f"{where}: unknown controlCap key {key!r}")
                summon[section][key] = _parse_overlay_scalar(value, "int", where)
            else:
                raise ValueError(f"{where}: nested entry under scalar {section!r}")
    for entry_slug, entry in overlay.items():
        summon = entry["summon"]
        if not summon.get("creatures") and not summon.get("inlineSeed"):
            raise ValueError(f"{entry_slug}: summon needs creatures or inlineSeed")
        if summon.get("disposition") not in VALID_DISPOSITIONS:
            raise ValueError(f"{entry_slug}: disposition must be one of {sorted(VALID_DISPOSITIONS)}")
        for table_key in SUMMON_TABLE_KEYS:
            for level, count in summon.get(table_key, {}).items():
                if not 1 <= level <= max_slot:
                    raise ValueError(f"{entry_slug}: {table_key} level {level} outside 1-{max_slot}")
                if count < 1:
                    raise ValueError(f"{entry_slug}: {table_key} counts must be >= 1")
        for level, options in summon.get("countChoicesAtSlotLevel", {}).items():
            if not 1 <= level <= max_slot:
                raise ValueError(f"{entry_slug}: countChoicesAtSlotLevel level {level} outside 1-{max_slot}")
            if not options or any(o < 1 for o in options):
                raise ValueError(f"{entry_slug}: countChoicesAtSlotLevel options must be non-empty and >= 1")
        durations = [k for k in ("durationRounds", "durationDays") if summon.get(k) is not None]
        if len(durations) > 1:
            raise ValueError(f"{entry_slug}: set at most one of durationRounds/durationDays")
        for key in durations:
            if summon[key] < 1:
                raise ValueError(f"{entry_slug}: {key} must be >= 1")
        for key in SUMMON_CAP_INT_KEYS:
            if summon.get("controlCap", {}).get(key) is not None and summon["controlCap"][key] < 1:
                raise ValueError(f"{entry_slug}: controlCap.{key} must be >= 1")
        for creature in summon.get("creatures", []):
            if not (creatures_dir / f"{creature}.yaml").exists():
                raise ValueError(f"{entry_slug}: creature {creature!r} has no {creatures_dir.parent.name}/creatures YAML")
    return overlay


def apply_summon_overlay(slug: str, body: dict, overlay: dict[str, dict]) -> None:
    entry = overlay.get(slug)
    if not entry:
        return
    if "summon" in body:
        raise ValueError(f"overlay for {slug!r} collides with API-derived key 'summon'")
    body["summon"] = entry["summon"]


def parse_dnd5e_mechanics(detail: dict) -> dict:
    """Pull damage/save/heal/AoE off the already-fetched dnd5eapi.co spell detail JSON.
    Confirmed live: damage is always keyed by slot level for leveled spells, even
    non-scaling ones like Magic Missile, or by character level for cantrips; there's
    no separate flat-dice shape."""
    result: dict = {}

    damage_entries = detail.get("damage") or []
    if damage_entries:
        entry = damage_entries[0]
        damage_type = (entry.get("damage_type") or {}).get("index")
        if damage_type:
            result["damageType"] = damage_type
        by_slot = entry.get("damage_at_slot_level")
        if by_slot:
            result["damageAtSlotLevel"] = {int(k): v for k, v in by_slot.items()}
        by_char = entry.get("damage_at_character_level")
        if by_char:
            result["damageAtCharacterLevel"] = {int(k): v for k, v in by_char.items()}

        if len(damage_entries) > 1:
            # Multi-pool spell (SRD audit 2026-09: exactly ice-storm, meteor-swarm,
            # flame-strike — all additive "takes X and Y" shapes). Emit every pool
            # verbatim, but only when each pool is slot-keyed with roller-parseable
            # dice; anything else (flame-strike's "4d6 OR 5d6") skips API pools and
            # stays overlay-supplied via apply_damage_overlay's no-collision rule.
            pools: dict[str, dict[int, str]] = {}
            for pool_entry in damage_entries:
                pool_type = (pool_entry.get("damage_type") or {}).get("index")
                pool_by_slot = pool_entry.get("damage_at_slot_level")
                if not pool_type or not pool_by_slot or pool_type in pools:
                    pools = {}
                    break
                if not all(isinstance(dice, str) and DICE_VALUE_RE.match(dice)
                           for dice in pool_by_slot.values()):
                    pools = {}
                    break
                pools[pool_type] = {int(k): v for k, v in pool_by_slot.items()}
            if pools:
                result["damagePools"] = pools

    dc = detail.get("dc")
    if dc:
        save_type = (dc.get("dc_type") or {}).get("index")
        if save_type:
            result["saveType"] = save_type
        if dc.get("dc_success"):
            result["saveSuccess"] = dc["dc_success"]

    heal_by_slot = detail.get("heal_at_slot_level")
    if heal_by_slot:
        result["healAtSlotLevel"] = {int(k): v for k, v in heal_by_slot.items()}

    aoe = detail.get("area_of_effect")
    if aoe:
        if aoe.get("type"):
            result["areaOfEffectType"] = aoe["type"]
        if aoe.get("size") is not None:
            result["areaOfEffectSize"] = aoe["size"]

    return result


def generate_dnd5e() -> int:
    index = fetch_json("https://www.dnd5eapi.co/api/spells")
    spells = index["results"]
    DND5E_DIR.mkdir(parents=True, exist_ok=True)
    overlay = load_damage_overlay()
    summon_overlay = load_summon_overlay()

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
            **parse_dnd5e_mechanics(detail),
            # Private: raw multi-entry count for the pools notice below; popped before write.
            "_damageEntryCount": len(detail.get("damage") or []),
        }

    generated: dict[str, dict] = {}
    with ThreadPoolExecutor(max_workers=12) as pool:
        futures = {pool.submit(load_spell, e): e for e in spells}
        for i, future in enumerate(as_completed(futures), 1):
            slug, body = future.result()
            damage_entry_count = body.pop("_damageEntryCount", 0)
            if damage_entry_count > 1 and "damagePools" not in body and "damagePools" not in overlay.get(slug, {}):
                print(f"  dnd5e: {slug} has multi-entry damage but no clean pools and no overlay — flat first-entry only")
            apply_damage_overlay(slug, body, overlay)
            apply_summon_overlay(slug, body, summon_overlay)
            generated[slug] = body
            if i % 50 == 0:
                print(f"  dnd5e: {i}/{len(spells)}")

    unused = sorted(set(overlay) - set(generated))
    if unused:
        raise ValueError(f"overlay entries match no API spell: {', '.join(unused)}")
    unused_summon = sorted(set(summon_overlay) - set(generated))
    if unused_summon:
        raise ValueError(f"summon overlay entries match no API spell: {', '.join(unused_summon)}")

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

    summon_overlay = load_summon_overlay_file(
        SUMMON_PF2E_OVERLAY_PATH, PF2E_DIR.parent / "creatures", 10)
    for slug, body in generated.items():
        apply_summon_overlay(slug, body, summon_overlay)

    unused_summon = sorted(set(summon_overlay) - set(generated))
    if unused_summon:
        raise ValueError(f"pf2e summon overlay entries match no AoN spell: {', '.join(unused_summon)}")

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
