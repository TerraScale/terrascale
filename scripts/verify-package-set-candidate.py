#!/usr/bin/env python3
"""Verify the exact TerraScale .NET SDK and EF Core package set."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any


REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
PACKAGE_VERSION = "0.1.0"
SOURCE_REF = "refs/heads/develop"
REPOSITORY_URL = "https://git.terrascale.tech/TerraScale/TerraScale.DotNet"
HANDOFF_FORMAT = "TerraScale.DotNet.PackageSetHandoff/v2"
RECEIPT_FORMAT = "TerraScale.DotNet.PackageSetVerificationReceipt/v2"
PACKAGE_SET_OUTPUT = Path("artifacts/candidate/dotnet-package-set-v2")
HANDOFF_FILENAME = "terrascale-dotnet-package-set-handoff.json"
NUSPEC_NAMESPACE = "{http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd}"
SHA256_PATTERN = re.compile(r"[0-9a-f]{64}")
REVISION_PATTERN = re.compile(r"[0-9a-f]{40}")
FORBIDDEN_CONTENT = re.compile(
    rb"tsauth_[A-Za-z0-9_.-]+|localhost(?:[:/]|$)|127\.0\.0\.1|"
    rb"(?:^|[^A-Za-z0-9])/(?:home|workspaces)/|\\Users\\|/api/v1/internal|"
    rb"/api/v(?!1(?:/|$))[0-9]+(?:/|$)",
    re.IGNORECASE,
)
AUDIT_WARNINGS = "NU1900;NU1901;NU1902;NU1903;NU1904"
MAX_ARCHIVE_ENTRIES = 64
MAX_ENTRY_BYTES = 32 * 1024 * 1024
MAX_ARCHIVE_BYTES = 64 * 1024 * 1024
MAX_COMPRESSION_RATIO = 200
MAX_HANDOFF_BYTES = 64 * 1024


@dataclass(frozen=True)
class PackageDefinition:
    package_id: str
    project: str
    expected_entries: frozenset[str]
    dependencies: frozenset[tuple[str, str, str]]

    @property
    def filename(self) -> str:
        return f"{self.package_id}.{PACKAGE_VERSION}.nupkg"


PACKAGE_DEFINITIONS = {
    definition.package_id: definition
    for definition in (
        PackageDefinition(
            package_id="TerraScale.DotNet",
            project="src/TerraScale.DotNet/TerraScale.DotNet.csproj",
            expected_entries=frozenset(
                {
                    "_rels/.rels",
                    "TerraScale.DotNet.nuspec",
                    "README.md",
                    "lib/net10.0/TerraScale.DotNet.dll",
                    "lib/net10.0/TerraScale.DotNet.xml",
                    "[Content_Types].xml",
                    "package/services/metadata/core-properties/nuget.psmdcp",
                }
            ),
            dependencies=frozenset(
                {
                    ("Microsoft.Extensions.Http", "10.0.11", "Build,Analyzers"),
                    ("ZString", "2.6.0", "Build,Analyzers"),
                }
            ),
        ),
        PackageDefinition(
            package_id="TerraScale.EntityFrameworkCore",
            project="src/TerraScale.EntityFrameworkCore/TerraScale.EntityFrameworkCore.csproj",
            expected_entries=frozenset(
                {
                    "_rels/.rels",
                    "TerraScale.EntityFrameworkCore.nuspec",
                    "README.md",
                    "lib/net10.0/TerraScale.EntityFrameworkCore.dll",
                    "lib/net10.0/TerraScale.EntityFrameworkCore.xml",
                    "[Content_Types].xml",
                    "package/services/metadata/core-properties/nuget.psmdcp",
                }
            ),
            dependencies=frozenset(
                {
                    ("TerraScale.DotNet", PACKAGE_VERSION, "Build,Analyzers"),
                    ("Microsoft.EntityFrameworkCore", "10.0.11", "Build,Analyzers"),
                }
            ),
        ),
    )
}


class VerificationError(RuntimeError):
    """A fail-closed candidate validation error."""


@dataclass
class Candidate:
    definition: PackageDefinition
    source_path: Path
    expected_digest: str
    digest: str = ""
    identity: tuple[int, int, int, int, int] | None = None
    staged_path: Path | None = None


def run(command: list[str], *, capture: bool = False, cwd: Path = REPOSITORY_ROOT) -> str:
    try:
        completed = subprocess.run(
            command,
            cwd=cwd,
            check=True,
            text=True,
            stdout=subprocess.PIPE if capture else None,
            stderr=subprocess.PIPE if capture else None,
        )
    except FileNotFoundError as error:
        raise VerificationError(f"required command is unavailable: {command[0]}") from error
    except subprocess.CalledProcessError as error:
        if capture:
            detail = (error.stderr or error.stdout or "").strip()
            if detail:
                raise VerificationError(detail) from error
        raise VerificationError(f"command failed with exit code {error.returncode}: {' '.join(command)}") from error
    return completed.stdout.strip() if capture else ""


def git(*arguments: str) -> str:
    return run(["git", "-C", str(REPOSITORY_ROOT), *arguments], capture=True)


def file_identity(file_stat: os.stat_result) -> tuple[int, int, int, int, int]:
    return (
        file_stat.st_dev,
        file_stat.st_ino,
        file_stat.st_size,
        file_stat.st_mtime_ns,
        file_stat.st_ctime_ns,
    )


def require_plain_string(value: Any, name: str) -> str:
    if not isinstance(value, str) or not value or "\n" in value or "\r" in value:
        raise VerificationError(f"{name} must be a non-empty string without line breaks")
    return value


def safe_read_json(path: Path) -> tuple[Any, str]:
    def reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        value: dict[str, Any] = {}
        for key, item in pairs:
            if key in value:
                raise ValueError(f"duplicate JSON object key: {key}")
            value[key] = item
        return value

    try:
        path_stat = os.lstat(path)
        if not stat.S_ISREG(path_stat.st_mode) or stat.S_ISLNK(path_stat.st_mode):
            raise OSError("handoff must be a non-symlink regular file")
        if path_stat.st_size > MAX_HANDOFF_BYTES:
            raise OSError(f"handoff exceeds the {MAX_HANDOFF_BYTES}-byte limit")
        flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_CLOEXEC", 0)
        descriptor = os.open(path, flags)
        try:
            opened_stat = os.fstat(descriptor)
            expected_identity = file_identity(path_stat)
            if file_identity(opened_stat) != expected_identity:
                raise OSError("handoff changed while it was opened")
            with os.fdopen(descriptor, "rb") as stream:
                descriptor = -1
                contents = stream.read(MAX_HANDOFF_BYTES + 1)
            if len(contents) > MAX_HANDOFF_BYTES:
                raise OSError(f"handoff exceeds the {MAX_HANDOFF_BYTES}-byte limit")
            if file_identity(os.lstat(path)) != expected_identity:
                raise OSError("handoff changed while it was read")
            try:
                value = json.loads(contents.decode("utf-8"), object_pairs_hook=reject_duplicate_keys)
            except (UnicodeDecodeError, json.JSONDecodeError, ValueError) as error:
                raise OSError(f"handoff is not canonical UTF-8 JSON: {error}") from error
            return value, hashlib.sha256(contents).hexdigest()
        finally:
            if descriptor >= 0:
                os.close(descriptor)
    except (OSError, json.JSONDecodeError) as error:
        raise VerificationError(f"package-set handoff is not valid JSON: {error}") from error


def parse_handoff(path: Path) -> tuple[dict[str, Candidate], str, str, str, Path, str]:
    path = Path(os.path.abspath(path))
    if path != Path(os.path.realpath(path)):
        raise VerificationError("package-set handoff path and parent directories must not contain links")
    handoff, handoff_digest = safe_read_json(path)
    if not isinstance(handoff, dict) or set(handoff) != {"format", "packages", "source"}:
        raise VerificationError("package-set handoff contains unexpected fields")
    if handoff["format"] != HANDOFF_FORMAT:
        raise VerificationError(f"package-set handoff format must be {HANDOFF_FORMAT}")

    packages = handoff["packages"]
    source = handoff["source"]
    if not isinstance(packages, list) or len(packages) != len(PACKAGE_DEFINITIONS):
        raise VerificationError("package-set handoff must contain exactly the SDK and EF Core packages")
    if not isinstance(source, dict) or set(source) != {"revision", "ref", "tree"}:
        raise VerificationError("package-set handoff source contains unexpected fields")

    revision = require_plain_string(source["revision"], "source revision")
    source_ref = require_plain_string(source["ref"], "source ref")
    source_tree = require_plain_string(source["tree"], "source tree")
    if REVISION_PATTERN.fullmatch(revision) is None:
        raise VerificationError("source revision must be a 40-character lowercase hexadecimal Git commit")
    if REVISION_PATTERN.fullmatch(source_tree) is None:
        raise VerificationError("source tree must be a 40-character lowercase hexadecimal Git tree")
    if source_ref != SOURCE_REF:
        raise VerificationError(f"package-set source ref must be {SOURCE_REF}")

    candidates: dict[str, Candidate] = {}
    for index, package in enumerate(packages):
        if not isinstance(package, dict) or set(package) != {"id", "path", "sha256", "version"}:
            raise VerificationError(f"package-set entry {index} contains unexpected fields")
        package_id = require_plain_string(package["id"], f"package {index} id")
        version = require_plain_string(package["version"], f"package {index} version")
        package_path_value = require_plain_string(package["path"], f"package {index} path")
        digest = require_plain_string(package["sha256"], f"package {index} digest")
        if package_id not in PACKAGE_DEFINITIONS or package_id in candidates:
            raise VerificationError(f"package-set entry has an unknown or duplicate id: {package_id}")
        if version != PACKAGE_VERSION:
            raise VerificationError(f"{package_id} version must be {PACKAGE_VERSION}")
        if package_path_value != PACKAGE_DEFINITIONS[package_id].filename:
            raise VerificationError(
                f"{package_id} handoff path must be its exact sibling filename: "
                f"{PACKAGE_DEFINITIONS[package_id].filename}"
            )
        if SHA256_PATTERN.fullmatch(digest) is None:
            raise VerificationError(f"{package_id} digest must be lowercase SHA-256")
        package_path = Path(os.path.abspath(path.parent / package_path_value))
        candidates[package_id] = Candidate(PACKAGE_DEFINITIONS[package_id], package_path, digest)

    if set(candidates) != set(PACKAGE_DEFINITIONS):
        raise VerificationError("package-set handoff does not contain the required package identities")
    return candidates, revision, source_ref, source_tree, path, handoff_digest


def parse_direct(arguments: argparse.Namespace) -> tuple[dict[str, Candidate], str, str, str]:
    required = (
        arguments.sdk_package,
        arguments.sdk_sha256,
        arguments.ef_package,
        arguments.ef_sha256,
        arguments.source_revision,
    )
    if not all(required):
        raise VerificationError(
            "direct verification requires --sdk-package, --sdk-sha256, --ef-package, "
            "--ef-sha256, and --source-revision"
        )
    revision = arguments.source_revision.lower()
    if REVISION_PATTERN.fullmatch(revision) is None:
        raise VerificationError("source revision must be a 40-character lowercase hexadecimal Git commit")
    digests = {
        "TerraScale.DotNet": arguments.sdk_sha256.lower(),
        "TerraScale.EntityFrameworkCore": arguments.ef_sha256.lower(),
    }
    for package_id, digest in digests.items():
        if SHA256_PATTERN.fullmatch(digest) is None:
            raise VerificationError(f"{package_id} digest must be a 64-character hexadecimal SHA-256")
    candidates = {
        "TerraScale.DotNet": Candidate(
            PACKAGE_DEFINITIONS["TerraScale.DotNet"],
            Path(os.path.abspath(arguments.sdk_package)),
            digests["TerraScale.DotNet"],
        ),
        "TerraScale.EntityFrameworkCore": Candidate(
            PACKAGE_DEFINITIONS["TerraScale.EntityFrameworkCore"],
            Path(os.path.abspath(arguments.ef_package)),
            digests["TerraScale.EntityFrameworkCore"],
        ),
    }
    source_tree = git("rev-parse", "--verify", f"{revision}^{{tree}}")
    return candidates, revision, "", source_tree


def pack_clean_checkout(output: Path) -> tuple[dict[str, Candidate], str, str, str]:
    if git("status", "--porcelain=v1", "--untracked-files=all"):
        raise VerificationError("refusing no-argument pack from a dirty source tree; supply a verified immutable package set")
    revision = git("rev-parse", "--verify", "HEAD")
    source_tree = git("rev-parse", "--verify", "HEAD^{tree}")
    for definition in PACKAGE_DEFINITIONS.values():
        run(
            [
                "dotnet",
                "pack",
                str(REPOSITORY_ROOT / definition.project),
                "--configuration",
                "Release",
                f"--property:RepositoryCommit={revision}",
                "--output",
                str(output),
            ]
        )
    candidates: dict[str, Candidate] = {}
    for package_id, definition in PACKAGE_DEFINITIONS.items():
        source_path = output / definition.filename
        candidates[package_id] = Candidate(definition, source_path, sha256_file(source_path))
    return candidates, revision, SOURCE_REF, source_tree


def write_handoff(
    path: Path,
    candidates: dict[str, Candidate],
    source_revision: str,
    source_tree: str,
) -> str:
    if os.path.lexists(path):
        raise VerificationError("package-set handoff path must not already exist")
    handoff = {
        "format": HANDOFF_FORMAT,
        "packages": [
            {
                "id": package_id,
                "path": candidate.definition.filename,
                "sha256": candidate.expected_digest,
                "version": PACKAGE_VERSION,
            }
            for package_id, candidate in sorted(candidates.items())
        ],
        "source": {"ref": SOURCE_REF, "revision": source_revision, "tree": source_tree},
    }
    contents = (json.dumps(handoff, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
    try:
        with path.open("xb") as stream:
            stream.write(contents)
    except OSError as error:
        raise VerificationError(f"package-set handoff could not be written: {error}") from error
    return hashlib.sha256(contents).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def prepare_candidate_output() -> Path:
    output = REPOSITORY_ROOT / PACKAGE_SET_OUTPUT
    if os.path.lexists(output):
        raise VerificationError(f"candidate output must be absent before the build: {PACKAGE_SET_OUTPUT.as_posix()}")
    current = REPOSITORY_ROOT
    for component in PACKAGE_SET_OUTPUT.parent.parts:
        current /= component
        if os.path.lexists(current):
            current_stat = os.lstat(current)
            if not stat.S_ISDIR(current_stat.st_mode) or stat.S_ISLNK(current_stat.st_mode):
                raise VerificationError(f"candidate output parent must be a real directory: {current}")
        else:
            current.mkdir()
    output.mkdir()
    return output


def copy_candidate(candidate: Candidate, package_source: Path) -> None:
    source_path = candidate.source_path
    target_path = package_source / candidate.definition.filename
    try:
        path_stat = os.lstat(source_path)
        if not stat.S_ISREG(path_stat.st_mode) or stat.S_ISLNK(path_stat.st_mode):
            raise OSError("candidate package must be a non-symlink regular file")
        expected_identity = file_identity(path_stat)
        flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_CLOEXEC", 0)
        descriptor = os.open(source_path, flags)
        try:
            opened_stat = os.fstat(descriptor)
            if file_identity(opened_stat) != expected_identity:
                raise OSError("candidate package changed while it was opened")
            digest = hashlib.sha256()
            with os.fdopen(descriptor, "rb") as source_stream:
                descriptor = -1
                with target_path.open("xb") as target_stream:
                    for chunk in iter(lambda: source_stream.read(1024 * 1024), b""):
                        digest.update(chunk)
                        target_stream.write(chunk)
                final_identity = file_identity(os.fstat(source_stream.fileno()))
            if final_identity != expected_identity or file_identity(os.lstat(source_path)) != expected_identity:
                raise OSError("candidate package changed while it was copied")
        finally:
            if descriptor >= 0:
                os.close(descriptor)
    except OSError as error:
        raise VerificationError(f"{candidate.definition.package_id} could not be copied safely: {error}") from error

    actual_digest = digest.hexdigest()
    if actual_digest != candidate.expected_digest:
        raise VerificationError(f"{candidate.definition.package_id} digest does not match the handoff")
    candidate.digest = actual_digest
    candidate.identity = expected_identity
    candidate.staged_path = target_path


def exactly_one(parent: ET.Element, name: str) -> ET.Element:
    values = parent.findall(f"{NUSPEC_NAMESPACE}{name}")
    if len(values) != 1:
        raise VerificationError(f"candidate nuspec must contain exactly one {name} element")
    return values[0]


def verify_nuspec(contents: bytes, candidate: Candidate, source_revision: str) -> None:
    try:
        root = ET.fromstring(contents)
    except ET.ParseError as error:
        raise VerificationError(f"{candidate.definition.package_id} nuspec is invalid XML: {error}") from error
    if root.tag != f"{NUSPEC_NAMESPACE}package":
        raise VerificationError(f"{candidate.definition.package_id} nuspec has an unexpected XML root")
    metadata = root.find(f"{NUSPEC_NAMESPACE}metadata")
    if metadata is None:
        raise VerificationError(f"{candidate.definition.package_id} nuspec has no metadata")
    if exactly_one(metadata, "id").text != candidate.definition.package_id:
        raise VerificationError(f"{candidate.definition.package_id} nuspec id is invalid")
    if exactly_one(metadata, "version").text != PACKAGE_VERSION:
        raise VerificationError(f"{candidate.definition.package_id} nuspec version is invalid")
    license_element = exactly_one(metadata, "license")
    if license_element.attrib != {"type": "expression"} or license_element.text != "MIT":
        raise VerificationError(f"{candidate.definition.package_id} nuspec license is invalid")
    if exactly_one(metadata, "readme").text != "README.md":
        raise VerificationError(f"{candidate.definition.package_id} nuspec readme metadata is invalid")
    repository = exactly_one(metadata, "repository")
    expected_repository = {
        "type": "git",
        "url": REPOSITORY_URL,
        "branch": SOURCE_REF,
        "commit": source_revision,
    }
    if repository.attrib != expected_repository:
        raise VerificationError(f"{candidate.definition.package_id} nuspec repository identity is invalid")
    release_notes = exactly_one(metadata, "releaseNotes").text or ""
    for marker in ("TerraScale public API /api/v1", "stable .NET 10 LTS"):
        if marker not in release_notes:
            raise VerificationError(f"{candidate.definition.package_id} release notes are missing {marker}")
    dependencies = exactly_one(metadata, "dependencies")
    groups = dependencies.findall(f"{NUSPEC_NAMESPACE}group")
    if len(groups) != 1 or groups[0].attrib != {"targetFramework": "net10.0"}:
        raise VerificationError(f"{candidate.definition.package_id} must have one net10.0 dependency group")
    actual_dependencies = frozenset(
        (
            require_plain_string(dependency.attrib.get("id"), "dependency id"),
            require_plain_string(dependency.attrib.get("version"), "dependency version"),
            require_plain_string(dependency.attrib.get("exclude"), "dependency exclusion"),
        )
        for dependency in groups[0].findall(f"{NUSPEC_NAMESPACE}dependency")
    )
    if actual_dependencies != candidate.definition.dependencies:
        raise VerificationError(f"{candidate.definition.package_id} dependency closure declaration is invalid")


def verify_archive(candidate: Candidate, source_revision: str) -> None:
    if candidate.staged_path is None:
        raise VerificationError("candidate was not staged")
    try:
        with zipfile.ZipFile(candidate.staged_path) as archive:
            entries = archive.infolist()
            if not entries or len(entries) > MAX_ARCHIVE_ENTRIES:
                raise VerificationError(f"{candidate.definition.package_id} archive entry count is invalid")
            names: set[str] = set()
            total_size = 0
            for entry in entries:
                name = entry.filename
                if (
                    not name
                    or "\x00" in name
                    or "\\" in name
                    or name.startswith("/")
                    or re.match(r"^[A-Za-z]:", name)
                    or ".." in name.split("/")
                    or "*" in name
                    or "?" in name
                    or (("[" in name or "]" in name) and name != "[Content_Types].xml")
                ):
                    raise VerificationError(f"{candidate.definition.package_id} contains an unsafe ZIP path: {name!r}")
                if name in names:
                    raise VerificationError(f"{candidate.definition.package_id} contains a duplicate ZIP path: {name!r}")
                names.add(name)
                mode = (entry.external_attr >> 16) & 0xFFFF
                file_type = stat.S_IFMT(mode)
                is_directory = name.endswith("/")
                if file_type and file_type not in (stat.S_IFREG, stat.S_IFDIR):
                    raise VerificationError(f"{candidate.definition.package_id} contains a non-regular ZIP entry: {name!r}")
                if is_directory and file_type not in (0, stat.S_IFDIR):
                    raise VerificationError(f"{candidate.definition.package_id} contains an invalid directory entry: {name!r}")
                if not is_directory and file_type == stat.S_IFDIR:
                    raise VerificationError(f"{candidate.definition.package_id} contains a directory with a file path: {name!r}")
                if entry.file_size > MAX_ENTRY_BYTES:
                    raise VerificationError(f"{candidate.definition.package_id} entry exceeds the unpacked size limit: {name!r}")
                total_size += entry.file_size
                if total_size > MAX_ARCHIVE_BYTES:
                    raise VerificationError(f"{candidate.definition.package_id} exceeds the unpacked archive size limit")
                if entry.file_size and (
                    entry.compress_size == 0 or entry.file_size > entry.compress_size * MAX_COMPRESSION_RATIO
                ):
                    raise VerificationError(f"{candidate.definition.package_id} entry exceeds the compression-ratio limit: {name!r}")
            if names != set(candidate.definition.expected_entries):
                missing = sorted(set(candidate.definition.expected_entries) - names)
                unexpected = sorted(names - set(candidate.definition.expected_entries))
                raise VerificationError(
                    f"{candidate.definition.package_id} archive contents differ from the package contract; "
                    f"missing={missing}, unexpected={unexpected}"
                )
            invalid_crc = archive.testzip()
            if invalid_crc is not None:
                raise VerificationError(f"{candidate.definition.package_id} has an invalid CRC: {invalid_crc}")
            for entry in entries:
                if entry.is_dir():
                    continue
                contents = archive.read(entry)
                if FORBIDDEN_CONTENT.search(contents):
                    raise VerificationError(
                        f"{candidate.definition.package_id} contains a secret, local path, or unpublished endpoint: {entry.filename}"
                    )
            verify_nuspec(
                archive.read(f"{candidate.definition.package_id}.nuspec"),
                candidate,
                source_revision,
            )
    except (OSError, zipfile.BadZipFile, zipfile.LargeZipFile) as error:
        raise VerificationError(f"{candidate.definition.package_id} archive is invalid: {error}") from error


def verify_source_identity(source_revision: str, source_ref: str, source_tree: str, *, handoff: bool) -> None:
    if REVISION_PATTERN.fullmatch(source_revision) is None or REVISION_PATTERN.fullmatch(source_tree) is None:
        raise VerificationError("source revision and tree must be lowercase 40-character hexadecimal identities")
    run(["git", "-C", str(REPOSITORY_ROOT), "cat-file", "-e", f"{source_revision}^{{commit}}"])
    actual_tree = git("rev-parse", "--verify", f"{source_revision}^{{tree}}")
    if actual_tree != source_tree:
        raise VerificationError("source tree does not belong to the declared source revision")
    if not handoff:
        return
    checked_out_revision = git("rev-parse", "--verify", "HEAD")
    checked_out_tree = git("rev-parse", "--verify", "HEAD^{tree}")
    if source_revision != checked_out_revision or source_tree != checked_out_tree:
        raise VerificationError("package-set source identity does not match the protected checkout")
    try:
        resolved_ref = git("rev-parse", "--verify", "refs/remotes/origin/develop^{commit}")
    except VerificationError:
        resolved_ref = git("rev-parse", "--verify", f"{source_ref}^{{commit}}")
    if resolved_ref != source_revision:
        raise VerificationError("package-set source ref does not resolve to the protected source revision")


def find_assets(build_output: Path) -> Path:
    assets = list(build_output.rglob("project.assets.json"))
    if len(assets) != 1:
        raise VerificationError("clean package-set consumer did not produce exactly one project.assets.json")
    return assets[0]


def validate_assets(path: Path) -> list[tuple[str, str]]:
    try:
        assets = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise VerificationError(f"clean consumer assets are invalid JSON: {error}") from error
    expected = {f"{package_id}/{PACKAGE_VERSION}" for package_id in PACKAGE_DEFINITIONS}
    libraries = assets.get("libraries")
    targets = assets.get("targets")
    if not isinstance(libraries, dict) or not isinstance(targets, dict):
        raise VerificationError("clean consumer assets omit libraries or targets")
    for identity in expected:
        library = libraries.get(identity)
        if not isinstance(library, dict) or library.get("type") != "package":
            raise VerificationError(f"clean consumer did not restore {identity} as a package")
        if not any(isinstance(target, dict) and identity in target for target in targets.values()):
            raise VerificationError(f"clean consumer targets do not reference {identity}")
    closure: list[tuple[str, str]] = []
    for identity, library in libraries.items():
        if isinstance(library, dict) and library.get("type") == "package":
            if "/" not in identity:
                raise VerificationError(f"clean consumer contains an invalid package identity: {identity}")
            package_id, version = identity.rsplit("/", 1)
            closure.append((package_id, version))
    return sorted(closure, key=lambda item: (item[0].lower(), item[1]))


def restored_package_path(package_cache: Path, package_id: str, version: str) -> Path:
    directory = package_cache / package_id.lower() / version
    packages = list(directory.glob("*.nupkg")) if directory.is_dir() else []
    if len(packages) != 1:
        raise VerificationError(f"NuGet did not retain exactly one archive for {package_id}/{version}")
    return packages[0]


def dependency_licenses(package_cache: Path, closure: list[tuple[str, str]]) -> list[dict[str, str]]:
    dispositions: list[dict[str, str]] = []
    for package_id, version in closure:
        directory = package_cache / package_id.lower() / version
        nuspecs = list(directory.glob("*.nuspec")) if directory.is_dir() else []
        if len(nuspecs) != 1:
            raise VerificationError(f"restored package {package_id}/{version} has no unique nuspec")
        try:
            root = ET.parse(nuspecs[0]).getroot()
        except (OSError, ET.ParseError) as error:
            raise VerificationError(f"restored package {package_id}/{version} has an invalid nuspec: {error}") from error
        namespace = ""
        if root.tag.startswith("{"):
            namespace = root.tag[: root.tag.index("}") + 1]
        metadata = root.find(f"{namespace}metadata")
        licenses = [] if metadata is None else metadata.findall(f"{namespace}license")
        if len(licenses) != 1 or licenses[0].attrib.get("type") != "expression" or not licenses[0].text:
            raise VerificationError(f"restored package {package_id}/{version} lacks an SPDX license expression")
        expression = licenses[0].text.strip()
        if expression != "MIT":
            raise VerificationError(
                f"restored package {package_id}/{version} has an unapproved license expression: {expression}"
            )
        dispositions.append({"id": package_id, "version": version, "license": expression})
    return dispositions


def verify_clean_consumer(
    candidates: dict[str, Candidate],
    package_source: Path,
    package_cache: Path,
    build_output: Path,
    consumer_root: Path,
) -> list[dict[str, str]]:
    consumer_source = REPOSITORY_ROOT / "tests/TerraScale.PackageSetConsumer"
    for filename in ("Program.cs", "TerraScale.PackageSetConsumer.csproj"):
        source = consumer_source / filename
        if not source.is_file() or source.is_symlink():
            raise VerificationError(f"clean package-set consumer source is unavailable: {source}")
        shutil.copyfile(source, consumer_root / filename)
    if any(consumer_root.glob("Directory.Build.*")):
        raise VerificationError("clean package-set consumer inherited repository build imports")

    project = consumer_root / "TerraScale.PackageSetConsumer.csproj"
    run(
        [
            "dotnet",
            "restore",
            str(project),
            "--packages",
            str(package_cache),
            "--artifacts-path",
            str(build_output),
            "--source",
            str(package_source),
            "--source",
            "https://api.nuget.org/v3/index.json",
            "--no-cache",
            "--force-evaluate",
            "-p:NuGetAudit=true",
            "-p:NuGetAuditMode=all",
            "-p:NuGetAuditLevel=low",
            "--property:TreatWarningsAsErrors=true",
        ]
    )
    closure = validate_assets(find_assets(build_output))
    for package_id, candidate in candidates.items():
        restored = restored_package_path(package_cache, package_id, PACKAGE_VERSION)
        if sha256_file(restored) != candidate.digest:
            raise VerificationError(f"restored {package_id} bytes differ from the supplied candidate")
    licenses = dependency_licenses(package_cache, closure)
    run(
        [
            "dotnet",
            "build",
            str(project),
            "--configuration",
            "Release",
            "--no-restore",
            "--artifacts-path",
            str(build_output),
            "--property:TreatWarningsAsErrors=true",
        ]
    )
    return licenses


def revalidate_candidate(candidate: Candidate) -> None:
    if candidate.identity is None:
        raise VerificationError("candidate identity was not recorded")
    try:
        path_stat = os.lstat(candidate.source_path)
    except OSError as error:
        raise VerificationError(f"{candidate.definition.package_id} disappeared during verification: {error}") from error
    if (
        not stat.S_ISREG(path_stat.st_mode)
        or stat.S_ISLNK(path_stat.st_mode)
        or file_identity(path_stat) != candidate.identity
    ):
        raise VerificationError(f"{candidate.definition.package_id} changed during exact-byte verification")


def write_receipt(
    path: Path,
    candidates: dict[str, Candidate],
    source_revision: str,
    source_ref: str,
    source_tree: str,
    licenses: list[dict[str, str]],
    handoff: dict[str, str] | None,
) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if os.path.lexists(path):
        raise VerificationError("receipt path must not already exist")
    receipt = {
        "format": RECEIPT_FORMAT,
        "handoff": handoff,
        "verified": True,
        "packages": [
            {
                "id": package_id,
                "name": candidate.definition.filename,
                "path": str(candidate.source_path),
                "sha256": candidate.digest,
                "version": PACKAGE_VERSION,
            }
            for package_id, candidate in sorted(candidates.items())
        ],
        "source": {"revision": source_revision, "ref": source_ref, "tree": source_tree},
        "consumer": {
            "targetFramework": "net10.0",
            "warningsAsErrors": True,
            "directPackage": "TerraScale.EntityFrameworkCore",
        },
        "dependencyClosure": licenses,
        "vulnerabilityAudit": {
            "enabled": True,
            "mode": "all",
            "level": "low",
            "warningsAsErrors": AUDIT_WARNINGS.split(";"),
        },
    }
    temporary = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    try:
        with temporary.open("x", encoding="utf-8") as stream:
            json.dump(receipt, stream, sort_keys=True, separators=(",", ":"))
            stream.write("\n")
        os.link(temporary, path, follow_symlinks=False)
        temporary.unlink()
    except OSError as error:
        try:
            temporary.unlink()
        except OSError:
            pass
        raise VerificationError(f"verification receipt could not be written: {error}") from error


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Verify the immutable TerraScale.DotNet and TerraScale.EntityFrameworkCore package set."
    )
    parser.add_argument("--handoff", type=Path)
    parser.add_argument("--sdk-package", type=Path)
    parser.add_argument("--sdk-sha256")
    parser.add_argument("--ef-package", type=Path)
    parser.add_argument("--ef-sha256")
    parser.add_argument("--source-revision")
    parser.add_argument(
        "--emit-candidate",
        action="store_true",
        help=f"Build and verify the clean package set at {PACKAGE_SET_OUTPUT.as_posix()}.",
    )
    parsed = parser.parse_args()
    direct_values = (
        parsed.sdk_package,
        parsed.sdk_sha256,
        parsed.ef_package,
        parsed.ef_sha256,
        parsed.source_revision,
    )
    if parsed.handoff is not None and (any(direct_values) or parsed.emit_candidate):
        parser.error("--handoff cannot be combined with direct package arguments or --emit-candidate")
    if parsed.emit_candidate and any(direct_values):
        parser.error("--emit-candidate cannot be combined with direct package arguments")
    if parsed.handoff is None and any(direct_values) and not all(direct_values):
        parser.error("all direct package arguments are required together")
    return parsed


def main() -> int:
    parsed = arguments()
    with tempfile.TemporaryDirectory(prefix="terrascale-package-set-") as temporary_name:
        verification_root = Path(temporary_name)
        if parsed.emit_candidate:
            package_output = prepare_candidate_output()
        else:
            package_output = verification_root / "packed"
        package_source = verification_root / "source"
        package_cache = verification_root / "packages"
        build_output = verification_root / "build"
        consumer_root = verification_root / "consumer"
        for directory in (package_source, package_cache, build_output, consumer_root):
            directory.mkdir()
        if not parsed.emit_candidate:
            package_output.mkdir()

        handoff_evidence: dict[str, str] | None = None
        if parsed.handoff is not None:
            (
                candidates,
                source_revision,
                source_ref,
                source_tree,
                handoff_path,
                handoff_digest,
            ) = parse_handoff(parsed.handoff)
            handoff_evidence = {
                "format": HANDOFF_FORMAT,
                "name": handoff_path.name,
                "path": str(handoff_path),
                "sha256": handoff_digest,
            }
            handoff_mode = True
        elif parsed.sdk_package is not None:
            candidates, source_revision, source_ref, source_tree = parse_direct(parsed)
            handoff_mode = False
        else:
            candidates, source_revision, source_ref, source_tree = pack_clean_checkout(package_output)
            handoff_mode = False

        if parsed.emit_candidate:
            emitted_handoff = package_output / HANDOFF_FILENAME
            emitted_handoff_digest = write_handoff(
                emitted_handoff,
                candidates,
                source_revision,
                source_tree,
            )
            handoff_evidence = {
                "format": HANDOFF_FORMAT,
                "name": HANDOFF_FILENAME,
                "path": str(emitted_handoff),
                "sha256": emitted_handoff_digest,
            }
            emitted_files = {path.name for path in package_output.iterdir() if path.is_file()}
            expected_emitted_files = {
                HANDOFF_FILENAME,
                *(definition.filename for definition in PACKAGE_DEFINITIONS.values()),
            }
            if emitted_files != expected_emitted_files or any(path.is_symlink() for path in package_output.iterdir()):
                raise VerificationError("emitted candidate does not contain exactly the portable three-file package set")

        verify_source_identity(source_revision, source_ref, source_tree, handoff=handoff_mode)
        for package_id in sorted(candidates):
            copy_candidate(candidates[package_id], package_source)
        staged_files = {path.name for path in package_source.iterdir() if path.is_file()}
        expected_files = {definition.filename for definition in PACKAGE_DEFINITIONS.values()}
        if staged_files != expected_files or any(path.is_symlink() for path in package_source.iterdir()):
            raise VerificationError("isolated package source does not contain exactly the candidate package set")
        for package_id in sorted(candidates):
            verify_archive(candidates[package_id], source_revision)
        licenses = verify_clean_consumer(
            candidates,
            package_source,
            package_cache,
            build_output,
            consumer_root,
        )
        for candidate in candidates.values():
            revalidate_candidate(candidate)

        receipt_path_value = os.environ.get("TERRASCALE_PACKAGE_RECEIPT_PATH", "")
        if receipt_path_value:
            receipt_path = Path(receipt_path_value)
            if parsed.emit_candidate:
                receipt_absolute = Path(os.path.abspath(receipt_path))
                try:
                    receipt_absolute.relative_to(package_output)
                except ValueError:
                    pass
                else:
                    raise VerificationError("verification receipt must stay outside immutable package-set bytes")
            write_receipt(
                receipt_path,
                candidates,
                source_revision,
                source_ref,
                source_tree,
                licenses,
                handoff_evidence,
            )
        print(f"source-revision:{source_revision}")
        print(f"source-tree:{source_tree}")
        for package_id in sorted(candidates):
            candidate = candidates[package_id]
            print(f"sha256:{candidate.digest} package:{candidate.definition.filename}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except VerificationError as error:
        print(str(error), file=sys.stderr)
        raise SystemExit(1) from error
