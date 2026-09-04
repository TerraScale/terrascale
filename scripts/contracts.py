#!/usr/bin/env python3
"""Install and verify the pinned TerraScale contract bundle.

The producer (terrabase) is authoritative.  A consumer may use a producer
directory only when CONTRACT_SOURCE is explicitly supplied, or may download
an immutable archive with CONTRACT_URL and CONTRACT_ARCHIVE_SHA256.  Nothing
in this module guesses a sibling checkout.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import tarfile
import tempfile
import urllib.request
import uuid


MAX_ARCHIVE_BYTES = 32 * 1024 * 1024
MAX_ARCHIVE_ENTRY_BYTES = 8 * 1024 * 1024
MAX_ARCHIVE_TOTAL_BYTES = 32 * 1024 * 1024
MAX_ARCHIVE_MEMBERS = 4096
MAX_CORPUS_BYTES = 8 * 1024 * 1024
VERSION_RE = re.compile(r"v[1-9][0-9]*\Z")
HEX64_RE = re.compile(r"[0-9a-fA-F]{64}\Z")
REQUIRED_ARTIFACTS = (
    "openapi.json",
    "msgpack/terrascale.sync.v1.msgpack.json",
    "proto/terrabase/v1/engine.proto",
    "proto/terrabase/v1/family.proto",
)
# Conformance is published by the producer at the same seam as the contract.
# Keep it in the installed bundle even while older draft manifests only list
# the four protocol artifacts; a consumer must never silently reuse a stale
# local corpus.
CORPUS_FILES = (
    "conformance/http.json",
    "conformance/reactive.json",
    "conformance/msgpack/message-vectors.json",
    "conformance/msgpack/hostile-vectors.json",
)


class ContractError(RuntimeError):
    """A contract input failed the consumer safety or parity gate."""


def fail(message: str) -> "NoReturn":
    raise ContractError(f"contracts: {message}")


def read_json(path: Path):
    regular_file(path, str(path))

    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                fail(f"duplicate JSON key {key!r} in {path}")
            result[key] = value
        return result

    try:
        return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=pairs)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        fail(f"invalid JSON in {path}: {error}")


def regular_file(path: Path, label: str) -> None:
    try:
        info = path.lstat()
    except OSError as error:
        fail(f"{label} is unavailable: {error}")
    if stat.S_ISLNK(info.st_mode):
        fail(f"{label} is a symlink")
    if not stat.S_ISREG(info.st_mode):
        fail(f"{label} is not a regular file")
    if info.st_nlink != 1:
        fail(f"{label} is a hard link")


def reject_links_and_specials(root: Path) -> None:
    if root.is_symlink():
        fail(f"contract root is a symlink: {root}")
    if not root.is_dir():
        fail(f"contract root is not a directory: {root}")
    for path in root.rglob("*"):
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode):
            fail(f"contract input contains a symlink: {path}")
        if stat.S_ISREG(info.st_mode):
            if info.st_nlink != 1:
                fail(f"contract input contains a hard link: {path}")
        elif not stat.S_ISDIR(info.st_mode):
            fail(f"contract input contains a special entry: {path}")


def blake3(data: bytes) -> str:
    """BLAKE3-256 without a third-party dependency."""
    iv = (0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
          0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
          0x1F83D9AB ^ 0x6A09E667, 0x5BE0CD19 ^ 0xBB67AE85,
          0x510E527F ^ 0x3C6EF372, 0x3C6EF372 ^ 0xA54FF53A,
          0xBB67AE85 ^ 0x510E527F, 0x6A09E667 ^ 0x5BE0CD19,
          0x243F6A88, 0x85A308D3)
    # The BLAKE3 IV is the SHA-256 IV followed by the four fixed words.
    iv = (0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
          0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
          0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
          0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19)
    permutation = (2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8)
    mask = 0xFFFFFFFF

    def g(state, a, b, c, d, x, y):
        state[a] = (state[a] + state[b] + x) & mask
        state[d] = ((state[d] ^ state[a]) >> 16 | (state[d] ^ state[a]) << 16) & mask
        state[c] = (state[c] + state[d]) & mask
        state[b] = ((state[b] ^ state[c]) >> 12 | (state[b] ^ state[c]) << 20) & mask
        state[a] = (state[a] + state[b] + y) & mask
        state[d] = ((state[d] ^ state[a]) >> 8 | (state[d] ^ state[a]) << 24) & mask
        state[c] = (state[c] + state[d]) & mask
        state[b] = ((state[b] ^ state[c]) >> 7 | (state[b] ^ state[c]) << 25) & mask

    def compress(cv, words, counter, length, flags):
        state = list(cv) + list(iv)
        state[12] = counter & mask
        state[13] = counter >> 32
        state[14] = length
        state[15] = flags
        message = list(words)
        for _ in range(7):
            g(state, 0, 4, 8, 12, message[0], message[1])
            g(state, 1, 5, 9, 13, message[2], message[3])
            g(state, 2, 6, 10, 14, message[4], message[5])
            g(state, 3, 7, 11, 15, message[6], message[7])
            g(state, 0, 5, 10, 15, message[8], message[9])
            g(state, 1, 6, 11, 12, message[10], message[11])
            g(state, 2, 7, 8, 13, message[12], message[13])
            g(state, 3, 4, 9, 14, message[14], message[15])
            message = [message[index] for index in permutation]
        return [(state[index] ^ state[index + 8]) & mask for index in range(8)] + [
            (state[index + 8] ^ cv[index]) & mask for index in range(8)]

    def words(block):
        padded = block + b"\0" * (64 - len(block))
        return [int.from_bytes(padded[offset:offset + 4], "little") for offset in range(0, 64, 4)]

    def chunk_output(chunk, index):
        cv = list(iv[:8])
        blocks = max(1, (len(chunk) + 63) // 64)
        for block_index in range(blocks - 1):
            flags = 1 if block_index == 0 else 0
            cv = compress(cv, words(chunk[block_index * 64:(block_index + 1) * 64]), index, 64, flags)[:8]
        start = (blocks - 1) * 64
        flags = 2 | (1 if blocks == 1 else 0)
        return cv, words(chunk[start:]), index, len(chunk[start:]), flags

    def output_cv(output):
        return compress(*output)[:8]

    def parent_output(left, right):
        return list(iv[:8]), list(left) + list(right), 0, 64, 4

    def root_bytes(output):
        return compress(output[0], output[1], 0, output[3], output[4] | 8)[:8]

    chunks = [data[offset:offset + 1024] for offset in range(0, len(data), 1024)] or [b""]
    stack = []
    current = None
    for index, chunk in enumerate(chunks):
        current = chunk_output(chunk, index)
        current_cv = output_cv(current)
        total = index + 1
        while total & 1 == 0:
            current = parent_output(stack.pop(), current_cv)
            current_cv = output_cv(current)
            total >>= 1
        stack.append(current_cv)
    stack.pop()
    while stack:
        current = parent_output(stack.pop(), output_cv(current))
    return b"".join(word.to_bytes(4, "little") for word in root_bytes(current)).hex()


def safe_relative(value: str, label: str) -> str:
    if not value or "\0" in value or "\\" in value:
        fail(f"{label} has an invalid path")
    path = PurePosixPath(value)
    if path.is_absolute() or ".." in path.parts:
        fail(f"{label} contains path traversal: {value!r}")
    normalized = str(path)
    if normalized in ("", ".") or normalized != value.rstrip("/"):
        fail(f"{label} is not normalized: {value!r}")
    return normalized


def validate_bundle(root: Path, version: str) -> tuple[dict, dict[str, str]]:
    if VERSION_RE.fullmatch(version) is None:
        fail(f"invalid contract version {version!r}")
    reject_links_and_specials(root)
    manifest = read_json(root / "manifest.json")
    if manifest.get("schemaVersion") != 1:
        fail("manifest schemaVersion is not 1")
    if manifest.get("contractVersion") != version:
        fail(f"manifest contractVersion is {manifest.get('contractVersion')!r}, expected {version!r}")
    artifacts = manifest.get("artifacts")
    if not isinstance(artifacts, list) or not artifacts:
        fail("manifest has no artifacts")
    digests: dict[str, str] = {}
    for entry in artifacts:
        if not isinstance(entry, dict):
            fail("manifest artifact is not an object")
        relative = entry.get("path")
        expected = entry.get("blake3")
        if not isinstance(relative, str) or not isinstance(expected, str) or not HEX64_RE.fullmatch(expected):
            fail(f"manifest artifact has an invalid path or digest: {entry!r}")
        relative = safe_relative(relative, "manifest artifact path")
        if relative in digests:
            fail(f"manifest repeats artifact path: {relative}")
        regular_file(root / relative, relative)
        actual = blake3((root / relative).read_bytes())
        if actual.lower() != expected.lower():
            fail(f"manifest digest mismatch for {relative}: {actual} != {expected}")
        digests[relative] = expected.lower()
    for relative in REQUIRED_ARTIFACTS:
        if relative not in digests:
            fail(f"manifest does not pin required artifact {relative}")
    return manifest, digests


def validate_corpus(root: Path, version: str | None = None) -> dict[str, str]:
    result = {}
    manifest_path = root / "msgpack/terrascale.sync.v1.msgpack.json"
    regular_file(manifest_path, "MessagePack manifest")
    manifest_sha = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
    for relative in CORPUS_FILES:
        path = corpus_file(root, relative)
        regular_file(path, relative)
        if path.stat().st_size > MAX_CORPUS_BYTES:
            fail(f"corpus file is too large: {relative}")
        value = read_json(path)
        if version is not None and value.get("contractVersion") != version:
            fail(f"corpus {relative} pins a different contract version")
        if relative.endswith("http.json"):
            if value.get("schemaVersion") != 1 or not isinstance(value.get("operations"), list) or not value["operations"]:
                fail("HTTP conformance fixture is incomplete")
        elif relative.endswith("reactive.json"):
            if value.get("schemaVersion") != 1 or not isinstance(value.get("attach"), dict):
                fail("reactive conformance fixture is incomplete")
        else:
            vectors = value.get("vectors")
            if not isinstance(vectors, list):
                fail(f"corpus {relative} has no vector list")
            if relative.endswith("message-vectors.json"):
                if value.get("protocol") != "terrascale.sync.v1.msgpack" or value.get("version") != "1.0":
                    fail("MessagePack corpus identity is not 1.0")
                if value.get("manifestSha256") != manifest_sha:
                    fail("MessagePack corpus pins a different manifest SHA-256")
                if len(vectors) < 11:
                    fail("MessagePack corpus is incomplete")
                for vector in vectors:
                    if not isinstance(vector, dict) or not isinstance(vector.get("hex"), str):
                        fail("MessagePack corpus contains an invalid vector")
                    try:
                        wire = bytes.fromhex(vector["hex"])
                    except ValueError:
                        fail("MessagePack corpus contains non-hex wire data")
                    if not wire or len(wire) > MAX_CORPUS_BYTES:
                        fail("MessagePack corpus contains an oversized wire vector")
            else:
                if len(vectors) < 10:
                    fail("hostile MessagePack corpus is incomplete")
                for vector in vectors:
                    if not isinstance(vector, dict):
                        fail("hostile corpus contains an invalid vector")
                    if "repeat" in vector:
                        repeat = vector["repeat"]
                        if not isinstance(repeat, dict) or not isinstance(repeat.get("count"), int) or repeat["count"] < 0 or repeat["count"] > MAX_CORPUS_BYTES:
                            fail("hostile corpus repeat is outside the bound")
                    elif isinstance(vector.get("hex"), str):
                        try:
                            bytes.fromhex(vector["hex"])
                        except ValueError:
                            fail("hostile corpus contains non-hex wire data")
                    else:
                        fail("hostile corpus contains an invalid vector")
        result[relative] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result


def archive_member_path(name: str) -> str:
    return safe_relative(name, "archive entry name")


def extract_archive(payload: bytes, destination: Path) -> Path:
    if len(payload) > MAX_ARCHIVE_BYTES:
        fail("contract archive is too large")
    archive_path = destination / "contract.archive"
    archive_path.write_bytes(payload)
    extracted = destination / "extracted"
    extracted.mkdir()
    try:
        with tarfile.open(archive_path, mode="r:*") as archive:
            members = archive.getmembers()
            if len(members) > MAX_ARCHIVE_MEMBERS:
                fail("contract archive has too many entries")
            seen = set()
            total = 0
            for member in members:
                normalized = archive_member_path(member.name)
                if normalized in seen:
                    fail(f"contract archive repeats entry: {member.name!r}")
                seen.add(normalized)
                target = (extracted / normalized).resolve()
                if extracted.resolve() not in target.parents:
                    fail("contract archive contains an unsafe path")
                if member.issym() or member.islnk():
                    fail(f"contract archive contains a link: {member.name!r}")
                if not member.isdir() and not member.isreg():
                    fail(f"contract archive contains a special entry: {member.name!r}")
                if member.isreg():
                    if member.size < 0 or member.size > MAX_ARCHIVE_ENTRY_BYTES:
                        fail(f"contract archive entry is too large: {member.name!r}")
                    total += member.size
                    if total > MAX_ARCHIVE_TOTAL_BYTES:
                        fail("contract archive uncompressed content is too large")
            archive.extractall(extracted)
    except (tarfile.TarError, OSError) as error:
        fail(f"contract archive is invalid: {error}")
    candidates = [extracted] + [path for path in extracted.iterdir() if path.is_dir()]
    for candidate in candidates:
        if (candidate / "manifest.json").is_file():
            return candidate
    fail("contract archive has no manifest.json")


def load_producer(source: str, url: str, archive_sha256: str, temporary: Path) -> Path:
    if source and url:
        fail("set only one of CONTRACT_SOURCE or CONTRACT_URL")
    if source:
        producer = Path(source).expanduser()
        reject_links_and_specials(producer)
        return producer
    if not url:
        fail("set explicit CONTRACT_SOURCE or CONTRACT_URL; refusing sibling checkout inference")
    if not HEX64_RE.fullmatch(archive_sha256 or ""):
        fail("CONTRACT_URL requires a 64-character CONTRACT_ARCHIVE_SHA256")
    parsed = urllib.request.urlparse(url)
    if parsed.scheme not in ("http", "https"):
        fail("CONTRACT_URL must use http or https")
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            payload = response.read(MAX_ARCHIVE_BYTES + 1)
    except Exception as error:  # noqa: BLE001
        fail(f"could not download CONTRACT_URL: {error}")
    if len(payload) > MAX_ARCHIVE_BYTES:
        fail("downloaded contract archive is too large")
    actual = hashlib.sha256(payload).hexdigest()
    if actual.lower() != archive_sha256.lower():
        fail(f"contract archive SHA-256 mismatch: {actual} != {archive_sha256}")
    return extract_archive(payload, temporary)


def find_corpus(source: Path, explicit: str, existing: Path | None) -> Path:
    candidates = [Path(explicit).expanduser()] if explicit else []
    # A pre-publication terrabase checkout keeps the generated conformance
    # directory beside its contracts directory. This is considered only after
    # an explicit source has been supplied; no sibling inference is used to
    # select a contract bundle.
    candidates.extend([source, source.parent, source.parent / "conformance"])
    if existing is not None:
        candidates.append(existing)
    seen: set[Path] = set()
    for candidate in candidates:
        candidate = candidate.absolute()
        if candidate in seen:
            continue
        seen.add(candidate)
        # Accept either a contract root (conformance/...) or the conformance
        # directory itself (http.json, reactive.json, msgpack/...).
        if all(corpus_file(candidate, relative).is_file() for relative in CORPUS_FILES):
            reject_links_and_specials(candidate)
            return candidate
    fail("MessagePack corpus is missing; pass explicit CONTRACT_CORPUS_SOURCE")


def corpus_file(root: Path, relative: str) -> Path:
    candidates = [root / relative, root / relative.removeprefix("conformance/")]
    if relative.startswith("conformance/msgpack/"):
        candidates.append(root / "msgpack" / Path(relative).name)
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    return candidates[0]


def write_provenance(root: Path, manifest: dict, digests: dict[str, str], corpus: dict[str, str]) -> None:
    values = {
        "schemaVersion": 1,
        "contractVersion": manifest["contractVersion"],
        "manifestSha256": hashlib.sha256((root / "manifest.json").read_bytes()).hexdigest(),
        "artifacts": {
            relative: {
                "blake3": digest,
                "sha256": hashlib.sha256((root / relative).read_bytes()).hexdigest(),
                "bytes": (root / relative).stat().st_size,
            }
            for relative, digest in sorted(digests.items())
        },
        "corpus": {relative: {"sha256": digest, "bytes": corpus_file(root, relative).stat().st_size} for relative, digest in sorted(corpus.items())},
    }
    (root / "provenance.json").write_text(json.dumps(values, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def scoped_remove(path: Path, parent: Path, prefix: str) -> None:
    if path.parent != parent or not path.name.startswith(prefix) or path.is_symlink():
        fail(f"refusing to remove unresolved path: {path}")
    if path.is_dir():
        shutil.rmtree(path)
    elif path.exists():
        path.unlink()


def install(version: str, source: str = "", url: str = "", archive_sha256: str = "", corpus_source: str = "", destination: Path | None = None) -> None:
    if VERSION_RE.fullmatch(version) is None:
        fail(f"invalid contract version {version!r}")
    repo = Path(__file__).resolve().parent.parent
    contracts_root = repo / "contracts"
    if contracts_root.exists() and contracts_root.is_symlink():
        fail("contracts directory is a symlink")
    contracts_root.mkdir(exist_ok=True)
    destination = destination or contracts_root / version
    if destination.parent != contracts_root:
        fail("contract destination must be the scoped version directory")
    if destination.exists() and (destination.is_symlink() or not destination.is_dir()):
        fail("contract destination is not a directory")
    with tempfile.TemporaryDirectory(prefix="terrascale-contract-") as temporary_name:
        temporary = Path(temporary_name)
        producer = load_producer(source, url, archive_sha256, temporary)
        manifest, _ = validate_bundle(producer, version)
        old = destination if destination.exists() else None
        corpus = find_corpus(producer, corpus_source, old)
        staging = Path(tempfile.mkdtemp(prefix=f".{version}.staging-", dir=contracts_root))
        backup: Path | None = None
        installed = False
        try:
            shutil.copytree(producer, staging, symlinks=False, dirs_exist_ok=True)
            (staging / "conformance/msgpack").mkdir(parents=True, exist_ok=True)
            for relative in CORPUS_FILES:
                destination_file = staging / relative
                destination_file.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(corpus_file(corpus, relative), destination_file)
            reject_links_and_specials(staging)
            manifest, digests = validate_bundle(staging, version)
            corpus_digests = validate_corpus(staging, version)
            write_provenance(staging, manifest, digests, corpus_digests)
            if destination.exists():
                backup = contracts_root / f".{version}.backup-{uuid.uuid4().hex}"
                os.replace(destination, backup)
            os.replace(staging, destination)
            installed = True
            if backup is not None:
                scoped_remove(backup, contracts_root, f".{version}.backup-")
            print(f"contracts: installed {version} from explicit producer input")
        except BaseException:
            if staging.exists():
                scoped_remove(staging, contracts_root, f".{version}.staging-")
            if installed:
                failed = contracts_root / f".{version}.failed-{uuid.uuid4().hex}"
                os.replace(destination, failed)
                if backup is not None and backup.exists():
                    os.replace(backup, destination)
                else:
                    scoped_remove(failed, contracts_root, f".{version}.failed-")
            elif backup is not None and backup.exists() and not destination.exists():
                os.replace(backup, destination)
            raise


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default=os.environ.get("CONTRACT_VERSION", ""))
    parser.add_argument("--source", default=os.environ.get("CONTRACT_SOURCE") or os.environ.get("TERRASCALE_CONTRACT_SOURCE", ""))
    parser.add_argument("--url", default=os.environ.get("CONTRACT_URL") or os.environ.get("TERRASCALE_CONTRACT_URL") or os.environ.get("CONTRACT_REGISTRY_URL") or os.environ.get("TERRASCALE_CONTRACT_REGISTRY_URL", ""))
    parser.add_argument("--archive-sha256", default=os.environ.get("CONTRACT_ARCHIVE_SHA256") or os.environ.get("TERRASCALE_CONTRACT_ARCHIVE_SHA256") or os.environ.get("CONTRACT_BUNDLE_SHA256") or os.environ.get("TERRASCALE_CONTRACT_BUNDLE_SHA256", ""))
    parser.add_argument("--corpus-source", default=os.environ.get("CONTRACT_CORPUS_SOURCE") or os.environ.get("TERRASCALE_CONTRACT_CORPUS_SOURCE", ""))
    args = parser.parse_args()
    try:
        install(args.version, args.source, args.url, args.archive_sha256, args.corpus_source)
    except ContractError as error:
        print(error, file=os.sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
