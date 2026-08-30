#!/usr/bin/env python3
"""Create a deterministic CycloneDX SBOM from Cargo metadata."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import sys
import urllib.parse


def digest(path: pathlib.Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def cargo_ref(package: dict) -> str:
    name = urllib.parse.quote(package["name"], safe="._-")
    version = urllib.parse.quote(package["version"], safe="._-+")
    return f"pkg:cargo/{name}@{version}"


def reachable(metadata: dict, root_id: str) -> set[str]:
    resolution = metadata.get("resolve")
    if not resolution:
        raise ValueError("Cargo metadata did not contain a resolved dependency graph")
    edges = {
        node["id"]: [dependency["pkg"] for dependency in node.get("deps", [])]
        for node in resolution.get("nodes", [])
    }
    pending = [root_id]
    found: set[str] = set()
    while pending:
        package_id = pending.pop()
        if package_id in found:
            continue
        found.add(package_id)
        pending.extend(edges.get(package_id, []))
    return found


def license_entry(package: dict) -> list[dict]:
    expression = package.get("license")
    if not expression:
        raise ValueError(
            f"package {package['name']} {package['version']} has no SPDX license expression"
        )
    return [{"expression": expression}]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--metadata", required=True, type=pathlib.Path)
    parser.add_argument("--root", required=True)
    parser.add_argument("--artifact", required=True, type=pathlib.Path)
    parser.add_argument("--target", required=True)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    arguments = parser.parse_args()

    metadata = json.loads(arguments.metadata.read_text())
    packages = metadata.get("packages", [])
    roots = [package for package in packages if package["name"] == arguments.root]
    if len(roots) != 1:
        print(f"expected one Cargo package named {arguments.root}, found {len(roots)}", file=sys.stderr)
        return 1
    root = roots[0]
    package_by_id = {package["id"]: package for package in packages}
    included = reachable(metadata, root["id"])

    components = []
    for package_id in sorted(included):
        if package_id == root["id"]:
            continue
        package = package_by_id[package_id]
        reference = cargo_ref(package)
        component = {
            "bom-ref": reference,
            "type": "library",
            "name": package["name"],
            "version": package["version"],
            "purl": reference,
            "licenses": license_entry(package),
        }
        if package.get("source"):
            component["properties"] = [
                {"name": "terrabase:cargo-source", "value": package["source"]}
            ]
        components.append(component)
    components.sort(key=lambda item: item["bom-ref"])

    nodes = {
        node["id"]: [dependency["pkg"] for dependency in node.get("deps", [])]
        for node in metadata["resolve"]["nodes"]
    }
    root_reference = f"pkg:generic/{arguments.root}@{root['version']}?target={urllib.parse.quote(arguments.target)}"
    dependencies = []
    for package_id in sorted(included):
        reference = root_reference if package_id == root["id"] else cargo_ref(package_by_id[package_id])
        depends_on = [
            cargo_ref(package_by_id[dependency])
            for dependency in nodes.get(package_id, [])
            if dependency in included and dependency != root["id"]
        ]
        dependencies.append({"ref": reference, "dependsOn": sorted(set(depends_on))})

    document = {
        "bomFormat": "CycloneDX",
        "specVersion": "1.6",
        "version": 1,
        "metadata": {
            "component": {
                "bom-ref": root_reference,
                "type": "application",
                "name": arguments.root,
                "version": root["version"],
                "hashes": [{"alg": "SHA-256", "content": digest(arguments.artifact)}],
                "licenses": license_entry(root),
                "properties": [
                    {"name": "terrabase:rust-target", "value": arguments.target},
                    {"name": "terrabase:artifact", "value": arguments.artifact.name},
                ],
            }
        },
        "components": components,
        "dependencies": dependencies,
    }
    arguments.output.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
