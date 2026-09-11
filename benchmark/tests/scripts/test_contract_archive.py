#!/usr/bin/env python3
"""Adversarial and reproducibility tests for the producer archive handoff."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "package-contract-archive.py"


class ContractArchiveTests(unittest.TestCase):
    def setUp(self):
        self.work = Path(tempfile.mkdtemp(prefix="terrascale-contract-test-"))
        self.source = self.work / "contracts"
        shutil.copytree(ROOT / "contracts", self.source)

    def tearDown(self):
        shutil.rmtree(self.work)

    def run_package(self, output: Path):
        return subprocess.run(
            ["python3", str(SCRIPT), "--source", str(self.source), "--version", "v1", "--output", str(output)],
            check=False,
            capture_output=True,
            text=True,
        )

    def test_archive_is_reproducible_and_has_source_manifest(self):
        first = self.work / "one.tar"
        second = self.work / "two.tar"
        self.assertEqual(self.run_package(first).returncode, 0)
        self.assertEqual(self.run_package(second).returncode, 0)
        self.assertEqual(first.read_bytes(), second.read_bytes())
        digest = hashlib.sha256(first.read_bytes()).hexdigest()
        self.assertEqual(first.with_suffix(".tar.sha256").read_text(), f"{digest}  one.tar\n")
        metadata = json.loads(first.with_suffix(".tar.json").read_text())
        self.assertEqual(metadata["archiveSha256"], digest)
        self.assertEqual(metadata["contractVersion"], "v1")
        with tarfile.open(first) as archive:
            names = archive.getnames()
        self.assertIn("v1/manifest.json", names)
        self.assertTrue(all(name.startswith("v1/") for name in names))

    def test_existing_archive_cannot_be_replaced_after_source_change(self):
        output = self.work / "immutable.tar"
        self.assertEqual(self.run_package(output).returncode, 0)
        manifest = self.source / "manifest.json"
        manifest.write_bytes(manifest.read_bytes() + b" ")
        result = self.run_package(output)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("immutable output", result.stderr)

    def test_symlink_source_is_rejected(self):
        link = self.source / "unsafe"
        link.symlink_to(self.source / "openapi.json")
        result = self.run_package(self.work / "unsafe.tar")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("symlink", result.stderr)


if __name__ == "__main__":
    unittest.main()
