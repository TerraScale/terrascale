#!/usr/bin/env python3
"""Black-box tests for the pinned V1 citation validator."""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPO = Path(__file__).resolve().parents[1]
CLI = REPO / "scripts" / "validate_v1_citations.py"


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value) + "\n", encoding="utf-8")


def fixture(root: Path, scope: str) -> None:
    write_json(
        root / "parity" / "contracts" / "fixture.json",
        {
            "schema_version": "1.0",
            "domain": "fixture",
            "owner": "WS-CONTRACT/fixture",
            "items": [
                {
                    "id": "PB-1000",
                    "name": "fixture",
                    "classification": "core",
                    "v1_evidence": {"scope": scope},
                }
            ],
        },
    )


class V1CitationTests(unittest.TestCase):
    def run_cli(self, root: Path, v1: Path, *extra: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(CLI), "--root", str(root), "--v1-root", str(v1), *extra],
            capture_output=True,
            text=True,
            check=False,
        )

    def test_known_good_filename_with_space_and_symbol(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            (v1 / "src" / "folder with space").mkdir(parents=True)
            (v1 / "test" / "known").mkdir(parents=True)
            (v1 / "src" / "folder with space" / "source.cs").write_text("class KnownSymbol {}\n", encoding="utf-8")
            (v1 / "test" / "known" / "known.cs").write_text("void KnownTest() {}\n", encoding="utf-8")
            fixture(root, "[v1:src/folder with space/source.cs::KnownSymbol] [v1:test/known/known.cs::KnownTest]")
            result = self.run_cli(root, v1)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_missing_path_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            fixture(root, "[v1:src/missing.cs::Missing]")
            result = self.run_cli(root, v1)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("does not name an existing file", result.stderr)

    def test_ambiguous_test_glob_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            for directory in (v1 / "test" / "one", v1 / "test" / "two"):
                directory.mkdir(parents=True)
                (directory / "same.cs").write_text("void SameTest() {}\n", encoding="utf-8")
            fixture(root, "[v1:test/**/same.cs::SameTest]")
            result = self.run_cli(root, v1)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("glob matched 2 files", result.stderr)

    def test_missing_explicit_citation_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            fixture(root, "V1 behavior without a reproducible source reference")
            result = self.run_cli(root, v1)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("requires an explicit V1 or charter citation", result.stderr)


if __name__ == "__main__":
    unittest.main()
