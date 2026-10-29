#!/usr/bin/env python3
"""Download immutable dev-host artifacts and verify their source and hashes."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys

def main(language, revision, destination):
    if language not in {"dotnet", "js", "go", "python", "rust"} or not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise SystemExit("Unknown SDK or invalid source revision")
    subprocess.run([sys.executable, str(Path(__file__).with_name("check-proof.py")),
                    f"sdk-{language}", revision, "--release"], check=True)
    output = Path(destination).resolve()
    output.mkdir(parents=True, exist_ok=True)
    subprocess.run(["gh", "release", "download", f"sdk/{language}/qualified-{revision}",
                    "--repo", "TerraScale/terrascale", "--dir", str(output)], check=True)
    manifest = json.loads((output / "qualification.json").read_text())
    if manifest["revision"] != revision or manifest["language"] != language or not re.fullmatch(r"0\.0\.[0-9]+", manifest["version"]):
        raise SystemExit("Qualified artifact identity differs from the requested release")
    if not manifest["files"]:
        raise SystemExit("Qualification contains no artifacts")
    for name, expected in manifest["files"].items():
        path = output / name
        if path.parent != output or not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise SystemExit(f"Qualified artifact hash mismatch: {name}")
    if Path(os.environ.get("GITHUB_OUTPUT", "")).is_file():
        with open(os.environ["GITHUB_OUTPUT"], "a") as stream:
            stream.write(f"version={manifest['version']}\n")
    print(f"Verified {len(manifest['files'])} {language} artifacts from {revision}")


if __name__ == "__main__":
    main(*sys.argv[1:])
