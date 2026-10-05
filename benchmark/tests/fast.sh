#!/usr/bin/env bash
# Current product acceptance uses a real pinned C# release, never a fake server.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"
python3 https-document/acceptance.py --smoke --output-parent bench/csharp-smoke
