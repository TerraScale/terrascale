#!/usr/bin/env python3
"""Black-box tests for the repository product-name migration check."""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
CHECKER = REPOSITORY_ROOT / "scripts" / "check_product_name.py"
ALLOWLIST = Path("docs/rename/legacy-name-allowlist.tsv")
PRODUCT_PARTS = ("terra", "base", "v2")
SHORT_TOKEN = "tb" + "v2"
MAX_FILE_BYTES = 8 * 1024 * 1024


class ProductNameCheckTests(unittest.TestCase):
    """Exercises the checker through temporary Git repositories."""

    def create_repository(
        self,
        tracked_files: dict[str, str | bytes] | None = None,
        untracked_files: dict[str, str | bytes] | None = None,
    ) -> Path:
        """Creates one disposable repository containing the real checker."""

        temporary = tempfile.TemporaryDirectory(prefix="terrabase-product-name-")
        self.addCleanup(temporary.cleanup)
        repository = Path(temporary.name)
        (repository / "scripts").mkdir(parents=True)
        shutil.copyfile(CHECKER, repository / "scripts" / CHECKER.name)
        self.write_file(
            repository,
            ALLOWLIST.as_posix(),
            "# path<TAB>expected-match-count<TAB>match-fingerprint-sha256<TAB>reason\n",
        )
        for relative_path, content in (tracked_files or {}).items():
            self.write_file(repository, relative_path, content)

        self.git(repository, "init", "--quiet")
        self.git(repository, "add", ".")
        for relative_path, content in (untracked_files or {}).items():
            self.write_file(repository, relative_path, content)
        return repository

    @staticmethod
    def write_file(repository: Path, relative_path: str, content: str | bytes) -> None:
        """Writes one fixture, creating only its named parents."""

        path = repository / relative_path
        path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(content, bytes):
            path.write_bytes(content)
        else:
            path.write_text(content, encoding="utf-8")

    @staticmethod
    def git(repository: Path, *arguments: str) -> None:
        """Runs one non-interactive Git fixture command."""

        result = subprocess.run(
            ["git", *arguments],
            cwd=repository,
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode != 0:
            raise AssertionError(result.stderr)

    @staticmethod
    def run_checker(repository: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
        """Runs the copied checker with captured diagnostics."""

        return subprocess.run(
            [sys.executable, "scripts/check_product_name.py", *arguments],
            cwd=repository,
            capture_output=True,
            text=True,
            check=False,
        )

    def test_accepts_canonical_name_and_versioned_receipt_contract(self) -> None:
        repository = self.create_repository(
            {
                "README.md": (
                    "TerraBase\n"
                    "benchmark-receipt/v2 V2Faster V2Regression "
                    "v2-faster v2-regression\n"
                )
            }
        )
        self.assertEqual(self.run_checker(repository).returncode, 0)
        self.assertEqual(self.run_checker(repository, "--tracked-only").returncode, 0)

    def test_rejects_every_supported_separator_and_mixed_case(self) -> None:
        for separator in ("", "-", "_", " ", ".", "-_ "):
            with self.subTest(separator=separator):
                retired = separator.join(PRODUCT_PARTS).upper()
                repository = self.create_repository({"README.md": retired})
                result = self.run_checker(repository)
                self.assertEqual(result.returncode, 1)
                self.assertIn("retired-product-name", result.stderr)
                self.assertIn("README.md:1:1", result.stderr)

    def test_rejects_separator_and_case_variants_of_short_prefix_and_lane_symbol(self) -> None:
        for separator in ("", "-", "_", " ", ".", "-_ "):
            with self.subTest(separator=separator):
                short_prefix = separator.join(("tb", "v2")).upper()
                lane_symbol = separator.join(("v2", "lane")).swapcase()
                repository = self.create_repository(
                    {"src/identity.txt": f"{short_prefix}-operation\n{lane_symbol}\n"}
                )
                result = self.run_checker(repository)
                self.assertEqual(result.returncode, 1)
                self.assertIn("retired-short-prefix", result.stderr)
                self.assertIn("retired-lane-symbol", result.stderr)

    def test_reports_tracked_and_untracked_paths_and_text(self) -> None:
        retired_path = f"docs/{'-'.join(PRODUCT_PARTS)}.md"
        repository = self.create_repository(
            {retired_path: "historical\n"},
            {"scratch.txt": "_".join(PRODUCT_PARTS)},
        )
        result = self.run_checker(repository)
        self.assertEqual(result.returncode, 1)
        self.assertIn("tracked retired-product-name", result.stderr)
        self.assertIn("untracked retired-product-name", result.stderr)
        self.assertIn("path byte", result.stderr)

    def test_rejects_short_prefix_and_lane_symbol_in_paths(self) -> None:
        short_path = f"fixtures/{'-'.join(('tb', 'v2'))}.bin"
        lane_path = f"fixtures/{'_'.join(('v2', 'lane'))}.txt"
        repository = self.create_repository(
            {
                short_path: "TerraBase\n",
                lane_path: "TerraBase\n",
            }
        )
        result = self.run_checker(repository)
        self.assertEqual(result.returncode, 1)
        self.assertIn("retired-short-prefix", result.stderr)
        self.assertIn("retired-lane-symbol", result.stderr)
        self.assertGreaterEqual(result.stderr.count("path byte"), 2)

    def test_tracked_only_ignores_ordinary_untracked_files(self) -> None:
        repository = self.create_repository(
            {"README.md": "TerraBase\n"},
            {"scratch.txt": "_".join(PRODUCT_PARTS)},
        )
        self.assertEqual(self.run_checker(repository).returncode, 1)
        self.assertEqual(self.run_checker(repository, "--tracked-only").returncode, 0)

    def test_allowance_binds_count_location_rule_and_token_bytes(self) -> None:
        evidence_path = "docs/evidence/record.md"
        retired = "".join(PRODUCT_PARTS)
        repository = self.create_repository({evidence_path: retired})

        inventory = self.run_checker(repository, "--inventory")
        self.assertEqual(inventory.returncode, 0, inventory.stderr)
        record = next(line for line in inventory.stdout.splitlines() if line.startswith(evidence_path))
        path, count, fingerprint = record.split("\t")
        self.write_file(
            repository,
            ALLOWLIST.as_posix(),
            (
                "# path<TAB>expected-match-count<TAB>match-fingerprint-sha256<TAB>reason\n"
                f"{path}\t{count}\t{fingerprint}\tExact historical evidence.\n"
            ),
        )
        self.assertEqual(self.run_checker(repository).returncode, 0)

        self.write_file(repository, evidence_path, retired.upper())
        changed_token = self.run_checker(repository)
        self.assertEqual(changed_token.returncode, 1)
        self.assertIn("allowance mismatch", changed_token.stderr)

        self.write_file(repository, evidence_path, f"prefix {retired}")
        changed_location = self.run_checker(repository)
        self.assertEqual(changed_location.returncode, 1)
        self.assertIn("allowance mismatch", changed_location.stderr)

        self.write_file(repository, evidence_path, f"{retired}\n{retired}\n")
        changed_count = self.run_checker(repository)
        self.assertEqual(changed_count.returncode, 1)
        self.assertIn("found 2", changed_count.stderr)

    def test_stale_allowance_fails_after_the_token_is_removed(self) -> None:
        evidence_path = "docs/evidence/record.md"
        repository = self.create_repository({evidence_path: SHORT_TOKEN})
        inventory = self.run_checker(repository, "--inventory")
        record = next(line for line in inventory.stdout.splitlines() if line.startswith(evidence_path))
        path, count, fingerprint = record.split("\t")
        self.write_file(
            repository,
            ALLOWLIST.as_posix(),
            (
                "# path<TAB>expected-match-count<TAB>match-fingerprint-sha256<TAB>reason\n"
                f"{path}\t{count}\t{fingerprint}\tExact compatibility identity.\n"
            ),
        )
        self.write_file(repository, evidence_path, "TerraBase")
        result = self.run_checker(repository)
        self.assertEqual(result.returncode, 1)
        self.assertIn("found 0", result.stderr)

    def test_fails_closed_on_oversized_file(self) -> None:
        repository = self.create_repository(
            {"oversized.txt": b"a" * (MAX_FILE_BYTES + 1)}
        )
        result = self.run_checker(repository)
        self.assertEqual(result.returncode, 1)
        self.assertIn("file exceeds the 8388608-byte scan limit", result.stderr)

    @unittest.skipIf(not hasattr(os, "symlink"), "symbolic links unavailable")
    def test_fails_closed_on_tracked_symbolic_link(self) -> None:
        repository = self.create_repository({"target.txt": "TerraBase\n"})
        os.symlink("target.txt", repository / "link.txt")
        self.git(repository, "add", "link.txt")
        result = self.run_checker(repository)
        self.assertEqual(result.returncode, 1)
        self.assertIn("symbolic link requires explicit review", result.stderr)

    def test_skips_binary_content_but_still_checks_its_path(self) -> None:
        retired = "".join(PRODUCT_PARTS).encode("ascii")
        repository = self.create_repository({"fixture.bin": b"\x00" + retired})
        self.assertEqual(self.run_checker(repository).returncode, 0)

        retired_path = f"{''.join(PRODUCT_PARTS)}.bin"
        repository = self.create_repository({retired_path: b"\x00payload"})
        result = self.run_checker(repository)
        self.assertEqual(result.returncode, 1)
        self.assertIn("path byte", result.stderr)

    def test_skips_nested_repository_content(self) -> None:
        repository = self.create_repository({"README.md": "TerraBase\n"})
        nested = repository / "Dashboard"
        nested.mkdir()
        self.git(nested, "init", "--quiet")
        (nested / "README.md").write_text("".join(PRODUCT_PARTS), encoding="utf-8")
        result = self.run_checker(repository)
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
