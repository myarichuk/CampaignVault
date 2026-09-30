#!/usr/bin/env python3
"""Prose statistics for DM narration, read from a Unity client transcript export.

Export a transcript from the client (F12 Inspector -> EXPORT TRANSCRIPT); the file
is markdown in the campaignvault-transcript/1 format (one "## Turn N" section per
player message, with "### Player", optional "### Rolls" and "### DM" blocks and a
usage comment line).

Usage:
    python3 scripts/measure/prose_stats.py transcript.md            # per-turn table + totals
    python3 scripts/measure/prose_stats.py before.md after.md       # side-by-side totals
    python3 scripts/measure/prose_stats.py transcript.md --json     # machine-readable

Metrics (per DM block):
    words        words of narration
    paras        paragraphs (blank-line separated)
    sents        sentences
    mean_len     mean words per sentence
    frag         share of sentences under 5 words ("Kit on." "Dome already dead.")
    rolls        inline roll/number lines in the prose ("17 vs DC 14", "Rolled 12")
    prompt/out   provider-reported tokens for the whole turn (all model calls)

The numbers support a judgement about the prose; they don't replace reading it.
"""
from __future__ import annotations

import json
import re
import sys
from dataclasses import dataclass, asdict, field

FRAGMENT_WORDS = 5
SENTENCE_SPLIT = re.compile(r"(?<=[.!?…])[\"”’)\]*_]*\s+")
WORD = re.compile(r"[A-Za-z0-9’'\-]+")
ROLL_LINE = re.compile(r"\b\d+\s+vs\.?\s+(?:DC|AC)\s*\d+\b|\bRolled\s+\d+|\bd20\s*\(|\bnat(?:ural)?\s*(?:1|20)\b", re.I)
USAGE = re.compile(r"<!--\s*usage\s+(.*?)-->")


@dataclass
class Turn:
    index: int
    player: str = ""
    dm: str = ""
    rolls_block: list[str] = field(default_factory=list)
    prompt: int = 0
    completion: int = 0
    cached: int = 0
    calls: int = 0
    tools: int = 0


@dataclass
class Stats:
    words: int
    paras: int
    sents: int
    mean_len: float
    frag: float
    rolls: int


def parse(text: str) -> list[Turn]:
    turns: list[Turn] = []
    current: Turn | None = None
    block = None
    buf: list[str] = []

    def flush():
        if current is None or block is None:
            return
        body = "\n".join(buf).strip()
        if block == "player":
            current.player = body
        elif block == "dm":
            current.dm = "" if body == "(no narration)" else body
        elif block == "rolls":
            current.rolls_block = [l[2:].strip() for l in body.splitlines() if l.startswith("- ")]

    for line in text.splitlines():
        if line.startswith("## Turn "):
            flush()
            block, buf = None, []
            current = Turn(index=int(line.split()[2]))
            turns.append(current)
            continue
        m = USAGE.search(line)
        if m and current is not None and block is None:
            for pair in m.group(1).split():
                if "=" in pair:
                    k, v = pair.split("=", 1)
                    if k in ("prompt", "completion", "cached", "calls", "tools") and v.isdigit():
                        setattr(current, k, int(v))
            continue
        if line.startswith("### ") and current is not None:
            flush()
            buf = []
            name = line[4:].strip().lower()
            block = {"player": "player", "dm": "dm", "rolls": "rolls"}.get(name)
            continue
        if block is not None:
            buf.append(line)
    flush()
    return turns


def sentences(text: str) -> list[str]:
    out = []
    for para in paragraphs(text):
        flat = " ".join(para.split())
        out.extend(s for s in SENTENCE_SPLIT.split(flat) if WORD.search(s))
    return out


def paragraphs(text: str) -> list[str]:
    return [p for p in re.split(r"\n\s*\n", text.strip()) if p.strip()]


def measure(text: str) -> Stats:
    sents = sentences(text)
    lens = [len(WORD.findall(s)) for s in sents]
    words = len(WORD.findall(text))
    return Stats(
        words=words,
        paras=len(paragraphs(text)),
        sents=len(sents),
        mean_len=round(sum(lens) / len(lens), 1) if lens else 0.0,
        frag=round(sum(1 for n in lens if n < FRAGMENT_WORDS) / len(lens), 2) if lens else 0.0,
        rolls=len(ROLL_LINE.findall(text)),
    )


def summarize(turns: list[Turn]) -> dict:
    narrated = [t for t in turns if t.dm]
    all_text = "\n\n".join(t.dm for t in narrated)
    total = measure(all_text) if narrated else Stats(0, 0, 0, 0.0, 0.0, 0)
    n = len(turns) or 1
    return {
        "turns": len(turns),
        "narrated_turns": len(narrated),
        "words_per_turn": round(total.words / (len(narrated) or 1), 1),
        "paras_per_turn": round(total.paras / (len(narrated) or 1), 1),
        "mean_sentence_len": total.mean_len,
        "fragment_ratio": total.frag,
        "inline_roll_mentions": total.rolls,
        "prompt_tokens_per_turn": round(sum(t.prompt for t in turns) / n),
        "cached_tokens_per_turn": round(sum(t.cached for t in turns) / n),
        "completion_tokens_per_turn": round(sum(t.completion for t in turns) / n),
        "model_calls_per_turn": round(sum(t.calls for t in turns) / n, 1),
        "tool_calls_per_turn": round(sum(t.tools for t in turns) / n, 1),
    }


def load(path: str) -> list[Turn]:
    with open(path, encoding="utf-8") as f:
        text = f.read()
    turns = parse(text)
    if not turns:
        sys.exit(f"{path}: no '## Turn' sections (is this a campaignvault-transcript/1 export?)")
    return turns


def print_turns(turns: list[Turn]) -> None:
    head = f"{'turn':>4} {'words':>6} {'paras':>5} {'sents':>5} {'mean':>5} {'frag':>5} {'rolls':>5} {'prompt':>7} {'out':>5}  player"
    print(head)
    print("-" * len(head))
    for t in turns:
        s = measure(t.dm)
        player = " ".join(t.player.split())[:40]
        print(f"{t.index:>4} {s.words:>6} {s.paras:>5} {s.sents:>5} {s.mean_len:>5} {s.frag:>5} {s.rolls:>5} {t.prompt:>7} {t.completion:>5}  {player}")


def main(argv: list[str]) -> int:
    as_json = "--json" in argv
    paths = [a for a in argv if not a.startswith("--")]
    if not paths or len(paths) > 2:
        print(__doc__)
        return 2
    runs = [(p, load(p)) for p in paths]
    if as_json:
        out = {p: {"summary": summarize(t), "turns": [dict(index=x.index, **asdict(measure(x.dm)), prompt=x.prompt, completion=x.completion) for x in t]} for p, t in runs}
        print(json.dumps(out, indent=2))
        return 0
    if len(runs) == 1:
        path, turns = runs[0]
        print(path)
        print_turns(turns)
        print()
        for k, v in summarize(turns).items():
            print(f"{k:>28}: {v}")
        return 0
    (pa, ta), (pb, tb) = runs
    sa, sb = summarize(ta), summarize(tb)
    print(f"{'metric':>28}  {'before':>10}  {'after':>10}")
    for k in sa:
        print(f"{k:>28}  {sa[k]:>10}  {sb[k]:>10}")
    print(f"\nbefore = {pa}\nafter  = {pb}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
