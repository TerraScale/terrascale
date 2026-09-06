#!/usr/bin/env python3
"""Generate the .NET contract surface from one verified terrabase bundle.

The JSON files are the authority.  This generator deliberately has no route,
model, or wire-name tables of its own: every emitted name is derived from the
OpenAPI and MessagePack documents.  ``--check`` compares bytes and therefore
also makes generated output drift-gated in CI.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from contracts import ContractError, validate_bundle  # noqa: E402  (the verifier is the generator's input gate)


VERSION_RE = re.compile(r"v[1-9][0-9]*\Z")
IDENTIFIER_RE = re.compile(r"[^A-Za-z0-9]+")


def fail(message: str) -> None:
    raise SystemExit(f"contract generator: {message}")


def read_json(path: Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        fail(f"cannot read JSON {path}: {error}")


def pascal(value: str) -> str:
    parts = [p for p in re.split(r"[^A-Za-z0-9]+", value) if p]
    result = "".join(p[:1].upper() + p[1:] for p in parts) or "Value"
    if result[0].isdigit():
        result = "Value" + result
    return result


def string_literal(value: str) -> str:
    return json.dumps(value, ensure_ascii=False).replace("\\/", "/")


def csharp_literal(value):
    if value is None:
        return "null"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, str):
        return string_literal(value)
    return str(value)


class OpenApiGenerator:
    def __init__(self, document: dict):
        self.document = document
        self.schemas = document.get("components", {}).get("schemas", {})
        self.inline: dict[str, dict] = {}

    def resolve(self, schema: dict) -> dict:
        if isinstance(schema, bool):
            return {}
        reference = schema.get("$ref")
        if reference:
            name = reference.rsplit("/", 1)[-1]
            return self.schemas.get(name, {})
        return schema

    def collect_inline(self) -> None:
        for name, schema in self.schemas.items():
            self._collect_schema(schema, name)

    def _collect_schema(self, schema: dict, owner: str) -> None:
        schema = self.resolve(schema)
        if schema.get("type") == "object":
            for field, child in schema.get("properties", {}).items():
                child_resolved = self.resolve(child)
                if "$ref" not in child and child_resolved.get("type") == "object":
                    name = owner + pascal(field)
                    self.inline.setdefault(name, child_resolved)
                    self._collect_schema(child_resolved, name)
                elif "$ref" not in child and child_resolved.get("type") == "array":
                    item = self.resolve(child_resolved.get("items", {}))
                    if "$ref" not in child_resolved.get("items", {}) and item.get("type") == "object":
                        name = owner + pascal(field)
                        self.inline.setdefault(name, item)
                        self._collect_schema(item, name)

    def type_name(self, schema: dict, owner: str, nullable: bool = False) -> str:
        if isinstance(schema, bool):
            return "JsonElement?" if nullable else "JsonElement"
        if "$ref" in schema:
            target = self.resolve(schema)
            if schema["$ref"].endswith("/JsonValue"):
                result = "JsonElement"
            elif target.get("type") == "object":
                result = pascal(schema["$ref"].rsplit("/", 1)[-1])
            else:
                result = self.type_name(target, owner)
        elif schema.get("oneOf") or schema.get("anyOf") or schema.get("allOf"):
            result = "JsonElement"
        elif isinstance(schema.get("type"), list):
            non_null = [t for t in schema["type"] if t != "null"]
            result = self.type_name({**schema, "type": non_null[0] if non_null else "object"}, owner)
            nullable = nullable or "null" in schema["type"]
        else:
            kind = schema.get("type")
            if kind == "string":
                result = "string"
            elif kind == "integer":
                result = {"int64": "long", "uint64": "ulong", "uint32": "uint"}.get(schema.get("format"), "int")
            elif kind == "number":
                result = "double"
            elif kind == "boolean":
                result = "bool"
            elif kind == "array":
                result = f"IReadOnlyList<{self.type_name(schema.get('items', {}), owner)}>"
            elif kind == "object" and schema.get("additionalProperties") is not None:
                result = f"IReadOnlyDictionary<string, {self.type_name(schema['additionalProperties'], owner)}>"
            elif kind == "object":
                result = pascal(owner)
            else:
                result = "JsonElement"
        if nullable and result in {"string", "JsonElement", "long", "ulong", "int", "uint", "double", "bool"}:
            return result + "?"
        if nullable and (result.startswith("IReadOnly") or result[0].isupper()):
            return result + "?"
        return result

    def model_names(self) -> list[str]:
        return list(self.schemas) + list(self.inline)

    def model_schema(self, name: str) -> dict:
        if name in self.schemas:
            return self.schemas[name]
        return self.inline[name]

    def models(self) -> str:
        lines = [header("OpenAPI models"), "namespace TerraScale.DotNet.Generated;", "", "using System.Text.Json;", "using System.Text.Json.Serialization;", ""]
        for name in self.model_names():
            schema = self.model_schema(name)
            if schema.get("type") != "object":
                continue
            required = set(schema.get("required", []))
            fields = []
            for field, definition in schema.get("properties", {}).items():
                definition = dict(definition)
                nullable = field not in required or "null" in definition.get("type", []) if isinstance(definition.get("type"), list) else field not in required
                property_name = pascal(field)
                type_text = self.type_name(definition, name + property_name, nullable)
                required_attribute = ", JsonRequired" if field in required else ", JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)"
                fields.append(f"[property: JsonPropertyName({string_literal(field)}){required_attribute}] {type_text} {property_name}")
            if not fields:
                lines.append(f"public sealed record {pascal(name)};\n")
            else:
                lines.append(f"public sealed record {pascal(name)}(\n    " + ",\n    ".join(fields) + "\n);\n")
        return "\n".join(lines) + "\n"

    def json_context(self) -> str:
        lines = [header("source-generated System.Text.Json metadata"), "namespace TerraScale.DotNet.Generated;", "", "using System.Text.Json.Serialization;", "", "[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]"]
        for name in self.model_names():
            schema = self.model_schema(name)
            if schema.get("type") == "object":
                lines.append(f"[JsonSerializable(typeof({pascal(name)}))]")
        lines += ["public partial class TerraScaleContractJsonContext : JsonSerializerContext", "{", "}", ""]
        return "\n".join(lines)

    def routes(self) -> str:
        lines = [header("OpenAPI operation and route metadata"), "namespace TerraScale.DotNet.Generated;", "", "/// <summary>A pinned public OpenAPI operation.</summary>", "public sealed record TerraScaleContractRoute(\n    string OperationId,\n    string Method,\n    string PathTemplate,\n    string? RequestType,\n    string? ResponseType,\n    IReadOnlyList<string> SuccessStatuses,\n    IReadOnlyList<string> ErrorStatuses,\n    IReadOnlyList<string> RequestMediaTypes,\n    IReadOnlyList<string> ResponseMediaTypes,\n    IReadOnlyList<string> Parameters);\n"]
        entries = []
        for path, item in sorted(self.document.get("paths", {}).items()):
            for method, operation in sorted(item.items()):
                if method.lower() not in {"get", "post", "put", "patch", "delete", "head", "options", "trace"}:
                    continue
                op_id = operation.get("operationId")
                if not op_id:
                    fail(f"{method.upper()} {path} has no operationId")
                body = operation.get("requestBody", {}).get("content", {})
                request = next(iter(body.values()), {}).get("schema") if body else None
                responses = operation.get("responses", {})
                success = sorted(k for k in responses if k.isdigit() and 200 <= int(k) < 300)
                errors = sorted(k for k in responses if not (k.isdigit() and 200 <= int(k) < 300))
                response = None
                for status in success:
                    content = responses[status].get("content", {})
                    if content:
                        response = next(iter(content.values())).get("schema")
                        break
                request_type = self.type_name(request, op_id) if request else None
                response_type = self.type_name(response, op_id) if response else None
                req_media = sorted(body)
                resp_media = sorted({media for value in responses.values() for media in value.get("content", {})})
                params = sorted({p.get("name", "") for p in item.get("parameters", []) + operation.get("parameters", [])})
                entries.append((op_id, method.upper(), path, request_type, response_type, success, errors, req_media, resp_media, params))
        lines += ["/// <summary>The complete route set in canonical operation order.</summary>", "public static class TerraScaleContractRoutes", "{", "    public static IReadOnlyList<TerraScaleContractRoute> All { get; } = new TerraScaleContractRoute[]", "    {"]
        for entry in sorted(entries):
            op, method, path, req, resp, success, errors, req_media, resp_media, params = entry
            def arr(values):
                return "new string[] { " + ", ".join(string_literal(v) for v in values) + " }"
            lines.append(f"        new({string_literal(op)}, {string_literal(method)}, {string_literal(path)}, {csharp_literal(req)}, {csharp_literal(resp)}, {arr(success)}, {arr(errors)}, {arr(req_media)}, {arr(resp_media)}, {arr(params)}),")
        lines += ["    };", "", "    public static TerraScaleContractRoute Find(string operationId) =>", "        All.FirstOrDefault(route => string.Equals(route.OperationId, operationId, StringComparison.Ordinal))", "        ?? throw new KeyNotFoundException($\"Unknown TerraScale contract operation '{operationId}'.\");", "}", ""]
        return "\n".join(lines)


def msgpack_type(type_info: dict, names: set[str]) -> str:
    kind = type_info.get("kind")
    if kind == "u32": return "uint"
    if kind == "u64": return "ulong"
    if kind == "i64": return "long"
    if kind == "f64": return "double"
    if kind == "bool": return "bool"
    if kind == "text": return "string"
    if kind == "binary": return "byte[]"
    if kind == "ref": return pascal(type_info["name"])
    if kind == "option":
        value = msgpack_type(type_info["of"], names)
        return value + "?" if value not in {"string", "byte[]"} and not value.endswith("?") else value + "?"
    if kind == "array": return f"IReadOnlyList<{msgpack_type(type_info['items'], names)}>"
    return "object"


def messagepack(manifest: dict) -> str:
    types = manifest.get("types", [])
    names = {next(iter(entry.values())).get("name") for entry in types}
    lines = [header("MessagePack protocol metadata and wire models"), "namespace TerraScale.DotNet.Generated;", "", "using System.Collections.Generic;", ""]
    for entry in types:
        kind, definition = next(iter(entry.items()))
        name = pascal(definition["name"])
        if kind == "struct":
            fields = [f"[property: MessagePackFieldName({string_literal(field['name'])})] {msgpack_type(field['type'], names)} {pascal(field['name'])}" for field in definition["fields"]]
            lines.append(f"public sealed record {name}(\n    " + ",\n    ".join(fields) + "\n);\n")
        elif kind == "enum":
            lines.append(f"public enum {name}\n{{")
            for value in definition["values"]:
                lines.append(f"    {pascal(value['name'])} = {value['value']},")
            lines.append("}\n")
        elif kind == "union":
            lines.append(f"public abstract record {name};")
            for variant in definition["variants"]:
                variant_name = name + pascal(variant["name"])
                fields = [f"[property: MessagePackFieldName({string_literal(field['name'])})] {msgpack_type(field['type'], names)} {pascal(field['name'])}" for field in variant.get("fields", [])]
                suffix = ("(\n    " + ",\n    ".join(fields) + "\n)" if fields else "")
                lines.append(f"public sealed record {variant_name}{suffix} : {name};")
            lines.append("")
    lines.append("public abstract record TerraScaleSyncMessage;\n")
    for message in manifest.get("messages", []):
        name = pascal(message["name"]) + "Message"
        fields = [f"[property: MessagePackFieldName({string_literal(field['name'])})] {msgpack_type(field['type'], names)} {pascal(field['name'])}" for field in message.get("fields", [])]
        if fields:
            lines.append(f"public sealed record {name}(\n    " + ",\n    ".join(fields) + f"\n) : TerraScaleSyncMessage;\n")
        else:
            lines.append(f"public sealed record {name} : TerraScaleSyncMessage;\n")
    scan = manifest.get("scan", {})
    lines += ["/// <summary>Language-neutral names and limits for terrascale.sync.v1.</summary>", "public static class TerraScaleSyncV1Contract", "{", f"    public const int ProtocolMajor = {manifest.get('protocolMajor', 0)};", f"    public const int ProtocolMinor = {manifest.get('protocolMinor', 0)};", f"    public const uint SupportedFeatures = {manifest.get('supportedFeatures', 0)}u;", "    public const int EnvelopeArity = 7;", f"    public const int MaxMessageBytes = {scan.get('maxMessageBytes', 0)};", f"    public const int MaxDepth = {scan.get('maxDepth', 0)};", f"    public const int MaxStringBytes = {scan.get('maxStringBytes', 0)};", f"    public const int MaxBinaryBytes = {scan.get('maxBinaryBytes', 0)};", f"    public const int MaxArrayItems = {scan.get('maxArrayItems', 0)};", f"    public const int MaxMapEntries = {scan.get('maxMapEntries', 0)};", f"    public const bool ExtensionsAllowed = {str(scan.get('extensionsAllowed', False)).lower()};", f"    public const bool Float32Allowed = {str(scan.get('float32Allowed', False)).lower()};", "    public static IReadOnlyList<TerraScaleMessageDescriptor> Messages { get; } = new[]", "    {"]
    for message in manifest.get("messages", []):
        fields = "new string[] { " + ", ".join(string_literal(field["name"]) for field in message.get("fields", [])) + " }"
        lines.append(f"        new TerraScaleMessageDescriptor({string_literal(message['name'])}, {message['tag']}, {string_literal(message['direction'])}, {fields}),")
    lines += ["    };", "}", "", "public sealed record TerraScaleMessageDescriptor(string Name, int Tag, string Direction, IReadOnlyList<string> Fields);", "", "[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]", "internal sealed class MessagePackFieldNameAttribute(string name) : Attribute", "{", "    public string Name { get; } = name;", "}", ""]
    return "\n".join(lines)


def header(description: str) -> str:
    return f"// <auto-generated> TerraScale contract: {description}. Do not edit. </auto-generated>\n#nullable enable\n#pragma warning disable CS1591"


def pin_file(version: str, manifest: dict, manifest_bytes: bytes) -> str:
    digests = {entry["path"]: entry["blake3"] for entry in manifest.get("artifacts", [])}
    lines = [header("verified contract pin"), "namespace TerraScale.DotNet.Generated;", "", "/// <summary>Identity of the terrabase contract used for generated sources.</summary>", "public static class TerraScaleContractPin", "{", "    public const int ManifestSchemaVersion = 1;", f"    public const string Version = {string_literal(version)};", "    public const string ContractVersion = Version;", f"    public const string ManifestSha256 = {string_literal(hashlib.sha256(manifest_bytes).hexdigest())};"]
    for path, digest in sorted(digests.items()):
        const_name = "Blake3_" + pascal(path.replace("/", "_" ).replace(".", "_"))
        lines.append(f"    public const string {const_name} = {string_literal(digest)};")
    lines += ["}", ""]
    return "\n".join(lines)


def generate(contract: Path, output: Path, version: str) -> dict[str, bytes]:
    manifest_path = contract / "manifest.json"
    manifest_bytes = manifest_path.read_bytes()
    manifest, _ = validate_bundle(contract, version)
    openapi = read_json(contract / "openapi.json")
    msgpack = read_json(contract / "msgpack" / "terrascale.sync.v1.msgpack.json")
    api = OpenApiGenerator(openapi)
    api.collect_inline()
    files = {
        "ContractPin.g.cs": pin_file(version, manifest, manifest_bytes),
        "OpenApiModels.g.cs": api.models(),
        "OpenApiRoutes.g.cs": api.routes(),
        "JsonContext.g.cs": api.json_context(),
        "MessagePack.g.cs": messagepack(msgpack),
    }
    return {name: contents.encode("utf-8") for name, contents in files.items()}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--contract", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    if VERSION_RE.fullmatch(args.version) is None:
        fail(f"invalid contract version {args.version!r}")
    try:
        files = generate(args.contract, args.output, args.version)
    except ContractError as error:
        fail(str(error))
    if args.check:
        actual = {path.name: path.read_bytes() for path in args.output.glob("*.g.cs")} if args.output.is_dir() else {}
        if set(actual) != set(files):
            fail(f"generated files differ; expected {sorted(files)}, found {sorted(actual)}")
        for name, expected in files.items():
            if actual[name] != expected:
                fail(f"{args.output / name} is out of date; run make contracts")
        return 0
    args.output.mkdir(parents=True, exist_ok=True)
    for name, contents in files.items():
        (args.output / name).write_bytes(contents)
    stale = [path for path in args.output.glob("*.g.cs") if path.name not in files]
    for path in stale:
        fail(f"stale generated file {path}; delete it before regeneration")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
