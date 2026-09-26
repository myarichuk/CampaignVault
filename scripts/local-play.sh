#!/usr/bin/env bash
# Zero-effort local play for CampaignVault + opencode.
#
# One command: starts the local server (open localhost, no token) and wires an
# opencode target dir (AGENTS.md + skills + plugin + opencode.json).
# Then just: cd <dir> && opencode
#
# Usage:
#   scripts/local-play.sh [--slug SLUG] [--ruleset Dnd5e|Pf2e]
#       [--roster "chars/id - Name, ..."] [--dir TARGET_DIR] [--port PORT]
#       [--setup-only] [--foreground] [--stop] [--force]
#
#   --setup-only   Only wire the opencode target; assume the server is already
#                  running (skips `dotnet run` entirely).
#   --foreground   Run the server in the foreground (logs to terminal) instead
#                  of backgrounding it. Setup still runs first.
#   --stop         Stop the background server started by this script, then exit.
#   --force        Pass through to setup-opencode.sh (overwrite AGENTS.md
#                  without backup).
#
# Defaults are deliberately non-interactive: slug=local-play, ruleset=Dnd5e,
# roster="chars/hero - Hero", dir=$HOME/campaign-play, port=5275.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SETUP_SCRIPT="$REPO_ROOT/scripts/setup-opencode.sh"

SLUG="local-play"
RULESET="Dnd5e"
ROSTER="chars/hero - Hero"
TARGET_DIR="$HOME/campaign-play"
MCP_PORT="5275"
SETUP_ONLY=0
FOREGROUND=0
STOP=0
FORCE=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --slug) SLUG="$2"; shift 2 ;;
    --ruleset) RULESET="$2"; shift 2 ;;
    --roster) ROSTER="$2"; shift 2 ;;
    --dir) TARGET_DIR="$2"; shift 2 ;;
    --port) MCP_PORT="$2"; shift 2 ;;
    --setup-only) SETUP_ONLY=1; shift ;;
    --foreground) FOREGROUND=1; shift ;;
    --stop) STOP=1; shift ;;
    --force) FORCE=1; shift ;;
    -h|--help)
      grep '^#' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      echo "Unknown argument: $1 (see --help)" >&2; exit 1 ;;
  esac
done

DATA_DIR="$REPO_ROOT/.local-data"
PID_FILE="$DATA_DIR/server.pid"
LOG_FILE="$DATA_DIR/server.log"
mkdir -p "$DATA_DIR"

if [[ $STOP -eq 1 ]]; then
  if [[ -f "$PID_FILE" ]]; then
    PID="$(cat "$PID_FILE")"
    if kill -0 "$PID" 2>/dev/null; then
      kill "$PID"
      echo "Stopped local CampaignVault server (pid $PID)."
    else
      echo "No running server for pid $PID; removing stale pid file."
    fi
    rm -f "$PID_FILE"
  else
    echo "No pid file at $PID_FILE — nothing to stop."
  fi
  exit 0
fi

command -v dotnet >/dev/null || { echo "dotnet SDK not found — install .NET 10 SDK first." >&2; exit 1; }
command -v curl >/dev/null || { echo "curl not found — install curl first." >&2; exit 1; }

health_ok() {
  curl -sf "http://localhost:${MCP_PORT}/health" >/dev/null 2>&1
}

if [[ $SETUP_ONLY -eq 0 ]]; then
  if health_ok; then
    echo "Server already healthy at http://localhost:${MCP_PORT} — reusing it."
  else
    echo "== Starting local CampaignVault server (open localhost, no token) =="
    echo "  port:    $MCP_PORT"
    echo "  db path: $DATA_DIR"
    echo "  log:     $LOG_FILE"
    if [[ $FOREGROUND -eq 1 ]]; then
      echo "  mode:    foreground (Ctrl-C to stop)"
      echo
      CAMPAIGN_DB_PATH="$DATA_DIR" MCP_PORT="$MCP_PORT" MCP_BIND_ANY=0 \
        ASPNETCORE_ENVIRONMENT=Development \
        dotnet run --project "$REPO_ROOT/src/CampaignVault" --no-launch-profile
      exit 0
    fi
    # Background: this script is run by the user in their own shell, so a
    # plain background redirect is the portable choice (no setsid on macOS).
    CAMPAIGN_DB_PATH="$DATA_DIR" MCP_PORT="$MCP_PORT" MCP_BIND_ANY=0 \
      ASPNETCORE_ENVIRONMENT=Development \
      nohup dotnet run --project "$REPO_ROOT/src/CampaignVault" --no-launch-profile \
      >"$LOG_FILE" 2>&1 &
    echo $! > "$PID_FILE"
    echo "  pid:     $(cat "$PID_FILE")"
    echo "Waiting for /health ..."
    for _ in $(seq 1 60); do
      if health_ok; then break; fi
      sleep 2
    done
    if ! health_ok; then
      echo "Server did not become healthy within ~120s. Tail of $LOG_FILE:" >&2
      tail -n 30 "$LOG_FILE" >&2 || true
      exit 1
    fi
    echo "  healthy: http://localhost:${MCP_PORT}/health"
  fi
else
  echo "== Setup-only mode: skipping server start =="
fi

echo
echo "== Wiring opencode target =="
SETUP_ARGS=("$TARGET_DIR" --slug "$SLUG" --ruleset "$RULESET" --roster "$ROSTER" --mcp-port "$MCP_PORT")
if [[ $FORCE -eq 1 ]]; then SETUP_ARGS+=(--force); fi
"$SETUP_SCRIPT" "${SETUP_ARGS[@]}"

echo
echo "Done. Local play is ready:"
echo "  server:  http://localhost:${MCP_PORT}  (health: /health, play: /play, build: /build)"
echo "  target:  $TARGET_DIR"
echo "  next:    cd \"$TARGET_DIR\" && opencode"
echo "  seed:    /build create_campaign slug=\"$SLUG\" ruleset=$RULESET, then /world_build"
if [[ $SETUP_ONLY -eq 0 && $FOREGROUND -eq 0 ]]; then
  echo "  stop:    scripts/local-play.sh --stop  (log: $LOG_FILE)"
fi
