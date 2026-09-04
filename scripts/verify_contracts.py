#!/usr/bin/env python3
"""Verify the committed contract bundle without contacting the registry."""

import hashlib
from pathlib import Path

from contracts import load_json, validate_bundle


root = Path(__file__).resolve().parent.parent
bundle = root / "contracts" / "v1"
manifest, _ = validate_bundle(bundle, "v1")
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
