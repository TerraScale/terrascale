#!/usr/bin/env python3
"""Validate the parity inventory invariants without third-party packages."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCHEMA = ROOT / "parity" / "parity.schema.json"
INVENTORY = ROOT / "parity" / "inventory.json"
COMMIT = re.compile(r"^[0-9a-f]{40}$")
EVIDENCE = re.compile(r"^E-[0-9]{3}$")
ITEM = re.compile(r"^PB-[0-9]{4}$")
PROOF = re.compile(r"^P-[0-9]{3}$")
STATUSES = {"unresolved", "in-progress", "implemented", "qualified", "accepted-exclusion"}
CLASSIFICATIONS = {"core", "optional", "legacy", "admin", "protocol"}


def fail(message: str) -> None:
    raise ValueError(message)


def require(value: object, path: str) -> None:
    if value is None:
        fail(f"{path} is required")


def validate() -> None:
    try:
        json.loads(SCHEMA.read_text(encoding="utf-8"))
        data = json.loads(INVENTORY.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"invalid JSON or unreadable file: {error}")

    require(data, "inventory")
    if data.get("schema_version") != "1.0":
        fail("schema_version must be 1.0")
    source = data.get("source")
    if not isinstance(source, dict) or source.get("product") != "TerraBase V1":
        fail("source.product must be TerraBase V1")
    if not isinstance(source.get("commit"), str) or not COMMIT.fullmatch(source["commit"]):
        fail("source.commit must be a 40-character lowercase commit")
    evidence_ids = source.get("evidence_ids")
    if not isinstance(evidence_ids, list) or not evidence_ids or len(set(evidence_ids)) != len(evidence_ids):
        fail("source.evidence_ids must be a non-empty unique list")
    if any(not isinstance(item, str) or not EVIDENCE.fullmatch(item) for item in evidence_ids):
        fail("source.evidence_ids contains an invalid evidence ID")
    if set(data.get("status_values", [])) != STATUSES:
        fail("status_values must enumerate the five known statuses")

    items = data.get("items")
    if not isinstance(items, list) or not items:
        fail("items must be a non-empty list")
    ids: set[str] = set()
    for index, item in enumerate(items):
        path = f"items[{index}]"
        if not isinstance(item, dict):
            fail(f"{path} must be an object")
        item_id = item.get("id")
        if not isinstance(item_id, str) or not ITEM.fullmatch(item_id) or item_id in ids:
            fail(f"{path}.id must be unique and match PB-NNNN")
        ids.add(item_id)
        if not isinstance(item.get("name"), str) or not item["name"].strip():
            fail(f"{path}.name must be non-empty")
        if item.get("classification") not in CLASSIFICATIONS:
            fail(f"{path}.classification is invalid")
        v1 = item.get("v1_evidence")
        if not isinstance(v1, dict) or not COMMIT.fullmatch(str(v1.get("source_commit", ""))):
            fail(f"{path}.v1_evidence.source_commit is invalid")
        row_evidence = v1.get("evidence_ids")
        if not isinstance(row_evidence, list) or not row_evidence:
            fail(f"{path}.v1_evidence.evidence_ids must be non-empty")
        if any(not isinstance(value, str) or not EVIDENCE.fullmatch(value) for value in row_evidence):
            fail(f"{path}.v1_evidence contains an invalid evidence ID")
        if not set(row_evidence).issubset(set(evidence_ids)):
            fail(f"{path}.v1_evidence references an unlisted source evidence ID")
        v2 = item.get("v2")
        if not isinstance(v2, dict) or not all(isinstance(v2.get(key), str) and v2[key].strip() for key in ("owner", "public_seam")):
            fail(f"{path}.v2 must name an owner and public seam")
        if v2.get("status") not in STATUSES:
            fail(f"{path}.v2.status is invalid")
        contract = item.get("public_contract")
        if not isinstance(contract, dict) or contract.get("parity_required") is not True:
            fail(f"{path}.public_contract.parity_required must be true")
        if not isinstance(contract.get("semantics"), list) or not contract["semantics"]:
            fail(f"{path}.public_contract.semantics must be non-empty")
        proofs = item.get("proof_ids")
        if not isinstance(proofs, list) or len(set(proofs)) != len(proofs):
            fail(f"{path}.proof_ids must be a unique list")
        if any(not isinstance(value, str) or not PROOF.fullmatch(value) for value in proofs):
            fail(f"{path}.proof_ids contains an invalid Proof ID")
        if not isinstance(item.get("downstream_impact"), list) or not item["downstream_impact"]:
            fail(f"{path}.downstream_impact must be non-empty")
        risk = item.get("risk")
        if not isinstance(risk, dict) or risk.get("level") not in {"low", "medium", "high", "critical"}:
            fail(f"{path}.risk.level is invalid")
        if not isinstance(risk.get("description"), str) or not risk["description"].strip():
            fail(f"{path}.risk.description must be non-empty")

    print(f"parity inventory valid: {len(items)} items, schema 1.0, source {source['commit']}")


if __name__ == "__main__":
    try:
        validate()
    except ValueError as error:
        print(f"parity inventory invalid: {error}", file=sys.stderr)
        sys.exit(1)
