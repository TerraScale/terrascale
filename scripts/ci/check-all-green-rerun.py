#!/usr/bin/env python3

import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[2]
workflows = root / ".github/workflows"
expected = set()
for path in sorted(workflows.glob("*.yml")):
    text = path.read_text()
    name = re.search(r"^name:\s*(.+)$", text, re.MULTILINE)
    if not name:
        print(f"{path.name}: no name")
        sys.exit(1)
    on = re.split(r'^["\']?on["\']?:\s*$', text, maxsplit=1, flags=re.MULTILINE)
    if len(on) < 2:
        print(f"{path.name}: no on block")
        sys.exit(1)
    if not re.search(r"^\s{2}pull_request:\s*$", on[1], re.MULTILINE):
        continue
    if name.group(1).strip() == "all-green":
        continue
    expected.add(name.group(1).strip())

rerun = (workflows / "all-green-rerun.yml").read_text()
block = re.search(r"^\s{4}workflows:\n((?:\s{6}- .+\n)+)", rerun, re.MULTILINE)
if not block:
    print("all-green-rerun.yml: no workflows list")
    sys.exit(1)
listed = {line.strip()[2:].strip() for line in block.group(1).splitlines()}

missing = sorted(expected - listed)
extra = sorted(listed - expected)
for name in missing:
    print(f"missing from all-green-rerun: {name}")
for name in extra:
    print(f"extra in all-green-rerun: {name}")
if missing or extra:
    sys.exit(1)
print(f"all-green-rerun covers {len(listed)} workflows")
