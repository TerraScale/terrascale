#!/usr/bin/env python3
"""Generate and validate TerraBaseV2's parity inventory.

Contract fragments contain facts extracted from V1. State overlays contain
mutable V2 status, disposition, and Proof IDs. ``inventory.json`` is generated
output and must never be edited as a source file.
"""

from __future__ import annotations

import argparse
import copy
import importlib
import json
import re
import sys
from collections.abc import Callable
from pathlib import Path
from typing import Any, Protocol, cast


DEFAULT_ROOT = Path(__file__).resolve().parents[1]
SCHEMA_VERSION = "1.0"
PINNED_SOURCE_COMMIT = "fa99b9257776dbe625e6a2388c74c9ce916637ee"
SOURCE_PRODUCT = "TerraBase V1"
COMMIT = re.compile(r"^[0-9a-f]{40}$")
EVIDENCE = re.compile(r"^E-[0-9]{3}$")
ITEM = re.compile(r"^PB-[0-9]{4}$")
PROOF = re.compile(r"^P-[0-9]{3}$")
STATUSES = ("unresolved", "in-progress", "implemented", "qualified", "accepted-exclusion")
STATUS_SET = set(STATUSES)
CLASSIFICATIONS = {"core", "optional", "legacy", "admin", "protocol"}
RISK_LEVELS = {"low", "medium", "high", "critical"}
STATUS_TRANSITIONS = {
    "unresolved": {"unresolved", "in-progress", "accepted-exclusion"},
    "in-progress": {"in-progress", "implemented", "accepted-exclusion"},
    "implemented": {"implemented", "qualified"},
    "qualified": {"qualified"},
    "accepted-exclusion": {"accepted-exclusion"},
}


class _JsonSchemaValidator(Protocol):
    def validate(self, instance: object) -> None:
        """Validate an instance against the configured schema."""


class _JsonSchemaModule(Protocol):
    Draft202012Validator: Callable[[object], _JsonSchemaValidator]


def fail(message: str) -> None:
    raise ValueError(message)


def require_keys(value: object, required: set[str], allowed: set[str], path: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        fail(f"{path} must be an object")
    keys = set(value)
    missing = required - keys
    unknown = keys - allowed
    if missing:
        fail(f"{path} is missing {', '.join(sorted(missing))}")
    if unknown:
        fail(f"{path} has unknown fields: {', '.join(sorted(unknown))}")
    return value


def read_json(path: Path, label: str) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"invalid JSON in {label}: {error}")


def validate_evidence_ids(value: object, path: str, allowed: set[str] | None = None) -> list[str]:
    if not isinstance(value, list) or not value:
        fail(f"{path} must be a non-empty list")
    if len(set(value)) != len(value):
        fail(f"{path} must contain unique IDs")
    if any(not isinstance(item, str) or not EVIDENCE.fullmatch(item) for item in value):
        fail(f"{path} contains an invalid evidence ID")
    if allowed is not None and not set(value).issubset(allowed):
        fail(f"{path} references an unlisted source evidence ID")
    return value


def validate_contract_item(item: object, path: str, source_commit: str, source_evidence: set[str]) -> dict[str, Any]:
    item = require_keys(
        item,
        {"id", "name", "classification", "v1_evidence", "v2", "public_contract", "downstream_impact", "risk"},
        {"id", "name", "classification", "v1_evidence", "v2", "public_contract", "downstream_impact", "risk"},
        path,
    )
    item_id = item.get("id")
    if not isinstance(item_id, str) or not ITEM.fullmatch(item_id):
        fail(f"{path}.id must match PB-NNNN")
    if not isinstance(item.get("name"), str) or not item["name"].strip():
        fail(f"{path}.name must be non-empty")
    if item.get("classification") not in CLASSIFICATIONS:
        fail(f"{path}.classification is invalid")

    v1 = require_keys(
        item.get("v1_evidence"),
        {"evidence_ids", "source_commit", "scope"},
        {"evidence_ids", "source_commit", "scope"},
        f"{path}.v1_evidence",
    )
    validate_evidence_ids(v1.get("evidence_ids"), f"{path}.v1_evidence.evidence_ids", source_evidence)
    if v1.get("source_commit") != source_commit or not COMMIT.fullmatch(str(v1.get("source_commit"))):
        fail(f"{path}.v1_evidence.source_commit must equal the pinned V1 commit")
    if not isinstance(v1.get("scope"), str) or not v1["scope"].strip():
        fail(f"{path}.v1_evidence.scope must be non-empty")

    v2 = require_keys(item.get("v2"), {"owner", "public_seam"}, {"owner", "public_seam"}, f"{path}.v2")
    for field in ("owner", "public_seam"):
        if not isinstance(v2.get(field), str) or not v2[field].strip():
            fail(f"{path}.v2.{field} must be non-empty")

    contract = require_keys(
        item.get("public_contract"),
        {"parity_required", "semantics", "compatibility"},
        {"parity_required", "semantics", "compatibility"},
        f"{path}.public_contract",
    )
    if contract.get("parity_required") is not True:
        fail(f"{path}.public_contract.parity_required must be true")
    if not isinstance(contract.get("semantics"), list) or not contract["semantics"]:
        fail(f"{path}.public_contract.semantics must be non-empty")
    if any(not isinstance(value, str) or not value.strip() for value in contract["semantics"]):
        fail(f"{path}.public_contract.semantics contains an empty value")
    if not isinstance(contract.get("compatibility"), str) or not contract["compatibility"].strip():
        fail(f"{path}.public_contract.compatibility must be non-empty")

    downstream = item.get("downstream_impact")
    if not isinstance(downstream, list) or not downstream or any(not isinstance(value, str) or not value.strip() for value in downstream):
        fail(f"{path}.downstream_impact must be a non-empty list of strings")
    risk = require_keys(item.get("risk"), {"level", "description"}, {"level", "description"}, f"{path}.risk")
    if risk.get("level") not in RISK_LEVELS:
        fail(f"{path}.risk.level is invalid")
    if not isinstance(risk.get("description"), str) or not risk["description"].strip():
        fail(f"{path}.risk.description must be non-empty")
    return item


def load_contracts(parity: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    contracts = parity / "contracts"
    manifest_path = contracts / "manifest.json"
    manifest = require_keys(
        read_json(manifest_path, "contract manifest"),
        {"schema_version", "product", "source_commit", "evidence_ids"},
        {"schema_version", "product", "source_commit", "evidence_ids"},
        "contract manifest",
    )
    if manifest.get("schema_version") != SCHEMA_VERSION:
        fail("contract manifest schema_version must be 1.0")
    if manifest.get("product") != SOURCE_PRODUCT:
        fail("contract manifest product must be TerraBase V1")
    if manifest.get("source_commit") != PINNED_SOURCE_COMMIT or not COMMIT.fullmatch(str(manifest.get("source_commit"))):
        fail("contract manifest source_commit must equal the pinned V1 commit")
    evidence_ids = set(validate_evidence_ids(manifest.get("evidence_ids"), "contract manifest.evidence_ids"))

    fragment_paths = sorted(path for path in contracts.glob("*.json") if path.name != "manifest.json")
    if not fragment_paths:
        fail("contract fragments are required")
    items: list[dict[str, Any]] = []
    seen: set[str] = set()
    for fragment_path in fragment_paths:
        fragment = require_keys(
            read_json(fragment_path, f"contract fragment {fragment_path.name}"),
            {"schema_version", "domain", "owner", "items"},
            {"schema_version", "domain", "owner", "items"},
            f"contract fragment {fragment_path.name}",
        )
        if fragment.get("schema_version") != SCHEMA_VERSION:
            fail(f"contract fragment {fragment_path.name} schema_version must be 1.0")
        if not isinstance(fragment.get("domain"), str) or fragment.get("domain") != fragment_path.stem:
            fail(f"contract fragment {fragment_path.name} domain must match its filename")
        if fragment.get("owner") != f"WS-CONTRACT/{fragment['domain']}":
            fail(f"contract fragment {fragment_path.name} owner must be WS-CONTRACT/{fragment['domain']}")
        fragment_items = fragment.get("items")
        if not isinstance(fragment_items, list) or not fragment_items:
            fail(f"contract fragment {fragment_path.name}.items must be non-empty")
        for index, item in enumerate(fragment_items):
            item_path = f"contract fragment {fragment_path.name}.items[{index}]"
            item = validate_contract_item(item, item_path, manifest["source_commit"], evidence_ids)
            if item["id"] in seen:
                fail(f"duplicate item id {item['id']} in contract fragments")
            seen.add(item["id"])
            items.append(item)
    return manifest, sorted(items, key=lambda item: item["id"])


def load_overlays(parity: Path, item_ids: set[str]) -> dict[str, dict[str, Any]]:
    overlays_dir = parity / "overlays"
    overlay_paths = sorted(overlays_dir.glob("*.json"))
    if not overlay_paths:
        fail("state overlays are required")
    overlays: dict[str, dict[str, Any]] = {}
    for path in overlay_paths:
        overlay = require_keys(
            read_json(path, f"state overlay {path.name}"),
            {"schema_version", "item_id", "status", "proof_ids"},
            {"schema_version", "item_id", "status", "proof_ids", "disposition", "previous_status"},
            f"state overlay {path.name}",
        )
        if overlay.get("schema_version") != SCHEMA_VERSION:
            fail(f"state overlay {path.name} schema_version must be 1.0")
        item_id = overlay.get("item_id")
        if not isinstance(item_id, str) or not ITEM.fullmatch(item_id):
            fail(f"state overlay {path.name}.item_id is invalid")
        if item_id not in item_ids:
            fail(f"unknown overlay item id {item_id}")
        if path.stem != item_id:
            fail(f"state overlay {path.name} filename must match item_id")
        if item_id in overlays:
            fail(f"duplicate overlay item id {item_id}")
        status = overlay.get("status")
        if status not in STATUS_SET:
            fail(f"state overlay {path.name}.status is invalid")
        proof_ids = overlay.get("proof_ids")
        if not isinstance(proof_ids, list) or len(set(proof_ids)) != len(proof_ids):
            fail(f"state overlay {path.name}.proof_ids must be a unique list")
        if any(not isinstance(value, str) or not PROOF.fullmatch(value) for value in proof_ids):
            fail(f"state overlay {path.name}.proof_ids contains an invalid Proof ID")
        previous = overlay.get("previous_status")
        if previous is not None:
            if previous not in STATUS_SET:
                fail(f"state overlay {path.name}.previous_status is invalid")
            if status not in STATUS_TRANSITIONS[previous]:
                fail(f"state overlay {path.name} status transition {previous} -> {status} is not permitted")
        elif status != "unresolved":
            fail(f"state overlay {path.name} must name previous_status for a status transition")
        if status in {"implemented", "qualified", "accepted-exclusion"} and not proof_ids:
            fail(f"state overlay {path.name} {status} requires at least one proof ID")
        disposition = overlay.get("disposition")
        if disposition is not None and (not isinstance(disposition, str) or not disposition.strip()):
            fail(f"state overlay {path.name}.disposition must be non-empty when present")
        if status == "accepted-exclusion" and disposition is None:
            fail(f"state overlay {path.name} accepted-exclusion requires disposition")
        overlays[item_id] = overlay
    missing = item_ids - set(overlays)
    if missing:
        fail(f"missing state overlays for: {', '.join(sorted(missing))}")
    return overlays


def build_inventory(manifest: dict[str, Any], contracts: list[dict[str, Any]], overlays: dict[str, dict[str, Any]]) -> dict[str, Any]:
    items: list[dict[str, Any]] = []
    for contract in contracts:
        item = copy.deepcopy(contract)
        overlay = overlays[contract["id"]]
        item["v2"]["status"] = overlay["status"]
        if "disposition" in overlay:
            item["v2"]["disposition"] = overlay["disposition"]
        item["proof_ids"] = copy.deepcopy(overlay["proof_ids"])
        items.append(item)
    return {
        "schema_version": SCHEMA_VERSION,
        "source": {
            "product": manifest["product"],
            "commit": manifest["source_commit"],
            "evidence_ids": sorted(manifest["evidence_ids"]),
        },
        "status_values": list(STATUSES),
        "items": sorted(items, key=lambda item: item["id"]),
    }


def validate_inventory(inventory: object, manifest: dict[str, Any], contracts: list[dict[str, Any]]) -> None:
    inventory = require_keys(
        inventory,
        {"schema_version", "source", "status_values", "items"},
        {"schema_version", "source", "status_values", "items"},
        "inventory",
    )
    if inventory.get("schema_version") != SCHEMA_VERSION:
        fail("inventory.schema_version must be 1.0")
    source = require_keys(inventory.get("source"), {"product", "commit", "evidence_ids"}, {"product", "commit", "evidence_ids"}, "inventory.source")
    if source.get("product") != SOURCE_PRODUCT or source.get("commit") != PINNED_SOURCE_COMMIT:
        fail("inventory.source must use the pinned TerraBase V1 source")
    validate_evidence_ids(source.get("evidence_ids"), "inventory.source.evidence_ids")
    if source["evidence_ids"] != sorted(source["evidence_ids"]):
        fail("inventory.source.evidence_ids must be sorted")
    if inventory.get("status_values") != list(STATUSES):
        fail("inventory.status_values must use the canonical status order")
    items = inventory.get("items")
    if not isinstance(items, list) or not items:
        fail("inventory.items must be a non-empty list")
    contract_by_id = {item["id"]: item for item in contracts}
    if [item.get("id") for item in items] != sorted(contract_by_id):
        fail("inventory.items must be sorted by id and match contract fragments")
    for index, item in enumerate(items):
        item = require_keys(
            item,
            {"id", "name", "classification", "v1_evidence", "v2", "public_contract", "proof_ids", "downstream_impact", "risk"},
            {"id", "name", "classification", "v1_evidence", "v2", "public_contract", "proof_ids", "downstream_impact", "risk"},
            f"inventory.items[{index}]",
        )
        if item["id"] not in contract_by_id:
            fail(f"inventory.items[{index}] references unknown contract id {item['id']}")
        expected_contract = copy.deepcopy(contract_by_id[item["id"]])
        actual_contract = copy.deepcopy(item)
        actual_contract.pop("proof_ids")
        actual_contract["v2"].pop("status", None)
        actual_contract["v2"].pop("disposition", None)
        if actual_contract != expected_contract:
            fail(f"inventory.items[{index}] contract facts differ from its fragment")
        v2 = item["v2"]
        if v2.get("status") not in STATUS_SET:
            fail(f"inventory.items[{index}].v2.status is invalid")
        proofs = item["proof_ids"]
        if not isinstance(proofs, list) or len(set(proofs)) != len(proofs) or any(not isinstance(value, str) or not PROOF.fullmatch(value) for value in proofs):
            fail(f"inventory.items[{index}].proof_ids is invalid")


def canonical_json(value: object) -> str:
    return json.dumps(value, indent=2, ensure_ascii=False) + "\n"


def optional_draft_validation(inventory: dict[str, Any], schema: object) -> str:
    try:
        jsonschema = cast(_JsonSchemaModule, importlib.import_module("jsonschema"))
    except ModuleNotFoundError:
        return "Draft 2020-12 direct validation: unrun (optional jsonschema package unavailable); supported schema rules checked"
    try:
        jsonschema.Draft202012Validator(schema).validate(inventory)
    except Exception as error:  # jsonschema has several version-specific error types
        fail(f"Draft 2020-12 schema validation failed: {error}")
    return "Draft 2020-12 direct validation: passed"


def validate(root: Path, mode: str) -> None:
    parity = root / "parity"
    schema_path = parity / "parity.schema.json"
    schema = read_json(schema_path, "parity schema")
    if not isinstance(schema, dict) or schema.get("$schema") != "https://json-schema.org/draft/2020-12/schema":
        fail("parity schema must declare Draft 2020-12")
    manifest, contracts = load_contracts(parity)
    overlays = load_overlays(parity, {item["id"] for item in contracts})
    generated = build_inventory(manifest, contracts, overlays)
    validate_inventory(generated, manifest, contracts)
    inventory_path = parity / "inventory.json"
    if mode == "generate":
        try:
            inventory_path.write_text(canonical_json(generated), encoding="utf-8")
        except OSError as error:
            fail(f"unable to write generated inventory: {error}")
    else:
        try:
            actual = inventory_path.read_text(encoding="utf-8")
        except OSError as error:
            fail(f"unable to read generated inventory: {error}")
        if actual != canonical_json(generated):
            fail("generated inventory drift: run scripts/validate_parity.py --generate")
        validate_inventory(read_json(inventory_path, "generated inventory"), manifest, contracts)
    print(f"parity inventory {mode} valid: {len(generated['items'])} items, schema 1.0, source {manifest['source_commit']}")
    print(optional_draft_validation(generated, schema))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=DEFAULT_ROOT, help="repository root, primarily for isolated tests")
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--generate", action="store_true", help="write canonical parity/inventory.json")
    action.add_argument("--check", action="store_true", help="fail when generated inventory differs from fragments")
    args = parser.parse_args()
    try:
        validate(args.root.resolve(), "generate" if args.generate else "check")
    except ValueError as error:
        print(f"parity inventory invalid: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
