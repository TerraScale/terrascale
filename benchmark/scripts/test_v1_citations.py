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
    fixture_rows(root, [("PB-1000", scope)])


def fixture_rows(root: Path, rows: list[tuple[str, str]]) -> None:
    write_json(
        root / "parity" / "contracts" / "fixture.json",
        {
            "schema_version": "1.0",
            "domain": "fixture",
            "owner": "WS-CONTRACT/fixture",
            "items": [
                {
                    "id": row_id,
                    "name": "fixture",
                    "classification": "core",
                    "v1_evidence": {"scope": scope},
                }
                for row_id, scope in rows
            ],
        },
    )


def search_record_fixture(root: Path) -> None:
    (root / "docs" / "parity").mkdir(parents=True, exist_ok=True)
    (root / "docs" / "parity" / "search-record.md").write_text(
        "# fixture search record\n\n## PB-1000 section\n\nNo V1 test exists in the pinned checkout.\n",
        encoding="utf-8",
    )


def adjudication_entry(**overrides: object) -> dict[str, object]:
    entry: dict[str, object] = {
        "decision_id": "PD-2026-01-02-03",
        "search_record": "docs/parity/search-record.md#PB-1000 section",
        "v2_proof_obligation": "V2-side tests in the storage lane plus a Recovery Receipt",
        "owner": "WS-TEST/fixture",
    }
    entry.update(overrides)
    return entry


def write_adjudications(root: Path, entries: dict[str, dict[str, object]]) -> None:
    write_json(
        root / "parity" / "adjudications.json",
        {"schema_version": "1.0", "adjudications": entries},
    )


GAPPED_SCOPE = "[v1:src/folder with space/source.cs::KnownSymbol] [v1:test-gap:coverage is not isolated]"


def git_fixture(root: Path) -> str:
    subprocess.run(["git", "init", "--quiet", str(root)], check=True)
    subprocess.run(["git", "-C", str(root), "config", "user.email", "fixture@example.invalid"], check=True)
    subprocess.run(["git", "-C", str(root), "config", "user.name", "Fixture"], check=True)
    (root / "src" / "folder with space").mkdir(parents=True)
    (root / "test" / "known").mkdir(parents=True)
    (root / "src" / "folder with space" / "source.cs").write_text(
        """public interface IFoo { void KnownMember(); }
public class KnownSymbol
{
    public KnownSymbol() { }
    public void DeclaredMethod() { }
    public string DeclaredProperty { get; set; }
    private int DeclaredField = 1;
    public void CallSites()
    {
        return CalledOnly();
        var value = AssignedOnly;
        receiver.MemberOnly();
        // CommentOnly
        var literal = \"LiteralOnly\";
    }
}
public sealed class FixtureTests { public void KnownTest() {} }
""",
        encoding="utf-8",
    )
    (root / "test" / "known" / "known.cs").write_text(
        "public sealed class FixtureTests { public void KnownTest() {} }\n",
        encoding="utf-8",
    )
    outside = root.parent / "outside.cs"
    outside.write_text("public class EscapingSymbol {}\n", encoding="utf-8")
    (root / "src" / "in-root-alias.cs").symlink_to(Path("folder with space/source.cs"))
    (root / "src" / "escape.cs").symlink_to(outside)
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
                "[v1:src/in-root-alias.cs::KnownSymbol] "
                "[v1:test:test/known/known.cs#KnownTest]",
            )
            result = self.run_cli(root, v1, expected)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("1 test citations", result.stdout)

    def test_declaration_anchors_accept_types_constructors_members_and_tests(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            accepted = (
                "[v1:src/folder with space/source.cs::KnownSymbol]",
                "[v1:src/folder with space/source.cs::KnownSymbol.KnownSymbol]",
                "[v1:src/folder with space/source.cs::KnownSymbol.DeclaredMethod]",
                "[v1:src/folder with space/source.cs::KnownSymbol.DeclaredProperty]",
                "[v1:src/folder with space/source.cs::KnownSymbol.DeclaredField]",
                "[v1:src/folder with space/source.cs] [v1:test:test/known/known.cs#FixtureTests.KnownTest]",
            )
            for scope in accepted:
                fixture(root, f"{scope} [v1:test:test/known/known.cs]")
                result = self.run_cli(root, v1, expected)
                self.assertEqual(result.returncode, 0, f"{scope}: {result.stderr}")

    def test_declaration_anchors_reject_calls_assignments_access_comments_literals(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            rejected = (
                "CalledOnly",
                "AssignedOnly",
                "MemberOnly",
                "CommentOnly",
                "LiteralOnly",
                "IncidentalOnly",
            )
            for symbol in rejected:
                fixture(root, f"[v1:src/folder with space/source.cs::{symbol}]")
                result = self.run_cli(root, v1, expected)
                self.assertNotEqual(result.returncode, 0, symbol)
                self.assertIn("declaration is absent", result.stderr, symbol)

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

    def test_tracked_dirty_v1_root_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            source = v1 / "src" / "folder with space" / "source.cs"
            source.write_text(source.read_text(encoding="utf-8") + "\n// tracked edit\n", encoding="utf-8")
            fixture(root, "[v1:src/folder with space/source.cs::KnownSymbol]")
            result = self.run_cli(root, v1, expected)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("clean Git worktree", result.stderr)

    def test_staged_dirty_v1_root_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            source = v1 / "src" / "folder with space" / "source.cs"
            source.write_text(source.read_text(encoding="utf-8") + "\n// staged edit\n", encoding="utf-8")
            subprocess.run(["git", "-C", str(v1), "add", str(source)], check=True)
            fixture(root, "[v1:src/folder with space/source.cs::KnownSymbol]")
            result = self.run_cli(root, v1, expected)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("clean Git worktree", result.stderr)

    def test_untracked_v1_root_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            (v1 / "untracked.cs").write_text("class Untracked {}\n", encoding="utf-8")
            fixture(root, "[v1:src/folder with space/source.cs::KnownSymbol]")
            result = self.run_cli(root, v1, expected)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("clean Git worktree", result.stderr)

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

    def test_parent_path_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, "[v1:src/../src/folder with space/source.cs::KnownSymbol]")
            result = self.run_cli(root, v1, expected)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("stay inside", result.stderr)

    def test_absolute_path_token_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, "[v1:/etc/passwd::KnownSymbol]")
            result = self.run_cli(root, v1, expected)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("malformed citation token", result.stderr)

    def test_escaping_symlink_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, "[v1:src/escape.cs::EscapingSymbol]")
            result = self.run_cli(root, v1, expected)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("outside its supplied root", result.stderr)

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

    def test_adjudicated_gap_passes_strict_mode(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, GAPPED_SCOPE)
            search_record_fixture(root)
            write_adjudications(root, {"PB-1000": adjudication_entry()})
            result = self.run_cli(root, v1, expected, "--strict")
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("STRICT V1 QUALIFICATION PASSED", result.stdout)
            self.assertIn("1 test gaps (1 adjudicated, 0 unresolved)", result.stdout)
            self.assertIn("ADJUDICATED NO-V1-TEST GAPS: 1", result.stdout)
            self.assertIn("PD-2026-01-02-03", result.stdout)

    def test_unresolved_gap_still_fails_strict(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, GAPPED_SCOPE)
            search_record_fixture(root)
            result = self.run_cli(root, v1, expected, "--strict")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("STRICT V1 QUALIFICATION FAILED: 1 UNRESOLVED TEST GAPS", result.stderr)
            self.assertIn("1 test gaps (0 adjudicated, 1 unresolved)", result.stdout)

    def test_adjudicated_gap_contract_mode_still_records_gaps(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, GAPPED_SCOPE)
            search_record_fixture(root)
            write_adjudications(root, {"PB-1000": adjudication_entry()})
            result = self.run_cli(root, v1, expected)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("CONTRACT AUDIT COMPLETE WITH 0 UNRESOLVED TEST GAPS (1 ADJUDICATED)", result.stdout)

    def test_adjudication_missing_decision_id_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, GAPPED_SCOPE)
            search_record_fixture(root)
            entry = adjudication_entry()
            del entry["decision_id"]
            write_adjudications(root, {"PB-1000": entry})
            result = self.run_cli(root, v1, expected, "--strict")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("decision_id", result.stderr)

    def test_adjudication_missing_v2_proof_obligation_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, GAPPED_SCOPE)
            search_record_fixture(root)
            entry = adjudication_entry()
            del entry["v2_proof_obligation"]
            write_adjudications(root, {"PB-1000": entry})
            result = self.run_cli(root, v1, expected, "--strict")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("v2_proof_obligation", result.stderr)

    def test_adjudication_missing_search_record_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, GAPPED_SCOPE)
            entry = adjudication_entry()
            del entry["search_record"]
            write_adjudications(root, {"PB-1000": entry})
            result = self.run_cli(root, v1, expected, "--strict")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("search_record", result.stderr)

    def test_adjudication_missing_owner_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, GAPPED_SCOPE)
            search_record_fixture(root)
            entry = adjudication_entry()
            del entry["owner"]
            write_adjudications(root, {"PB-1000": entry})
            result = self.run_cli(root, v1, expected, "--strict")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("owner", result.stderr)

    def test_adjudication_decision_id_pattern_is_enforced(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, GAPPED_SCOPE)
            search_record_fixture(root)
            for decision_id in ("PD-2026-1-02-03", "PD-2026-01-02", "decision-1", "PD-2026-01-02-3"):
                write_adjudications(root, {"PB-1000": adjudication_entry(decision_id=decision_id)})
                result = self.run_cli(root, v1, expected, "--strict")
                self.assertNotEqual(result.returncode, 0, decision_id)
                self.assertIn("PD-YYYY-MM-DD-NN", result.stderr, decision_id)

    def test_adjudication_search_record_must_resolve(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, GAPPED_SCOPE)
            search_record_fixture(root)
            for search_record in (
                "docs/parity/missing-record.md#PB-1000 section",
                "docs/parity/search-record.md#absent section",
                "docs/parity/search-record.md",
            ):
                write_adjudications(root, {"PB-1000": adjudication_entry(search_record=search_record)})
                result = self.run_cli(root, v1, expected, "--strict")
                self.assertNotEqual(result.returncode, 0, search_record)
                self.assertIn("search record", result.stderr, search_record)

    def test_adjudicated_gap_reported_distinctly(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, GAPPED_SCOPE)
            search_record_fixture(root)
            entry = adjudication_entry()
            write_adjudications(root, {"PB-1000": entry})
            report = root / "parity" / "v1-test-gaps.json"
            result = self.run_cli(root, v1, expected, "--strict", "--gap-report", str(report))
            self.assertEqual(result.returncode, 0, result.stderr)
            payload = json.loads(report.read_text(encoding="utf-8"))
            self.assertEqual(payload["status"], "complete_with_adjudicated_gaps")
            self.assertEqual(payload["counts"]["test_gaps"], 1)
            self.assertEqual(payload["counts"]["test_gaps_adjudicated"], 1)
            self.assertEqual(payload["counts"]["test_gaps_unresolved"], 0)
            echoed = payload["gaps"][0]["adjudicated"]
            self.assertEqual(echoed["decision_id"], entry["decision_id"])
            self.assertEqual(echoed["search_record"], entry["search_record"])
            self.assertEqual(echoed["v2_proof_obligation"], entry["v2_proof_obligation"])
            self.assertEqual(echoed["owner"], entry["owner"])

    def test_non_adjudicated_rows_unaffected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture_rows(
                root,
                [
                    ("PB-1000", GAPPED_SCOPE),
                    ("PB-1001", "[v1:src/folder with space/source.cs::KnownSymbol] [v1:test:test/known/known.cs]"),
                ],
            )
            search_record_fixture(root)
            write_adjudications(root, {"PB-1000": adjudication_entry()})
            result = self.run_cli(root, v1, expected, "--strict")
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("2 rows", result.stdout)
            self.assertIn("1 test citations", result.stdout)
            self.assertIn("1 test gaps (1 adjudicated, 0 unresolved)", result.stdout)

    def test_stale_adjudication_without_gap_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, "[v1:src/folder with space/source.cs::KnownSymbol] [v1:test:test/known/known.cs]")
            search_record_fixture(root)
            write_adjudications(root, {"PB-1000": adjudication_entry()})
            result = self.run_cli(root, v1, expected, "--strict")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("does not correspond to an open test gap", result.stderr)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "v2"
            v1 = Path(temporary) / "v1"
            expected = git_fixture(v1)
            fixture(root, GAPPED_SCOPE)
            search_record_fixture(root)
            write_adjudications(root, {"PB-9999": adjudication_entry()})
            result = self.run_cli(root, v1, expected, "--strict")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("unknown contract row", result.stderr)


if __name__ == "__main__":
    unittest.main()
