#!/usr/bin/env python3
"""Black-box tests for the benchmark-receipt discipline checker."""

from __future__ import annotations

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
CLI = REPO / "scripts" / "check_benchmark_receipts.py"

CANONICAL = "PostgreSQL-parity-or-better: inconclusive\n"


class BenchmarkReceiptDisciplineTests(unittest.TestCase):
    def run_cli(self, root: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys_python(), str(CLI), "--receipts-root", str(root)],
            capture_output=True,
            text=True,
            check=False,
        )

    @staticmethod
    def receipt(temporary: str, name: str = "seXX-receipt.md") -> Path:
        root = Path(temporary) / "benchmarks"
        root.mkdir(parents=True, exist_ok=True)
        return root / name

    def test_a_compliant_receipt_passes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = self.receipt(temporary)
            path.write_text(
                "# SE-XX Benchmark Receipt\n\n"
                "verdict: inconclusive\n"
                f"{CANONICAL}"
                "note: nothing measured.\n",
                encoding="utf-8",
            )
            result = self.run_cli(path.parent)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("1 receipts", result.stdout)

    def test_every_inconclusive_status_is_honest_and_passes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = self.receipt(temporary)
            path.write_text(f"PostgreSQL-parity-or-better: not met\n", encoding="utf-8")
            result = self.run_cli(path.parent)
            # 'not met' needs an owner line, so this is a separate case below;
            # here only the vocabulary is exercised.
            self.assertEqual(result.returncode, 1)

    def test_a_missing_status_line_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = self.receipt(temporary)
            path.write_text(
                "# SE-XX Benchmark Receipt\n\nLots of numbers, no status.\n", encoding="utf-8"
            )
            result = self.run_cli(path.parent)
            self.assertEqual(result.returncode, 1)
            self.assertIn("no canonical objective-status line", result.stderr)

    def test_a_prose_status_that_is_not_a_canonical_line_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = self.receipt(temporary)
            # The SE-07 receipts originally stated the status in prose; the
            # discipline requires the one-line canonical form a scanner can
            # rely on, so prose alone must fail.
            path.write_text(
                "Objective status against PD-2026-08-26-03: **inconclusive**.\n", encoding="utf-8"
            )
            result = self.run_cli(path.parent)
            self.assertEqual(result.returncode, 1)
            self.assertIn("no canonical objective-status line", result.stderr)

    def test_an_out_of_vocabulary_status_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = self.receipt(temporary)
            path.write_text("PostgreSQL-parity-or-better: probably fine\n", encoding="utf-8")
            result = self.run_cli(path.parent)
            self.assertEqual(result.returncode, 1)
            self.assertIn("outside the vocabulary", result.stderr)

    def test_two_status_lines_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = self.receipt(temporary)
            path.write_text(
                f"{CANONICAL}PostgreSQL-parity-or-better: met\n", encoding="utf-8"
            )
            result = self.run_cli(path.parent)
            self.assertEqual(result.returncode, 1)
            self.assertIn("exactly one is required", result.stderr)

    def test_a_not_met_receipt_requires_a_named_regression_owner(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = self.receipt(temporary)
            path.write_text("PostgreSQL-parity-or-better: not met\n", encoding="utf-8")
            result = self.run_cli(path.parent)
            self.assertEqual(result.returncode, 1)
            self.assertIn("regression follow-up owner", result.stderr)

            path.write_text(
                "PostgreSQL-parity-or-better: not met\n"
                "regression follow-up owner: the named owner\n",
                encoding="utf-8",
            )
            result = self.run_cli(path.parent)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_an_out_of_vocabulary_verdict_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = self.receipt(temporary)
            path.write_text(
                f"{CANONICAL}verdict: v2-much-faster\n", encoding="utf-8"
            )
            result = self.run_cli(path.parent)
            self.assertEqual(result.returncode, 1)
            self.assertIn("verdict 'v2-much-faster' is outside the vocabulary", result.stderr)

    def test_verdicts_used_in_prose_are_not_flagged(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = self.receipt(temporary)
            # Receipts quote the vocabulary in prose and in tables; only a
            # line that *states* a run's verdict is checked.
            path.write_text(
                f"{CANONICAL}"
                "The vocabulary is `v2-faster`, `no-improvement`, `v2-regression`, "
                "`inconclusive`; this row's verdict cell reads `no-improvement`.\n",
                encoding="utf-8",
            )
            result = self.run_cli(path.parent)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_quoting_the_rule_inside_a_fenced_block_is_not_a_status(self) -> None:
        fence = "`" * 3
        with tempfile.TemporaryDirectory() as temporary:
            path = self.receipt(temporary)
            # A receipt that documents the discipline quotes the canonical
            # line; that quotation neither satisfies the rule nor duplicates
            # the receipt's own status line.
            path.write_text(
                f"{CANONICAL}"
                f"{fence}\n"
                "PostgreSQL-parity-or-better: met\n"
                f"{fence}\n"
                "The quoted line above documents the shape only.\n",
                encoding="utf-8",
            )
            result = self.run_cli(path.parent)
            self.assertEqual(result.returncode, 0, result.stderr)

        with tempfile.TemporaryDirectory() as temporary:
            path = self.receipt(temporary)
            path.write_text(
                f"{fence}\n"
                "PostgreSQL-parity-or-better: met\n"
                f"{fence}\n"
                "A fenced quotation alone must not satisfy the rule.\n",
                encoding="utf-8",
            )
            result = self.run_cli(path.parent)
            self.assertEqual(result.returncode, 1)
            self.assertIn("no canonical objective-status line", result.stderr)

    def test_a_receipt_root_with_no_receipts_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            empty = Path(temporary) / "benchmarks"
            empty.mkdir()
            result = self.run_cli(empty)
            self.assertEqual(result.returncode, 1)
            self.assertIn("no receipts", result.stderr)

    def test_the_repository_receipts_pass(self) -> None:
        root = REPO / "evidence" / "benchmarks"
        if not root.is_dir() or not list(root.glob("*.md")):
            self.skipTest("no receipts in this checkout")
        result = self.run_cli(root)
        self.assertEqual(result.returncode, 0, result.stderr)


def sys_python() -> str:
    """The interpreter running this test, so the CLI runs on the same one."""
    return sys.executable


if __name__ == "__main__":
    unittest.main()
