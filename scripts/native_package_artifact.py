#!/usr/bin/env python3
"""Create or verify source-bound native NuGet artifact metadata; never publish."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile

from qualification.check_native_package import (
    EXPECTED_ARTIFACTS, EXPECTED_AUTH_ARTIFACTS, EXPECTED_PROVENANCE_SHA256,
)


ROOT = Path(__file__).resolve().parents[1]
MANIFEST_NAME = "native-package.json"
PACKAGE_LIMIT = 64 * 1024 * 1024
FRAMEWORKS = ("net10.0", "net11.0")


def inspect_package(package: Path, revision: str, version: str) -> dict:
    if not package.is_file() or package.stat().st_size > PACKAGE_LIMIT:
        raise ValueError("The native package is missing or exceeds 64 MiB.")
    if re.fullmatch(r"[0-9a-f]{40}", revision) is None:
        raise ValueError("Supply the exact lowercase Git source revision.")
    if re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", version) is None:
        raise ValueError("Supply the exact NuGet package version.")
    expected_filename = f"TerraScale.DotNet.{version}.nupkg"
    if package.name != expected_filename:
        raise ValueError("The native package filename differs from its version.")
    entries = {}
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError("The package contains duplicate archive entries.")
        if sum(entry.file_size for entry in archive.infolist()) > 128 * 1024 * 1024:
            raise ValueError("The package expanded content exceeds 128 MiB.")
        nuspecs = [name for name in names if name.endswith(".nuspec")]
        if len(nuspecs) != 1 or archive.getinfo(nuspecs[0]).file_size > 1024 * 1024:
            raise ValueError("The native package metadata is invalid.")
        metadata = ET.fromstring(archive.read(nuspecs[0]))
        fields = {element.tag.rsplit("}", 1)[-1]: element.text for element in metadata.iter()}
        repository = next((element for element in metadata.iter()
                           if element.tag.rsplit("}", 1)[-1] == "repository"), None)
        if fields.get("id") != "TerraScale.DotNet" or fields.get("version") != version:
            raise ValueError("The package ID or version differs.")
        if repository is None or repository.get("commit") != revision or repository.get("type") != "git" or \
                repository.get("url") != "https://git.terrascale.tech/TerraScale/ts-dotnet":
            raise ValueError("The package does not bind the expected official Git source revision.")
        expected_contracts = {"contracts/native/" + name for name in EXPECTED_ARTIFACTS}
        expected_contracts.update("contracts/auth/" + name for name in EXPECTED_AUTH_ARTIFACTS)
        expected_contracts.add("contracts/native/provenance.json")
        if {name for name in names if name.startswith("contracts/")} != expected_contracts:
            raise ValueError("The package contract layout differs.")
        for name, (size, digest) in EXPECTED_ARTIFACTS.items():
            path = "contracts/native/" + name
            if archive.getinfo(path).file_size != size:
                raise ValueError("The package native contract size differs.")
            data = archive.read(path)
            if len(data) != size or hashlib.sha256(data).hexdigest() != digest:
                raise ValueError("The package native contract bytes differ.")
            entries[path] = {"bytes": size, "sha256": digest}
        for name, (size, digest) in EXPECTED_AUTH_ARTIFACTS.items():
            path = "contracts/auth/" + name
            if archive.getinfo(path).file_size != size:
                raise ValueError("The package auth contract size differs.")
            data = archive.read(path)
            if len(data) != size or hashlib.sha256(data).hexdigest() != digest:
                raise ValueError("The package auth contract bytes differ.")
            entries[path] = {"bytes": size, "sha256": digest}
        if archive.getinfo("contracts/native/provenance.json").file_size > 8192:
            raise ValueError("The package provenance exceeds its bound.")
        provenance = archive.read("contracts/native/provenance.json")
        if hashlib.sha256(provenance).hexdigest() != EXPECTED_PROVENANCE_SHA256:
            raise ValueError("The package provenance differs.")
        entries["contracts/native/provenance.json"] = {
            "bytes": len(provenance), "sha256": EXPECTED_PROVENANCE_SHA256}
        libraries = {name for name in names if name.startswith("lib/") and name.endswith(".dll")}
        if libraries != {f"lib/{framework}/TerraScale.DotNet.dll" for framework in FRAMEWORKS}:
            raise ValueError("The official artifact must contain exactly the .NET 10 and .NET 11 SDK assemblies.")
        for path in sorted(libraries):
            if archive.getinfo(path).file_size > PACKAGE_LIMIT:
                raise ValueError("The package assembly exceeds its bound.")
            data = archive.read(path)
            entries[path] = {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
    return {
        "schema": "terrascale.dotnet.native-package-artifact.v1",
        "package_id": "TerraScale.DotNet",
        "package_version": version,
        "source_revision": revision,
        "package_file": expected_filename,
        "package_bytes": package.stat().st_size,
        "package_sha256": hashlib.sha256(package.read_bytes()).hexdigest(),
        "archive_entries": entries,
        "publication": "NOT PUBLISHED",
    }


def create(arguments: argparse.Namespace) -> None:
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    dirty = subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=all"],
                                    cwd=ROOT, text=True)
    if head != arguments.source_revision or dirty:
        raise ValueError("Artifact creation requires the exact clean checked-out source revision.")
    manifest = inspect_package(arguments.package, arguments.source_revision, arguments.version)
    arguments.directory.mkdir(parents=True, exist_ok=False)
    shutil.copyfile(arguments.package, arguments.directory / manifest["package_file"])
    (arguments.directory / MANIFEST_NAME).write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (arguments.directory / "SHA256SUMS").write_text(
        manifest["package_sha256"] + "  " + manifest["package_file"] + "\n", encoding="utf-8")
    print(json.dumps(manifest, separators=(",", ":")))


def verify(arguments: argparse.Namespace) -> None:
    if re.fullmatch(r"[0-9a-f]{64}", arguments.sha256) is None:
        raise ValueError("Supply the independently retained exact package SHA-256.")
    metadata_path = arguments.directory / MANIFEST_NAME
    if metadata_path.stat().st_size > 64 * 1024:
        raise ValueError("The artifact metadata exceeds its bound.")
    expected = json.loads(metadata_path.read_text(encoding="utf-8"))
    package = arguments.directory / f"TerraScale.DotNet.{arguments.version}.nupkg"
    actual = inspect_package(package, arguments.source_revision, arguments.version)
    if expected != actual or actual["package_sha256"] != arguments.sha256:
        raise ValueError("Artifact metadata or independently retained package identity differs.")
    if arguments.feed is not None:
        arguments.feed.mkdir(parents=True, exist_ok=False)
        shutil.copyfile(package, arguments.feed / package.name)
        if hashlib.sha256((arguments.feed / package.name).read_bytes()).hexdigest() != arguments.sha256:
            raise ValueError("The feed copy differs from the qualified package.")
    print(json.dumps({"status": "VERIFIED_EXACT_NATIVE_PACKAGE", **actual}, separators=(",", ":")))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("create", "verify"):
        command = commands.add_parser(name)
        command.add_argument("--directory", type=Path, required=True)
        command.add_argument("--source-revision", required=True)
        command.add_argument("--version", required=True)
        if name == "create":
            command.add_argument("--package", type=Path, required=True)
        else:
            command.add_argument("--sha256", required=True)
            command.add_argument("--feed", type=Path)
    arguments = parser.parse_args()
    try:
        (create if arguments.command == "create" else verify)(arguments)
    except (OSError, ValueError, KeyError, ET.ParseError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        raise SystemExit(str(error)) from error


if __name__ == "__main__":
    main()
