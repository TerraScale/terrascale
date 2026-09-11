#!/usr/bin/env python3
"""Create a reproducible, content-addressed public contract archive.

The contract generator owns the directory bundle.  This script is the
producer-side handoff to SDK consumers: it packages exactly that directory
with stable tar metadata and refuses to replace an existing archive.  The
archive is intentionally ordinary POSIX tar so the language SDK installers
can consume the same bytes without a sibling checkout.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import tarfile
import tempfile


REQUIRED = (
    "manifest.json",
    "openapi.json",
    "msgpack/terrascale.sync.v1.msgpack.json",
)
VERSION_RE = re.compile(r"v[1-9][0-9]*\Z")


def fail(message: str) -> "NoReturn":
    raise SystemExit(f"contract archive: {message}")


def regular_file(path: Path, label: str) -> None:
    try:
        info = path.lstat()
    except OSError as error:
        fail(f"{label} is unavailable: {error}")
    if not stat.S_ISREG(info.st_mode):
        fail(f"{label} is not a regular file")
    if info.st_nlink != 1:
        fail(f"{label} is a hard link")


def validate_source(source: Path, version: str) -> dict:
    if VERSION_RE.fullmatch(version) is None:
        fail(f"invalid contract version {version!r}")
    if source.is_symlink() or not source.is_dir():
        fail(f"source is not a safe directory: {source}")
    for path in source.rglob("*"):
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode):
            fail(f"source contains a symlink: {path}")
        if stat.S_ISREG(info.st_mode) and info.st_nlink != 1:
            fail(f"source contains a hard link: {path}")
        if not stat.S_ISREG(info.st_mode) and not stat.S_ISDIR(info.st_mode):
            fail(f"source contains a special entry: {path}")

    manifest_path = source / "manifest.json"
    regular_file(manifest_path, "manifest.json")
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        fail(f"manifest.json is invalid: {error}")
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 1:
        fail("manifest schemaVersion is not 1")
    if manifest.get("contractVersion") != version:
        fail(
            f"manifest contractVersion is {manifest.get('contractVersion')!r}, "
            f"expected {version!r}"
        )
    artifacts = manifest.get("artifacts")
    if not isinstance(artifacts, list) or not artifacts:
        fail("manifest has no artifacts")
    listed = set()
    for entry in artifacts:
        if not isinstance(entry, dict) or not isinstance(entry.get("path"), str):
            fail("manifest contains an invalid artifact")
        relative = entry["path"]
        posix = PurePosixPath(relative)
        if not relative or posix.is_absolute() or ".." in posix.parts or "\\" in relative:
            fail(f"manifest contains an unsafe artifact path: {relative!r}")
        if relative in listed:
            fail(f"manifest repeats artifact {relative!r}")
        listed.add(relative)
        regular_file(source / Path(relative), relative)
    for relative in REQUIRED:
        regular_file(source / relative, relative)
        if relative != "manifest.json" and relative not in listed:
            fail(f"manifest does not pin required artifact {relative}")
    return manifest


def archive_bytes(source: Path, version: str) -> bytes:
    """Build tar bytes with no source filesystem metadata in the result."""
    with tempfile.TemporaryDirectory(prefix="terrascale-contract-archive-") as temporary:
        path = Path(temporary) / "bundle.tar"
        with tarfile.open(path, mode="w", format=tarfile.USTAR_FORMAT) as archive:
            entries = sorted(
                (candidate for candidate in source.rglob("*") if candidate.is_file()),
                key=lambda candidate: candidate.relative_to(source).as_posix(),
            )
            for candidate in entries:
                relative = candidate.relative_to(source).as_posix()
                member = tarfile.TarInfo(f"{version}/{relative}")
                data = candidate.read_bytes()
                member.size = len(data)
                member.mtime = 0
                member.mode = 0o644
                member.uid = 0
                member.gid = 0
                member.uname = ""
                member.gname = ""
                archive.addfile(member, io.BytesIO(data))
        return path.read_bytes()


def write_once(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() or path.is_symlink():
        regular_file(path, str(path))
        if path.read_bytes() != data:
            fail(f"refusing to replace immutable output {path}")
        return
    temporary = path.with_name(f".{path.name}.tmp-{os.getpid()}")
    try:
        with temporary.open("xb") as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def package(source: Path, output: Path, version: str) -> dict:
    manifest = validate_source(source, version)
    payload = archive_bytes(source, version)
    digest = hashlib.sha256(payload).hexdigest()
    manifest_digest = hashlib.sha256((source / "manifest.json").read_bytes()).hexdigest()
    metadata = {
        "schemaVersion": 1,
        "contractVersion": version,
        "archive": output.name,
        "archiveSha256": digest,
        "manifestSha256": manifest_digest,
        "artifactCount": len(manifest["artifacts"]),
    }
    write_once(output, payload)
    metadata_bytes = (json.dumps(metadata, indent=2, sort_keys=True) + "\n").encode()
    write_once(Path(f"{output}.json"), metadata_bytes)
    write_once(Path(f"{output}.sha256"), f"{digest}  {output.name}\n".encode())
    return metadata


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", default="contracts")
    parser.add_argument("--output", required=True)
    parser.add_argument("--version", default="v1")
    args = parser.parse_args()
    metadata = package(Path(args.source).expanduser(), Path(args.output).expanduser(), args.version)
    print(json.dumps(metadata, sort_keys=True))


if __name__ == "__main__":
    main()
