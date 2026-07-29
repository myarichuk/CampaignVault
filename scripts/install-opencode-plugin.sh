#!/usr/bin/env bash
# Install the campaign-vault opencode plugin into a target folder
# Usage: ./scripts/install-opencode-plugin.sh <target-folder>

set -euo pipefail

TARGET_DIR="${1:-}"
PLUGIN_SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../opencode-plugin" && pwd)"

if [[ -z "$TARGET_DIR" ]]; then
    echo "Usage: $0 <target-folder>"
    echo "Example: $0 ~/my-campaign"
    exit 1
fi

if [[ ! -d "$PLUGIN_SRC_DIR" ]]; then
    echo "Plugin source not found at $PLUGIN_SRC_DIR"
    exit 1
fi

if [[ ! -d "$TARGET_DIR" ]]; then
    echo "Target directory does not exist: $TARGET_DIR"
    exit 1
fi

echo "Building plugin..."
cd "$PLUGIN_SRC_DIR"
if [[ ! -d node_modules ]]; then
    npm install
fi
npm run build

PLUGIN_DEST_DIR="$TARGET_DIR/.opencode/plugin"
mkdir -p "$PLUGIN_DEST_DIR"

cp "$PLUGIN_SRC_DIR/dist/index.js" "$PLUGIN_DEST_DIR/campaign-vault.js"
echo "Plugin installed to $PLUGIN_DEST_DIR/campaign-vault.js"

OPENCODE_JSON="$TARGET_DIR/opencode.json"
if [[ ! -f "$OPENCODE_JSON" ]]; then
    cat > "$OPENCODE_JSON" <<EOF
{
  "plugin": ["file://.opencode/plugin/campaign-vault.js"]
}
EOF
    echo "Created $OPENCODE_JSON with plugin registration"
else
    echo "opencode.json already exists at $OPENCODE_JSON"
    echo "Add this to your existing opencode.json manually:"
    echo '  "plugin": ["file://.opencode/plugin/campaign-vault.js"]'
fi

echo "Done. Plugin installed to $TARGET_DIR/.opencode/plugin/campaign-vault.js"