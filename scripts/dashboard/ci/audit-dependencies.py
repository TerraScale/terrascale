#!/usr/bin/env python3
"""Audit every committed resolved dependency graph, including standalone scripts."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parents[3]
scanner = str(Path(sys.argv[1]).resolve())
tracked = subprocess.check_output(["git", "ls-files", "-z", "--", "src/TerraScale.Dashboard", "src/TerraScale.Dashboard.Analyzers",
                                   "tests/TerraScale.Dashboard.E2E", "scripts/dashboard", "sdk/dotnet"], cwd=root).decode().split("\0")
with tempfile.TemporaryDirectory(prefix="terrascale-advisory-locks-") as directory:
    staging = Path(directory)
    expected = set()
    project_references = {}
    empty_graphs = set()
    for name in sorted(filter(None, tracked)):
        source = root / name
        canonical = source.name
        if canonical.endswith("packages.lock.json"):
            canonical = "packages.lock.json"
        if canonical not in {"packages.lock.json", "package-lock.json", "Cargo.lock", "go.mod", "requirements.txt"}:
            continue
        print(f"Audit: {name} sha256={hashlib.sha256(source.read_bytes()).hexdigest()}", flush=True)
        if canonical.endswith(".json"):
            json.loads(source.read_text())  # A malformed lock is an unavailable scan, never clean.
        destination = staging / name / canonical
        destination.parent.mkdir(parents=True)
        shutil.copyfile(source, destination)
        if canonical == "packages.lock.json":
            lock = json.loads(source.read_text())
            if lock["version"] not in (1, 2) or not isinstance(lock["dependencies"], dict) or not lock["dependencies"]:
                raise SystemExit(f"Invalid NuGet dependency graph: {name}")
            if all(isinstance(framework, dict) and not framework for framework in lock["dependencies"].values()):
                empty_graphs.add(str(destination))
            project_references[str(destination)] = {
                package.lower() for framework in lock["dependencies"].values()
                for package, value in framework.items() if value["type"] == "Project"
            }
        if canonical == "go.mod" and source.with_name("go.sum").is_file():
            shutil.copyfile(source.with_name("go.sum"), destination.with_name("go.sum"))
        expected.add(str(destination))
    if not expected:
        raise SystemExit("No committed dependency graphs found")
    # Canonical copies include vendored *.packages.lock.json that recursive auto-discovery misses.
    # Keep every severity visible and fail on any finding or scanner/network/parser failure.
    report = staging / "audit.json"
    scan = subprocess.run([scanner, "scan", "source", "--recursive", "--no-resolve",
                           "--no-call-analysis=go,rust", "--all-vulns", "--all-packages",
                           "--format=json", "--output-file", str(report), directory])
    results = json.loads(report.read_text())["results"]
    if not isinstance(results, list):
        raise SystemExit("Invalid advisory scanner results")
    seen = set()
    findings = []
    for result in results:
        source = result["source"]["path"]
        packages = result["packages"]
        if (source not in expected or source in seen or not isinstance(packages, list)
                or (not packages and source not in empty_graphs)):
            raise SystemExit(f"Missing, duplicate or unexpected dependency inventory: {source}")
        seen.add(source)
        for package in packages:
            identity = package["package"]
            if "source=LOCAL" in identity.get("purl", ""):
                raise SystemExit(f"Unscannable local package source: {source}: {identity['name']}")
            local_project = (identity["ecosystem"] == "NuGet" and not identity["version"]
                             and identity["name"].lower() in project_references.get(source, set()))
            if not identity["name"] or not identity["ecosystem"] or (not identity["version"] and not local_project):
                raise SystemExit(f"Incomplete package identity: {source}")
            for advisory in package.get("vulnerabilities", []):
                findings.append({"source": source, "package": identity, "advisory": advisory})
        print(f"Inventoried: {source.removeprefix(directory + '/')}: {len(packages)} packages")
    for source in sorted(empty_graphs - seen):
        print(f"Inventoried: {source.removeprefix(directory + '/')}: declared empty NuGet graph")
    if seen | empty_graphs != expected:
        raise SystemExit(f"Scanner omitted committed graphs: {sorted(expected - seen - empty_graphs)}")
    if findings:
        print(json.dumps(findings, indent=2))
    if scan.returncode != 0 or findings:
        raise SystemExit(f"Dependency advisory scan failed: status={scan.returncode}, findings={len(findings)}")
    print(f"Dependency advisory audit: {len(expected)} committed graphs, zero findings.")
