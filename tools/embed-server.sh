#!/usr/bin/env bash
# Stages a self-contained CampaignVault server build (+ embedding models) into
# the Unity client's StreamingAssets, so player builds ship the MCP server and
# can launch it on localhost. Generated output: never hand-edit, just re-run.
#
# Usage: tools/embed-server.sh [rid]
#   RIDs: osx-arm64 (default, Apple Silicon) | osx-x64 | win-x64 | linux-x64
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TARGET="$ROOT/src/CampaignVault/CampaignVault.csproj"
OUT="$ROOT/UnityClient/Assets/StreamingAssets/CampaignVault/Server/$RID"

dotnet publish "$TARGET" -c Release -r "$RID" --self-contained -o "$OUT"

# Embedding models resolve at <binary>/models/embedding. Stage the real files;
# skip a dangling LFS pointer stub (model.onnx under 1MB is not a model).
MODEL_SRC="$ROOT/models/embedding/model.onnx"
if [ -f "$MODEL_SRC" ] && [ "$(wc -c < "$MODEL_SRC")" -gt 1000000 ]; then
  mkdir -p "$OUT/models/embedding"
  cp "$ROOT/models/embedding/"*.onnx "$ROOT/models/embedding/"*.txt "$ROOT/models/embedding/"*.json "$OUT/models/embedding/" 2>/dev/null || true
  echo "Staged embedding models."
else
  echo "WARNING: real model.onnx not found (LFS stub?) — embedded server will run without embeddings, like dev without git-lfs."
fi

echo "Embedded server staged at $OUT"
