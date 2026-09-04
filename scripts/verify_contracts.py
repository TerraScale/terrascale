#!/usr/bin/env python3
"""Verify the committed contract bundle without contacting the registry."""

import hashlib
import re
from pathlib import Path

from contracts import load_json, validate_bundle


root = Path(__file__).resolve().parent.parent
version_path = root / "CONTRACT_VERSION"
try:
    pinned_version = version_path.read_text(encoding="utf-8").strip()
except OSError as error:
    raise SystemExit(f"contracts: committed CONTRACT_VERSION is unavailable: {error}") from error
if re.fullmatch(r"v[1-9][0-9]*", pinned_version) is None:
    raise SystemExit(f"contracts: committed CONTRACT_VERSION is invalid: {pinned_version!r}")
bundle = root / "contracts" / pinned_version
manifest, _ = validate_bundle(bundle, pinned_version)
vectors_path = bundle / "msgpack" / "message-vectors.json"
hostile_path = bundle / "msgpack" / "hostile-vectors.json"
vectors = load_json(vectors_path)
manifest_sha = hashlib.sha256(
    (bundle / "msgpack" / "terrascale.sync.v2.msgpack.json").read_bytes()
).hexdigest()
if vectors.get("manifestSha256") != manifest_sha:
    raise SystemExit("contracts: MessagePack corpus pins a different manifest SHA-256")
if not isinstance(vectors.get("vectors"), list) or len(vectors["vectors"]) < 11:
    raise SystemExit("contracts: MessagePack corpus is incomplete")
if not hostile_path.is_file():
    raise SystemExit("contracts: hostile MessagePack corpus is missing")
print(f"contracts: verified committed {manifest['contractVersion']} bundle")
