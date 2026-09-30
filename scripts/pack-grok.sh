#!/usr/bin/env bash
# Pack the Grok Web kit into dist/grok/: stable file names, a changed-files report, a zip,
# and the filled project instructions (--copy puts them on the clipboard).
# Options and details: scripts/pack_grok.py --help, or the "Grok Web" section of INSTALLATION.md.
set -euo pipefail
exec python3 "$(dirname "$0")/pack_grok.py" "$@"
