#!/usr/bin/env bash
# Sets up an opencode environment for a Campaign Vault campaign:
#   - Extracts the fenced system-prompt block from recommended-system-prompt.md,
#     fills in campaign-specific placeholders, and writes it to <target>/AGENTS.md
#   - Copies the dnd-* skills into <target>/.opencode/skills/<name>/SKILL.md
#   - Registers (or prints) the Campaign Vault MCP server entry for opencode.json
#
# Usage:
#   setup-opencode.sh <target-dir> [--slug SLUG] [--ruleset Dnd5e|Pf2e] \
#       [--roster "chars/id1 - Name1, chars/id2 - Name2"] [--mcp-port 5275] [--force]
#
# Any of --slug/--ruleset/--roster omitted are prompted for interactively.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROMPT_SRC="$REPO_ROOT/recommended-system-prompt.opencode.md"
if [[ ! -f "$PROMPT_SRC" ]]; then
  echo "WARNING: recommended-system-prompt.opencode.md not found — falling back to the generic recommended-system-prompt.md (it won't mention the campaign-vault plugin's mechanical enforcement)." >&2
  PROMPT_SRC="$REPO_ROOT/recommended-system-prompt.md"
fi
SKILLS_SRC="$REPO_ROOT/claude_skills"
PLUGIN_SRC_DIR="$REPO_ROOT/opencode-plugin"

SLUG=""
RULESET=""
ROSTER=""
MCP_PORT="5275"
FORCE=0
TARGET_DIR=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --slug) SLUG="$2"; shift 2 ;;
    --ruleset) RULESET="$2"; shift 2 ;;
    --roster) ROSTER="$2"; shift 2 ;;
    --mcp-port) MCP_PORT="$2"; shift 2 ;;
    --force) FORCE=1; shift ;;
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

if [[ -z "$TARGET_DIR" ]]; then
  read -r -p "Target campaign directory (will be created if missing): " TARGET_DIR
fi
[[ -z "$TARGET_DIR" ]] && { echo "A target directory is required." >&2; exit 1; }
mkdir -p "$TARGET_DIR"
TARGET_DIR="$(cd "$TARGET_DIR" && pwd)"

if [[ -z "$SLUG" ]]; then
  read -r -p "Campaign slug (campaignName, e.g. my-campaign): " SLUG
fi
if [[ -z "$RULESET" ]]; then
  read -r -p "Ruleset [Dnd5e|Pf2e] (default Dnd5e): " RULESET
  RULESET="${RULESET:-Dnd5e}"
fi
if [[ -z "$ROSTER" ]]; then
  read -r -p "PC roster, e.g. 'chars/abdel - Abdel, chars/nia - Nia': " ROSTER
fi
[[ -z "$SLUG" ]] && { echo "A campaign slug is required." >&2; exit 1; }
[[ -z "$ROSTER" ]] && { echo "A PC roster is required." >&2; exit 1; }

[[ -f "$PROMPT_SRC" ]] || { echo "Cannot find $PROMPT_SRC — is this script still inside the CampaignVault repo?" >&2; exit 1; }

echo "== Writing AGENTS.md =="
FENCE="$(awk '/^```text$/{f=1;next} /^```$/{f=0} f' "$PROMPT_SRC")"

CAMPAIGN_LINE="**CAMPAIGN:** campaignName=\"${SLUG}\" — always use this exact value on every campaign-scoped call, never ask the player or re-derive it. PC roster: ${ROSTER} — use these ids as characterId on their checks/actions. Ruleset: ${RULESET}."

AGENTS_PATH="$TARGET_DIR/AGENTS.md"
if [[ -f "$AGENTS_PATH" && $FORCE -eq 0 ]]; then
  BACKUP="$AGENTS_PATH.bak.$(date +%Y%m%d%H%M%S)"
  cp "$AGENTS_PATH" "$BACKUP"
  echo "  existing AGENTS.md backed up to $(basename "$BACKUP")"
fi

printf '%s\n' "$FENCE" | awk -v line="$CAMPAIGN_LINE" '
  /^\*\*CAMPAIGN:\*\* campaignName=/ { print line; next }
  { print }
' > "$AGENTS_PATH"
echo "  wrote $AGENTS_PATH"

echo "== Copying skills =="
SKILLS_DEST="$TARGET_DIR/.opencode/skills"
mkdir -p "$SKILLS_DEST"
for d in "$SKILLS_SRC"/dnd-*; do
  name="$(basename "$d")"
  rm -rf "${SKILLS_DEST:?}/$name"
  cp -R "$d" "$SKILLS_DEST/$name"
  echo "  $name"
done
echo "  (note: opencode also reads .claude/skills/ natively — this copy is convenience, not required)"

echo "== Building and installing the campaign-vault plugin =="
PLUGIN_DEST_REL=".opencode/plugin/campaign-vault.js"
if [[ -d "$PLUGIN_SRC_DIR" ]]; then
  if [[ ! -d "$PLUGIN_SRC_DIR/node_modules" ]]; then
    echo "  installing plugin dependencies (npm install)..."
    (cd "$PLUGIN_SRC_DIR" && npm install)
  fi
  echo "  building plugin (npm run build)..."
  (cd "$PLUGIN_SRC_DIR" && npm run build)
  PLUGIN_DEST="$TARGET_DIR/$PLUGIN_DEST_REL"
  mkdir -p "$(dirname "$PLUGIN_DEST")"
  cp "$PLUGIN_SRC_DIR/dist/index.js" "$PLUGIN_DEST"
  echo "  copied dist/index.js -> $PLUGIN_DEST"
else
  echo "  WARNING: $PLUGIN_SRC_DIR not found — skipping plugin build/install. opencode.json will not reference a plugin."
  PLUGIN_DEST_REL=""
fi

echo "== MCP server + plugin registration =="
OPENCODE_JSON="$TARGET_DIR/opencode.json"
MCP_SNIPPET=$(cat <<EOF
  "mcp": {
    "campaign-vault": {
      "type": "remote",
      "url": "http://localhost:${MCP_PORT}",
      "enabled": true
    }
  }
EOF
)
if [[ -n "$PLUGIN_DEST_REL" ]]; then
  PLUGIN_SNIPPET="  \"plugin\": [\"file://${PLUGIN_DEST_REL}\"]"
else
  PLUGIN_SNIPPET=""
fi

if [[ -f "$OPENCODE_JSON" ]]; then
  echo "  opencode.json already exists at $OPENCODE_JSON — not overwriting."
  echo "  Add/merge these blocks manually (adjust if you already have \"mcp\"/\"plugin\" keys):"
  echo "$MCP_SNIPPET"
  [[ -n "$PLUGIN_SNIPPET" ]] && echo "$PLUGIN_SNIPPET"
else
  if [[ -n "$PLUGIN_SNIPPET" ]]; then
    cat > "$OPENCODE_JSON" <<EOF
{
$MCP_SNIPPET,
$PLUGIN_SNIPPET
}
EOF
  else
    cat > "$OPENCODE_JSON" <<EOF
{
$MCP_SNIPPET
}
EOF
  fi
  echo "  wrote $OPENCODE_JSON"
fi
echo "  NOTE: this assumes the Campaign Vault MCP server (this repo, MCP_PORT=${MCP_PORT}) is already running via 'dotnet run' — start it separately."

echo
echo "Done. Campaign '$SLUG' wired up in $TARGET_DIR."
