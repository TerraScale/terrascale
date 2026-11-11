import base64
import io
import re
import sys
import zipfile
import zlib
from pathlib import Path
from urllib.parse import quote

state = Path(sys.argv[1])
roots = [Path(root) for root in sys.argv[2:]]
redacted = b"[redacted]"
embedded = re.compile(rb"data:application/zip;base64,([A-Za-z0-9+/=]+)")


def registered() -> set[str]:
    values = set()
    secrets = state / "secrets"
    if secrets.exists():
        values.update(line.strip() for line in secrets.read_text().splitlines())
    folder = state / "secrets.d"
    if folder.exists():
        values.update(path.read_text().strip() for path in folder.iterdir())
    return {value for value in values if len(value) >= 16}


def forms(value: str) -> set[bytes]:
    encoded = quote(value, safe="")
    lower = re.sub(r"%[0-9A-F]{2}", lambda escape: escape.group(0).lower(), encoded)
    escaped = {value.replace("+", plus).replace("/", slash) for plus in ("+", "\\u002B", "\\u002b") for slash in ("/", "\\/")}
    return {form.encode() for form in {value, encoded, lower, *escaped}}


values = registered()
needles = sorted({form for value in values for form in forms(value)}, key=len, reverse=True)
redactions = 0


def clean(data: bytes) -> bytes:
    global redactions
    if data[:4] == b"PK\x03\x04":
        source = zipfile.ZipFile(io.BytesIO(data))
        entries = [(entry, source.read(entry)) for entry in source.infolist()]
        cleaned = [(entry, clean(body)) for entry, body in entries]
        if cleaned == entries:
            return data
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as target:
            for entry, body in cleaned:
                target.writestr(entry, body)
        return output.getvalue()
    data = embedded.sub(reencoded, data)
    for needle in needles:
        found = data.count(needle)
        if found:
            data = data.replace(needle, redacted)
            redactions += found
    return data


def reencoded(match: re.Match[bytes]) -> bytes:
    return b"data:application/zip;base64," + base64.b64encode(clean(base64.b64decode(match.group(1), validate=True)))


def holds(data: bytes) -> bool:
    if data[:4] == b"PK\x03\x04":
        source = zipfile.ZipFile(io.BytesIO(data))
        return any(holds(source.read(entry)) for entry in source.infolist())
    if any(holds(base64.b64decode(match.group(1), validate=True)) for match in embedded.finditer(data)):
        return True
    return any(needle in data for needle in needles)


files = [path for root in roots if root.exists() for path in sorted(root.rglob("*")) if path.is_file()]
changed = 0
failed = []
for path in files:
    try:
        before = path.read_bytes()
        after = clean(before)
        if after != before:
            path.write_bytes(after)
            changed += 1
        if holds(path.read_bytes()):
            raise ValueError("a secret is still there after redaction")
    except (OSError, ValueError, EOFError, RuntimeError, zipfile.BadZipFile, zlib.error) as error:
        path.unlink(missing_ok=True)
        failed.append(path)
        print(f"deleted {path}: it could not be scrubbed ({type(error).__name__}: {error})")
print(
    f"scrubbed {len(files)} files in {len(roots)} folders against {len(values)} secrets "
    f"({len(needles)} needle forms): {redactions} redactions in {changed} files, {len(failed)} files deleted"
)
sys.exit(1 if failed else 0)
