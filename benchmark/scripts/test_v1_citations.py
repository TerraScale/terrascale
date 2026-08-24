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


def git_fixture(root: Path) -> str:
    subprocess.run(["git", "init", "--quiet", str(root)], check=True)
    subprocess.run(["git", "-C", str(root), "config", "user.email", "fixture@example.invalid"], check=True)
    subprocess.run(["git", "-C", str(root), "config", "user.name", "Fixture"], check=True)
    (root / "src" / "folder with space").mkdir(parents=True)
    (root / "test" / "known").mkdir(parents=True)
    (root / "src" / "folder with space" / "source.cs").write_text(
        "public interface IFoo { void KnownMember(); }\npublic class KnownSymbol {}\n",
        encoding="utf-8",
    )
    (root / "test" / "known" / "known.cs").write_text(
        "public sealed class FixtureTests { public void KnownTest() {} }\n",
        encoding="utf-8",
    )
    subprocess.run(["git", "-C", str(root), "add", "."], check=True)
    subprocess.run(["git", "-C", str(root), "commit", "--quiet", "-m", "fixture"], check=True)
    return subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()


class V1CitationTests(unittest.TestCase):
    def run_cli(
        self,
        root: Path,
        v1: Path | None,
        expected: str | None = None,
        *extra: str,
    ) -> subprocess.CompletedProcess[str]:
        command = [sys.executable, str(CLI), "--root", str(root), "--v1-root", str(v1)]
        if v1 is None:
            command = [sys.executable, str(CLI), "--root", str(root)]
        if expected:
            command.extend(["--expected-commit", expected])
        command.extend(extra)
        return subprocess.run(command, capture_output=True, text=True, check=False)

    def test_declaration_and_path_only_source_and_test_pass(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(
                root,
                "[v1:src/folder with space/source.cs::IFoo.KnownMember] "
                "[v1:src/folder with space/source.cs] "
                "[v1:test:test/known/known.cs#KnownTest]",
            )
            result = self.run_cli(root, v1, expected)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("1 test citations", result.stdout)

    def test_incidental_substring_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, "[v1:src/folder with space/source.cs::Known]")
            result = self.run_cli(root, v1, expected)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("declaration is absent", result.stderr)

    def test_non_git_v1_root_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            (v1 / "src").mkdir(parents=True)
            (v1 / "src" / "source.cs").write_text("class KnownSymbol {}\n", encoding="utf-8")
            fixture(root, "[v1:src/source.cs::KnownSymbol]")
            result = self.run_cli(root, v1, "0" * 40)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("not a Git worktree", result.stderr)

    def test_wrong_commit_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            git_fixture(v1)
            fixture(root, "[v1:src/folder with space/source.cs::KnownSymbol]")
            result = self.run_cli(root, v1, "0" * 40)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("expected pinned commit", result.stderr)

    def test_git_worktree_file_is_supported(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            repository = Path(temporary) / "repository"
            linked = Path(temporary) / "linked"
            expected = git_fixture(repository)
            subprocess.run(["git", "-C", str(repository), "worktree", "add", "--detach", "--quiet", str(linked)], check=True)
            fixture(
                root,
                "[v1:src/folder with space/source.cs::KnownSymbol] [v1:test:test/known/known.cs]",
            )
            result = self.run_cli(root, linked, expected)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_missing_path_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, "[v1:src/missing.cs::Missing]")
            result = self.run_cli(root, v1, expected)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("does not name an existing file", result.stderr)

    def test_contract_mode_reports_gap_and_strict_mode_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, "[v1:src/folder with space/source.cs::KnownSymbol] [v1:test-gap:coverage is not isolated]")
            result = self.run_cli(root, v1, expected)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("1 test gaps", result.stdout)
            self.assertIn("CONTRACT AUDIT COMPLETE WITH 1 UNRESOLVED TEST GAPS", result.stdout)
            strict = self.run_cli(root, v1, expected, "--strict")
            self.assertNotEqual(strict.returncode, 0)
            self.assertIn("STRICT V1 QUALIFICATION FAILED", strict.stderr)

    def test_gap_report_is_machine_readable(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, "[v1:src/folder with space/source.cs::KnownSymbol] [v1:test-gap:manual parity test remains]")
            report = root / "parity" / "v1-test-gaps.json"
            result = self.run_cli(root, v1, expected, "--gap-report", str(report))
            self.assertEqual(result.returncode, 0, result.stderr)
            payload = json.loads(report.read_text(encoding="utf-8"))
            self.assertEqual(payload["status"], "complete_with_gaps")
            self.assertEqual(payload["counts"]["test_gaps"], 1)

    def test_missing_v1_root_is_unrun_and_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            fixture(root, "[v1:src/source.cs::KnownSymbol] [v1:test-gap:checkout unavailable]")
            result = self.run_cli(root, None)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("UNRUN", result.stderr)

    def test_expected_commit_override_supports_black_box_fixture(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, "[v1:src/folder with space/source.cs::KnownSymbol] [v1:test:test/known/known.cs]")
            result = self.run_cli(root, v1, expected)
            self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
