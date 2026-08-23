#!/usr/bin/env python3
"""Check license metadata and the explicit pre-release gate."""

from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[1]
manifest = (ROOT / "Cargo.toml").read_text(encoding="utf-8")
match = re.search(r'^license\s*=\s*"([^"]+)"$', manifest, re.MULTILINE)
if not match:
    print("Cargo.toml must declare license metadata", file=sys.stderr)
    sys.exit(1)
if match.group(1) != "LicenseRef-TerraScale-Pending":
    print(f"unexpected foundation license marker: {match.group(1)}", file=sys.stderr)
    sys.exit(1)
notice = (ROOT / "LICENSE.md").read_text(encoding="utf-8")
if "before a public release" not in notice:
    print("LICENSE.md must keep the public-release gate explicit", file=sys.stderr)
    sys.exit(1)
print("license metadata valid: distribution license pending release decision")
