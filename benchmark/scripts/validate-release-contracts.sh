#!/usr/bin/env bash
# Fail closed when the checked-in contract bundle drifts from its sources.
# With --previous, reject non-additive OpenAPI, TBEP, or MessagePack changes
# against an explicitly supplied previous bundle.
set -euo pipefail

usage() {
  echo "usage: $0 [--version v<positive-integer>] [--previous <bundle-directory>]" >&2
  exit 2
}

contract_version=v1
previous_bundle=
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      [[ $# -ge 2 ]] || usage
      contract_version=$2
      shift 2
      ;;
    --previous)
      [[ $# -ge 2 ]] || usage
      previous_bundle=$2
      shift 2
      ;;
    *) usage ;;
  esac
done

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

[[ -d contracts && ! -L contracts ]] || {
  echo "missing or unsafe contracts/; run 'make contracts'" >&2
  exit 1
}

reject_recursive_symlinks() {
  local bundle=$1
  local label=$2
  local link
  link=$(find -P "$bundle" -type l -print -quit)
  if [[ -n "$link" ]]; then
    echo "$label contract bundle contains a symlink: $link" >&2
    exit 1
  fi
}

reject_recursive_symlinks "$repo_root/contracts" current

if [[ -n "$previous_bundle" ]]; then
  [[ "$previous_bundle" = /* ]] || previous_bundle="$repo_root/$previous_bundle"
  [[ -d "$previous_bundle" && ! -L "$previous_bundle" ]] || {
    echo "previous contract bundle is missing or unsafe: $previous_bundle" >&2
    exit 1
  }
  reject_recursive_symlinks "$previous_bundle" previous
  [[ -f "$previous_bundle/manifest.json" ]] || {
    echo "previous contract bundle has no manifest.json: $previous_bundle" >&2
    exit 1
  }
  [[ -d "$previous_bundle/proto" && ! -L "$previous_bundle/proto" ]] || {
    echo "previous contract bundle has no safe proto directory: $previous_bundle" >&2
    exit 1
  }
  [[ -f "$previous_bundle/msgpack/terrascale.sync.v2.msgpack.json" ]] || {
    echo "previous contract bundle has no MessagePack manifest" >&2
    exit 1
  }
fi

work="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/terrabase-contracts.XXXXXXXX")"
cleanup() {
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-contracts.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT

cargo run --quiet --locked -p terrabase-server --bin terrabase-contracts -- \
  --repository-root "$repo_root" --output "$work/contracts" --version "$contract_version"
reject_recursive_symlinks "$work/contracts" generated
diff -r contracts "$work/contracts"

# Validate the producer-owned conformance corpus after the generated tree has
# matched the checked-in bundle.  This keeps fixture drift and accidental
# hand-edits fail-closed while leaving the additive compatibility checks below
# unchanged.
python3 - "$repo_root/contracts" "$contract_version" <<'PY'
import hashlib
import json
import re
import sys
from pathlib import Path

root = Path(sys.argv[1])
version = sys.argv[2]

def load(relative):
    path = root / relative
    if path.is_symlink() or not path.is_file():
        raise SystemExit(f"invalid conformance artifact: {relative}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise SystemExit(f"invalid conformance JSON {relative}: {error}") from error

manifest = load("manifest.json")
artifact_paths = {entry.get("path") for entry in manifest.get("artifacts", []) if isinstance(entry, dict)}
required = {
    "conformance/http.json",
    "conformance/reactive.json",
    "conformance/msgpack/message-vectors.json",
    "conformance/msgpack/hostile-vectors.json",
    "msgpack/message-vectors.json",
    "msgpack/hostile-vectors.json",
}
if not required.issubset(artifact_paths):
    raise SystemExit("manifest does not list every conformance artifact")

http = load("conformance/http.json")
if http.get("schemaVersion") != 1 or http.get("contractVersion") != version:
    raise SystemExit("HTTP conformance identity does not match the contract")
operations = http.get("operations")
if not isinstance(operations, list) or not operations:
    raise SystemExit("HTTP conformance corpus is empty")
ids = [item.get("operationId") for item in operations if isinstance(item, dict)]
if len(ids) != len(operations) or len(set(ids)) != len(ids):
    raise SystemExit("HTTP conformance operation IDs are not unique")
openapi = load("openapi.json")
published = {
    operation.get("operationId")
    for item in openapi.get("paths", {}).values()
    if isinstance(item, dict)
    for operation in item.values()
    if isinstance(operation, dict) and operation.get("operationId")
}
if set(ids) != published:
    raise SystemExit("HTTP conformance corpus does not cover exactly the published operations")
for item in operations:
    if not isinstance(item.get("request"), dict) or not item["request"].get("method"):
        raise SystemExit(f"HTTP fixture {item.get('operationId')} has no request")
    if not isinstance(item.get("responses"), list) or not item["responses"]:
        raise SystemExit(f"HTTP fixture {item.get('operationId')} has no response")
    if not isinstance(item.get("errors"), list):
        raise SystemExit(f"HTTP fixture {item.get('operationId')} has no error corpus")

for relative in ("conformance/reactive.json",):
    reactive = load(relative)
    if reactive.get("schemaVersion") != 1 or reactive.get("contractVersion") != version:
        raise SystemExit("reactive conformance identity does not match the contract")
    for key in ("attach", "snapshot", "log", "reset"):
        if key not in reactive:
            raise SystemExit(f"reactive conformance is missing {key}")
    for key in ("snapshot", "log", "reset"):
        if re.fullmatch(r"[0-9a-f]*", reactive[key].get("hex", "")) is None:
            raise SystemExit(f"reactive {key} bytes are not canonical hex")

msgpack_manifest = root / "msgpack" / "terrascale.sync.v2.msgpack.json"
manifest_bytes = msgpack_manifest.read_bytes()
for relative in ("conformance/msgpack/message-vectors.json", "conformance/msgpack/hostile-vectors.json"):
    corpus = load(relative)
    if corpus.get("protocol") != "terrascale.sync.v2.msgpack" or corpus.get("version") != "2.1":
        raise SystemExit(f"{relative} has the wrong protocol identity")
    if corpus.get("contractVersion") != version:
        raise SystemExit(f"{relative} has the wrong contract version")
    if corpus.get("manifestSha256") != hashlib.sha256(manifest_bytes).hexdigest():
        raise SystemExit(f"{relative} is not tied to the MessagePack manifest")
    if corpus.get("manifestBlake3") != next((entry.get("blake3") for entry in manifest.get("artifacts", []) if entry.get("path") == "msgpack/terrascale.sync.v2.msgpack.json"), None):
        raise SystemExit(f"{relative} has the wrong MessagePack manifest digest")
    vectors = corpus.get("vectors")
    if not isinstance(vectors, list) or not vectors:
        raise SystemExit(f"{relative} is empty")
    names = {vector.get("name") for vector in vectors if isinstance(vector, dict)}
    if relative.endswith("message-vectors.json"):
        required_names = {"subscribe", "unsubscribe", "snapshot", "delta", "reset", "heartbeat", "checkpoint_ack", "authorization_changed", "region_route_change", "error", "backpressure"}
        if not required_names.issubset(names):
            raise SystemExit("MessagePack corpus does not cover every published message tag")
    elif len(vectors) < 10:
        raise SystemExit("hostile MessagePack corpus is incomplete")
    for vector in vectors:
        if not isinstance(vector, dict) or not isinstance(vector.get("hex"), str) or len(vector["hex"]) % 2 or re.fullmatch(r"[0-9a-f]*", vector["hex"]) is None:
            raise SystemExit(f"{relative} contains a non-canonical byte vector")
for canonical, compatibility in (("conformance/msgpack/message-vectors.json", "msgpack/message-vectors.json"), ("conformance/msgpack/hostile-vectors.json", "msgpack/hostile-vectors.json")):
    if (root / canonical).read_bytes() != (root / compatibility).read_bytes():
        raise SystemExit(f"{compatibility} is not the producer-generated corpus")
PY

if [[ -n "$previous_bundle" ]]; then
  python3 - "$previous_bundle" "$repo_root/contracts" "$contract_version" <<'PY'
import json
import re
import sys
from pathlib import Path

previous_root = Path(sys.argv[1])
current_root = Path(sys.argv[2])
requested_version = sys.argv[3]


def load_json(path):
    def pairs(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"duplicate JSON key {key!r} in {path}")
            result[key] = value
        return result

    try:
        return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=pairs)
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as error:
        raise SystemExit(f"invalid contract JSON {path}: {error}") from error


def fail(message):
    raise SystemExit(f"incompatible contract: {message}")


def version_number(value, path):
    if not isinstance(value, str) or re.fullmatch(r"v[1-9][0-9]*", value) is None:
        fail(f"{path} is not a valid contract version")
    return int(value[1:])


def validate_manifest(manifest, path, expected_version=None):
    if not isinstance(manifest, dict):
        fail(f"{path} is not an object")
    if manifest.get("schemaVersion") != 1:
        fail(f"{path}.schemaVersion is unsupported")
    number = version_number(manifest.get("contractVersion"), f"{path}.contractVersion")
    if expected_version is not None and manifest["contractVersion"] != expected_version:
        fail(
            f"{path}.contractVersion is {manifest['contractVersion']!r}, "
            f"expected {expected_version!r}"
        )
    return number


def same(old, new, path):
    if type(old) is not type(new):
        fail(f"{path} changed type")
    if isinstance(old, dict):
        for key, value in old.items():
            if key not in new:
                fail(f"{path}.{key} was removed")
            same(value, new[key], f"{path}.{key}")
    elif isinstance(old, list):
        if old != new:
            fail(f"{path} changed")
    elif old != new:
        fail(f"{path} changed from {old!r} to {new!r}")


def by_name(entries, path):
    result = {}
    for index, entry in enumerate(entries):
        if not isinstance(entry, dict) or not isinstance(entry.get("name"), str):
            fail(f"{path}[{index}] has no stable name")
        name = entry["name"]
        if name in result:
            fail(f"{path} repeats {name!r}")
        result[name] = entry
    return result


def compare_fields(old, new, path):
    if not isinstance(new, list):
        fail(f"{path} is not an array")
    old_fields = by_name(old, path)
    new_fields = by_name(new, path)
    old_order = [field["name"] for field in old]
    new_order = [field["name"] for field in new]
    if new_order != old_order:
        fail(f"{path} added, removed, or reordered a field")
    for name, field in old_fields.items():
        if name not in new_fields:
            fail(f"{path}.{name} was removed")
        same(field, new_fields[name], f"{path}.{name}")


def compare_message(old, new, path):
    if not isinstance(old, dict) or not isinstance(new, dict):
        fail(f"{path} has an invalid shape")
    for key, value in old.items():
        if key == "fields":
            if key not in new:
                fail(f"{path}.fields was removed")
            compare_fields(value, new[key], f"{path}.fields")
        elif key not in new:
            fail(f"{path}.{key} was removed")
        else:
            same(value, new[key], f"{path}.{key}")


def compare_input_schema(old, new, path):
    """Ensure a request schema still accepts every old valid input."""
    if not isinstance(old, dict) or not isinstance(new, dict):
        fail(f"{path} is not an object")

    metadata = {"title", "description", "default", "examples", "deprecated", "$comment"}
    bounded_lower = {"minimum", "exclusiveMinimum", "minLength", "minItems", "minProperties"}
    bounded_upper = {"maximum", "exclusiveMaximum", "maxLength", "maxItems", "maxProperties"}

    for key, old_value in old.items():
        if key not in new:
            fail(f"{path}.{key} was removed")
        new_value = new[key]
        if key == "properties":
            if not isinstance(old_value, dict) or not isinstance(new_value, dict):
                fail(f"{path}.properties is not an object")
            for name, old_property in old_value.items():
                if name not in new_value:
                    fail(f"{path}.properties.{name} was removed")
                compare_input_schema(old_property, new_value[name], f"{path}.properties.{name}")
        elif key == "required":
            if not isinstance(old_value, list) or not isinstance(new_value, list):
                fail(f"{path}.required is not an array")
            if not set(old_value).issubset(new_value):
                fail(f"{path}.required removed an existing property")
            if not set(new_value).issubset(old_value):
                fail(f"{path}.required added a required property")
        elif key == "enum":
            if not isinstance(old_value, list) or not isinstance(new_value, list):
                fail(f"{path}.enum is not an array")
            if any(value not in new_value for value in old_value):
                fail(f"{path}.enum narrowed the accepted values")
        elif key in bounded_lower:
            if old_value != new_value and (not isinstance(new_value, (int, float)) or not isinstance(old_value, (int, float)) or new_value > old_value):
                fail(f"{path}.{key} became more restrictive")
        elif key in bounded_upper:
            if old_value != new_value and (not isinstance(new_value, (int, float)) or not isinstance(old_value, (int, float)) or new_value < old_value):
                fail(f"{path}.{key} became more restrictive")
        elif key == "additionalProperties":
            if old_value is False and new_value is not False:
                pass
            elif old_value is True and new_value is False:
                fail(f"{path}.additionalProperties became restrictive")
            elif isinstance(old_value, dict):
                if new_value is False:
                    fail(f"{path}.additionalProperties became restrictive")
                if isinstance(new_value, dict):
                    compare_input_schema(old_value, new_value, f"{path}.additionalProperties")
                else:
                    same(old_value, new_value, f"{path}.additionalProperties")
            elif old_value != new_value:
                same(old_value, new_value, f"{path}.additionalProperties")
        elif key == "items":
            compare_input_schema(old_value, new_value, f"{path}.items")
        else:
            same(old_value, new_value, f"{path}.{key}")

    for key, new_value in new.items():
        if key in old:
            continue
        if key in metadata or key == "properties":
            if key == "properties" and not isinstance(new_value, dict):
                fail(f"{path}.properties is not an object")
            continue
        if key == "required":
            if new_value:
                fail(f"{path}.required added a required property")
            continue
        if key == "enum":
            fail(f"{path}.enum narrowed the accepted values")
        if key in bounded_lower or key in bounded_upper or key in {"pattern", "format", "const", "type", "not", "oneOf", "anyOf", "allOf", "prefixItems", "contains", "uniqueItems", "nullable"}:
            fail(f"{path}.{key} added a restrictive input constraint")
        if key == "additionalProperties":
            if new_value is False or isinstance(new_value, dict):
                fail(f"{path}.additionalProperties added a restrictive input constraint")
            continue
        if key == "items":
            fail(f"{path}.items added a restrictive input constraint")
        fail(f"{path}.{key} was added to an input schema")


def type_entries(entries, path):
    result = {}
    for index, entry in enumerate(entries):
        if not isinstance(entry, dict) or len(entry) != 1:
            fail(f"{path}[{index}] has an invalid shape")
        kind, definition = next(iter(entry.items()))
        if not isinstance(definition, dict) or not isinstance(definition.get("name"), str):
            fail(f"{path}[{index}] has no stable name")
        name = definition["name"]
        if name in result:
            fail(f"{path} repeats {name!r}")
        result[name] = (kind, definition)
    return result


def compare_message_pack(previous, current):
    for key in ("manifestVersion", "protocol", "protocolMajor", "protocolMinor", "supportedFeatures", "doc", "scan"):
        if key not in previous or key not in current:
            fail(f"msgpack.{key} is absent")
    same(previous["manifestVersion"], current["manifestVersion"], "msgpack.manifestVersion")
    same(previous["protocol"], current["protocol"], "msgpack.protocol")
    same(previous["protocolMajor"], current["protocolMajor"], "msgpack.protocolMajor")
    if current["protocolMinor"] < previous["protocolMinor"]:
        fail("msgpack.protocolMinor moved backwards")
    same(previous["supportedFeatures"], current["supportedFeatures"], "msgpack.supportedFeatures")
    same(previous["doc"], current["doc"], "msgpack.doc")
    same(previous["scan"], current["scan"], "msgpack.scan")

    old_envelope = previous.get("envelope")
    new_envelope = current.get("envelope")
    if not isinstance(old_envelope, dict) or not isinstance(new_envelope, dict):
        fail("msgpack envelope is not an object")
    for key, value in old_envelope.items():
        if key not in new_envelope:
            fail(f"msgpack.envelope.{key} was removed")
        if key == "elements":
            if not isinstance(value, list) or not isinstance(new_envelope[key], list):
                fail("msgpack.envelope.elements is not an array")
            if new_envelope[key] != value:
                fail("msgpack.envelope.elements changed or reordered")
        else:
            same(value, new_envelope[key], f"msgpack.envelope.{key}")

    old_types = type_entries(previous.get("types", []), "msgpack.types")
    new_types = type_entries(current.get("types", []), "msgpack.types")
    for name, (kind, definition) in old_types.items():
        if name not in new_types:
            fail(f"msgpack.types.{name} was removed")
        new_kind, new_definition = new_types[name]
        if kind != new_kind:
            fail(f"msgpack.types.{name} changed kind")
        if kind == "struct":
            compare_message(definition, new_definition, f"msgpack.types.{name}")
        elif kind in ("union", "enum"):
            collection = "variants" if kind == "union" else "values"
            old_items = by_name(definition.get(collection, []), f"msgpack.types.{name}.{collection}")
            new_items = by_name(new_definition.get(collection, []), f"msgpack.types.{name}.{collection}")
            fixed_old = {key: value for key, value in definition.items() if key != collection}
            fixed_new = {key: value for key, value in new_definition.items() if key != collection}
            same(fixed_old, fixed_new, f"msgpack.types.{name}")
            if list(new_items)[: len(old_items)] != list(old_items):
                fail(f"msgpack.types.{name}.{collection} removed or reordered an item")
            for item_name, item in old_items.items():
                if item_name not in new_items:
                    fail(f"msgpack.types.{name}.{item_name} was removed")
                compare_message(item, new_items[item_name], f"msgpack.types.{name}.{item_name}")

    old_messages = by_name(previous.get("messages", []), "msgpack.messages")
    new_messages = by_name(current.get("messages", []), "msgpack.messages")
    old_tags = {}
    new_tags = {}
    for name, message in old_messages.items():
        if name not in new_messages:
            fail(f"msgpack.messages.{name} was removed")
        compare_message(message, new_messages[name], f"msgpack.messages.{name}")
        old_tags[message.get("tag")] = name
    for name, message in new_messages.items():
        tag = message.get("tag")
        if tag in new_tags and new_tags[tag] != name:
            fail(f"msgpack.messages reuses tag {tag}")
        new_tags[tag] = name
    # New message tags are additive. Existing tags remain fixed above, and
    # the uniqueness check rejects reusing a withdrawn or occupied tag.
    for tag, name in old_tags.items():
        if tag not in new_tags:
            fail(f"msgpack.messages.{name} tag {tag} was removed")


def compare_parameters(old, new, path):
    if not isinstance(old, list) or not isinstance(new, list):
        fail(f"{path} is not an array")
    old_map = {(item.get("name"), item.get("in")): item for item in old if isinstance(item, dict)}
    new_map = {(item.get("name"), item.get("in")): item for item in new if isinstance(item, dict)}
    if len(old_map) != len(old) or len(new_map) != len(new):
        fail(f"{path} contains an invalid or duplicate parameter")
    for identity, old_item in old_map.items():
        if identity not in new_map:
            fail(f"{path} removed parameter {identity}")
        new_item = new_map[identity]
        if not old_item.get("required", False) and new_item.get("required", False):
            fail(f"{path} made parameter {identity} required")
        for key, old_value in old_item.items():
            if key == "schema":
                if key not in new_item:
                    fail(f"{path}.{identity}.schema was removed")
                compare_input_schema(old_value, new_item[key], f"{path}.{identity}.schema")
            elif key == "content":
                if key not in new_item:
                    fail(f"{path}.{identity}.content was removed")
                compare_content(old_value, new_item[key], f"{path}.{identity}.content", input_schema=True)
            elif key not in new_item:
                fail(f"{path}.{identity}.{key} was removed")
            else:
                same(old_value, new_item[key], f"{path}.{identity}.{key}")
        for key in ("schema", "content"):
            if key in new_item and key not in old_item:
                fail(f"{path}.{identity}.{key} changed the input representation")
    for identity, new_item in new_map.items():
        if identity not in old_map and new_item.get("required", False):
            fail(f"{path} added required parameter {identity}")


def compare_content(old, new, path, input_schema=False):
    if not isinstance(old, dict) or not isinstance(new, dict):
        fail(f"{path} is not an object")
    for media_type, old_value in old.items():
        if media_type not in new:
            fail(f"{path}.{media_type} was removed")
        new_value = new[media_type]
        if input_schema and isinstance(old_value, dict) and isinstance(new_value, dict) and "schema" in old_value:
            for key, value in old_value.items():
                if key == "schema":
                    if key not in new_value:
                        fail(f"{path}.{media_type}.schema was removed")
                    compare_input_schema(value, new_value[key], f"{path}.{media_type}.schema")
                elif key not in new_value:
                    fail(f"{path}.{media_type}.{key} was removed")
                else:
                    same(value, new_value[key], f"{path}.{media_type}.{key}")
        elif input_schema and isinstance(old_value, dict) and isinstance(new_value, dict) and "schema" in new_value:
            fail(f"{path}.{media_type}.schema was added to an input")
        else:
            same(old_value, new_value, f"{path}.{media_type}")


def compare_request_body(old, new, path):
    if not isinstance(old, dict) or not isinstance(new, dict):
        fail(f"{path} has an invalid shape")
    if not old.get("required", False) and new.get("required", False):
        fail(f"{path} became required")
    for key, old_value in old.items():
        if key == "content":
            compare_content(old_value, new.get(key), f"{path}.content", input_schema=True)
        elif key not in new:
            fail(f"{path}.{key} was removed")
        else:
            same(old_value, new[key], f"{path}.{key}")


def compare_responses(old, new, path):
    if not isinstance(old, dict) or not isinstance(new, dict):
        fail(f"{path} is not an object")
    for status, old_response in old.items():
        if status not in new:
            fail(f"{path}.{status} was removed")
        if not isinstance(old_response, dict) or not isinstance(new[status], dict):
            fail(f"{path}.{status} has an invalid shape")
        for key, old_value in old_response.items():
            if key == "content":
                compare_content(old_value, new[status].get(key), f"{path}.{status}.content")
            elif key not in new[status]:
                fail(f"{path}.{status}.{key} was removed")
            else:
                same(old_value, new[status][key], f"{path}.{status}.{key}")


def compare_operation(old, new, path):
    if not isinstance(old, dict) or not isinstance(new, dict):
        fail(f"{path} has an invalid shape")
    for key, old_value in old.items():
        if key == "parameters":
            compare_parameters(old_value, new.get(key), f"{path}.parameters")
        elif key == "requestBody":
            if key not in new:
                fail(f"{path}.requestBody was removed")
            compare_request_body(old_value, new[key], f"{path}.requestBody")
        elif key == "responses":
            if key not in new:
                fail(f"{path}.responses was removed")
            compare_responses(old_value, new[key], f"{path}.responses")
        elif key not in new:
            fail(f"{path}.{key} was removed")
        else:
            same(old_value, new[key], f"{path}.{key}")
    if "parameters" in new and "parameters" not in old:
        compare_parameters([], new["parameters"], f"{path}.parameters")
    if "requestBody" in new and "requestBody" not in old:
        request_body = new["requestBody"]
        if not isinstance(request_body, dict):
            fail(f"{path}.requestBody has an invalid shape")
        if request_body.get("required", False):
            fail(f"{path}.requestBody was added as required")


def compare_openapi(previous, current):
    for key, old_value in previous.items():
        if key == "paths":
            continue
        if key not in current:
            fail(f"openapi.{key} was removed")
        same(old_value, current[key], f"openapi.{key}")
    old_paths = previous.get("paths")
    new_paths = current.get("paths")
    if not isinstance(old_paths, dict) or not isinstance(new_paths, dict):
        fail("openapi.paths is not an object")
    methods = {"get", "put", "post", "delete", "options", "head", "patch", "trace"}
    for path, old_item in old_paths.items():
        if path not in new_paths:
            fail(f"openapi path {path} was removed")
        new_item = new_paths[path]
        if not isinstance(old_item, dict) or not isinstance(new_item, dict):
            fail(f"openapi path {path} has an invalid shape")
        for key, old_value in old_item.items():
            if key in methods:
                if key not in new_item:
                    fail(f"openapi operation {key.upper()} {path} was removed")
                compare_operation(old_value, new_item[key], f"openapi {key.upper()} {path}")
            elif key == "parameters":
                compare_parameters(old_value, new_item.get(key), f"openapi {path}.parameters")
            elif key not in new_item:
                fail(f"openapi {path}.{key} was removed")
            else:
                same(old_value, new_item[key], f"openapi {path}.{key}")
        if "parameters" in new_item and "parameters" not in old_item:
            compare_parameters([], new_item["parameters"], f"openapi {path}.parameters")


previous_manifest = load_json(previous_root / "manifest.json")
current_manifest = load_json(current_root / "manifest.json")
previous_version = validate_manifest(previous_manifest, "previous.manifest")
current_version = validate_manifest(current_manifest, "current.manifest", requested_version)
if previous_version >= current_version:
    fail("previous.manifest.contractVersion must be older than current --version")

compare_openapi(load_json(previous_root / "openapi.json"), load_json(current_root / "openapi.json"))
compare_message_pack(
    load_json(previous_root / "msgpack" / "terrascale.sync.v2.msgpack.json"),
    load_json(current_root / "msgpack" / "terrascale.sync.v2.msgpack.json"),
)
print("OpenAPI and MessagePack contracts are additive")
PY

  [[ -f "$previous_bundle/proto/terrabase/v1/engine.proto" ]] || {
    echo "previous contract bundle is missing engine.proto" >&2
    exit 1
  }
  [[ -f "$previous_bundle/proto/terrabase/v1/family.proto" ]] || {
    echo "previous contract bundle is missing family.proto" >&2
    exit 1
  }
  work_proto="$work/current-proto"
  cp -a contracts/proto "$work_proto"
  buf_config=$'version: v2\nmodules:\n  - path: .\nbreaking:\n  use:\n    - FILE'
  (
    cd "$work_proto"
    buf breaking . --against "$previous_bundle/proto" --config "$buf_config" --disable-symlinks
  )
  echo "TBEP protobuf contract is additive"
fi

echo "contract artifacts are up to date"
