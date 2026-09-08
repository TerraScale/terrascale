#!/usr/bin/env python3
"""Install and generate the pinned TerraBase v1 contract for the Rust SDK.

The producer bundle is authoritative.  A local producer is accepted only when
``CONTRACT_SOURCE`` is explicitly supplied (pre-publication development). CI
and normal consumer builds fail closed until an immutable package URL and its
archive digest are configured.  MessagePack conformance fixtures are always
discovered in that producer bundle; they are never copied from another SDK.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import tarfile
import tempfile
import urllib.parse
import urllib.request
from pathlib import Path


REQUIRED = (
    "openapi.json",
    "msgpack/terrascale.sync.v1.msgpack.json",
    "proto/terrabase/v1/engine.proto",
    "proto/terrabase/v1/family.proto",
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
        relative = safe_relative(relative, "manifest artifact path")
        if relative in digests:
            die(f"manifest repeats artifact path: {relative}")
        path = root / relative
        regular_file(path, relative)
        actual = blake3(path.read_bytes())
        if actual != expected:
            die(f"manifest digest mismatch for {relative}: {actual} != {expected}")
        digests[relative] = expected
    for relative in REQUIRED + CONFORMANCE:
        regular_file(root / relative, relative)
        if relative not in digests:
            die(f"manifest does not pin required artifact {relative}")
    return manifest, digests


def safe_relative(value: str, label: str) -> str:
    if not value or "\0" in value or "\\" in value:
        die(f"{label} has an invalid path")
    path = Path(value)
    if path.is_absolute() or ".." in path.parts or str(path) != value.rstrip("/"):
        die(f"{label} is not normalized: {value!r}")
    return value


def validate_corpus(root: Path) -> dict[str, str]:
    """Validate fixtures discovered in the producer's own conformance tree."""
    manifest_path = root / "msgpack/terrascale.sync.v1.msgpack.json"
    manifest_sha = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
    result: dict[str, str] = {}
    for relative in CONFORMANCE:
        path = root / relative
        regular_file(path, f"producer conformance fixture {relative}")
        if path.stat().st_size > MAX_ARCHIVE_ENTRY_BYTES:
            die(f"conformance fixture is too large: {relative}")
        value = load_json(path)
        if relative.endswith("message-vectors.json"):
            if value.get("protocol") != "terrascale.sync.v1.msgpack" or value.get("version") != "1.0":
                die("producer MessagePack corpus identity is not 1.0")
            if value.get("manifestSha256") != manifest_sha:
                die("producer MessagePack corpus pins a different manifest SHA-256")
            vectors = value.get("vectors")
            if not isinstance(vectors, list) or len(vectors) < 11:
                die("producer MessagePack corpus is incomplete")
            for vector in vectors:
                if not isinstance(vector, dict) or not isinstance(vector.get("name"), str):
                    die("producer MessagePack vector has no name")
                try:
                    bytes.fromhex(vector.get("hex", ""))
                except (TypeError, ValueError):
                    die(f"producer MessagePack vector {vector.get('name')!r} is not hex")
        elif relative.endswith("hostile-vectors.json"):
            vectors = value.get("vectors")
            if not isinstance(vectors, list) or len(vectors) < 10:
                die("producer hostile MessagePack corpus is incomplete")
        elif relative.endswith("http.json"):
            if value.get("schemaVersion") != 1 or not isinstance(value.get("operations"), list):
                die("producer HTTP conformance fixture has an invalid shape")
        elif relative.endswith("reactive.json"):
            if value.get("schemaVersion") != 1 or not value.get("attach"):
                die("producer reactive conformance fixture has an invalid shape")
        result[relative] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result


def unpack_url(url: str, archive_sha256: str, destination: Path) -> Path:
    if not url:
        die("set CONTRACT_SOURCE or CONTRACT_URL with CONTRACT_ARCHIVE_SHA256")
    if not re.fullmatch(r"[0-9a-fA-F]{64}", archive_sha256 or ""):
        die("CONTRACT_URL requires a 64-character CONTRACT_ARCHIVE_SHA256")
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme not in ("http", "https"):
        die("CONTRACT_URL must use http or https")
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            payload = response.read(MAX_ARCHIVE_BYTES + 1)
    except Exception as error:  # noqa: BLE001 - preserve a useful gate error
        die(f"could not download pinned contract URL {url!r}: {error}")
    if len(payload) > MAX_ARCHIVE_BYTES:
        die("downloaded contract archive is too large")
    actual = hashlib.sha256(payload).hexdigest()
    if actual.lower() != archive_sha256.lower():
        die(f"contract archive SHA-256 mismatch: {actual} != {archive_sha256}")
    archive = destination / "contract.tar"
    archive.write_bytes(payload)
    extracted = destination / "source"
    extracted.mkdir()
    try:
        with tarfile.open(archive, mode="r:*") as bundle:
            members = bundle.getmembers()
            if len(members) > MAX_ARCHIVE_MEMBERS:
                die("downloaded contract archive has too many entries")
            seen: set[str] = set()
            total = 0
            for member in members:
                normalized = safe_relative(member.name, "archive entry name")
                if normalized in seen:
                    die(f"downloaded contract archive repeats entry: {member.name!r}")
                seen.add(normalized)
                target = (extracted / normalized).resolve()
                if extracted.resolve() not in target.parents:
                    die("downloaded contract archive contains an unsafe path")
                if member.issym() or member.islnk():
                    die(f"downloaded contract archive contains a link: {member.name!r}")
                if not member.isdir() and not member.isreg():
                    die(f"downloaded contract archive contains a special entry: {member.name!r}")
                if member.isreg():
                    if member.size < 0 or member.size > MAX_ARCHIVE_ENTRY_BYTES:
                        die(f"downloaded contract archive entry is too large: {member.name!r}")
                    total += member.size
                    if total > MAX_ARCHIVE_TOTAL_BYTES:
                        die("downloaded contract archive is too large uncompressed")
            bundle.extractall(extracted)
    except (tarfile.TarError, OSError) as error:
        die(f"downloaded contract is not a tar archive: {error}")
    candidates = [extracted] + [path for path in extracted.iterdir() if path.is_dir()]
    for candidate in candidates:
        if (candidate / "manifest.json").is_file():
            return candidate
    die("downloaded contract archive has no manifest.json")


def load_producer(source: str, url: str, archive_sha256: str, temporary: Path) -> Path:
    if source and url:
        die("set only one of CONTRACT_SOURCE or CONTRACT_URL")
    if source:
        producer = Path(source).expanduser()
        if producer.is_symlink():
            die(f"producer contract directory is a symlink: {producer}")
        if not producer.is_dir():
            die(f"producer contract directory is unavailable: {producer}")
        reject_links(producer)
        return producer
    if not url:
        die("no contract source configured; set CONTRACT_SOURCE or immutable CONTRACT_URL")
    return unpack_url(url, archive_sha256, temporary)


def rust_string(value: str) -> str:
    return json.dumps(value, ensure_ascii=False)


def rust_ident(value: str) -> str:
    result = re.sub(r"[^A-Za-z0-9_]", "_", value)
    if not result or result[0].isdigit():
        result = "_" + result
    if result in {"type", "self", "Self", "crate", "super", "mod", "ref", "move", "match", "loop", "const", "static", "enum", "struct", "fn", "where", "use", "pub", "impl", "trait", "async", "await", "dyn", "in", "as", "for", "return", "true", "false"}:
        result += "_"
    return result


def rust_field_ident(value: str) -> str:
    """Convert an OpenAPI property name to an idiomatic Rust field name."""
    result = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", value)
    return rust_ident(result.lower())


def schema_type(schema: dict, required: bool, models: dict) -> str:
    if "$ref" in schema:
        name = schema["$ref"].split("/")[-1]
        return rust_ident(name)
    types = schema.get("type")
    if isinstance(types, list):
        non_null = [item for item in types if item != "null"]
        if len(non_null) == 1:
            inner = schema_type({**schema, "type": non_null[0]}, True, models)
            return f"Option<{inner}>"
        return "serde_json::Value"
    if "oneOf" in schema or "anyOf" in schema:
        options = schema.get("oneOf", schema.get("anyOf", []))
        refs = [item for item in options if isinstance(item, dict) and "$ref" in item]
        return schema_type(refs[0], required, models) if len(refs) == 1 else "serde_json::Value"
    kind = types or ("object" if "properties" in schema else None)
    if kind == "string":
        return "String"
    if kind == "integer":
        return {"uint64": "u64", "uint32": "u32", "uint16": "u16", "uint8": "u8", "int32": "i32", "int16": "i16", "int8": "i8"}.get(schema.get("format"), "i64")
    if kind == "number":
        return "f64"
    if kind == "boolean":
        return "bool"
    if kind == "array":
        return f"Vec<{schema_type(schema.get('items', {}), True, models)}>"
    if kind == "object":
        return "std::collections::BTreeMap<String, serde_json::Value>"
    return "serde_json::Value"


def render_generated(root: Path, manifest: dict, digests: dict[str, str], corpus: dict[str, str]) -> str:
    openapi = load_json(root / "openapi.json")
    msgpack = load_json(root / "msgpack/terrascale.sync.v1.msgpack.json")
    models = openapi.get("components", {}).get("schemas", {})
    lines = [
        "// Code generated by scripts/contracts.py; DO NOT EDIT.",
        "// Source: the pinned terrabase contract bundle.",
        "#![allow(clippy::all)]",
        "use serde::{Deserialize, Serialize};",
        "",
        f"pub const CONTRACT_VERSION: &str = {rust_string(manifest['contractVersion'])};",
        f"pub const MANIFEST_SHA256: &str = {rust_string(hashlib.sha256((root / 'manifest.json').read_bytes()).hexdigest())};",
        f"pub const OPENAPI_BLAKE3: &str = {rust_string(digests['openapi.json'])};",
        f"pub const MSGPACK_MANIFEST_BLAKE3: &str = {rust_string(digests['msgpack/terrascale.sync.v1.msgpack.json'])};",
        f"pub const MSGPACK_PROTOCOL: &str = {rust_string(msgpack.get('protocol', ''))};",
        f"pub const MSGPACK_PROTOCOL_MAJOR: u32 = {msgpack.get('protocolMajor', 0)};",
        f"pub const MSGPACK_PROTOCOL_MINOR: u32 = {msgpack.get('protocolMinor', 0)};",
        f"pub const MSGPACK_SUPPORTED_FEATURES: u32 = {msgpack.get('supportedFeatures', 0)};",
        "",
        "#[derive(Debug, Clone, Copy, PartialEq, Eq)]",
        "pub struct ContractArtifact { pub path: &'static str, pub blake3: &'static str }",
        "",
        "pub const ARTIFACTS: &[ContractArtifact] = &[",
    ]
    for relative in sorted(digests):
        lines.append(
            f"    ContractArtifact {{ path: {rust_string(relative)}, blake3: {rust_string(digests[relative])} }},"
        )
    lines += ["];", "", "#[derive(Debug, Clone, Copy, PartialEq, Eq)]",
        "pub enum HttpMethod { Get, Post, Put, Patch, Delete, Options, Head, Trace }",
        "",
        "#[derive(Debug, Clone, Copy, PartialEq, Eq)]",
        "pub struct Route { pub operation_id: &'static str, pub method: HttpMethod, pub path: &'static str, pub request_model: Option<&'static str>, pub response_model: Option<&'static str> }",
        "pub type Operation = Route;",
        "",
    ]
    operations = []
    for path, item in openapi.get("paths", {}).items():
        for method, operation in item.items():
            if method not in {"get", "post", "put", "patch", "delete", "options", "head", "trace"}:
                continue
            operations.append((operation.get("operationId", ""), path, method, operation))
    operations.sort(key=lambda entry: (entry[0], entry[1], entry[2]))
    lines.append("pub const ROUTES: &[Route] = &[")
    for operation_id, path, method, operation in operations:
        request = None
        content = operation.get("requestBody", {}).get("content", {})
        if content:
            schema = next(iter(content.values())).get("schema", {})
            request = schema_type(schema, True, models)
        response = None
        responses = operation.get("responses", {})
        for status in sorted(responses, key=lambda value: int(value) if str(value).isdigit() else 999):
            if str(status).startswith("2"):
                content = responses[status].get("content", {})
                if content:
                    schema = next(iter(content.values())).get("schema", {})
                    response = schema_type(schema, True, models)
                break
        method_name = method.capitalize()
        request_expr = f"Some({rust_string(request)})" if request else "None"
        response_expr = f"Some({rust_string(response)})" if response else "None"
        lines.append(f"    Route {{ operation_id: {rust_string(operation_id)}, method: HttpMethod::{method_name}, path: {rust_string(path)}, request_model: {request_expr}, response_model: {response_expr} }},")
    lines += ["];", "pub const OPERATIONS: &[Operation] = ROUTES;", "", "pub mod models {", "    use super::*;", ""]
    for name in sorted(models):
        schema = models[name]
        ident = rust_ident(name)
        if schema.get("type") == "object" and isinstance(schema.get("properties"), dict):
            required = set(schema.get("required", []))
            lines += ["    #[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]", f"    pub struct {ident} {{"]
            for field, field_schema in schema["properties"].items():
                field_ident = rust_field_ident(field)
                field_type = schema_type(field_schema, field in required, models)
                if field not in required and not field_type.startswith("Option<"):
                    field_type = f"Option<{field_type}>"
                if field_ident != field:
                    lines.append(f"        #[serde(rename = {rust_string(field)})]")
                lines.append(f"        pub {field_ident}: {field_type},")
            lines += ["    }", ""]
        else:
            lines += [f"    pub type {ident} = serde_json::Value;", ""]
    lines += ["}", "", "#[derive(Debug, Clone, Copy, PartialEq, Eq)]", "pub struct MessagePackEnvelopeField { pub index: u8, pub name: &'static str, pub kind: &'static str }", ""]
    envelope = msgpack.get("envelope", {}).get("elements", [])
    lines.append("pub const MSGPACK_ENVELOPE: &[MessagePackEnvelopeField] = &[")
    for element in envelope:
        kind = element.get("type", {}).get("kind", "unknown")
        lines.append(f"    MessagePackEnvelopeField {{ index: {element.get('index', 0)}, name: {rust_string(element.get('name', ''))}, kind: {rust_string(kind)} }},")
    lines += ["];", "", "#[derive(Debug, Clone, Copy, PartialEq, Eq)]", "pub struct MessagePackMessage { pub name: &'static str, pub tag: u32, pub direction: &'static str }", "", "pub const MSGPACK_MESSAGES: &[MessagePackMessage] = &["]
    for message in sorted(msgpack.get("messages", []), key=lambda item: item.get("tag", 0)):
        lines.append(f"    MessagePackMessage {{ name: {rust_string(message.get('name', ''))}, tag: {message.get('tag', 0)}, direction: {rust_string(message.get('direction', ''))} }},")
    lines += ["];", "", "#[derive(Debug, Clone, Copy, PartialEq, Eq)]", "pub struct MessagePackType { pub name: &'static str, pub kind: &'static str }", "", "pub const MSGPACK_TYPES: &[MessagePackType] = &["]
    for item in msgpack.get("types", []):
        definition = next(iter(item.values()), {})
        kind = next(iter(item), "unknown")
        lines.append(f"    MessagePackType {{ name: {rust_string(definition.get('name', ''))}, kind: {rust_string(kind)} }},")
    lines += ["];", "", "#[derive(Debug, Clone, Copy, PartialEq, Eq)]", "pub struct MessagePackScan { pub max_message_bytes: u32, pub max_depth: u32, pub max_string_bytes: u32, pub max_binary_bytes: u32, pub max_array_items: u32, pub max_map_entries: u32, pub extensions_allowed: bool, pub float32_allowed: bool }", "", "pub const MSGPACK_SCAN: MessagePackScan = MessagePackScan {"]
    scan = msgpack.get("scan", {})
    for key, field, default in [("maxMessageBytes", "max_message_bytes", 0), ("maxDepth", "max_depth", 0), ("maxStringBytes", "max_string_bytes", 0), ("maxBinaryBytes", "max_binary_bytes", 0), ("maxArrayItems", "max_array_items", 0), ("maxMapEntries", "max_map_entries", 0), ("extensionsAllowed", "extensions_allowed", False), ("float32Allowed", "float32_allowed", False)]:
        value = scan.get(key, default)
        rendered = str(value).lower() if isinstance(value, bool) else str(value)
        lines.append(f"    {field}: {rendered},")
    lines += ["};", "", f"pub const MESSAGE_VECTOR_SHA256: &str = {rust_string(corpus['conformance/msgpack/message-vectors.json'])};", f"pub const HOSTILE_VECTOR_SHA256: &str = {rust_string(corpus['conformance/msgpack/hostile-vectors.json'])};", f"pub const HTTP_CONFORMANCE_SHA256: &str = {rust_string(corpus['conformance/http.json'])};", f"pub const REACTIVE_CONFORMANCE_SHA256: &str = {rust_string(corpus['conformance/reactive.json'])};", ""]
    return "\n".join(lines)


def scoped_remove(path: Path, parent: Path, prefix: str) -> None:
    if path.parent != parent or not path.name.startswith(prefix) or path.is_symlink():
        die(f"refusing to remove unresolved path: {path}")
    if path.is_dir():
        shutil.rmtree(path)
    elif path.exists():
        path.unlink()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default=os.environ.get("CONTRACT_VERSION", "v1"))
    parser.add_argument("--source", default=os.environ.get("CONTRACT_SOURCE", ""))
    parser.add_argument("--url", default=os.environ.get("CONTRACT_URL", ""))
    parser.add_argument("--archive-sha256", default=os.environ.get("CONTRACT_ARCHIVE_SHA256", ""))
    args = parser.parse_args()
    if re.fullmatch(r"v[1-9][0-9]*", args.version) is None:
        die(f"invalid contract version {args.version!r}")
    repo = Path(__file__).resolve().parent.parent
    pin_path = repo / "CONTRACT_VERSION"
    regular_file(pin_path, "CONTRACT_VERSION")
    pinned = pin_path.read_text(encoding="utf-8").strip()
    if pinned != args.version:
        die(f"CONTRACT_VERSION contains {pinned!r}, requested {args.version!r}")
    contracts_root = repo / "contracts"
    if contracts_root.exists() and contracts_root.is_symlink():
        die(f"refusing to use symlink contracts directory {contracts_root}")
    contracts_root.mkdir(exist_ok=True)
    destination = contracts_root / args.version
    generated_target = repo / "src/generated_contract.rs"
    if destination.exists() and (destination.is_symlink() or not destination.is_dir()):
        die(f"contract destination is unsafe: {destination}")
    if generated_target.exists() and generated_target.is_symlink():
        die(f"refusing to replace symlink generated artifact {generated_target}")
    with tempfile.TemporaryDirectory(prefix="terrascale-contract-") as temporary_name:
        temporary = Path(temporary_name)
        producer = load_producer(args.source, args.url, args.archive_sha256, temporary)
        manifest, _ = validate_bundle(producer, args.version)
        corpus = validate_corpus(producer)
        staging = Path(tempfile.mkdtemp(prefix=f".{args.version}.staging-", dir=contracts_root))
        generated_staging = repo / f".generated_contract-{next(tempfile._get_candidate_names())}.rs"
        backup = None
        generated_backup = None
        installed = False
        try:
            shutil.copytree(producer, staging, symlinks=False, dirs_exist_ok=True)
            reject_links(staging)
            manifest, digests = validate_bundle(staging, args.version)
            corpus = validate_corpus(staging)
            generated_staging.write_text(render_generated(staging, manifest, digests, corpus), encoding="utf-8")
            subprocess.run(
                ["rustfmt", "--edition", "2021", str(generated_staging)],
                check=True,
            )
            if destination.exists():
                backup = contracts_root / f".{args.version}.backup-{next(tempfile._get_candidate_names())}"
                os.replace(destination, backup)
            if generated_target.exists():
                generated_backup = repo / f".generated_contract.rs.backup-{next(tempfile._get_candidate_names())}"
                os.replace(generated_target, generated_backup)
            os.replace(staging, destination)
            installed = True
            os.replace(generated_staging, generated_target)
            if backup is not None:
                scoped_remove(backup, contracts_root, f".{args.version}.backup-")
            if generated_backup is not None:
                generated_backup.unlink()
            print(f"contracts: pinned {args.version} from producer bundle {producer}")
        except BaseException:
            if generated_staging.exists():
                generated_staging.unlink()
            if staging.exists():
                scoped_remove(staging, contracts_root, f".{args.version}.staging-")
            if installed:
                rollback = contracts_root / f".{args.version}.rollback-{next(tempfile._get_candidate_names())}"
                os.replace(destination, rollback)
                if backup is not None and backup.exists():
                    os.replace(backup, destination)
                else:
                    scoped_remove(rollback, contracts_root, f".{args.version}.rollback-")
            elif backup is not None and backup.exists() and not destination.exists():
                os.replace(backup, destination)
            if generated_backup is not None and generated_backup.exists():
                if generated_target.exists():
                    generated_target.unlink()
                os.replace(generated_backup, generated_target)
            raise


if __name__ == "__main__":
    main()
