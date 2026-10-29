#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -gt 1 ]; then
    echo "usage: qualify-release.sh [artifact-directory-outside-worktree]" >&2
    exit 2
fi
sdk_root=$(cd -- "$(dirname -- "$0")/.." && pwd)
repo_root=$(git -C "$sdk_root" rev-parse --show-toplevel)
if [ -n "$(git -C "$repo_root" status --porcelain)" ]; then
    echo "Commit the source changes before qualifying the exact clean revision." >&2
    exit 1
fi
if [ "$#" -eq 1 ]; then
    destination=$(python3 -c 'from pathlib import Path; import sys; print(Path(sys.argv[1]).resolve())' "$1")
else
    destination=$(mktemp -d "${TMPDIR:-/tmp}/ts-dotnet-qualified.XXXXXXXX")
fi
python3 - "$destination" "$repo_root" <<'PYTHON'
from pathlib import Path
import sys
if Path(sys.argv[1]).is_relative_to(Path(sys.argv[2])):
    raise SystemExit("Choose an artifact output directory outside the source worktree.")
PYTHON
mkdir -p "$destination"
cd "$sdk_root"
bash scripts/check-release.sh 2>&1 | tee "$destination/release-checks.log"
python3 scripts/pack-reproducible.py "$destination"
python3 - "$destination" <<'PYTHON'
import hashlib
import json
from pathlib import Path
import sys
output = Path(sys.argv[1])
provenance = output / "provenance.json"
metadata = json.loads(provenance.read_text())
metadata["releaseChecks"] = {
    "command": "bash scripts/check-release.sh",
    "result": "passed",
    "log": "release-checks.log",
    "logSha256": hashlib.sha256((output / "release-checks.log").read_bytes()).hexdigest(),
}
provenance.write_text(json.dumps(metadata, indent=2) + "\n")
PYTHON
printf 'Qualified package artifacts: %s\n' "$destination"
