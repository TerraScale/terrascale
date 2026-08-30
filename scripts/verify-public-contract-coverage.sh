#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
canonical_path="${repository_root}/tests/TerraScale.DotNet.Tests/contracts/canonical-public-routes.v1.json"
bindings_path="${repository_root}/tests/TerraScale.DotNet.Tests/contracts/dotnet-public-route-bindings.v1.json"
strict=false
route_manifest_path=""
contract_descriptor_path=""
openapi_path=""

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --strict)
      strict=true
      shift
      ;;
    --route-manifest)
      if [[ "$#" -lt 2 ]]; then
        echo "--route-manifest requires a path" >&2
        exit 2
      fi
      route_manifest_path="$2"
      shift 2
      ;;
    --contract-descriptor)
      if [[ "$#" -lt 2 ]]; then
        echo "--contract-descriptor requires a path" >&2
        exit 2
      fi
      contract_descriptor_path="$2"
      shift 2
      ;;
    --openapi)
      if [[ "$#" -lt 2 ]]; then
        echo "--openapi requires a path" >&2
        exit 2
      fi
      openapi_path="$2"
      shift 2
      ;;
    *)
      echo "usage: $0 [--strict] [--route-manifest path/to/sdk-route-manifest.json --contract-descriptor path/to/sdk-openapi-contract-descriptor.json --openapi path/to/server.openapi.json]" >&2
      exit 2
      ;;
  esac
done

external_input_count=0
for external_path in "${route_manifest_path}" "${contract_descriptor_path}" "${openapi_path}"; do
  if [[ -n "${external_path}" ]]; then
    external_input_count=$((external_input_count + 1))
  fi
done

if [[ "${external_input_count}" -ne 0 && "${external_input_count}" -ne 3 ]]; then
  echo "--route-manifest, --contract-descriptor, and --openapi must be supplied together" >&2
  exit 2
fi

if [[ "${strict}" == true && "${external_input_count}" -ne 3 ]]; then
  echo "strict public contract coverage requires the exact external route manifest, contract descriptor, and owner OpenAPI fixture" >&2
  exit 2
fi

python3 - \
  "${canonical_path}" \
  "${bindings_path}" \
  "${strict}" \
  "${route_manifest_path}" \
  "${contract_descriptor_path}" \
  "${openapi_path}" <<'PY'
import hashlib
import json
import os
import re
import stat
import sys

canonical_path, bindings_path, strict_text, route_manifest_path, contract_descriptor_path, openapi_path = sys.argv[1:]
strict = strict_text == "true"


def read_regular_json(path, label):
    try:
        path_stat = os.lstat(path)
        if not stat.S_ISREG(path_stat.st_mode) or stat.S_ISLNK(path_stat.st_mode):
            raise OSError("must be a non-symlink regular file")
        flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_CLOEXEC", 0)
        descriptor = os.open(path, flags)
        try:
            opened_stat = os.fstat(descriptor)
            before = (path_stat.st_dev, path_stat.st_ino, path_stat.st_size, path_stat.st_mtime_ns, path_stat.st_ctime_ns)
            opened = (opened_stat.st_dev, opened_stat.st_ino, opened_stat.st_size, opened_stat.st_mtime_ns, opened_stat.st_ctime_ns)
            if before != opened:
                raise OSError("changed while it was opened")
            with os.fdopen(descriptor, "rb") as stream:
                descriptor = -1
                contents = stream.read()
                final_stat = os.fstat(stream.fileno())
            after_stat = os.lstat(path)
            final = (final_stat.st_dev, final_stat.st_ino, final_stat.st_size, final_stat.st_mtime_ns, final_stat.st_ctime_ns)
            after = (after_stat.st_dev, after_stat.st_ino, after_stat.st_size, after_stat.st_mtime_ns, after_stat.st_ctime_ns)
            if final != opened or after != opened:
                raise OSError("changed while it was read")
        finally:
            if descriptor >= 0:
                os.close(descriptor)
        return json.loads(contents), hashlib.sha256(contents).hexdigest()
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise SystemExit(f"{label} is not a stable JSON file: {error}")


def exact_keys(value, expected, label):
    if not isinstance(value, dict) or set(value) != set(expected):
        raise SystemExit(f"{label} must contain exactly: {', '.join(expected)}")


def validate_routes(routes, expected_count, label):
    if not isinstance(routes, list) or len(routes) != expected_count:
        raise SystemExit(f"{label} must contain exactly {expected_count} routes")
    names = set()
    method_paths = set()
    for index, route in enumerate(routes):
        exact_keys(route, ("routeName", "method", "pathTemplate", "operationId"), f"{label} route {index}")
        if not all(isinstance(route[field], str) and route[field] for field in route):
            raise SystemExit(f"{label} route {index} fields must be non-empty strings")
        if not re.fullmatch(r"[A-Z]+", route["method"]):
            raise SystemExit(f"{label} route {index} has an invalid HTTP method")
        if not route["pathTemplate"].startswith("/api/"):
            raise SystemExit(f"{label} route {index} has an invalid public path")
        if route["routeName"] in names:
            raise SystemExit(f"{label} contains duplicate route name {route['routeName']}")
        method_path = (route["method"], route["pathTemplate"])
        if method_path in method_paths:
            raise SystemExit(f"{label} contains duplicate method/path {method_path[0]} {method_path[1]}")
        names.add(route["routeName"])
        method_paths.add(method_path)
    return names


canonical, canonical_digest = read_regular_json(canonical_path, "canonical public-route fixture")
exact_keys(canonical, ("format", "routeCount", "sources", "routes"), "canonical public-route fixture")
if canonical["format"] != "TerraScale.DotNet.CanonicalPublicRoutes/v1" or canonical["routeCount"] != 91:
    raise SystemExit("canonical public-route fixture identity or route count is invalid")
exact_keys(canonical["sources"], ("routeManifest", "contractDescriptor", "openApi"), "canonical source identities")
for source_name, source in canonical["sources"].items():
    exact_keys(source, ("path", "sha256"), f"canonical {source_name} identity")
    if not isinstance(source["path"], str) or not source["path"] or not re.fullmatch(r"[0-9a-f]{64}", source["sha256"]):
        raise SystemExit(f"canonical {source_name} identity is invalid")
canonical_names = validate_routes(canonical["routes"], 91, "canonical public-route fixture")
canonical_by_name = {route["routeName"]: route for route in canonical["routes"]}

bindings, _ = read_regular_json(bindings_path, ".NET public-route bindings")
exact_keys(bindings, ("format", "canonicalSha256", "routeCount", "routes"), ".NET public-route bindings")
if bindings["format"] != "TerraScale.DotNet.PublicRouteBindings/v1":
    raise SystemExit(".NET public-route binding format is invalid")
if bindings["canonicalSha256"] != canonical_digest:
    raise SystemExit(".NET public-route bindings do not identify the exact canonical fixture")
if not isinstance(bindings["routeCount"], int):
    raise SystemExit(".NET public-route binding count must be an integer")
binding_names = validate_routes(bindings["routes"], bindings["routeCount"], ".NET public-route bindings")
unexpected = sorted(binding_names - canonical_names)
if unexpected:
    raise SystemExit(f".NET public-route bindings contain unknown routes: {', '.join(unexpected)}")
for name in binding_names:
    if canonical_by_name[name] != next(route for route in bindings["routes"] if route["routeName"] == name):
        raise SystemExit(f".NET public-route binding drift for {name}")

if route_manifest_path:
    route_manifest, route_manifest_digest = read_regular_json(route_manifest_path, "external SDK route manifest")
    if route_manifest_digest != canonical["sources"]["routeManifest"]["sha256"]:
        raise SystemExit("external SDK route manifest digest does not match the canonical source identity")
    if route_manifest.get("routeCount") != 91 or not isinstance(route_manifest.get("routes"), list):
        raise SystemExit("external SDK route manifest must contain exactly 91 routes")
    external_routes = [
        {
            "routeName": route.get("routeName"),
            "method": route.get("method"),
            "pathTemplate": route.get("pathTemplate"),
            "operationId": canonical_by_name.get(route.get("routeName"), {}).get("operationId"),
        }
        for route in route_manifest["routes"]
    ]
    if external_routes != canonical["routes"]:
        raise SystemExit("external SDK route manifest routes do not match the canonical 91-route fixture")

    contract_descriptor, contract_descriptor_digest = read_regular_json(
        contract_descriptor_path,
        "external SDK contract descriptor",
    )
    if contract_descriptor_digest != canonical["sources"]["contractDescriptor"]["sha256"]:
        raise SystemExit("external SDK contract descriptor digest does not match the canonical source identity")
    if contract_descriptor.get("routeCount") != 91 or not isinstance(contract_descriptor.get("routes"), list):
        raise SystemExit("external SDK contract descriptor must contain exactly 91 routes")
    descriptor_by_name = {}
    for route in contract_descriptor["routes"]:
        if not isinstance(route, dict) or not isinstance(route.get("routeName"), str):
            raise SystemExit("external SDK contract descriptor contains an invalid route")
        name = route["routeName"]
        if name in descriptor_by_name:
            raise SystemExit(f"external SDK contract descriptor contains duplicate route name {name}")
        descriptor_by_name[name] = route
    if set(descriptor_by_name) != canonical_names:
        raise SystemExit("external SDK contract descriptor route names do not match the canonical fixture")
    for route in canonical["routes"]:
        descriptor = descriptor_by_name[route["routeName"]]
        for field in ("method", "pathTemplate", "operationId"):
            if descriptor.get(field) != route[field]:
                raise SystemExit(
                    f"external SDK contract descriptor drift for {route['routeName']} field {field}"
                )

    openapi, openapi_digest = read_regular_json(openapi_path, "external owner OpenAPI fixture")
    if openapi_digest != canonical["sources"]["openApi"]["sha256"]:
        raise SystemExit("external owner OpenAPI digest does not match the canonical source identity")
    paths = openapi.get("paths")
    if not isinstance(paths, dict):
        raise SystemExit("external owner OpenAPI fixture has no paths object")
    for route in canonical["routes"]:
        path_item = paths.get(route["pathTemplate"])
        operation = path_item.get(route["method"].lower()) if isinstance(path_item, dict) else None
        if not isinstance(operation, dict) or operation.get("operationId") != route["operationId"]:
            raise SystemExit(
                f"external owner OpenAPI does not bind {route['routeName']} to "
                f"{route['method']} {route['pathTemplate']} ({route['operationId']})"
            )

missing = [route["routeName"] for route in canonical["routes"] if route["routeName"] not in binding_names]
print(f".NET public route coverage: {len(binding_names)}/91 bound; {len(missing)} missing")
if missing:
    print("missing route bindings:")
    for name in missing:
        print(f"  {name}")

if strict and missing:
    raise SystemExit("strict public contract coverage failed because .NET does not bind every canonical route")
PY
