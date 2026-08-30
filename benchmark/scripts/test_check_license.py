"""Hostile and positive tests for first-party license admission."""

from __future__ import annotations

from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import scripts.check_license as checker


ROOT = Path(__file__).resolve().parents[1]
CHECKER = ROOT / "scripts" / "check_license.py"
APPROVED_LICENSE = "LicenseRef-TerraScale-Commercial-v1"


class CurrentRepositoryLicenseGateTests(unittest.TestCase):
    def run_checker(self, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(CHECKER), *arguments],
            cwd=ROOT,
            check=False,
            capture_output=True,
            text=True,
        )

    def test_development_gate_records_every_pending_package(self) -> None:
        result = self.run_checker()

        self.assertEqual(result.returncode, 0, result.stderr)
        evidence = json.loads(result.stdout)
        self.assertEqual(evidence["status"], "pending-development")
        self.assertEqual(evidence["packageCount"], 11)
        self.assertTrue(evidence["cargoDenyPrivateIgnore"])
        self.assertFalse(evidence["releaseQualified"])

    def test_release_gate_refuses_the_pending_disposition(self) -> None:
        result = self.run_checker("--release")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("release license gate blocked", result.stderr)

    def test_main_push_automation_runs_the_release_gate(self) -> None:
        workflow = (ROOT / ".forgejo" / "workflows" / "ci.yml").read_text(encoding="utf-8")

        self.assertRegex(workflow, r"branches:\s*\[develop, main\]")
        self.assertRegex(
            workflow,
            r"(?m)^      - name: Release license admission\n"
            r"        if: \$\{\{ forgejo\.event_name == 'push' && "
            r"forgejo\.ref == 'refs/heads/main' \}\}\n"
            r"        env:\n"
            r"          TERRABASE_TRUSTED_APPROVAL_SHA256: "
            r"\$\{\{ secrets\.TERRABASE_TRUSTED_APPROVAL_SHA256 \}\}\n"
            r"        run: python3 scripts/check_license\.py --release$",
        )


class FixtureLicenseGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="terrabase-license-gate-")
        self.root = Path(self.temporary.name)
        self.trusted_approval_sha256: str | None = None

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def validate_approved(self) -> dict[str, object]:
        self.assertIsNotNone(self.trusted_approval_sha256)
        return checker.validate_repository(
            self.root,
            require_release=True,
            trusted_approval_sha256=self.trusted_approval_sha256,
        )

    def test_toml_scope_cannot_override_the_real_package_license(self) -> None:
        self.write_pending_fixture(
            root_prefix=(
                "[workspace.package]\n"
                f'license = "{APPROVED_LICENSE}"\n\n'
            )
        )

        evidence = checker.validate_repository(self.root, require_release=False)

        self.assertEqual(evidence["status"], "pending-development")
        with self.assertRaisesRegex(checker.LicenseValidationError, "release license gate blocked"):
            checker.validate_repository(self.root, require_release=True)

    def test_missing_child_license_is_rejected_while_cargo_deny_ignores_private_crates(self) -> None:
        self.write_pending_fixture()
        (self.root / "crates" / "child" / "Cargo.toml").write_text(
            '[package]\nname = "child"\nversion = "0.1.0"\npublish = false\n',
            encoding="utf-8",
        )

        with self.assertRaisesRegex(checker.LicenseValidationError, "package.license"):
            checker.validate_repository(self.root, require_release=False)

    def test_mixed_or_composite_pending_identifiers_are_rejected(self) -> None:
        self.write_pending_fixture()
        child = self.root / "crates" / "child" / "Cargo.toml"
        child.write_text(
            child.read_text(encoding="utf-8").replace(
                checker.PENDING_LICENSE,
                f"{checker.PENDING_LICENSE} OR MIT",
            ),
            encoding="utf-8",
        )

        with self.assertRaisesRegex(checker.LicenseValidationError, "one exact marker"):
            checker.validate_repository(self.root, require_release=False)

    def test_exact_approved_terms_disposition_and_inventory_pass(self) -> None:
        self.write_approved_fixture()

        evidence = self.validate_approved()

        self.assertEqual(evidence["status"], checker.APPROVED_GATE)
        self.assertTrue(evidence["releaseQualified"])
        self.assertEqual(evidence["licenseIdentifier"], APPROVED_LICENSE)
        self.assertEqual(evidence["packageCount"], 2)
        self.assertRegex(evidence["dispositionSha256"], r"^[0-9a-f]{64}$")
        self.assertRegex(evidence["approvalEvidenceSha256"], r"^[0-9a-f]{64}$")

    def test_approved_development_metadata_never_claims_release_qualification(self) -> None:
        self.write_approved_fixture()

        evidence = checker.validate_repository(self.root, require_release=False)

        self.assertEqual(evidence["status"], checker.APPROVED_METADATA_GATE)
        self.assertFalse(evidence["releaseQualified"])
        self.assertEqual(evidence["licenseIdentifier"], APPROVED_LICENSE)
        self.assertRegex(evidence["approvalEvidenceSha256"], r"^[0-9a-f]{64}$")

    def test_arbitrary_identifier_cannot_masquerade_as_approved(self) -> None:
        self.write_approved_fixture(license_identifier="garbage")

        with self.assertRaisesRegex(checker.LicenseValidationError, "approved proprietary"):
            self.validate_approved()

        self.write_approved_fixture(license_identifier="LicenseRef-TerraScale-Commercial")
        with self.assertRaisesRegex(checker.LicenseValidationError, "approved proprietary"):
            self.validate_approved()

    def test_reworded_pending_notice_blocks_an_otherwise_approved_fixture(self) -> None:
        self.write_approved_fixture(
            license_text=(
                "# Distribution terms pending\n\n"
                + "This placeholder grants no distribution rights. " * 20
            )
        )

        with self.assertRaisesRegex(checker.LicenseValidationError, "pending license state"):
            self.validate_approved()

    def test_tampered_terms_or_approval_bytes_are_rejected(self) -> None:
        self.write_approved_fixture()
        (self.root / "LICENSE.md").write_text("tampered terms " * 40, encoding="utf-8")
        with self.assertRaisesRegex(checker.LicenseValidationError, "exact LICENSE.md bytes"):
            self.validate_approved()

        self.write_approved_fixture()
        approval = self.root / "docs" / "legal" / "approvals" / "LEGAL-2026-001.json"
        approval.write_text(approval.read_text(encoding="utf-8") + " ", encoding="utf-8")
        with self.assertRaisesRegex(checker.LicenseValidationError, "approval evidence bytes"):
            self.validate_approved()

    def test_incomplete_package_inventory_is_rejected(self) -> None:
        disposition = self.write_approved_fixture()
        disposition["packages"] = disposition["packages"][:-1]
        self.write_disposition(disposition)

        with self.assertRaisesRegex(checker.LicenseValidationError, "package inventory"):
            self.validate_approved()

    def test_malformed_or_extra_disposition_fields_are_rejected(self) -> None:
        self.write_approved_fixture()
        disposition_path = self.root / checker.DISPOSITION_PATH
        disposition_path.write_text("not json", encoding="utf-8")
        with self.assertRaisesRegex(checker.LicenseValidationError, "cannot be parsed"):
            self.validate_approved()

        disposition = self.write_approved_fixture()
        disposition["unreviewed"] = True
        self.write_disposition(disposition)
        with self.assertRaisesRegex(checker.LicenseValidationError, "extra=.*unreviewed"):
            self.validate_approved()

    def test_approved_evidence_requires_an_out_of_band_trust_anchor(self) -> None:
        self.write_approved_fixture()

        with self.assertRaisesRegex(
            checker.LicenseValidationError,
            checker.TRUSTED_APPROVAL_SHA256_ENV,
        ):
            checker.validate_repository(self.root, require_release=True)

    def test_recomputed_attacker_approval_does_not_move_the_trust_anchor(self) -> None:
        disposition = self.write_approved_fixture()
        approval_path = self.root / "docs" / "legal" / "approvals" / "LEGAL-2026-001.json"
        approval = json.loads(approval_path.read_text(encoding="utf-8"))
        approval["reviewerIdentities"] = ["attacker"]
        attacker_bytes = json.dumps(approval, sort_keys=True).encode("utf-8")
        approval_path.write_bytes(attacker_bytes)
        disposition["approvalEvidenceSha256"] = digest(attacker_bytes)
        self.write_disposition(disposition)

        with self.assertRaisesRegex(checker.LicenseValidationError, "release trust anchor"):
            self.validate_approved()

    def test_approval_binds_distribution_scope_and_terms_version(self) -> None:
        disposition = self.write_approved_fixture()
        disposition["distributionScope"] = "Worldwide unrestricted redistribution"
        self.write_disposition(disposition)

        with self.assertRaisesRegex(
            checker.LicenseValidationError,
            "approval evidence distributionScope",
        ):
            self.validate_approved()

        versioned_terms = (
            f"# {APPROVED_LICENSE}\n\n"
            "TerraScale Example Entity issues owner-approved termsVersion v1, with v2 "
            "also named for this binding test. This license grant permits installation, use, "
            "and distribution and defines termination, warranty, and liability obligations. " * 4
        )
        disposition = self.write_approved_fixture(license_text=versioned_terms)
        disposition["termsVersion"] = "v2"
        self.write_disposition(disposition)
        with self.assertRaisesRegex(checker.LicenseValidationError, "approval evidence termsVersion"):
            self.validate_approved()

    def test_hidden_workspace_member_is_inventoried_and_bound_by_approval(self) -> None:
        disposition = self.write_approved_fixture()
        root_manifest = self.root / "Cargo.toml"
        root_manifest.write_text(
            root_manifest.read_text(encoding="utf-8")
            + '\n[workspace]\nmembers = ["node_modules/hidden"]\n',
            encoding="utf-8",
        )
        self.write_manifest(
            self.root / "node_modules" / "hidden" / "Cargo.toml",
            "hidden",
            APPROVED_LICENSE,
        )
        packages = list(disposition["packages"])
        packages.append(
            {
                "license": APPROVED_LICENSE,
                "manifest": "node_modules/hidden/Cargo.toml",
                "name": "hidden",
            }
        )
        disposition["packages"] = packages
        self.write_disposition(disposition)

        with self.assertRaisesRegex(checker.LicenseValidationError, "approval evidence packages"):
            self.validate_approved()

    def test_hidden_path_dependency_is_inventoried_and_bound_by_approval(self) -> None:
        disposition = self.write_approved_fixture()
        root_manifest = self.root / "Cargo.toml"
        root_manifest.write_text(
            root_manifest.read_text(encoding="utf-8")
            + '\n[dependencies.hidden]\npath = "node_modules/hidden"\n',
            encoding="utf-8",
        )
        self.write_manifest(
            self.root / "node_modules" / "hidden" / "Cargo.toml",
            "hidden",
            APPROVED_LICENSE,
        )
        packages = list(disposition["packages"])
        packages.append(
            {
                "license": APPROVED_LICENSE,
                "manifest": "node_modules/hidden/Cargo.toml",
                "name": "hidden",
            }
        )
        disposition["packages"] = packages
        self.write_disposition(disposition)

        with self.assertRaisesRegex(checker.LicenseValidationError, "approval evidence packages"):
            self.validate_approved()

    def test_recursive_workspace_glob_inventories_every_matching_member(self) -> None:
        disposition = self.write_approved_fixture()
        root_manifest = self.root / "Cargo.toml"
        root_manifest.write_text(
            root_manifest.read_text(encoding="utf-8")
            + '\n[workspace]\nmembers = ["node_modules/**/crate"]\n',
            encoding="utf-8",
        )
        for parent, name in (("x", "decoy"), ("a/b", "nested")):
            self.write_manifest(
                self.root / "node_modules" / parent / "crate" / "Cargo.toml",
                name,
                APPROVED_LICENSE,
            )
            disposition["packages"].append(
                {
                    "license": APPROVED_LICENSE,
                    "manifest": f"node_modules/{parent}/crate/Cargo.toml",
                    "name": name,
                }
            )
        disposition["packages"] = sorted(
            disposition["packages"],
            key=lambda package: package["manifest"],
        )
        self.write_disposition(disposition)

        with self.assertRaisesRegex(checker.LicenseValidationError, "approval evidence packages"):
            self.validate_approved()

    def test_hidden_patch_dependency_is_inventoried_and_bound_by_approval(self) -> None:
        disposition = self.write_approved_fixture()
        root_manifest = self.root / "Cargo.toml"
        root_manifest.write_text(
            root_manifest.read_text(encoding="utf-8")
            + '\n[dependencies]\nhidden = "0.1.0"\n'
            + '\n[patch.crates-io.hidden]\npath = "node_modules/hidden"\n',
            encoding="utf-8",
        )
        self.write_manifest(
            self.root / "node_modules" / "hidden" / "Cargo.toml",
            "hidden",
            APPROVED_LICENSE,
        )
        disposition["packages"].append(
            {
                "license": APPROVED_LICENSE,
                "manifest": "node_modules/hidden/Cargo.toml",
                "name": "hidden",
            }
        )
        self.write_disposition(disposition)

        with self.assertRaisesRegex(checker.LicenseValidationError, "approval evidence packages"):
            self.validate_approved()

    def test_hidden_replace_dependency_is_inventoried_and_bound_by_approval(self) -> None:
        disposition = self.write_approved_fixture()
        root_manifest = self.root / "Cargo.toml"
        root_manifest.write_text(
            root_manifest.read_text(encoding="utf-8")
            + '\n[replace]\n"hidden:0.1.0" = { path = "node_modules/hidden" }\n',
            encoding="utf-8",
        )
        self.write_manifest(
            self.root / "node_modules" / "hidden" / "Cargo.toml",
            "hidden",
            APPROVED_LICENSE,
        )
        disposition["packages"].append(
            {
                "license": APPROVED_LICENSE,
                "manifest": "node_modules/hidden/Cargo.toml",
                "name": "hidden",
            }
        )
        self.write_disposition(disposition)

        with self.assertRaisesRegex(checker.LicenseValidationError, "approval evidence packages"):
            self.validate_approved()

    def test_repository_cargo_path_overrides_are_rejected(self) -> None:
        self.write_approved_fixture()
        cargo_config = self.root / ".cargo" / "config.toml"
        cargo_config.parent.mkdir(parents=True)
        cargo_config.write_text('paths = ["node_modules/hidden"]\n', encoding="utf-8")

        with self.assertRaisesRegex(checker.LicenseValidationError, "path overrides are forbidden"):
            self.validate_approved()

    def test_symlinked_workspace_member_is_rejected(self) -> None:
        self.write_approved_fixture()
        root_manifest = self.root / "Cargo.toml"
        root_manifest.write_text(
            root_manifest.read_text(encoding="utf-8")
            + '\n[workspace]\nmembers = ["crates/linked"]\n',
            encoding="utf-8",
        )
        with tempfile.TemporaryDirectory(prefix="terrabase-external-package-") as external_name:
            external = Path(external_name)
            self.write_manifest(
                external / "Cargo.toml",
                "external",
                checker.PENDING_LICENSE,
            )
            (self.root / "crates" / "linked").symlink_to(external, target_is_directory=True)

            with self.assertRaisesRegex(checker.LicenseValidationError, "cannot contain symlinks"):
                self.validate_approved()

    def test_explicit_package_workspace_is_rejected(self) -> None:
        self.write_approved_fixture()
        child = self.root / "crates" / "child" / "Cargo.toml"
        child.write_text(
            child.read_text(encoding="utf-8") + 'workspace = "../../../external-workspace"\n',
            encoding="utf-8",
        )

        with self.assertRaisesRegex(checker.LicenseValidationError, "package.workspace is unsupported"):
            self.validate_approved()

    def test_inherited_workspace_dependency_is_rejected(self) -> None:
        self.write_approved_fixture()
        child = self.root / "crates" / "child" / "Cargo.toml"
        child.write_text(
            child.read_text(encoding="utf-8")
            + "\n[dependencies.hidden]\nworkspace = true\n",
            encoding="utf-8",
        )

        with self.assertRaisesRegex(checker.LicenseValidationError, "workspace is unsupported"):
            self.validate_approved()

    def test_explicitly_denied_rights_cannot_pass_keyword_checks(self) -> None:
        denied_terms = (
            f"# {APPROVED_LICENSE}\n\n"
            "TerraScale Example Entity termsVersion v1. The grant is withheld. "
            "Use and distribution are prohibited. Termination and liability apply. " * 8
        )
        self.write_approved_fixture(license_text=denied_terms)

        with self.assertRaisesRegex(checker.LicenseValidationError, "explicitly denies"):
            self.validate_approved()

        excluded_grant = (
            f"# {APPROVED_LICENSE}\n\n"
            "TerraScale Example Entity termsVersion v1. This grant expressly excludes every "
            "permission to use or distribute this software and excludes all distribution. "
            "Termination and liability obligations apply. " * 8
        )
        self.write_approved_fixture(license_text=excluded_grant)
        with self.assertRaisesRegex(checker.LicenseValidationError, "explicitly denies"):
            self.validate_approved()

        semantic_denial = (
            f"# {APPROVED_LICENSE}\n\n"
            "TerraScale Example Entity termsVersion v1. The grant clause states that no person "
            "is granted permission to use or distribute this software. No authorization exists. "
            "Termination and liability terms remain applicable. " * 8
        )
        self.write_approved_fixture(license_text=semantic_denial)
        with self.assertRaisesRegex(checker.LicenseValidationError, "explicitly denies"):
            self.validate_approved()

        may_not_use = (
            f"# {APPROVED_LICENSE}\n\n"
            "TerraScale Example Entity termsVersion v1. The grant clause states that no party "
            "may use or distribute this software. Distribution and use receive no rights under "
            "these terms. Termination and liability terms remain applicable. " * 8
        )
        self.write_approved_fixture(license_text=may_not_use)
        with self.assertRaisesRegex(checker.LicenseValidationError, "explicitly denies"):
            self.validate_approved()

    def test_trademark_reservation_does_not_negate_software_grant(self) -> None:
        scoped_terms = (
            f"# {APPROVED_LICENSE}\n\n"
            "TerraScale Example Entity termsVersion v1. This license grants authorized recipients "
            "permission for software use and distribution. It does not grant permission to use "
            "TerraScale trademarks. Termination and liability obligations apply. " * 8
        )
        self.write_approved_fixture(license_text=scoped_terms)

        evidence = self.validate_approved()

        self.assertTrue(evidence["releaseQualified"])
        self.assertEqual(evidence["status"], checker.APPROVED_GATE)

    def test_non_string_reviewer_identity_is_a_validation_error(self) -> None:
        disposition = self.write_approved_fixture()
        approval_path = self.root / "docs" / "legal" / "approvals" / "LEGAL-2026-001.json"
        approval = json.loads(approval_path.read_text(encoding="utf-8"))
        approval["reviewerIdentities"] = [{"name": "attacker"}]
        approval_bytes = json.dumps(approval, sort_keys=True).encode("utf-8")
        approval_path.write_bytes(approval_bytes)
        self.trusted_approval_sha256 = digest(approval_bytes)
        disposition["approvalEvidenceSha256"] = self.trusted_approval_sha256
        self.write_disposition(disposition)

        with self.assertRaisesRegex(checker.LicenseValidationError, "bounded, nonblank string"):
            self.validate_approved()

    def test_duplicate_json_fields_are_rejected_before_schema_validation(self) -> None:
        disposition = self.write_approved_fixture()
        approval_path = self.root / "docs" / "legal" / "approvals" / "LEGAL-2026-001.json"
        ambiguous = approval_path.read_text(encoding="utf-8").replace(
            '"schemaVersion": 1',
            '"schemaVersion": true, "schemaVersion": 1',
        )
        ambiguous_bytes = ambiguous.encode("utf-8")
        approval_path.write_bytes(ambiguous_bytes)
        self.trusted_approval_sha256 = digest(ambiguous_bytes)
        disposition["approvalEvidenceSha256"] = self.trusted_approval_sha256
        self.write_disposition(disposition)

        with self.assertRaisesRegex(checker.LicenseValidationError, "duplicate JSON field"):
            self.validate_approved()

    def write_pending_fixture(self, root_prefix: str = "") -> None:
        self.write_common_files()
        self.write_manifest(
            self.root / "Cargo.toml",
            "terrabase",
            checker.PENDING_LICENSE,
            checker.PENDING_GATE,
            prefix=root_prefix,
        )
        self.write_manifest(
            self.root / "crates" / "child" / "Cargo.toml",
            "child",
            checker.PENDING_LICENSE,
        )
        (self.root / "LICENSE.md").write_text(
            "# Distribution license pending\n\n"
            "Owner approval is required before a public release. This grants no distribution rights.\n",
            encoding="utf-8",
        )

    def write_approved_fixture(
        self,
        license_identifier: str = APPROVED_LICENSE,
        license_text: str | None = None,
    ) -> dict[str, object]:
        self.write_common_files()
        self.write_manifest(
            self.root / "Cargo.toml",
            "terrabase",
            license_identifier,
            checker.APPROVED_GATE,
        )
        self.write_manifest(
            self.root / "crates" / "child" / "Cargo.toml",
            "child",
            license_identifier,
        )
        terms = license_text or (
            f"# {APPROVED_LICENSE}\n\n"
            "TerraScale Example Entity issues these owner-approved termsVersion v1. "
            "This license grant defines permitted installation, use, distribution, support, "
            "warranty, liability, termination, and governing obligations. " * 8
        )
        license_bytes = terms.encode("utf-8")
        (self.root / "LICENSE.md").write_bytes(license_bytes)
        distribution_scope = "TerraBase production artifacts and authorized installations"
        terms_version = "v1"
        packages = [
            {"license": license_identifier, "manifest": "Cargo.toml", "name": "terrabase"},
            {
                "license": license_identifier,
                "manifest": "crates/child/Cargo.toml",
                "name": "child",
            },
        ]
        approval_path = self.root / "docs" / "legal" / "approvals" / "LEGAL-2026-001.json"
        approval_path.parent.mkdir(parents=True, exist_ok=True)
        approved_at = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        approval_bytes = json.dumps(
            {
                "approvalId": "LEGAL-2026-001",
                "approvedAt": approved_at,
                "authorizedRights": ["distribution", "use"],
                "distributionScope": distribution_scope,
                "legalEntity": "TerraScale Example Entity",
                "licenseFileSha256": digest(license_bytes),
                "licenseIdentifier": license_identifier,
                "packages": packages,
                "reviewerIdentities": ["legal-reviewer@example.test"],
                "schemaVersion": 1,
                "status": checker.APPROVED_GATE,
                "termsVersion": terms_version,
            },
            sort_keys=True,
        ).encode("utf-8")
        approval_path.write_bytes(approval_bytes)
        self.trusted_approval_sha256 = digest(approval_bytes)
        disposition: dict[str, object] = {
            "approvalEvidencePath": "docs/legal/approvals/LEGAL-2026-001.json",
            "approvalEvidenceSha256": self.trusted_approval_sha256,
            "approvalId": "LEGAL-2026-001",
            "approvedAt": approved_at,
            "distributionScope": distribution_scope,
            "legalEntity": "TerraScale Example Entity",
            "licenseFile": "LICENSE.md",
            "licenseFileSha256": digest(license_bytes),
            "licenseIdentifier": license_identifier,
            "packages": packages,
            "schemaVersion": 1,
            "status": checker.APPROVED_GATE,
            "termsVersion": terms_version,
        }
        self.write_disposition(disposition)
        return disposition

    def write_common_files(self) -> None:
        (self.root / "deny.toml").write_text(
            "[licenses]\nversion = 2\n\n[licenses.private]\nignore = true\n",
            encoding="utf-8",
        )

    def write_manifest(
        self,
        path: Path,
        name: str,
        license_identifier: str,
        gate: str | None = None,
        prefix: str = "",
    ) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        metadata = (
            f'\n[package.metadata.terrabase]\nlicense_gate = "{gate}"\n'
            if gate is not None
            else ""
        )
        path.write_text(
            prefix
            + "[package]\n"
            + f'name = "{name}"\n'
            + 'version = "0.1.0"\n'
            + f'license = "{license_identifier}"\n'
            + "publish = false\n"
            + metadata,
            encoding="utf-8",
        )

    def write_disposition(self, disposition: dict[str, object]) -> None:
        path = self.root / checker.DISPOSITION_PATH
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(
            json.dumps(disposition, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


if __name__ == "__main__":
    unittest.main()
