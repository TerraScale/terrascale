#!/usr/bin/env python3
"""Black-box tests for the parity fragment and state CLI."""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPO = Path(__file__).resolve().parents[1]
CLI = REPO / "scripts" / "validate_parity.py"


class ParityToolingTests(unittest.TestCase):
    def fixture(self, directory: Path) -> Path:
        root = directory / "fixture"
        shutil.copytree(REPO / "parity", root / "parity")
        return root

    def run_cli(self, root: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(CLI), "--root", str(root), *arguments],
            capture_output=True,
            text=True,
            check=False,
        )

    @staticmethod
    def read_json(path: Path) -> object:
        return json.loads(path.read_text(encoding="utf-8"))

    @staticmethod
    def write_json(path: Path, value: object) -> None:
        path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")

    def test_generate_check_and_ordering(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = self.fixture(Path(temporary))
            generated = self.run_cli(root, "--generate")
            self.assertEqual(generated.returncode, 0, generated.stderr)
            inventory = self.read_json(root / "parity" / "inventory.json")
            self.assertEqual(
                [item["id"] for item in inventory["items"]],
                sorted(item["id"] for item in inventory["items"]),
            )
            checked = self.run_cli(root, "--check")
            self.assertEqual(checked.returncode, 0, checked.stderr)

    def test_duplicate_fragment_id_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = self.fixture(Path(temporary))
            duplicate_path = root / "parity" / "contracts" / "duplicate.json"
            duplicate = self.read_json(root / "parity" / "contracts" / "documents.json")
            duplicate["domain"] = "duplicate"
            duplicate["owner"] = "WS-CONTRACT/duplicate"
            self.write_json(duplicate_path, duplicate)
            result = self.run_cli(root, "--check")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("duplicate item id PB-2000", result.stderr)

    def test_malformed_fragment_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = self.fixture(Path(temporary))
            path = root / "parity" / "contracts" / "query.json"
            fragment = self.read_json(path)
            del fragment["items"][0]["public_contract"]
            self.write_json(path, fragment)
            result = self.run_cli(root, "--check")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("public_contract", result.stderr)

    def test_generated_inventory_drift_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = self.fixture(Path(temporary))
            generated = self.run_cli(root, "--generate")
            self.assertEqual(generated.returncode, 0, generated.stderr)
            inventory_path = root / "parity" / "inventory.json"
            inventory = self.read_json(inventory_path)
            inventory["items"][0]["name"] = "drift"
            self.write_json(inventory_path, inventory)
            result = self.run_cli(root, "--check")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("generated inventory drift", result.stderr)

    def test_unknown_overlay_id_and_status_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = self.fixture(Path(temporary))
            overlay_path = root / "parity" / "overlays" / "PB-1000.json"
            overlay = self.read_json(overlay_path)
            overlay["item_id"] = "PB-9999"
            self.write_json(overlay_path, overlay)
            result = self.run_cli(root, "--check")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("unknown overlay item id PB-9999", result.stderr)

        with tempfile.TemporaryDirectory() as temporary:
            root = self.fixture(Path(temporary))
            overlay_path = root / "parity" / "overlays" / "PB-1000.json"
            overlay = self.read_json(overlay_path)
            overlay["status"] = "done"
            self.write_json(overlay_path, overlay)
            result = self.run_cli(root, "--check")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("status is invalid", result.stderr)

    def test_status_transition_and_proof_requirements_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = self.fixture(Path(temporary))
            overlay_path = root / "parity" / "overlays" / "PB-1000.json"
            overlay = self.read_json(overlay_path)
            # Explicitly emptied: the live overlay may already carry proofs.
            overlay.update({"status": "implemented", "previous_status": "in-progress", "proof_ids": []})
            self.write_json(overlay_path, overlay)
            result = self.run_cli(root, "--check")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("requires at least one proof ID", result.stderr)

        with tempfile.TemporaryDirectory() as temporary:
            root = self.fixture(Path(temporary))
            overlay_path = root / "parity" / "overlays" / "PB-1000.json"
            overlay = self.read_json(overlay_path)
            overlay.update(
                {"status": "implemented", "previous_status": "qualified", "proof_ids": ["P-001"]}
            )
            self.write_json(overlay_path, overlay)
            result = self.run_cli(root, "--check")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("status transition", result.stderr)

    def test_accepted_exclusion_requires_disposition_and_proof(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = self.fixture(Path(temporary))
            overlay_path = root / "parity" / "overlays" / "PB-1000.json"
            overlay = self.read_json(overlay_path)
            # Explicitly removed: the live overlay may already carry a disposition.
            overlay.pop("disposition", None)
            overlay.update({"status": "accepted-exclusion", "previous_status": "unresolved", "proof_ids": ["P-001"]})
            self.write_json(overlay_path, overlay)
            result = self.run_cli(root, "--check")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("accepted-exclusion requires disposition", result.stderr)

    def test_invalid_proof_id_format_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = self.fixture(Path(temporary))
            overlay_path = root / "parity" / "overlays" / "PB-1000.json"
            overlay = self.read_json(overlay_path)
            overlay["proof_ids"] = ["P-invalid"]
            self.write_json(overlay_path, overlay)
            result = self.run_cli(root, "--check")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("contains an invalid Proof ID", result.stderr)

    def test_duplicate_proof_ids_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = self.fixture(Path(temporary))
            overlay_path = root / "parity" / "overlays" / "PB-1000.json"
            overlay = self.read_json(overlay_path)
            overlay["proof_ids"] = ["P-001", "P-001"]
            self.write_json(overlay_path, overlay)
            result = self.run_cli(root, "--check")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("proof_ids must be a unique list", result.stderr)


if __name__ == "__main__":
    unittest.main()
