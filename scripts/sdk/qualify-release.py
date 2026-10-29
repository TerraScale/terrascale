#!/usr/bin/env python3
"""Run SDK release gates on a dev host and publish immutable qualification artifacts."""
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import tomllib
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


def run(*command, capture=False):
    return subprocess.run(command, cwd=ROOT, check=True, text=True,
                          stdout=subprocess.PIPE if capture else None).stdout


def main(language):
    if language not in {"dotnet", "js", "go", "python", "rust"}:
        raise SystemExit("Unknown SDK")
    if run("git", "status", "--porcelain", capture=True).strip():
        raise SystemExit("Commit all changes before qualifying a release")
    revision = run("git", "rev-parse", "HEAD", capture=True).strip()
    run("git", "fetch", "origin", "main")
    if revision != run("git", "rev-parse", "FETCH_HEAD", capture=True).strip():
        raise SystemExit("Release qualification requires the current main revision")
    runs = json.loads(run("gh", "run", "list", "--repo", "TerraScale/terrascale", "--branch", "main",
                          "--commit", revision, "--event", "push", "--limit", "100", "--json",
                          "status,conclusion,workflowName", capture=True))
    if not runs or any(item["status"] != "completed" or item["conclusion"] not in {"success", "skipped"} for item in runs):
        raise SystemExit("Main must be green before release qualification")
    area = ROOT / "sdk" / language
    if language == "dotnet":
        version = ET.parse(area / "src/TerraScale.DotNet/TerraScale.DotNet.csproj").findtext(".//Version")
    elif language == "js":
        version = json.loads((area / "package.json").read_text())["version"]
    elif language == "python":
        version = re.search(r'__version__ = "([^"]+)"', (area / "src/terrascale/_version.py").read_text())[1]
    elif language == "rust":
        version = tomllib.loads((area / "Cargo.toml").read_text())["package"]["version"]
    else:
        version = "0.0.11"
    if not re.fullmatch(r"0\.0\.[0-9]+", version):
        raise SystemExit("SDK versions must be 0.0.X")
    with tempfile.TemporaryDirectory(prefix=f"sdk-{language}-qualified-") as temporary:
        output = Path(temporary)
        run("bash", str(area / "scripts/qualify-release.sh"), str(output))
        files = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in output.iterdir() if path.is_file()}
        if not files:
            raise SystemExit("Qualification produced no package artifacts")
        manifest = {"revision": revision, "language": language, "version": version, "files": files}
        (output / "qualification.json").write_text(json.dumps(manifest, indent=2) + "\n")
        tag = f"sdk/{language}/qualified-{revision}"
        run("gh", "release", "create", tag, "--repo", "TerraScale/terrascale", "--target", revision,
            "--prerelease", "--title", f"{language} {version} qualified artifacts", "--notes",
            f"Dev-host release qualification for {revision}. Package hashes are in qualification.json.",
            *[str(path) for path in output.iterdir() if path.is_file()])
        run("gh", "api", f"repos/TerraScale/terrascale/statuses/{revision}", "-f", "state=success",
            "-f", f"context=sdk-release/sdk-{language}", "-f", f"description=Release gates and exact package bytes qualified ({version})",
            "-f", f"target_url=https://github.com/TerraScale/terrascale/releases/tag/{tag}")


if __name__ == "__main__":
    main(*sys.argv[1:])
