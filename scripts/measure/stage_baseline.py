#!/usr/bin/env python3
"""Stage an older commit's DM content (and optionally its server) into this tree's Unity client.

The Unity client's MEASURE panel (Settings -> MEASURE) only exists in the current tree, so a baseline of the old
guidance cannot be run from an old checkout. Instead the old *content* is put into the current client:

    python3 scripts/measure/stage_baseline.py --ref 555e22a1 --server    # the old prompts, skills and server
    ... run Settings -> MEASURE (remedies off for the storyteller) ...
    python3 scripts/measure/stage_baseline.py --ref WORKTREE --server    # back to the files on disk

What is staged, exactly as CampaignVault -> Stage DM Prompt + Skills does it (DmContentStager.cs):
  recommended-system-prompt.md           -> system-prompt.md            (only the ```text fence, template CAMPAIGN line dropped)
  recommended-system-prompt.narrative.md -> system-prompt.narrative.md
  claude_skills/NAME/SKILL.md            -> skills/NAME/SKILL.md
With --server the server for that ref is published (tools/embed-server.sh) and replaces Server/<rid>. Old refs are built in a
temporary `git worktree`, which is removed afterwards. WORKTREE means "the files on disk now".

Plugin skills (plugin-skills/) are not touched. The staged files are git-ignored copies.
"""
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
STREAMING = ROOT / "UnityClient" / "Assets" / "StreamingAssets" / "CampaignVault"
PROMPTS = [
    ("recommended-system-prompt.md", "system-prompt.md"),
    ("recommended-system-prompt.narrative.md", "system-prompt.narrative.md"),
]


def extract_prompt_body(markdown: str) -> str:
    """SystemPromptProvider.ExtractPromptBody: the ```text fence, without CAMPAIGN lines, blank runs collapsed."""
    lines = markdown.replace("\r\n", "\n").split("\n")
    open_at = close_at = -1
    for i, line in enumerate(lines):
        t = line.strip()
        if open_at < 0 and t == "```text":
            open_at = i
            continue
        if open_at >= 0 and t == "```":
            close_at = i
            break
    start, end = (open_at + 1, close_at) if open_at >= 0 and close_at > open_at else (0, len(lines))
    out: list[str] = []
    last_blank = True
    for line in lines[start:end]:
        t = line.strip()
        if t.startswith("CAMPAIGN:") or t.startswith("**CAMPAIGN:**"):
            continue
        blank = len(t) == 0
        if blank and last_blank:
            continue
        out.append(line.rstrip() + "\n")
        last_blank = blank
    # The C# method trims the result; the stager then appends one newline.
    return "".join(out).strip()


def git(*args: str, cwd: Path = ROOT) -> str:
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout


class Source:
    """Files of one ref, or of the working tree."""

    def __init__(self, ref: str):
        self.ref = ref

    def read(self, path: str) -> str | None:
        if self.ref == "WORKTREE":
            p = ROOT / path
            return p.read_text(encoding="utf-8") if p.is_file() else None
        try:
            return git("show", f"{self.ref}:{path}")
        except subprocess.CalledProcessError:
            return None

    def skills(self) -> dict[str, str]:
        found: dict[str, str] = {}
        if self.ref == "WORKTREE":
            for d in sorted((ROOT / "claude_skills").iterdir()):
                f = d / "SKILL.md"
                if d.is_dir() and f.is_file():
                    found[d.name] = f.read_text(encoding="utf-8")
            return found
        for name in git("ls-tree", "--name-only", f"{self.ref}:claude_skills").split():
            text = self.read(f"claude_skills/{name}/SKILL.md")
            if text is not None:
                found[name] = text
        return found


def stage_content(src: Source, out: Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    for source_name, target_name in PROMPTS:
        text = src.read(source_name)
        target = out / target_name
        if text is None:
            target.unlink(missing_ok=True)
            continue
        target.write_text(extract_prompt_body(text) + "\n", encoding="utf-8")
    skills_dir = out / "skills"
    if skills_dir.exists():
        shutil.rmtree(skills_dir)
    skills = src.skills()
    for name, text in skills.items():
        (skills_dir / name).mkdir(parents=True, exist_ok=True)
        (skills_dir / name / "SKILL.md").write_text(text, encoding="utf-8")
    print(f"staged {len(PROMPTS)} prompts and {len(skills)} skills from {src.ref} into {out}")


def stage_server(src: Source, out: Path, rid: str) -> None:
    if src.ref == "WORKTREE":
        subprocess.run([str(ROOT / "tools" / "embed-server.sh"), rid], cwd=ROOT, check=True)
        return
    # Resolved: on macOS /var is a symlink to /private/var, and MSBuild globs then see every source twice (duplicate resources).
    worktree = Path(tempfile.mkdtemp(prefix="cv-baseline-")).resolve()
    try:
        git("worktree", "add", "--detach", str(worktree), src.ref)
        subprocess.run([str(worktree / "tools" / "embed-server.sh"), rid], cwd=worktree, check=True)
        built = worktree / "UnityClient" / "Assets" / "StreamingAssets" / "CampaignVault" / "Server" / rid
        if not built.is_dir():
            sys.exit(f"the server for {src.ref} was not staged at {built}")
        target = out / "Server" / rid
        if target.exists():
            shutil.rmtree(target)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(built, target, symlinks=True)
        print(f"staged the {src.ref} server into {target}")
    finally:
        subprocess.run(["git", "worktree", "remove", "--force", str(worktree)], cwd=ROOT, capture_output=True)
        shutil.rmtree(worktree, ignore_errors=True)


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ref", required=True, help="a git ref (e.g. 555e22a1), or WORKTREE for the files on disk")
    ap.add_argument("--server", action="store_true", help="also publish and stage that ref's server (tools/embed-server.sh)")
    ap.add_argument("--rid", default="osx-arm64", help="runtime id for --server (default osx-arm64)")
    ap.add_argument("--out", default=str(STREAMING), help="where to stage (default: the client's StreamingAssets/CampaignVault)")
    args = ap.parse_args(argv)
    src = Source(args.ref)
    out = Path(args.out)
    stage_content(src, out)
    if args.server:
        stage_server(src, out, args.rid)
    if args.ref != "WORKTREE":
        print("\nA baseline is staged. Run Settings -> MEASURE, then put things back with:\n"
              "  python3 scripts/measure/stage_baseline.py --ref WORKTREE --server")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
