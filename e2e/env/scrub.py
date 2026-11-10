import base64
import io
import json
import re
import sys
import zipfile
from pathlib import Path

state = Path(sys.argv[1])
roots = [Path(root) for root in sys.argv[2:]]

values = set()
secrets = state / "secrets"
if secrets.exists():
    values.update(line.strip() for line in secrets.read_text().splitlines())
journey = state / "journey.json"
if journey.exists():
    recorded = json.loads(journey.read_text())
    values.update(recorded[key] for key in ("password", "apiKey"))
extra = state / "secrets.d"
if extra.exists():
    values.update(path.read_text().strip() for path in extra.iterdir())
needles = [value.encode() for value in values if len(value) >= 16]
embedded = re.compile(rb"data:application/zip;base64,([A-Za-z0-9+/=]+)")


def holds_secret(data: bytes) -> bool:
    if any(needle in data for needle in needles):
        return True
    for match in embedded.finditer(data):
        if holds_secret(base64.b64decode(match.group(1))):
            return True
    if data[:4] == b"PK\x03\x04":
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            return any(
                holds_secret(archive.read(entry)) for entry in archive.namelist()
            )
    return False


removed = 0
for root in roots:
    if not root.exists():
        continue
    for path in sorted(root.rglob("*")):
        if path.is_file() and holds_secret(path.read_bytes()):
            print(f"removed {path}: it holds a generated secret")
            path.unlink()
            removed += 1
print(
    f"scrubbed {len(roots)} folders against {len(needles)} secrets, removed {removed} files"
)
