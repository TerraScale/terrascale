#!/usr/bin/env python3
"""Require a successful dev-host result on the exact source revision."""
import json
import re
import subprocess
import sys


def require_proof(area, revision, release=False):
    if not re.fullmatch(r"sdk-(dotnet|js|go|python|rust)|docs|dashboard", area):
        raise SystemExit("Unknown monorepo area")
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise SystemExit("A full source revision is required")
    context = f"sdk-release/{area}" if release else f"ts-ship/{area}"
    response = subprocess.check_output([
        "gh", "api", "--paginate", "--slurp",
        f"repos/TerraScale/terrascale/commits/{revision}/statuses?per_page=100",
    ], text=True)
    statuses = [status for page in json.loads(response) for status in page]
    latest = next((status for status in statuses if status["context"] == context), None)
    if latest is None or latest["state"] != "success" or latest["creator"]["login"] != "MarioGK":
        raise SystemExit(f"Missing successful {context} for {revision}; qualify it on a dev host")
    print(f"Verified {context} on {revision}: {latest['description']}")


if __name__ == "__main__":
    if len(sys.argv) not in (3, 4) or (len(sys.argv) == 4 and sys.argv[3] != "--release"):
        raise SystemExit("usage: check-proof.py sdk-<lang> <sha> [--release]")
    require_proof(sys.argv[1], sys.argv[2], len(sys.argv) == 4)
