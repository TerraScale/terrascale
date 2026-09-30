#!/usr/bin/env python3
"""Verify frozen Atlas contracts and migrated fixed-clock codec fixture bytes."""
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = ROOT / "contracts/atlas"


def require(condition, message):
    if not condition:
        raise SystemExit(message)


raw = (CONTRACT / "manifest.json").read_bytes()
manifest = json.loads(raw)
pins = dict(re.findall(r'pub const (\w+): &str =\s*"([^"]+)";', (ROOT / "src/generated_contract.rs").read_text()))
digest = hashlib.sha256(raw).hexdigest()
require(pins.get("ATLAS_MANIFEST_SHA256") == digest, "Atlas manifest pin drift")
for name, expected in manifest["artifacts"].items():
    require(Path(name).name == name, "Invalid Atlas artifact path")
    require(hashlib.sha256((CONTRACT / name).read_bytes()).hexdigest() == expected, f"Atlas artifact drift: {name}")
receiver = json.loads((CONTRACT / "ATLAS_NATIVE_RECEIVER_V1.json").read_bytes())
discovery = receiver["discovery"]
require(discovery["method"] == "GET" and discovery["path"] == "/api/v1/discovery", "Atlas operation drift")
require(discovery["required_query"] == ["tenant_id", "database_id", "shard_id"], "Atlas scope drift")
require(discovery["observed_query_all_or_none"] == ["observed_deployment_id", "observed_cell", "observed_publisher_generation", "rev"], "Atlas observed cursor drift")
consumer = json.loads((CONTRACT / "RUST_ATLAS_CONSUMER_V1.json").read_bytes())
require(consumer["operation_requirements"]["transaction_status"] == ["client_v1", "write"], "Atlas status reconciliation drift")
for name in ["native-discovery-success.json", "native-discovery-forbidden.json"]:
    require(manifest["artifacts"][name] == consumer["codec_fixtures"][name]["sha256"], f"Codec fixture drift: {name}")
require(manifest["fixture_unix_millis"] == 1790769600000 and manifest["fixture_consumer_bound_millis"] == 100, "Fixed-clock codec provenance drift")
print(json.dumps({"contract": manifest["version"], "manifest_sha256": digest, "evidence": "codec_only", "result": "passed"}, sort_keys=True))
