#!/usr/bin/env python3
"""Black-box tests for the repository secret scanner."""

from __future__ import annotations

import subprocess
import tempfile
import unittest
from pathlib import Path


REPO = Path(__file__).resolve().parents[1]
CLI = REPO / "scripts" / "check_secrets.sh"


class SecretScanTests(unittest.TestCase):
    def run_cli(self, root: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [str(CLI), "--root", str(root)],
            capture_output=True,
            text=True,
            check=False,
        )

    @staticmethod
    def create_root(temporary: str, *, with_tests: bool = False) -> Path:
        root = Path(temporary) / "fixture"
        (root / "src").mkdir(parents=True)
        (root / "scripts").mkdir()
        if with_tests:
            (root / "tests").mkdir()
        return root

    def test_missing_tests_root_is_reported_and_is_not_claimed_scanned(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = self.create_root(temporary)
            (root / "src" / "safe.txt").write_text("safe\n", encoding="utf-8")
            result = self.run_cli(root)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("secret scan skipped missing roots: tests", result.stdout)
            self.assertIn("secret scan passed for:", result.stdout)
            self.assertNotIn("source, tests, and scripts", result.stdout)

    def test_all_missing_declared_roots_is_honest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "fixture"
            root.mkdir()
            result = self.run_cli(root)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("secret scan skipped missing roots: src tests scripts", result.stdout)
            self.assertIn("no declared scan roots exist", result.stdout)

    def test_secret_detection_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = self.create_root(temporary)
            (root / "src" / "unsafe.txt").write_text('password = "fixture-value"\n', encoding="utf-8")
            result = self.run_cli(root)
            self.assertEqual(result.returncode, 1)
            self.assertIn("possible secret material found", result.stderr)

    def test_successful_scan_includes_only_existing_roots(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = self.create_root(temporary, with_tests=True)
            (root / "src" / "safe.txt").write_text("safe\n", encoding="utf-8")
            (root / "tests" / "safe.txt").write_text("safe\n", encoding="utf-8")
            result = self.run_cli(root)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("secret scan passed for:", result.stdout)
            self.assertNotIn("skipped missing roots", result.stdout)
            self.assertIn(str(root / "src"), result.stdout)
            self.assertIn(str(root / "tests"), result.stdout)
            self.assertIn(str(root / "scripts"), result.stdout)


if __name__ == "__main__":
    unittest.main()
