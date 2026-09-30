#!/usr/bin/env bash
set -euo pipefail
phase=${1:-exercise}
case "$phase" in atlas-point|tls-check|seed|verify|exercise|cleanup) ;; *) echo "invalid phase" >&2; exit 2 ;; esac
: "${TERRASCALE_SDK_CRATE:?missing separately packaged SDK crate}"
: "${TERRASCALE_SDK_CRATE_SHA256:?missing exact SDK crate SHA-256}"
runner_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
task_tmp=$(mktemp -d)
trap 'rm -rf -- "$task_tmp"' EXIT

python3 - "$TERRASCALE_SDK_CRATE" "$TERRASCALE_SDK_CRATE_SHA256" "$task_tmp" "$runner_dir" <<'PY'
import hashlib
import json
import re
import shutil
import sys
import tarfile
import tomllib
from pathlib import Path, PurePosixPath

archive, expected, directory, runner = map(Path, sys.argv[1:])
expected = str(expected)
if archive.is_symlink() or not archive.is_file() or not re.fullmatch(r"[a-f0-9]{64}", expected):
    raise SystemExit("Invalid native package artifact or SHA-256")
actual = hashlib.sha256(archive.read_bytes()).hexdigest()
if actual != expected:
    raise SystemExit("Native SDK package SHA-256 mismatch")
with tarfile.open(archive, "r:gz") as package:
    entries = package.getmembers()
    roots = {PurePosixPath(entry.name).parts[0] for entry in entries if PurePosixPath(entry.name).parts}
    if len(roots) != 1:
        raise SystemExit("Native crate must have one package root")
    root = roots.pop()
    if not re.fullmatch(r"terrascale-[0-9][A-Za-z0-9.+-]*", root):
        raise SystemExit("Invalid native crate package root")
    for entry in entries:
        path = PurePosixPath(entry.name)
        if path.is_absolute() or ".." in path.parts or not (entry.isfile() or entry.isdir()):
            raise SystemExit("Invalid native crate entry")
    package.extractall(directory / "sdk", filter="data")
manifest = directory / "sdk" / root / "Cargo.toml"
package = tomllib.loads(manifest.read_text())["package"]
if package["name"] != "terrascale" or root != f'terrascale-{package["version"]}':
    raise SystemExit("Native package name/version mismatch")
text = (runner / "Cargo.toml").read_text()
text = text.replace('terrascale = { path = "../../" }', f'terrascale = {{ path = "sdk/{root}" }}')
(directory / "Cargo.toml").write_text(text)
shutil.copyfile(runner / "Cargo.lock", directory / "Cargo.lock")
(directory / "src").mkdir()
shutil.copyfile(runner / "src/main.rs", directory / "src/main.rs")
print(json.dumps({"package": root, "sha256": actual, "consumer": "isolated_unpacked_crate"}, sort_keys=True))
PY

if [[ -n ${TS_QUALIFICATION_CARGO_TARGET_DIR:-} ]]; then
    task_target="${TS_QUALIFICATION_CARGO_TARGET_DIR%/}/$TERRASCALE_SDK_CRATE_SHA256"
else
    task_target="$task_tmp/target"
fi
cd -- "$task_tmp"
env CARGO_BUILD_JOBS=2 CARGO_TARGET_DIR="$task_target" cargo +1.98.1 run --offline --locked --manifest-path "$task_tmp/Cargo.toml" -- "$phase"
