#!/usr/bin/env bash
# Expose a running local CampaignVault through ngrok, with only /play and /build reachable.
#
# usage: scripts/tunnel.sh
#   MCP_PORT     local server port (default 5275)
#   NGROK_URL    optional reserved domain, e.g. https://my-vault.ngrok-free.app (stable connector URLs)
#   BEARER_TOKEN the token the server runs with; only used to print ready-to-paste connector URLs
#   ALLOW_OPEN=1 tunnel a server that has no auth (don't)
#
# Start the server first, with auth, e.g.:
#   BEARER_TOKEN=$(openssl rand -hex 24) dotnet run -c Debug --project src/CampaignVault/
set -euo pipefail

PORT="${MCP_PORT:-5275}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
POLICY="$ROOT/ngrok/traffic-policy.yml"
LOCAL="http://localhost:$PORT"

command -v ngrok >/dev/null || { echo "ngrok not found: brew install ngrok, then ngrok config add-authtoken <token>" >&2; exit 1; }
ngrok config check >/dev/null 2>&1 || { echo "ngrok is not configured: ngrok config add-authtoken <token>" >&2; exit 1; }

if ! curl -sf -m 3 "$LOCAL/health" >/dev/null; then
  echo "No CampaignVault on $LOCAL (GET /health failed). Start it first." >&2
  exit 1
fi

# A tunnel is public. Refuse to expose a server that answers MCP without a token.
status=$(curl -s -m 5 -o /dev/null -w '%{http_code}' -X POST "$LOCAL/play" \
  -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"ping"}')
if [ "$status" != "401" ] && [ "${ALLOW_OPEN:-}" != "1" ]; then
  echo "Refusing: $LOCAL/play answered $status without a token, so the tunnel would be open to anyone." >&2
  echo "Restart the server with BEARER_TOKEN set (or ALLOW_OPEN=1 if you really mean it)." >&2
  exit 1
fi

args=(http "$PORT" --traffic-policy-file "$POLICY" --log stdout --log-level warn)
[ -n "${NGROK_URL:-}" ] && args+=(--url "$NGROK_URL")

ngrok "${args[@]}" &
NGROK_PID=$!
trap 'kill $NGROK_PID 2>/dev/null' EXIT INT TERM

# Read the public URL from the agent's local API.
public=""
for _ in $(seq 1 20); do
  public=$(curl -s -m 1 http://127.0.0.1:4040/api/tunnels 2>/dev/null \
    | python3 -c 'import json,sys; t=json.load(sys.stdin)["tunnels"]; print(next(x["public_url"] for x in t if x["public_url"].startswith("https")))' 2>/dev/null || true)
  [ -n "$public" ] && break
  kill -0 $NGROK_PID 2>/dev/null || { echo "ngrok exited; see its output above." >&2; exit 1; }
  sleep 0.5
done
[ -n "$public" ] || { echo "ngrok started but no public URL appeared on :4040." >&2; exit 1; }

# Build the optional lines outside the heredoc: macOS bash 3.2 misparses quotes inside ${..:+..} there.
suffix=""
token_note=""
if [ -n "${BEARER_TOKEN:-}" ]; then
  suffix="?token=$BEARER_TOKEN"
  token_note="  (?token= is for clients that can't set headers, e.g. Grok Web; prefer Authorization: Bearer)"
fi
cat <<EOF

CampaignVault is public at $public (only /play, /build, /health pass; / is blocked)
  play connector : $public/play$suffix
  build connector: $public/build$suffix
$token_note
Ctrl-C stops the tunnel; the server keeps running.
EOF

wait $NGROK_PID
