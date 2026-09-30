#!/usr/bin/env python3
"""Pack the Grok Web kit: project instructions plus the knowledge files, with stable names.

Grok Web projects take two things: the project *instructions* (pasted into a text box) and
*knowledge files* (uploaded). This script builds both from the repo, so the Grok set can't drift
from the prompts and skills the other clients use:

    project-prompt.txt     recommended-system-prompt(.narrative).md's ```text body, with the
                           CAMPAIGN line filled in, followed by grok/project-prompt.md
    00-style-anchor.md     grok/style-anchor.md
    skill-<name>.md        claude_skills/<name>/SKILL.md, or grok/skills/<name>.md when a
                           Grok-specific variant exists

File names never change between packs, so each one replaces the Grok file of the same name.
MANIFEST.json keeps a hash per file; every run compares with the previous pack and prints only
what changed. changed/ holds just those files, ready to drag in; grok-kit.zip holds the full set.

Usage:
    scripts/pack-grok.sh [--campaign <slug>] [--pcs "chars/id — Name, ..."] [--ruleset dnd5e|pf2e|narrative]
                         [--copy] [--dry-run] [--out dist/grok] [--skills-dir claude_skills]
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
FORMAT = "campaignvault-grok-kit/1"
PROMPT_FILE = "project-prompt.txt"
RULESETS = {
    "dnd5e": ("recommended-system-prompt.md", "Dnd5e"),
    "pf2e": ("recommended-system-prompt.md", "Pf2e"),
    "narrative": ("recommended-system-prompt.narrative.md", "Narrative"),
}
# Fixed timestamp inside the zip, so an unchanged kit zips to identical bytes.
ZIP_TIME = (2020, 1, 1, 0, 0, 0)


def extract_prompt_body(markdown: str) -> str:
    """The ```text fence of a recommended-system-prompt*.md, like the Unity client's ExtractPromptBody."""
    start = markdown.find("```text")
    if start < 0:
        return markdown.strip()
    start = markdown.find("\n", start) + 1
    end = markdown.find("```", start)
    return (markdown[start:end] if end > 0 else markdown[start:]).strip()


def fill_campaign_line(body: str, campaign: str | None, pcs: str | None, ruleset_label: str) -> tuple[str, bool]:
    """Replace the template CAMPAIGN line. Returns (text, filled)."""
    lines = body.split("\n")
    for i, line in enumerate(lines):
        if line.startswith("CAMPAIGN:"):
            if not campaign:
                return body, False
            roster = pcs or "<chars/id — Name, ...>"
            lines[i] = f'CAMPAIGN: campaignName="{campaign}" on every call | PCs: {roster} | Ruleset: {ruleset_label}'
            return "\n".join(lines), True
    return body, False


def build_kit(skills_dir: Path, ruleset: str, campaign: str | None, pcs: str | None) -> tuple[dict[str, bytes], str, bool]:
    source, label = RULESETS[ruleset]
    body = extract_prompt_body((REPO / source).read_text(encoding="utf-8"))
    body, filled = fill_campaign_line(body, campaign, pcs, label)
    addendum = (REPO / "grok" / "project-prompt.md").read_text(encoding="utf-8").strip()
    prompt = body + "\n\n" + addendum + "\n"

    files: dict[str, bytes] = {"00-style-anchor.md": (REPO / "grok" / "style-anchor.md").read_bytes()}
    overrides = REPO / "grok" / "skills"
    for skill in sorted(p for p in skills_dir.iterdir() if (p / "SKILL.md").is_file()):
        variant = overrides / f"{skill.name}.md"
        files[f"skill-{skill.name}.md"] = (variant if variant.is_file() else skill / "SKILL.md").read_bytes()
    return files, prompt, filled


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_manifest(out: Path) -> dict:
    try:
        return json.loads((out / "MANIFEST.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def copy_to_clipboard(text: str) -> str | None:
    """Returns the tool used, or None when no clipboard tool is available."""
    candidates = [["pbcopy"], ["clip.exe"], ["clip"], ["wl-copy"], ["xclip", "-selection", "clipboard"], ["xsel", "--clipboard", "--input"]]
    for cmd in candidates:
        if shutil.which(cmd[0]):
            try:
                subprocess.run(cmd, input=text.encode("utf-8"), check=True)
                return cmd[0]
            except (OSError, subprocess.CalledProcessError):
                continue
    return None


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description="Pack the Grok Web kit (stable names, changed-files report, zip).")
    ap.add_argument("--campaign", help="campaign slug for the CAMPAIGN line")
    ap.add_argument("--pcs", help='PC roster for the CAMPAIGN line, e.g. "chars/aric — Aric"')
    ap.add_argument("--ruleset", choices=sorted(RULESETS), default="dnd5e")
    ap.add_argument("--copy", action="store_true", help="copy the project instructions to the clipboard")
    ap.add_argument("--dry-run", action="store_true", help="report what would change; write nothing")
    ap.add_argument("--out", default=str(REPO / "dist" / "grok"))
    ap.add_argument("--skills-dir", default=str(REPO / "claude_skills"))
    args = ap.parse_args(argv)

    out = Path(args.out)
    files, prompt, filled = build_kit(Path(args.skills_dir), args.ruleset, args.campaign, args.pcs)
    old = load_manifest(out)
    old_files: dict[str, str] = old.get("files", {})
    new_files = {name: sha(data) for name, data in files.items()}

    added = [n for n in new_files if n not in old_files]
    changed = [n for n in new_files if n in old_files and old_files[n] != new_files[n]]
    removed = [n for n in old_files if n not in new_files]
    prompt_changed = old.get("instructions") != sha(prompt.encode("utf-8"))
    first_pack = not old

    print(f"Grok kit: {len(files)} knowledge files, ruleset {args.ruleset}, instructions {len(prompt):,} chars")
    if not filled:
        print("  note: CAMPAIGN line still has placeholders (pass --campaign and --pcs to fill it)")
    if first_pack:
        print("  first pack here: upload every file")
    elif not (added or changed or removed or prompt_changed):
        print("  nothing changed since the last pack")
    else:
        for n in added:
            print(f"  new      {n}")
        for n in changed:
            print(f"  changed  {n}")
        for n in removed:
            print(f"  removed  {n}   (delete it from the Grok project)")
        if prompt_changed:
            print(f"  changed  project instructions ({PROMPT_FILE})")

    if args.dry_run:
        print("dry run: nothing written")
        return 0

    out.mkdir(parents=True, exist_ok=True)
    for name in set(old_files) | {PROMPT_FILE}:
        (out / name).unlink(missing_ok=True)
    shutil.rmtree(out / "changed", ignore_errors=True)
    to_upload = list(files) if first_pack else added + changed
    if to_upload:
        (out / "changed").mkdir()
    for name, data in files.items():
        (out / name).write_bytes(data)
        if name in to_upload:
            (out / "changed" / name).write_bytes(data)
    (out / PROMPT_FILE).write_text(prompt, encoding="utf-8")

    with zipfile.ZipFile(out / "grok-kit.zip", "w", zipfile.ZIP_DEFLATED) as z:
        for name in sorted(files):
            info = zipfile.ZipInfo(name, ZIP_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, files[name])

    manifest = {
        "format": FORMAT,
        "ruleset": args.ruleset,
        "campaign": args.campaign or "",
        "instructions": sha(prompt.encode("utf-8")),
        "files": dict(sorted(new_files.items())),
    }
    (out / "MANIFEST.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    rel = os.path.relpath(out)
    if rel.startswith(".."):
        rel = str(out.resolve())
    print(f"\nWrote {rel}/ (full set in grok-kit.zip" + (f", {len(to_upload)} to upload in changed/)" if to_upload else ")"))
    if args.copy:
        tool = copy_to_clipboard(prompt)
        print(f"Project instructions copied to the clipboard ({tool})." if tool else "No clipboard tool found; open project-prompt.txt instead.")

    print("\nIn the Grok project:")
    step = 1
    if removed or (to_upload and not first_pack):
        print(f"  {step}. Delete the Grok files named: {', '.join(removed + changed) if (removed or changed) else '(none)'}")
        step += 1
    if to_upload:
        print(f"  {step}. Upload everything in {rel}/changed/")
        step += 1
    if prompt_changed or first_pack:
        how = "paste from the clipboard" if args.copy else f"paste {rel}/{PROMPT_FILE}"
        print(f"  {step}. Replace the project instructions ({how})")
        step += 1
    if step == 1:
        print("  nothing to do")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
