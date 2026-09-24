#!/usr/bin/env bash
# Deploys this repo's claude_skills/* into a target campaign directory's skills folder,
# WITHOUT touching anything else under that target (no AGENTS.md, no MCP config, no plugin —
# see setup-opencode.sh for the full environment bootstrap).
#
# Only manages entries that belong to this repo's skill set (by name). Any other subdirectory
# already present under the destination skills folder (a skill the user added themselves, or one
# from a different source) is left alone, always — pruning only ever removes a stale copy of a
# skill this repo currently ships or has previously deployed and recorded in the manifest below.
#
# Usage:
#   deploy-skills.sh <target-dir> [--opencode] [--prune] [--dry-run] [--only name1,name2,...]
#
#   <target-dir>   Campaign/vault directory to deploy into, e.g. ~/Roleplaying
#   --opencode     Deploy to <target-dir>/.opencode/skills instead of the default
#                  <target-dir>/.claude/skills
#   --prune        Remove destination skill dirs that this repo previously deployed (tracked in
#                  .deployed-skills.manifest inside the destination) but no longer ships. Without
#                  this flag, stale entries are just reported, never deleted.
#   --dry-run      Print what would happen; touch nothing.
#   --only LIST    Comma-separated subset of skill names to deploy (default: all of claude_skills/*).
#
# Example:
#   scripts/deploy-skills.sh ~/Roleplaying --prune
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SKILLS_SRC="$REPO_ROOT/claude_skills"

TARGET_DIR=""
DEST_SUBPATH=".claude/skills"
PRUNE=0
DRY_RUN=0
ONLY=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --opencode) DEST_SUBPATH=".opencode/skills"; shift ;;
    --prune) PRUNE=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    --only) ONLY="$2"; shift 2 ;;
    -h|--help)
      grep '^#' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      if [[ -z "$TARGET_DIR" ]]; then TARGET_DIR="$1"; shift; else
        echo "Unknown argument: $1" >&2; exit 1
      fi
      ;;
  esac
done

[[ -z "$TARGET_DIR" ]] && { echo "Usage: $0 <target-dir> [--opencode] [--prune] [--dry-run] [--only name1,name2]" >&2; exit 1; }
[[ -d "$SKILLS_SRC" ]] || { echo "Cannot find $SKILLS_SRC — is this script still inside the CampaignVault repo?" >&2; exit 1; }

# Resolve target without requiring it to pre-exist (mirrors setup-opencode.sh's own handling),
# but never silently create a directory tree from a typo'd path outside the intended vault.
if [[ ! -d "$TARGET_DIR" ]]; then
  echo "Target directory '$TARGET_DIR' does not exist." >&2
  exit 1
fi
TARGET_DIR="$(cd "$TARGET_DIR" && pwd)"
DEST_DIR="$TARGET_DIR/$DEST_SUBPATH"
MANIFEST="$DEST_DIR/.deployed-skills.manifest"

ALL_REPO_SKILLS=()
while IFS= read -r name; do
  ALL_REPO_SKILLS+=("$name")
done < <(find "$SKILLS_SRC" -mindepth 1 -maxdepth 1 -type d -exec basename {} \; | sort)
SOURCE_SKILLS=("${ALL_REPO_SKILLS[@]}")

if [[ -n "$ONLY" ]]; then
  IFS=',' read -ra ONLY_LIST <<< "$ONLY"
  FILTERED=()
  for name in "${ONLY_LIST[@]:-}"; do
    if [[ -d "$SKILLS_SRC/$name" ]]; then
      FILTERED+=("$name")
    else
      echo "WARNING: --only requested unknown skill '$name' — skipping." >&2
    fi
  done
  SOURCE_SKILLS=("${FILTERED[@]:-}")
fi

echo "Deploying ${#SOURCE_SKILLS[@]} skill(s) from $SKILLS_SRC -> $DEST_DIR"
[[ $DRY_RUN -eq 1 ]] && echo "(dry run — no files will be written)"

# Read the PRIOR manifest before this run's deploy overwrites it — the stale check below needs
# the "before" list, not the one we're about to write.
PREVIOUSLY_DEPLOYED=()
if [[ -f "$MANIFEST" ]]; then
  while IFS= read -r prev; do
    PREVIOUSLY_DEPLOYED+=("$prev")
  done < "$MANIFEST"
fi

if [[ $DRY_RUN -eq 0 ]]; then
  mkdir -p "$DEST_DIR"
fi

for name in "${SOURCE_SKILLS[@]:-}"; do
  src="$SKILLS_SRC/$name"
  dest="$DEST_DIR/$name"
  if [[ $DRY_RUN -eq 1 ]]; then
    if [[ -d "$dest" ]]; then
      echo "  would update: $name"
    else
      echo "  would add:    $name"
    fi
    continue
  fi
  rm -rf "${dest:?}"
  cp -R "$src" "$dest"
  echo "  $name"
done

# Stale detection: skills this repo deployed previously (per the manifest read above, before this
# run's deploy touched anything) that are no longer shipped by this repo at all — i.e. missing from
# ALL_REPO_SKILLS, NOT just excluded from this run's (possibly --only-filtered) SOURCE_SKILLS. A
# `--only` run must never mark every OTHER previously-deployed skill as stale just because this
# particular call didn't ask to redeploy it. Only ever considers names this script itself wrote to
# the manifest — never a skill directory the user placed there by hand.
STALE=()
for prev in "${PREVIOUSLY_DEPLOYED[@]:-}"; do
  [[ -z "$prev" ]] && continue
  if ! printf '%s\n' "${ALL_REPO_SKILLS[@]:-}" | grep -qxF "$prev"; then
    STALE+=("$prev")
  fi
done

REMAINING_STALE=("${STALE[@]:-}")
if [[ ${#STALE[@]} -gt 0 ]]; then
  if [[ $PRUNE -eq 1 && $DRY_RUN -eq 0 ]]; then
    echo "Pruning stale skill(s) no longer shipped by this repo:"
    REMAINING_STALE=()
    for s in "${STALE[@]:-}"; do
      if [[ -d "$DEST_DIR/$s" ]]; then
        rm -rf "${DEST_DIR:?}/${s:?}"
        echo "  removed: $s"
      fi
    done
  else
    echo "Stale skill(s) previously deployed here but no longer in this repo (re-run with --prune to remove):"
    for s in "${STALE[@]:-}"; do
      echo "  $s"
    done
  fi
fi

# Manifest tracks every skill this repo currently ships (ALL_REPO_SKILLS, not the possibly
# --only-filtered SOURCE_SKILLS — a partial --only run must not make the manifest forget skills it
# simply wasn't asked to redeploy this time) plus any stale name not yet pruned, so a plain
# (non---prune) run never forgets a stale entry before the user gets a chance to prune it.
if [[ $DRY_RUN -eq 0 ]]; then
  { printf '%s\n' "${ALL_REPO_SKILLS[@]:-}"; [[ ${#REMAINING_STALE[@]} -gt 0 ]] && printf '%s\n' "${REMAINING_STALE[@]:-}"; } > "$MANIFEST"
fi

echo "Done."
