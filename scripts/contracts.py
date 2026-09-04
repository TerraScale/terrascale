#!/usr/bin/env python3
"""Fetch the pinned TerraBase contract and generate Go contract metadata.

The producer bundle is authoritative.  A local producer checkout is accepted
only when explicitly supplied for pre-publication development.  Normal and CI
invocations require an immutable package URL.  No consumer (and especially no
other SDK) is ever used as a contract source.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import secrets
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
from pathlib import Path


REQUIRED = (
    "manifest.json",
    "openapi.json",
    "msgpack/terrascale.sync.v2.msgpack.json",
)
CONFORMANCE = (
    "conformance/http.json",
    "conformance/reactive.json",
    "conformance/msgpack/message-vectors.json",
    "conformance/msgpack/hostile-vectors.json",
)
MAX_ARCHIVE_MEMBERS = 256
MAX_ARCHIVE_ENTRY_BYTES = 16 * 1024 * 1024
MAX_ARCHIVE_TOTAL_BYTES = 64 * 1024 * 1024
MAX_ARCHIVE_BYTES = 80 * 1024 * 1024


def die(message: str) -> "NoReturn":
    raise SystemExit(f"contracts: {message}")


def regular_file(path: Path, label: str) -> None:
    try:
        mode = path.lstat().st_mode
    except OSError as error:
        die(f"{label} is unavailable: {error}")
    if not stat.S_ISREG(mode):
        die(f"{label} is not a regular file")
    if path.stat().st_nlink != 1:
        die(f"{label} is a hard link")


def reject_links(root: Path) -> None:
    for path in root.rglob("*"):
        mode = path.lstat().st_mode
        if stat.S_ISLNK(mode):
            die(f"contract input contains a symlink: {path}")
        if stat.S_ISREG(mode) and path.stat().st_nlink != 1:
            die(f"contract input contains a hard link: {path}")
        if not stat.S_ISREG(mode) and not stat.S_ISDIR(mode):
            die(f"contract input contains a non-file entry: {path}")


def load_json(path: Path):
    regular_file(path, str(path))

    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError(f"duplicate key {key!r}")
            result[key] = value
        return result

    try:
        return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=pairs)
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as error:
        die(f"invalid JSON in {path}: {error}")


def blake3(data: bytes) -> str:
    """Return BLAKE3-256 without making the consumer depend on a package.

    b3sum or the Python blake3 module is preferred.  The small fallback keeps
    `make contracts` usable on the minimal CI image too.
    """
    try:
        import blake3 as blake3_module  # type: ignore

        return blake3_module.blake3(data).hexdigest()
    except ImportError:
        return _blake3_pure(data)


_IV = (0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
       0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19)
_PERMUTATION = (2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8)
_MASK = 0xFFFFFFFF
_CHUNK_START, _CHUNK_END, _PARENT, _ROOT = 1, 2, 4, 8


def _g(state, a, b, c, d, x, y):
    state[a] = (state[a] + state[b] + x) & _MASK
    state[d] = ((state[d] ^ state[a]) >> 16 | (state[d] ^ state[a]) << 16) & _MASK
    state[c] = (state[c] + state[d]) & _MASK
    state[b] = ((state[b] ^ state[c]) >> 12 | (state[b] ^ state[c]) << 20) & _MASK
    state[a] = (state[a] + state[b] + y) & _MASK
    state[d] = ((state[d] ^ state[a]) >> 8 | (state[d] ^ state[a]) << 24) & _MASK
    state[c] = (state[c] + state[d]) & _MASK
    state[b] = ((state[b] ^ state[c]) >> 7 | (state[b] ^ state[c]) << 25) & _MASK


def _compress(cv, words, counter, block_len, flags):
    state = list(cv) + list(_IV)
    state[12] = counter & _MASK
    state[13] = counter >> 32
    state[14] = block_len
    state[15] = flags
    message = list(words)
    for round_number in range(7):
        _g(state, 0, 4, 8, 12, message[0], message[1])
        _g(state, 1, 5, 9, 13, message[2], message[3])
        _g(state, 2, 6, 10, 14, message[4], message[5])
        _g(state, 3, 7, 11, 15, message[6], message[7])
        _g(state, 0, 5, 10, 15, message[8], message[9])
        _g(state, 1, 6, 11, 12, message[10], message[11])
        _g(state, 2, 7, 8, 13, message[12], message[13])
        _g(state, 3, 4, 9, 14, message[14], message[15])
        message = [message[index] for index in _PERMUTATION]
    return [(state[index] ^ state[index + 8]) & _MASK for index in range(8)] + [
        (state[index + 8] ^ cv[index]) & _MASK for index in range(8)
    ]


def _words(block: bytes):
    padded = block + b"\0" * (64 - len(block))
    return [int.from_bytes(padded[offset:offset + 4], "little") for offset in range(0, 64, 4)]


def _chunk_output(chunk: bytes, index: int):
    cv = list(_IV)
    blocks = max(1, (len(chunk) + 63) // 64)
    for block_index in range(blocks - 1):
        flags = _CHUNK_START if block_index == 0 else 0
        cv = _compress(cv, _words(chunk[block_index * 64:(block_index + 1) * 64]), index, 64, flags)[:8]
    start = (blocks - 1) * 64
    final = chunk[start:]
    flags = _CHUNK_END | (_CHUNK_START if blocks == 1 else 0)
    return (cv, _words(final), index, len(final), flags)


def _output_cv(output):
    cv, words, counter, length, flags = output
    return _compress(cv, words, counter, length, flags)[:8]


def _parent_output(left, right, flags=0):
    return (list(_IV), list(left) + list(right), 0, 64, _PARENT | flags)


def _root_bytes(output):
    return _compress(output[0], output[1], 0, output[3], output[4] | _ROOT)[:8]


def _blake3_pure(data: bytes) -> str:
    chunks = [data[offset:offset + 1024] for offset in range(0, len(data), 1024)] or [b""]
    stack = []
    current = None
    for index, chunk in enumerate(chunks):
        current = _chunk_output(chunk, index)
        current_cv = _output_cv(current)
        total = index + 1
        while total & 1 == 0:
            current = _parent_output(stack.pop(), current_cv)
            current_cv = _output_cv(current)
            total >>= 1
        stack.append(current_cv)
    stack.pop()  # the current subtree is represented by current
    while stack:
        current = _parent_output(stack.pop(), _output_cv(current))
    digest = b"".join(word.to_bytes(4, "little") for word in _root_bytes(current))
    return digest.hex()


def validate_bundle(root: Path, version: str) -> tuple[dict, dict[str, str]]:
    reject_links(root)
    manifest = load_json(root / "manifest.json")
    if manifest.get("schemaVersion") != 1:
        die("manifest schemaVersion is not 1")
    if manifest.get("contractVersion") != version:
        die(
            f"manifest contractVersion is {manifest.get('contractVersion')!r}, expected {version!r}"
        )
    artifacts = manifest.get("artifacts")
    if not isinstance(artifacts, list) or not artifacts:
        die("manifest has no artifacts")
    digests: dict[str, str] = {}
    for entry in artifacts:
        if not isinstance(entry, dict):
            die("manifest artifact is not an object")
        relative = entry.get("path")
        expected = entry.get("blake3")
        if (
            not isinstance(relative, str)
            or not relative
            or Path(relative).is_absolute()
            or ".." in Path(relative).parts
            or not isinstance(expected, str)
            or len(expected) != 64
        ):
            die(f"manifest artifact has an invalid path or digest: {entry!r}")
        if relative in digests:
            die(f"manifest repeats artifact path: {relative}")
        path = root / relative
        regular_file(path, relative)
        actual = blake3(path.read_bytes())
        if actual != expected:
            die(f"manifest digest mismatch for {relative}: {actual} != {expected}")
        digests[relative] = expected
    for relative in REQUIRED:
        regular_file(root / relative, relative)
        if relative != "manifest.json" and relative not in digests:
            die(f"manifest does not pin required artifact {relative}")
    return manifest, digests


def unpack_url(url: str, destination: Path) -> Path:
    if not url:
        die(
            "contract v1 is not published in the registry; set CONTRACT_SOURCE "
            "to a local terrabase/contracts directory or CONTRACT_URL to the "
            "immutable v1 package URL (registry blocker)"
        )
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            chunks = []
            size = 0
            while True:
                chunk = response.read(min(1024 * 1024, MAX_ARCHIVE_BYTES + 1 - size))
                if not chunk:
                    break
                chunks.append(chunk)
                size += len(chunk)
                if size > MAX_ARCHIVE_BYTES:
                    die("downloaded contract archive is too large")
            payload = b"".join(chunks)
    except Exception as error:  # noqa: BLE001 - preserve a useful gate error
        die(f"could not download pinned contract URL {url!r}: {error}")
    archive = destination / "contract.tar"
    archive.write_bytes(payload)
    extracted = destination / "source"
    extracted.mkdir()
    try:
        with tarfile.open(archive, mode="r:*") as bundle:
            members = bundle.getmembers()
            if len(members) > MAX_ARCHIVE_MEMBERS:
                die("downloaded contract archive has too many entries")
            seen = set()
            total = 0
            for member in members:
                name = member.name
                if not name or "\0" in name or "\\" in name:
                    die("downloaded contract archive contains an invalid entry name")
                if Path(name).is_absolute() or ".." in Path(name).parts:
                    die(f"downloaded contract archive contains path traversal: {name!r}")
                normalized = str(Path(name))
                if normalized in seen:
                    die(f"downloaded contract archive repeats entry: {name!r}")
                seen.add(normalized)
                target = (extracted / normalized).resolve()
                if extracted.resolve() not in target.parents and target != extracted.resolve():
                    die("downloaded contract archive contains an unsafe path")
                if member.issym() or member.islnk():
                    die(f"downloaded contract archive contains a link: {name!r}")
                if not member.isdir() and not member.isreg():
                    die(f"downloaded contract archive contains a special entry: {name!r}")
                if member.isreg():
                    if member.size < 0 or member.size > MAX_ARCHIVE_ENTRY_BYTES:
                        die(f"downloaded contract archive entry is too large: {name!r}")
                    total += member.size
                    if total > MAX_ARCHIVE_TOTAL_BYTES:
                        die("downloaded contract archive is too large")
            bundle.extractall(extracted)
    except (tarfile.TarError, OSError) as error:
        die(f"downloaded contract is not a tar archive: {error}")
    candidates = [extracted, *[path for path in extracted.iterdir() if path.is_dir()]]
    for candidate in candidates:
        if (candidate / "manifest.json").is_file():
            return candidate
    die("downloaded contract archive has no manifest.json")


def copy_conformance(source: Path, destination: Path) -> None:
    """Copy only the producer's published conformance corpus.

    E-1 owns this corpus.  A consumer must never silently retain a fixture
    from an older checkout or copy one from another SDK.
    """
    missing = [relative for relative in CONFORMANCE if not (source / relative).is_file()]
    if missing:
        names = ", ".join(missing)
        die(
            "producer conformance corpus is unavailable (missing "
            f"{names}); E-1 must publish these versioned conformance artifacts "
            "before ts-go can regenerate contract v1"
        )
    for relative in CONFORMANCE:
        source_file = source / relative
        regular_file(source_file, f"producer conformance artifact {relative}")
        destination_file = destination / relative
        destination_file.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source_file, destination_file)
        # Keep the established Go MessagePack test paths as a generated
        # compatibility mirror; both copies come from this producer artifact.
        if relative.startswith("conformance/msgpack/"):
            mirror = destination / "msgpack" / Path(relative).name
            mirror.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source_file, mirror)


def ref_name(schema: object) -> str:
    if isinstance(schema, dict):
        reference = schema.get("$ref")
        if isinstance(reference, str) and reference.startswith("#/components/schemas/"):
            return reference.rsplit("/", 1)[-1]
    return ""


def openapi_metadata(openapi: dict) -> tuple[list[dict], list[dict], list[dict]]:
    paths = openapi.get("paths")
    components = openapi.get("components")
    schemas = components.get("schemas") if isinstance(components, dict) else None
    if not isinstance(paths, dict) or not isinstance(schemas, dict):
        die("OpenAPI artifact has no usable paths/components.schemas metadata")
    operations = []
    routes = []
    methods = ("get", "post", "put", "patch", "delete", "options", "head", "trace")
    for path in sorted(paths):
        path_item = paths[path]
        if not isinstance(path_item, dict):
            continue
        for method in methods:
            operation = path_item.get(method)
            if not isinstance(operation, dict):
                continue
            operation_id = operation.get("operationId")
            if not isinstance(operation_id, str) or not operation_id:
                die(f"OpenAPI {method} {path} has no operationId")
            request = operation.get("requestBody")
            request_model = ""
            if isinstance(request, dict) and isinstance(request.get("content"), dict):
                for content in request["content"].values():
                    if isinstance(content, dict):
                        request_model = ref_name(content.get("schema"))
                        if request_model:
                            break
            response_model = ""
            responses = operation.get("responses")
            if isinstance(responses, dict):
                for status in sorted(responses, key=lambda value: (not str(value).startswith("2"), str(value))):
                    if not str(status).startswith("2"):
                        continue
                    response = responses[status]
                    if isinstance(response, dict) and isinstance(response.get("content"), dict):
                        for content in response["content"].values():
                            if isinstance(content, dict):
                                response_model = ref_name(content.get("schema"))
                                if response_model:
                                    break
                    if response_model:
                        break
            item = {
                "operationID": operation_id,
                "method": method.upper(),
                "path": path,
                "requestModel": request_model,
                "responseModel": response_model,
            }
            operations.append(item)
            routes.append({"method": method.upper(), "path": path, "operationID": operation_id})
    models = []
    for name in sorted(schemas):
        schema = schemas[name]
        properties = schema.get("properties", {}) if isinstance(schema, dict) else {}
        models.append({
            "name": name,
            "properties": sorted(properties) if isinstance(properties, dict) else [],
        })
    operations.sort(key=lambda item: (item["operationID"], item["method"], item["path"]))
    routes.sort(key=lambda item: (item["path"], item["method"], item["operationID"]))
    return operations, routes, models


def msgpack_metadata(manifest: dict) -> list[dict]:
    messages = manifest.get("messages")
    if not isinstance(messages, list) or not messages:
        die("MessagePack artifact has no messages metadata")
    result = []
    for message in messages:
        if not isinstance(message, dict) or not isinstance(message.get("name"), str):
            die("MessagePack message has no stable name")
        fields = message.get("fields")
        if not isinstance(fields, list):
            die(f"MessagePack message {message['name']} has no fields")
        field_names = []
        for field in fields:
            if not isinstance(field, dict) or not isinstance(field.get("name"), str):
                die(f"MessagePack message {message['name']} has an invalid field")
            field_names.append(field["name"])
        tag = message.get("tag")
        if not isinstance(tag, int) or tag < 1:
            die(f"MessagePack message {message['name']} has an invalid tag")
        direction = message.get("direction", "")
        if not isinstance(direction, str):
            die(f"MessagePack message {message['name']} has an invalid direction")
        result.append({
            "name": message["name"],
            "tag": tag,
            "direction": direction,
            "fields": field_names,
        })
    return sorted(result, key=lambda item: (item["tag"], item["name"]))


def go_string(value: str) -> str:
    return json.dumps(value, ensure_ascii=True)


def go_strings(values: list[str]) -> str:
    return "[]string{" + ", ".join(go_string(value) for value in values) + "}"


def render_contract_go(openapi: dict, msgpack: dict, version: str, digests: dict[str, str]) -> str:
    operations, routes, models = openapi_metadata(openapi)
    frames = msgpack_metadata(msgpack)
    lines = [
        "// Code generated by scripts/contracts.py; DO NOT EDIT.",
        "// Contract metadata is derived only from the pinned TerraBase bundle.",
        "package sdk", "",
        "// ContractMetadata describes one generated OpenAPI operation.",
        "type ContractMetadata struct {", "\tOperationID string", "\tMethod string", "\tPath string", "\tRequestModel string", "\tResponseModel string", "}",
        "", "// ContractRoute describes one generated route template.",
        "type ContractRoute struct {", "\tMethod string", "\tPath string", "\tOperationID string", "}",
        "", "// ContractModel describes one generated OpenAPI schema model.",
        "type ContractModel struct {", "\tName string", "\tProperties []string", "}",
        "", "// ContractFrame describes one generated MessagePack message.",
        "type ContractFrame struct {", "\tName string", "\tTag uint32", "\tDirection string", "\tFields []string", "}",
        "", "const (",
        f"\tContractVersion = {go_string(version)}",
        f"\tContractOpenAPIBLAKE3 = {go_string(digests['openapi.json'])}",
        f"\tContractMessagePackBLAKE3 = {go_string(digests['msgpack/terrascale.sync.v2.msgpack.json'])}",
        ")", "",
        "var ContractOperations = []ContractMetadata{",
    ]
    for item in operations:
        lines.append("\t{" + ", ".join(go_string(item[key]) for key in ("operationID", "method", "path", "requestModel", "responseModel")) + "},")
    lines += ["}", "", "var ContractRoutes = []ContractRoute{"]
    for item in routes:
        lines.append("\t{" + ", ".join(go_string(item[key]) for key in ("method", "path", "operationID")) + "},")
    lines += ["}", "", "var ContractModels = []ContractModel{"]
    for item in models:
        lines.append("\t{" + go_string(item["name"]) + ", " + go_strings(item["properties"]) + "},")
    lines += ["}", "", "var ContractFrames = []ContractFrame{"]
    for item in frames:
        lines.append("\t{" + go_string(item["name"]) + f", {item['tag']}, " + go_string(item["direction"]) + ", " + go_strings(item["fields"]) + "},")
    lines += ["}", ""]
    source = "\n".join(lines)
    try:
        formatted = subprocess.run(
            ["gofmt"], input=source, text=True, capture_output=True, check=True
        )
    except (OSError, subprocess.CalledProcessError) as error:
        die(f"could not format generated Go contract metadata: {error}")
    return formatted.stdout


def render_generated(destination: Path, manifest: dict, digests: dict[str, str]) -> str:
    corpus_sha = hashlib.sha256((destination / "msgpack" / "message-vectors.json").read_bytes()).hexdigest()
    hostile_sha = hashlib.sha256((destination / "msgpack" / "hostile-vectors.json").read_bytes()).hexdigest()
    return f'''// Code generated by scripts/contracts.py; DO NOT EDIT.
// Contract artifacts are verified by conformance_contract_test.go.
package sdk

const (
	pinnedContractVersion     = {json.dumps(manifest["contractVersion"])}
	pinnedOpenAPIBLAKE3       = {json.dumps(digests["openapi.json"])}
	pinnedMessagePackBLAKE3   = {json.dumps(digests["msgpack/terrascale.sync.v2.msgpack.json"])}
	pinnedMessageVectorSHA256 = {json.dumps(corpus_sha)}
	pinnedHostileVectorSHA256 = {json.dumps(hostile_sha)}
)
'''


def safe_remove_tree(path: Path, parent: Path, prefix: str) -> None:
    """Remove only a directory we created directly under the known parent."""
    if path.parent != parent or not path.name.startswith(prefix):
        die(f"refusing to remove an unresolved path: {path}")
    if path.is_symlink() or not path.is_dir():
        die(f"refusing to remove an unsafe path: {path}")
    shutil.rmtree(path)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default=os.environ.get("CONTRACT_VERSION", "v1"))
    parser.add_argument("--source", default=os.environ.get("CONTRACT_SOURCE", ""))
    parser.add_argument("--url", default=os.environ.get("CONTRACT_URL", ""))
    parser.add_argument("--destination", default="contracts/v1")
    args = parser.parse_args()
    if re.fullmatch(r"v[1-9][0-9]*", args.version) is None:
        die(f"invalid contract version {args.version!r}")
    repo = Path(__file__).resolve().parent.parent
    source = Path(args.source).expanduser() if args.source else None
    if source is not None and args.url:
        die("CONTRACT_SOURCE and CONTRACT_URL are mutually exclusive")
    if source is not None:
        if source.is_symlink():
            die(f"local producer artifact is a symlink: {source}")
        allowed_source = (repo.parent / "terrabase" / "contracts").resolve()
        try:
            source_resolved = source.resolve(strict=False)
        except OSError as error:
            die(f"cannot resolve CONTRACT_SOURCE {source}: {error}")
        if source_resolved != allowed_source:
            die(
                "CONTRACT_SOURCE is development-only and must point to "
                f"../terrabase/contracts ({allowed_source})"
            )
    if os.environ.get("CI") and source is not None:
        die("CI requires CONTRACT_URL for an immutable published package; local CONTRACT_SOURCE is development-only")
    with tempfile.TemporaryDirectory(prefix="terrascale-contract-") as temporary:
        temporary_root = Path(temporary)
        if source is not None and source.is_symlink():
            die(f"local producer artifact is a symlink: {source}")
        if source is not None and source.is_dir():
            producer = source
        elif args.url:
            producer = unpack_url(args.url, temporary_root)
        else:
            die("no immutable CONTRACT_URL supplied; local ../terrabase/contracts is available only via explicit CONTRACT_SOURCE (E-1 registry publication)")
        manifest, _ = validate_bundle(producer, args.version)
        contracts_parent = repo / "contracts"
        if contracts_parent.exists() and contracts_parent.is_symlink():
            die(f"refusing to use symlink contracts directory {contracts_parent}")
        contracts_parent.mkdir(exist_ok=True)
        destination = contracts_parent / args.version
        generated_target = repo / "generated_contract_test.go"
        contract_target = repo / "contract.go"
        if destination.exists() and destination.is_symlink():
            die(f"refusing to replace symlink destination {destination}")
        if destination.exists() and not destination.is_dir():
            die(f"refusing to replace non-directory destination {destination}")
        if destination.exists():
            reject_links(destination)
        if generated_target.is_symlink():
            die(f"refusing to replace symlink generated artifact {generated_target}")
        if contract_target.is_symlink():
            die(f"refusing to replace symlink contract artifact {contract_target}")
        # Stage beside the exact version directory. Nothing is installed until
        # this copy, corpus check, and a second full digest pass succeed.
        staging = Path(tempfile.mkdtemp(prefix=f".{args.version}.staging-", dir=contracts_parent))
        backup = None
        generated_staging = contracts_parent / f".{args.version}.generated-{secrets.token_hex(8)}"
        generated_backup = None
        contract_staging = contracts_parent / f".{args.version}.contract-{secrets.token_hex(8)}"
        contract_backup = None
        installed = False
        try:
            # Install only files named by the producer manifest. This prevents
            # an old consumer fixture or an unpinned producer extra entering
            # the committed bundle.
            # The manifest describes those artifacts but is not itself listed
            # as an artifact, so install it explicitly for the validation pass
            # and for the committed bundle's provenance.
            source_manifest = producer / "manifest.json"
            destination_manifest = staging / "manifest.json"
            regular_file(source_manifest, "producer manifest")
            shutil.copyfile(source_manifest, destination_manifest)
            for relative in [entry["path"] for entry in manifest["artifacts"]]:
                source_file = producer / relative
                destination_file = staging / relative
                destination_file.parent.mkdir(parents=True, exist_ok=True)
                regular_file(source_file, f"producer artifact {relative}")
                shutil.copyfile(source_file, destination_file)
            copy_conformance(producer, staging)
            # The corpus is tied to the exact MessagePack manifest, not merely
            # its protocol name. This catches a stale fixture from another v1.
            vectors = load_json(staging / "msgpack" / "message-vectors.json")
            expected_sha = hashlib.sha256((staging / "msgpack" / "terrascale.sync.v2.msgpack.json").read_bytes()).hexdigest()
            if vectors.get("manifestSha256") != expected_sha:
                die("MessagePack corpus pins a different manifest SHA-256")
            manifest, digests = validate_bundle(staging, args.version)
            openapi = load_json(staging / "openapi.json")
            msgpack = load_json(staging / "msgpack/terrascale.sync.v2.msgpack.json")
            generated_staging.write_text(render_generated(staging, manifest, digests), encoding="utf-8")
            contract_staging.write_text(render_contract_go(openapi, msgpack, args.version, digests), encoding="utf-8")
            if generated_target.exists():
                regular_file(generated_target, str(generated_target))
                generated_backup = contracts_parent / f".{args.version}.generated-backup-{secrets.token_hex(8)}"
                os.replace(generated_target, generated_backup)
            if contract_target.exists():
                regular_file(contract_target, str(contract_target))
                contract_backup = contracts_parent / f".{args.version}.contract-backup-{secrets.token_hex(8)}"
                os.replace(contract_target, contract_backup)
            if destination.exists():
                backup = contracts_parent / f".{args.version}.backup-{secrets.token_hex(8)}"
                os.replace(destination, backup)
            os.replace(staging, destination)
            installed = True
            os.replace(generated_staging, generated_target)
            os.replace(contract_staging, contract_target)
            if backup is not None:
                safe_remove_tree(backup, contracts_parent, f".{args.version}.backup-")
            if generated_backup is not None:
                regular_file(generated_backup, str(generated_backup))
                generated_backup.unlink()
            if contract_backup is not None:
                regular_file(contract_backup, str(contract_backup))
                contract_backup.unlink()
            print(f"contracts: pinned {args.version} from {producer}")
        except BaseException:
            if generated_staging.exists():
                generated_staging.unlink()
            if contract_staging.exists():
                contract_staging.unlink()
            if staging.exists():
                safe_remove_tree(staging, contracts_parent, f".{args.version}.staging-")
            if installed:
                rollback = contracts_parent / f".{args.version}.rollback-{secrets.token_hex(8)}"
                os.replace(destination, rollback)
                if backup is not None and backup.exists():
                    os.replace(backup, destination)
                else:
                    safe_remove_tree(rollback, contracts_parent, f".{args.version}.rollback-")
            elif backup is not None and backup.exists() and not destination.exists():
                os.replace(backup, destination)
            if generated_backup is not None and generated_backup.exists():
                if generated_target.exists():
                    generated_target.unlink()
                os.replace(generated_backup, generated_target)
            if contract_backup is not None and contract_backup.exists():
                if contract_target.exists():
                    contract_target.unlink()
                os.replace(contract_backup, contract_target)
            raise


if __name__ == "__main__":
    main()
