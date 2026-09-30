#!/usr/bin/env python3
"""Verify exact native contract bytes and required producer route/schema shape."""
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = ROOT / "contracts/native"


def require(condition, message):
    if not condition:
        raise SystemExit(message)


manifest_bytes = (CONTRACT / "manifest.json").read_bytes()
manifest = json.loads(manifest_bytes)
digest = hashlib.sha256(manifest_bytes).hexdigest()
require((ROOT / "CONTRACT_VERSION").read_text().strip() == manifest["version"], "Contract version drift")
require((ROOT / "CONTRACT_ARCHIVE_SHA256").read_text().strip() == digest, "Native manifest SHA-256 drift")
pins = dict(re.findall(r'pub const (\w+): &str =\s*"([^"]+)";', (ROOT / "src/generated_contract.rs").read_text()))
require(pins.get("MANIFEST_SHA256") == digest, "Generated manifest pin drift")
require(pins.get("CONTRACT_VERSION") == manifest["version"], "Generated version pin drift")
for artifact, expected in manifest["artifacts"].items():
    require(Path(artifact).name == artifact, "Invalid native artifact path")
    require(hashlib.sha256((CONTRACT / artifact).read_bytes()).hexdigest() == expected, f"Native artifact drift: {artifact}")
require(pins.get("OPENAPI_SHA256") == manifest["artifacts"]["openapi.json"], "Generated OpenAPI pin drift")
require(pins.get("ATOMIC_SHA256") == manifest["artifacts"]["ATOMIC_V1.json"], "Generated atomic pin drift")
require(pins.get("ATOMIC_VECTORS_SHA256") == manifest["artifacts"]["atomic_vectors.json"], "Generated atomic vectors pin drift")

api = json.loads((CONTRACT / "openapi.json").read_bytes())
prefix = "/v1/tenants/{tenant}/databases/{database}/shards/{shard}"
collection = prefix + "/collections/{collection}/documents"
for path, method in [(collection + "/{document}", "get"), (collection + "/query", "post"), (collection + "/subscriptions", "post"), (prefix + "/transactions", "post"), (prefix + "/transactions/status", "post")]:
    require(method in api["paths"].get(path, {}), f"Missing native operation: {method} {path}")
    success = [status for status in api["paths"][path][method]["responses"] if status.startswith("2")]
    require(success == ["200"], f"Unexpected native success status: {method} {path}")
require(not any(path.startswith("/api/") for path in api["paths"]), "Obsolete HTTP shim contract")
schemas = api["components"]["schemas"]
error_codes = schemas["Error"]["properties"]["error"]["properties"]["code"]["enum"]
require({"precondition_failed", "authority_changed", "authority_unavailable"} <= set(error_codes), "Missing conditional/authority errors")
require("state_version" in schemas["PointResponse"]["required"], "Missing required nullable point state token")
for name in ["MutationAuthority", "WriterIdentity", "DocumentCondition", "TransactionStatusRequest", "TransactionStatusResponse"]:
    require(name in schemas, f"Missing native schema {name}")
print(json.dumps({"contract": manifest["version"], "manifest_sha256": digest, "openapi_sha256": manifest["artifacts"]["openapi.json"], "result": "passed"}, sort_keys=True))
