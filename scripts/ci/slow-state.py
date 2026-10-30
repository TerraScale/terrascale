#!/usr/bin/env python3
"""Select changed Slow inputs against the most recent suite that actually executed."""
import fnmatch
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import urllib.error
import urllib.request


def fingerprint(area, revision="HEAD"):
    # Auth starts TerraBase; both services source-reference the shared libraries.
    paths = ["Dockerfile", "Makefile", "global.json", "NuGet.config", "BannedSymbols.txt",
             ".editorconfig", "Directory.*", "*.sln*", ".config",
             "src/TerraScale.Endpoints*", "src/TerraScale.Logging", "src/vendor",
             "src/TerraBase*", "src/TerraScale.Authentication*", "sdk/dotnet",
             "scripts/vendor", "scripts/ci/slow-state.py",
             ".github/workflows/slow.yml", "scripts/ci/slow-report.sh"]
    if area == "auth":
        paths += ["src/TerraScale.Authentication*", "tests/TerraScale.Authentication*",
                  "scripts/auth", "docs/auth/*.yaml"]
    else:
        paths += ["tests/TerraBase*", "tests/Turso.Data.Sqlite.Tests", "scripts/terrabase",
                  "tests/TerraScale.Authentication.TestHarness/FakeGitHubActions.cs",
                  "benchmark/dotnet/TerraBase.LiveRestoreDrill/ScratchCleanup.cs"]
    entries = subprocess.check_output(["git", "ls-tree", "-r", "-z", revision]).split(b"\0")
    selected = []
    for entry in entries:
        if not entry:
            continue
        name = entry.split(b"\t", 1)[1].decode()
        if any(fnmatch.fnmatchcase(name, pattern) or name.startswith(pattern + "/") for pattern in paths):
            selected.append(entry)
    return hashlib.sha256(b"\0".join(selected) + area.encode()).hexdigest()


def public_api(path):
    # This repository and its Actions metadata are public. No new token scopes,
    # credentials or runner bridge are needed to read the last actual suite SHA.
    request = urllib.request.Request("https://api.github.com/" + path,
                                     headers={"Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def previous(area):
    repo = os.environ["GITHUB_REPOSITORY"]
    # The hash is metadata so public reads need no artifact download permission.
    # If main was busy enough to evict the last marker from this page, rerun.
    artifacts = public_api(f"repos/{repo}/actions/artifacts?per_page=100")["artifacts"]
    prefix = f"slow-state-{area}-"
    for artifact in artifacts:
        value = artifact["name"].removeprefix(prefix)
        if (not artifact["name"].startswith(prefix) or artifact["expired"] or
                len(value) != 64 or any(c not in "0123456789abcdef" for c in value)):
            continue
        run = artifact["workflow_run"]
        if run["head_branch"] != "main":
            continue
        if public_api(f"repos/{repo}/actions/runs/{run['id']}")["event"] == "schedule":
            return value
    return None


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "record":
        value = fingerprint(sys.argv[2])
        Path("slow/fingerprint").write_text(value + "\n")
        with open(os.environ["GITHUB_OUTPUT"], "a") as output:
            output.write(f"fingerprint={value}\n")
        return
    changed = []
    for area in ("auth", "terrabase"):
        try:
            last = previous(area)
        except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
            # An unavailable public record must never suppress Slow coverage.
            print(f"Slow {area}: previous execution record unavailable; running ({type(error).__name__})")
            last = None
        if last != fingerprint(area):
            changed.append(area)
        else:
            print(f"Slow {area}: unchanged since last actual suite; skipped")
    with open(os.environ["GITHUB_OUTPUT"], "a") as output:
        output.write(f"slow={json.dumps(changed)}\n")


if __name__ == "__main__":
    main()
