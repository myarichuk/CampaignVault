#!/usr/bin/env bash
# Stages a self-contained CampaignVault server build (+ embedding models) into
# the Unity client's StreamingAssets, so player builds ship the MCP server and
# can launch it on localhost. Generated output: never hand-edit, just re-run.
# Player builds do the same through ServerEmbedBuildProcessor (which also
# removes other RIDs' payloads); this script is for play-testing in the editor.
#
# Usage: tools/embed-server.sh [rid]
#   RIDs: osx-arm64 (default, Apple Silicon) | osx-x64 | win-x64 | linux-x64
# ALLOW_NO_EMBEDDINGS=1 stages without the embedding model (a clone without git-lfs).
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TARGET="$ROOT/src/CampaignVault/CampaignVault.csproj"
SERVER_ROOT="$ROOT/UnityClient/Assets/StreamingAssets/CampaignVault/Server"
OUT="$SERVER_ROOT/$RID"

# A dangling LFS pointer stub (clone without git-lfs) is not a model.
MODEL_SRC="$ROOT/models/embedding/model.onnx"
HAVE_MODEL=1
if [ ! -f "$MODEL_SRC" ] || head -c 64 "$MODEL_SRC" | grep -q "version https://git-lfs"; then
  if [ "${ALLOW_NO_EMBEDDINGS:-}" = "1" ]; then
    echo "WARNING: model.onnx is missing or a git-lfs pointer; staging without embeddings (ALLOW_NO_EMBEDDINGS=1)."
    HAVE_MODEL=0
  else
    echo "ERROR: models/embedding/model.onnx is missing or a git-lfs pointer. Install git-lfs and run 'git lfs pull'," >&2
    echo "       or set ALLOW_NO_EMBEDDINGS=1 to stage a server without embeddings." >&2
    exit 1
  fi
fi

rm -rf "$OUT"
dotnet publish "$TARGET" -c Release -r "$RID" --self-contained -o "$OUT"

# Shipped as Production: no debug symbols, no Development settings.
find "$OUT" \( -name '*.pdb' -o -name 'appsettings.Development.json' \) -delete

# Embedding models resolve at <binary>/models/embedding.
if [ "$HAVE_MODEL" = "1" ]; then
  mkdir -p "$OUT/models/embedding"
  cp "$ROOT/models/embedding/"*.onnx "$ROOT/models/embedding/"*.txt "$ROOT/models/embedding/"*.json "$OUT/models/embedding/" 2>/dev/null || true
  echo "Staged embedding models."
fi

# The server version the client expects on /health.
sed -n 's/.*Current *= *"\([^"]*\)".*/\1/p' "$ROOT/src/CampaignVault/Plugins/EngineVersion.cs" > "$SERVER_ROOT/version.txt"

echo "Embedded server staged at $OUT (version $(cat "$SERVER_ROOT/version.txt"))"
