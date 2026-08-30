#!/usr/bin/env python3
"""Validate every first-party license and fail closed for release admission."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
import glob
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import tomllib
from typing import Any


PENDING_LICENSE = "LicenseRef-TerraScale-Pending"
PENDING_GATE = "pending-before-release"
APPROVED_GATE = "approved-for-release"
APPROVED_METADATA_GATE = "approved-metadata-untrusted"
DISPOSITION_PATH = Path("docs/legal/terrabase-license-disposition.json")
TRUSTED_APPROVAL_SHA256_ENV = "TERRABASE_TRUSTED_APPROVAL_SHA256"
SKIPPED_DIRECTORIES = {".git", ".mypy_cache", ".pytest_cache", "node_modules", "target"}
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
PROPRIETARY_LICENSE_PATTERN = re.compile(
    r"^LicenseRef-TerraScale-[A-Za-z0-9][A-Za-z0-9.-]{1,63}"
    r"-v[1-9][0-9]*(?:\.[0-9]+){0,2}$"
)
TERMS_VERSION_PATTERN = re.compile(r"^v[1-9][0-9]*(?:\.[0-9]+){0,2}$")
APPROVAL_ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:/-]{7,127}$")
APPROVED_AT_PATTERN = re.compile(
    r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?Z$"
)
PENDING_NOTICE_PATTERNS = (
    re.compile(r"\bpending\b", re.IGNORECASE),
    re.compile(r"grants?\s+no\s+distribution\s+rights?", re.IGNORECASE),
    re.compile(r"before\s+(?:a\s+)?public\s+release", re.IGNORECASE),
    re.compile(r"must\s+be\s+(?:selected|approved|recorded)", re.IGNORECASE),
    re.compile(r"not\s+(?:a\s+)?(?:distribution\s+)?licen[cs]e", re.IGNORECASE),
)
DENIED_RIGHTS_PATTERNS = (
    re.compile(r"\bthe\s+(?:license\s+)?grant\s+is\s+(?:withheld|denied)\b", re.IGNORECASE),
    re.compile(
        r"\b(?:use|distribution)(?:\s+and\s+(?:use|distribution))?\s+"
        r"(?:is|are)\s+(?:prohibited|forbidden)\b",
        re.IGNORECASE,
    ),
    re.compile(
        r"\bno\s+(?:person|one|party|user|recipient)\s+is\s+"
        r"(?:granted|authorized|permitted)\b[^.\n]{0,200}"
        r"\b(?:use|distribut\w*)\b",
        re.IGNORECASE,
    ),
    re.compile(
        r"\bno\s+(?:person|one|party|user|recipient)\s+may\b"
        r"[^.\n]{0,200}\b(?:use|distribut\w*)\b",
        re.IGNORECASE,
    ),
    re.compile(
        r"\bno\s+(?:authorization|permission|license|grant|distribution\s+right)"
        r"s?\s+(?:exists|is\s+(?:given|provided|granted))\b",
        re.IGNORECASE,
    ),
    re.compile(
        r"\bdoes\s+not\s+(?:grant|authorize|permit)\b"
        r"(?=[^.\n]{0,200}\b(?:use|distribut\w*)\b)"
        r"(?=[^.\n]{0,200}\b(?:software|source|code|binaries|artifacts?|product)\b)"
        r"[^.\n]{0,200}",
        re.IGNORECASE,
    ),
    re.compile(
        r"\b(?:use|distribut\w*)\s+(?:is|are)\s+not\s+"
        r"(?:granted|authorized|permitted|allowed)\b",
        re.IGNORECASE,
    ),
    re.compile(
        r"\b(?:use|distribution)(?:\s+and\s+(?:use|distribution))?\s+"
        r"(?:receives?|has|have)\s+no\s+rights?\b",
        re.IGNORECASE,
    ),
    re.compile(
        r"\b(?:grant|license)\b"
        r"(?=[^.\n]{0,200}\b(?:excludes?|withholds?|denies?)\b)"
        r"(?=[^.\n]{0,200}\b(?:permission|rights?)\b)"
        r"(?=[^.\n]{0,200}\b(?:use|distribut\w*)\b)"
        r"(?=[^.\n]{0,200}\b(?:software|source|code|binaries|artifacts?|product)\b)"
        r"[^.\n]{0,200}",
        re.IGNORECASE,
    ),
)
POSITIVE_USE_GRANT_PATTERNS = (
    re.compile(r"\b(?:grant\w*|authoriz\w*|permit\w*)\b[^.\n]{0,200}\buse\b", re.IGNORECASE),
    re.compile(r"\bmay\s+use\b", re.IGNORECASE),
)
POSITIVE_DISTRIBUTION_GRANT_PATTERNS = (
    re.compile(
        r"\b(?:grant\w*|authoriz\w*|permit\w*)\b[^.\n]{0,200}\bdistribut(?:e|ion)\b",
        re.IGNORECASE,
    ),
    re.compile(r"\bmay\s+distribut(?:e|ion)\b", re.IGNORECASE),
)


class LicenseValidationError(ValueError):
    """The repository cannot produce valid license evidence."""


@dataclass(frozen=True)
class PackageLicense:
    manifest: str
    name: str
    license_identifier: str
    publish: bool | list[str] | None


def validate_repository(
    root: Path,
    require_release: bool,
    trusted_approval_sha256: str | None = None,
) -> dict[str, Any]:
    """Validate development metadata or return exact release evidence."""

    repository_root = root.resolve()
    root_manifest_path = repository_root / "Cargo.toml"
    root_manifest = read_toml(root_manifest_path, "root Cargo manifest")
    reject_repository_cargo_path_overrides(repository_root)
    packages = discover_packages(repository_root, root_manifest)
    if not packages or packages[0].manifest != "Cargo.toml":
        raise LicenseValidationError("Cargo.toml must define the root TerraBase package.")

    package_names = [package.name for package in packages]
    if len(package_names) != len(set(package_names)):
        raise LicenseValidationError("first-party Cargo package names must be unique")

    root_gate = nested_string(
        root_manifest,
        ("package", "metadata", "terrabase", "license_gate"),
        "Cargo.toml package.metadata.terrabase.license_gate",
    )
    license_path = repository_root / "LICENSE.md"
    license_bytes = read_bytes(license_path, "LICENSE.md")
    try:
        license_text = license_bytes.decode("utf-8")
    except UnicodeDecodeError as error:
        raise LicenseValidationError("LICENSE.md must be UTF-8 text") from error

    deny_private_ignore = cargo_deny_private_ignore(repository_root)
    pending_notice = any(pattern.search(license_text) for pattern in PENDING_NOTICE_PATTERNS)
    pending_packages = [
        package
        for package in packages
        if "pending" in package.license_identifier.casefold()
    ]
    pending = bool(pending_packages) or root_gate == PENDING_GATE or pending_notice

    if pending:
        validate_pending_state(
            repository_root,
            packages,
            root_gate,
            license_text,
            require_release,
        )
        return {
            "status": "pending-development",
            "licenseIdentifier": PENDING_LICENSE,
            "licenseFileSha256": sha256(license_bytes),
            "packageCount": len(packages),
            "cargoDenyPrivateIgnore": deny_private_ignore,
            "dispositionSha256": None,
            "approvalEvidenceSha256": None,
            "releaseQualified": False,
        }

    return validate_approved_state(
        repository_root,
        packages,
        root_gate,
        license_bytes,
        license_text,
        deny_private_ignore,
        require_release,
        trusted_approval_sha256,
    )


def discover_packages(
    root: Path,
    root_manifest: dict[str, Any] | None = None,
) -> list[PackageLicense]:
    """Inventory every source Cargo package, including excluded tool crates."""

    manifests: list[Path] = []
    for current_root, directory_names, file_names in os.walk(root):
        directory_names[:] = sorted(
            name
            for name in directory_names
            if name not in SKIPPED_DIRECTORIES
            and not (Path(current_root) / name).is_symlink()
        )
        if "Cargo.toml" in file_names:
            manifests.append(Path(current_root) / "Cargo.toml")

    if root_manifest is None:
        root_manifest = read_toml(root / "Cargo.toml", "root Cargo manifest")
    manifests.extend(declared_workspace_manifests(root, root_manifest))

    packages: list[PackageLicense] = []
    pending_manifests = sorted(set(manifests))
    visited_manifests: set[Path] = set()
    while pending_manifests:
        manifest_path = pending_manifests.pop(0)
        manifest_path = confined_path(root, manifest_path.relative_to(root))
        if manifest_path in visited_manifests:
            continue
        visited_manifests.add(manifest_path)
        relative_manifest = manifest_path.relative_to(root).as_posix()
        data = read_toml(manifest_path, relative_manifest)
        pending_manifests.extend(
            path_dependency_manifests(root, manifest_path, data, visited_manifests)
        )
        pending_manifests = sorted(set(pending_manifests))
        package = data.get("package")
        if package is None:
            continue
        if not isinstance(package, dict):
            raise LicenseValidationError(f"{relative_manifest} package must be a TOML table")
        if "workspace" in package:
            raise LicenseValidationError(
                f"{relative_manifest} package.workspace is unsupported for release inventory"
            )
        name = required_clean_string(package.get("name"), f"{relative_manifest} package.name")
        license_identifier = required_clean_string(
            package.get("license"),
            f"{relative_manifest} package.license",
        )
        publish = package.get("publish")
        if publish is not None and not isinstance(publish, (bool, list)):
            raise LicenseValidationError(f"{relative_manifest} package.publish has an invalid type")
        if isinstance(publish, list) and not all(
            isinstance(registry, str) and registry.strip() == registry and registry
            for registry in publish
        ):
            raise LicenseValidationError(
                f"{relative_manifest} package.publish registry list is invalid"
            )
        packages.append(
            PackageLicense(
                manifest=relative_manifest,
                name=name,
                license_identifier=license_identifier,
                publish=publish,
            )
        )
    return sorted(packages, key=lambda package: package.manifest)


def declared_workspace_manifests(
    root: Path,
    root_manifest: dict[str, Any],
) -> list[Path]:
    """Load declared workspace members and excludes even below skipped directories."""

    workspace = root_manifest.get("workspace")
    if workspace is None:
        return []
    if not isinstance(workspace, dict):
        raise LicenseValidationError("Cargo.toml workspace must be a TOML table")

    manifests: list[Path] = []
    for field in ("members", "exclude"):
        entries = workspace.get(field, [])
        if not isinstance(entries, list) or not all(isinstance(entry, str) for entry in entries):
            raise LicenseValidationError(f"Cargo.toml workspace.{field} must be an array of paths")
        for index, entry_value in enumerate(entries):
            entry = required_clean_string(
                entry_value,
                f"Cargo.toml workspace.{field}[{index}]",
            )
            entry_path = Path(entry)
            if entry_path.is_absolute() or ".." in entry_path.parts or entry_path.as_posix() != entry:
                raise LicenseValidationError(
                    f"Cargo.toml workspace.{field}[{index}] must be a normalized repository-relative glob"
                )
            matches = [
                Path(match)
                for match in sorted(
                    glob.glob(str(root / entry), recursive=True, include_hidden=True)
                )
            ]
            if not matches:
                raise LicenseValidationError(
                    f"Cargo.toml workspace.{field}[{index}] does not match a repository path"
                )
            for match in matches:
                relative_match = match.relative_to(root)
                confined_match = confined_path(root, relative_match)
                manifest = (
                    confined_match
                    if confined_match.name == "Cargo.toml"
                    else confined_match / "Cargo.toml"
                )
                relative_manifest = manifest.relative_to(root)
                manifest = confined_path(root, relative_manifest)
                if not manifest.is_file():
                    raise LicenseValidationError(
                        f"Cargo.toml workspace.{field}[{index}] does not identify a Cargo package"
                    )
                manifests.append(manifest)
    return manifests


def path_dependency_manifests(
    root: Path,
    manifest_path: Path,
    manifest: dict[str, Any],
    visited_manifests: set[Path],
) -> list[Path]:
    """Inventory in-repository path dependencies, including target-specific ones."""

    dependency_tables: list[tuple[str, Any]] = []
    for table_name in ("dependencies", "dev-dependencies", "build-dependencies"):
        dependency_tables.append((table_name, manifest.get(table_name)))

    workspace = manifest.get("workspace")
    if isinstance(workspace, dict):
        dependency_tables.append(("workspace.dependencies", workspace.get("dependencies")))
    replacement = manifest.get("replace")
    dependency_tables.append(("replace", replacement))
    patches = manifest.get("patch")
    if patches is not None:
        if not isinstance(patches, dict):
            raise LicenseValidationError(
                f"{manifest_path.relative_to(root).as_posix()} patch must be a TOML table"
            )
        for source_name, source_patches in patches.items():
            dependency_tables.append((f"patch.{source_name}", source_patches))
    target = manifest.get("target")
    if isinstance(target, dict):
        for target_name, target_config in target.items():
            if not isinstance(target_config, dict):
                continue
            for table_name in ("dependencies", "dev-dependencies", "build-dependencies"):
                dependency_tables.append(
                    (f"target.{target_name}.{table_name}", target_config.get(table_name))
                )

    discovered: list[Path] = []
    for table_name, table in dependency_tables:
        if table is None:
            continue
        if not isinstance(table, dict):
            raise LicenseValidationError(
                f"{manifest_path.relative_to(root).as_posix()} {table_name} must be a TOML table"
            )
        for dependency_name, dependency in table.items():
            if isinstance(dependency, dict) and "workspace" in dependency:
                raise LicenseValidationError(
                    f"{manifest_path.relative_to(root).as_posix()} "
                    f"{table_name}.{dependency_name}.workspace is unsupported for release inventory"
                )
            if not isinstance(dependency, dict) or "path" not in dependency:
                continue
            path_value = required_clean_string(
                dependency["path"],
                f"{manifest_path.relative_to(root).as_posix()} {table_name}.{dependency_name}.path",
            )
            candidate = manifest_path.parent / path_value
            candidate_manifest = candidate if candidate.name == "Cargo.toml" else candidate / "Cargo.toml"
            try:
                relative_candidate = candidate_manifest.relative_to(root)
            except ValueError as error:
                raise LicenseValidationError(
                    f"path dependency escapes repository root: {path_value}"
                ) from error
            confined_candidate = confined_path(root, relative_candidate)
            if not confined_candidate.is_file():
                raise LicenseValidationError(
                    f"path dependency does not identify a Cargo package: {path_value}"
                )
            if confined_candidate not in visited_manifests:
                discovered.append(confined_candidate)
    return discovered


def validate_pending_state(
    root: Path,
    packages: list[PackageLicense],
    root_gate: str,
    license_text: str,
    require_release: bool,
) -> None:
    unexpected = [
        f"{package.manifest}={package.license_identifier}"
        for package in packages
        if package.license_identifier != PENDING_LICENSE
    ]
    if unexpected:
        raise LicenseValidationError(
            "pending license state must use one exact marker in every first-party package: "
            + ", ".join(unexpected)
        )
    publishable = [package.manifest for package in packages if package.publish is not False]
    if publishable:
        raise LicenseValidationError(
            "pending first-party packages must set publish = false: " + ", ".join(publishable)
        )
    if root_gate != PENDING_GATE:
        raise LicenseValidationError(
            f"pending license metadata has unexpected root gate: {root_gate}"
        )
    if not any(pattern.search(license_text) for pattern in PENDING_NOTICE_PATTERNS):
        raise LicenseValidationError("LICENSE.md must state that distribution terms remain pending")
    disposition_path = root / DISPOSITION_PATH
    if disposition_path.exists():
        raise LicenseValidationError(
            f"pending metadata cannot coexist with {DISPOSITION_PATH.as_posix()}"
        )
    if require_release:
        raise LicenseValidationError(
            "release license gate blocked: owner-approved identifier, terms, and disposition evidence are missing"
        )


def validate_approved_state(
    root: Path,
    packages: list[PackageLicense],
    root_gate: str,
    license_bytes: bytes,
    license_text: str,
    deny_private_ignore: bool,
    require_release: bool,
    trusted_approval_sha256: str | None,
) -> dict[str, Any]:
    if root_gate != APPROVED_GATE:
        raise LicenseValidationError(
            f"resolved license metadata has unexpected root gate: {root_gate}"
        )
    identifiers = {package.license_identifier for package in packages}
    if len(identifiers) != 1:
        raise LicenseValidationError(
            "all first-party packages must declare the same approved license identifier"
        )
    license_identifier = next(iter(identifiers))
    if "pending" in license_identifier.casefold():
        raise LicenseValidationError("approved license identifier cannot contain a pending marker")
    if not PROPRIETARY_LICENSE_PATTERN.fullmatch(license_identifier):
        raise LicenseValidationError(
            "approved proprietary license identifier must use LicenseRef-TerraScale-<versioned-name>"
        )
    if len(license_bytes) < 256:
        raise LicenseValidationError("LICENSE.md is too short to contain approved distribution terms")
    if any(pattern.search(license_text) for pattern in PENDING_NOTICE_PATTERNS):
        raise LicenseValidationError("LICENSE.md still contains pending or no-rights language")

    disposition_path = confined_path(root, DISPOSITION_PATH)
    disposition_bytes = read_bytes(disposition_path, DISPOSITION_PATH.as_posix())
    disposition = read_json_object(disposition_bytes, DISPOSITION_PATH.as_posix())
    required_keys = {
        "approvalEvidencePath",
        "approvalEvidenceSha256",
        "approvalId",
        "approvedAt",
        "distributionScope",
        "legalEntity",
        "licenseFile",
        "licenseFileSha256",
        "licenseIdentifier",
        "packages",
        "schemaVersion",
        "status",
        "termsVersion",
    }
    if set(disposition) != required_keys:
        missing = sorted(required_keys - set(disposition))
        extra = sorted(set(disposition) - required_keys)
        raise LicenseValidationError(
            f"license disposition keys disagree; missing={missing}, extra={extra}"
        )
    if type(disposition["schemaVersion"]) is not int or disposition["schemaVersion"] != 1:
        raise LicenseValidationError("license disposition schemaVersion must be integer 1")
    if disposition["status"] != APPROVED_GATE:
        raise LicenseValidationError("license disposition schemaVersion/status is not approved v1")
    if disposition["licenseIdentifier"] != license_identifier:
        raise LicenseValidationError("license disposition identifier does not match Cargo packages")
    if disposition["licenseFile"] != "LICENSE.md":
        raise LicenseValidationError("license disposition must bind LICENSE.md")
    license_sha256 = sha256(license_bytes)
    if disposition["licenseFileSha256"] != license_sha256:
        raise LicenseValidationError("license disposition does not bind the exact LICENSE.md bytes")
    if any(pattern.search(license_text) for pattern in DENIED_RIGHTS_PATTERNS):
        raise LicenseValidationError("LICENSE.md explicitly denies use or distribution rights")
    if not any(pattern.search(license_text) for pattern in POSITIVE_USE_GRANT_PATTERNS):
        raise LicenseValidationError("LICENSE.md does not contain a positive use grant")
    if not any(
        pattern.search(license_text) for pattern in POSITIVE_DISTRIBUTION_GRANT_PATTERNS
    ):
        raise LicenseValidationError("LICENSE.md does not contain a positive distribution grant")

    legal_entity = required_clean_string(
        disposition["legalEntity"], "license disposition legalEntity", minimum=3
    )
    required_clean_string(
        disposition["distributionScope"],
        "license disposition distributionScope",
        minimum=3,
    )
    terms_version = required_clean_string(
        disposition["termsVersion"], "license disposition termsVersion"
    )
    if not TERMS_VERSION_PATTERN.fullmatch(terms_version):
        raise LicenseValidationError("license disposition termsVersion must use v<positive-integer>")
    approval_id = required_clean_string(
        disposition["approvalId"], "license disposition approvalId", minimum=8
    )
    if not APPROVAL_ID_PATTERN.fullmatch(approval_id):
        raise LicenseValidationError("license disposition approvalId has an invalid shape")
    approved_at = validate_approved_at(disposition["approvedAt"])

    for exact_value, name in (
        (license_identifier, "license identifier"),
        (legal_entity, "legal entity"),
        (terms_version, "terms version"),
    ):
        if exact_value not in license_text:
            raise LicenseValidationError(f"LICENSE.md does not name the approved {name}")
    for term in ("grant", "use", "distribution", "termination", "liability"):
        if re.search(rf"\b{term}\b", license_text, re.IGNORECASE) is None:
            raise LicenseValidationError(f"LICENSE.md does not contain {term} terms")

    approval_relative = safe_relative_path(
        disposition["approvalEvidencePath"], "license disposition approvalEvidencePath"
    )
    if approval_relative.parts[:3] != ("docs", "legal", "approvals"):
        raise LicenseValidationError("approval evidence must live below docs/legal/approvals")
    approval_path = confined_path(root, approval_relative)
    approval_bytes = read_bytes(approval_path, approval_relative.as_posix())
    if len(approval_bytes) < 100:
        raise LicenseValidationError("approval evidence is too short to identify a legal review")
    approval_sha256 = required_sha256(
        disposition["approvalEvidenceSha256"],
        "license disposition approvalEvidenceSha256",
    )
    if sha256(approval_bytes) != approval_sha256:
        raise LicenseValidationError("license disposition does not bind the approval evidence bytes")
    if require_release:
        if trusted_approval_sha256 is None:
            raise LicenseValidationError(
                f"{TRUSTED_APPROVAL_SHA256_ENV} is required from the protected release environment"
            )
        trusted_approval_sha256 = required_sha256(
            trusted_approval_sha256,
            TRUSTED_APPROVAL_SHA256_ENV,
        )
        if approval_sha256 != trusted_approval_sha256:
            raise LicenseValidationError(
                "approval evidence does not match the protected release trust anchor"
            )
    approval = read_json_object(approval_bytes, approval_relative.as_posix())
    approval_keys = {
        "approvalId",
        "approvedAt",
        "authorizedRights",
        "distributionScope",
        "legalEntity",
        "licenseFileSha256",
        "licenseIdentifier",
        "packages",
        "reviewerIdentities",
        "schemaVersion",
        "status",
        "termsVersion",
    }
    if set(approval) != approval_keys:
        raise LicenseValidationError("approval evidence fields do not match the required v1 schema")
    if type(approval["schemaVersion"]) is not int or approval["schemaVersion"] != 1:
        raise LicenseValidationError("approval evidence schemaVersion must be integer 1")
    declared_packages = disposition["packages"]
    if not isinstance(declared_packages, list):
        raise LicenseValidationError("license disposition packages must be an array")
    expected_packages = [
        {
            "license": package.license_identifier,
            "manifest": package.manifest,
            "name": package.name,
        }
        for package in packages
    ]
    if declared_packages != expected_packages:
        raise LicenseValidationError(
            "license disposition package inventory does not match every first-party Cargo package"
        )

    expected_approval = {
        "approvalId": approval_id,
        "approvedAt": approved_at,
        "authorizedRights": ["distribution", "use"],
        "distributionScope": disposition["distributionScope"],
        "legalEntity": legal_entity,
        "licenseFileSha256": license_sha256,
        "licenseIdentifier": license_identifier,
        "packages": expected_packages,
        "status": APPROVED_GATE,
        "termsVersion": terms_version,
    }
    for field, expected in expected_approval.items():
        if approval[field] != expected:
            raise LicenseValidationError(
                f"approval evidence {field} does not match the disposition"
            )
    reviewers = approval["reviewerIdentities"]
    if not isinstance(reviewers, list) or not 1 <= len(reviewers) <= 10:
        raise LicenseValidationError(
            "approval evidence reviewerIdentities must contain 1 to 10 unique identities"
        )
    validated_reviewers: list[str] = []
    for index, reviewer in enumerate(reviewers):
        validated_reviewers.append(
            required_clean_string(
                reviewer,
                f"approval evidence reviewerIdentities[{index}]",
                minimum=3,
            )
        )
    if len(validated_reviewers) != len(set(validated_reviewers)):
        raise LicenseValidationError(
            "approval evidence reviewerIdentities must contain 1 to 10 unique identities"
        )

    return {
        "status": APPROVED_GATE if require_release else APPROVED_METADATA_GATE,
        "licenseIdentifier": license_identifier,
        "licenseFileSha256": license_sha256,
        "packageCount": len(packages),
        "cargoDenyPrivateIgnore": deny_private_ignore,
        "dispositionSha256": sha256(disposition_bytes),
        "approvalEvidenceSha256": approval_sha256,
        "releaseQualified": require_release,
    }


def cargo_deny_private_ignore(root: Path) -> bool:
    data = read_toml(root / "deny.toml", "deny.toml")
    licenses = data.get("licenses")
    if not isinstance(licenses, dict):
        return False
    private = licenses.get("private")
    if not isinstance(private, dict):
        return False
    ignore = private.get("ignore", False)
    if not isinstance(ignore, bool):
        raise LicenseValidationError("deny.toml licenses.private.ignore must be a boolean")
    return ignore


def reject_repository_cargo_path_overrides(root: Path) -> None:
    """Forbid local Cargo source substitution that escapes the package inventory."""

    for relative in (Path(".cargo/config.toml"), Path(".cargo/config")):
        path = root / relative
        if not path.exists() and not path.is_symlink():
            continue
        config = read_toml(confined_path(root, relative), relative.as_posix())
        paths = config.get("paths")
        if paths is None:
            continue
        if not isinstance(paths, list) or not all(isinstance(value, str) for value in paths):
            raise LicenseValidationError(
                f"{relative.as_posix()} paths must be an array of repository-relative paths"
            )
        if paths:
            raise LicenseValidationError(
                f"{relative.as_posix()} Cargo path overrides are forbidden for release admission"
            )


def validate_approved_at(value: Any) -> str:
    approved_at = required_clean_string(value, "license disposition approvedAt")
    if not APPROVED_AT_PATTERN.fullmatch(approved_at):
        raise LicenseValidationError(
            "license disposition approvedAt must be an exact UTC ISO-8601 instant"
        )
    try:
        instant = datetime.fromisoformat(approved_at.removesuffix("Z") + "+00:00")
    except ValueError as error:
        raise LicenseValidationError("license disposition approvedAt is not an ISO-8601 instant") from error
    if instant > datetime.now(timezone.utc) + timedelta(minutes=5):
        raise LicenseValidationError("license disposition approvedAt cannot be in the future")
    return approved_at


def read_toml(path: Path, name: str) -> dict[str, Any]:
    try:
        data = read_bytes(path, name)
        value = tomllib.loads(data.decode("utf-8"))
    except (OSError, UnicodeDecodeError, tomllib.TOMLDecodeError) as error:
        raise LicenseValidationError(f"{name} cannot be parsed: {error}") from error
    if not isinstance(value, dict):
        raise LicenseValidationError(f"{name} must contain a TOML document")
    return value


def read_json_object(data: bytes, name: str) -> dict[str, Any]:
    def reject_duplicate_fields(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for field, value in pairs:
            if field in result:
                raise LicenseValidationError(
                    f"{name} contains a duplicate JSON field: {field}"
                )
            result[field] = value
        return result

    def reject_non_finite(value: str) -> None:
        raise LicenseValidationError(f"{name} contains a non-finite JSON number: {value}")

    try:
        text = data.decode("utf-8")
        value = json.loads(
            text,
            object_pairs_hook=reject_duplicate_fields,
            parse_constant=reject_non_finite,
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise LicenseValidationError(f"{name} cannot be parsed as UTF-8 JSON: {error}") from error
    if not isinstance(value, dict):
        raise LicenseValidationError(f"{name} must contain a JSON object")
    return value


def read_bytes(path: Path, name: str) -> bytes:
    try:
        if path.is_symlink() or not path.is_file():
            raise LicenseValidationError(f"{name} must be a regular non-symlink file")
        return path.read_bytes()
    except LicenseValidationError:
        raise
    except OSError as error:
        raise LicenseValidationError(f"{name} cannot be read: {error}") from error


def confined_path(root: Path, relative: Path) -> Path:
    candidate = root
    for part in relative.parts:
        candidate /= part
        if candidate.is_symlink():
            raise LicenseValidationError(
                f"repository evidence path cannot contain symlinks: {relative.as_posix()}"
            )
    resolved_candidate = candidate.resolve()
    try:
        resolved_candidate.relative_to(root.resolve())
    except ValueError as error:
        raise LicenseValidationError(f"path escapes repository root: {relative.as_posix()}") from error
    return resolved_candidate


def safe_relative_path(value: Any, name: str) -> Path:
    text = required_clean_string(value, name)
    relative = Path(text)
    if relative.is_absolute() or ".." in relative.parts or relative.as_posix() != text:
        raise LicenseValidationError(f"{name} must be a normalized repository-relative path")
    return relative


def nested_string(data: dict[str, Any], keys: tuple[str, ...], name: str) -> str:
    value: Any = data
    for key in keys:
        if not isinstance(value, dict) or key not in value:
            raise LicenseValidationError(f"{name} is missing")
        value = value[key]
    return required_clean_string(value, name)


def required_clean_string(value: Any, name: str, minimum: int = 1) -> str:
    if (
        not isinstance(value, str)
        or value.strip() != value
        or len(value) < minimum
        or len(value) > 256
        or any(ord(character) < 0x20 for character in value)
    ):
        raise LicenseValidationError(f"{name} must be a bounded, nonblank string")
    return value


def required_sha256(value: Any, name: str) -> str:
    if not isinstance(value, str) or not SHA256_PATTERN.fullmatch(value):
        raise LicenseValidationError(f"{name} must be 64 lowercase hexadecimal characters")
    return value


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def main(arguments: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--release",
        action="store_true",
        help="require owner-approved distribution terms and exact disposition evidence",
    )
    parsed = parser.parse_args(arguments)
    root = Path(__file__).resolve().parents[1]
    try:
        evidence = validate_repository(
            root,
            parsed.release,
            trusted_approval_sha256=os.environ.get(TRUSTED_APPROVAL_SHA256_ENV),
        )
    except LicenseValidationError as error:
        print(str(error), file=sys.stderr)
        return 1
    print(json.dumps(evidence, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
