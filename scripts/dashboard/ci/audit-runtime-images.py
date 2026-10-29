#!/usr/bin/env python3
"""Audit the pinned operating-system bases used by production entrypoint stages."""
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parents[3]
scanner = str(Path(sys.argv[1]).resolve())
images = set()
for dockerfile in [root / "scripts/dashboard/Dockerfile", *sorted((root / "scripts/dashboard/ci").glob("Dockerfile.*"))]:
    if not dockerfile.is_file():
        continue
    for stage in re.finditer(r"^FROM\s+(?:--\S+\s+)*(\S+)[^\n]*\n(.*?)(?=^FROM\s|\Z)", dockerfile.read_text(), re.M | re.S):
        if not re.search(r"^ENTRYPOINT\s", stage[2], re.M):
            continue
        if not re.fullmatch(r"[^\s$]+@sha256:[0-9a-f]{64}", stage[1]):
            raise SystemExit(f"Production runtime base must be immutable: {dockerfile.relative_to(root)}: {stage[1]}")
        images.add(stage[1])
if not images:
    raise SystemExit("No production runtime bases found")
targets = []
for image in sorted(images):
    index = json.loads(subprocess.check_output(["docker", "manifest", "inspect", image]))
    platforms = {}
    for manifest in index["manifests"]:
        platform = manifest["platform"]
        if platform["os"] != "linux" or platform["architecture"] not in {"amd64", "arm64"}:
            continue
        architecture = platform["architecture"]
        digest = manifest["digest"]
        if architecture in platforms or not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
            raise SystemExit("Duplicate platform or invalid runtime child digest")
        platforms[architecture] = image.rsplit("@", 1)[0] + "@" + digest
    if set(platforms) != {"amd64", "arm64"}:
        raise SystemExit("Runtime base must provide both Linux release architectures")
    targets.extend((architecture, child) for architecture, child in sorted(platforms.items()))
with tempfile.TemporaryDirectory(prefix="runtime-advisories-") as directory:
    for index, (architecture, image) in enumerate(targets):
        report = Path(directory) / f"{index}.json"
        scan = subprocess.run([scanner, "scan", "image", "--all-vulns", "--all-packages",
                               "--no-call-analysis=go,rust", "--format=json", "--output-file", str(report), image])
        results = json.loads(report.read_text())["results"]
        if not isinstance(results, list):
            raise SystemExit("Invalid runtime advisory report")
        operating_system = []
        findings = []
        for source in results:
            packages = source["packages"]
            if not isinstance(packages, list):
                raise SystemExit("Invalid runtime package inventory")
            for package in packages:
                identity = package["package"]
                if not all(identity.get(field) for field in ("name", "version", "ecosystem")):
                    raise SystemExit("Incomplete runtime package identity")
                if source["source"]["type"] == "os":
                    operating_system.append(identity)
                findings.extend(package.get("vulnerabilities", []))
        if not operating_system:
            raise SystemExit("Runtime image has no operating-system inventory")
        print(f"Runtime base ({architecture}): {image}")
        print(json.dumps(operating_system, indent=2))
        if findings:
            print(json.dumps(findings, indent=2))
        if scan.returncode != 0 or findings:
            raise SystemExit(f"Runtime advisory scan failed: status={scan.returncode}, findings={len(findings)}")
        print(f"Runtime OS advisory audit: {len(operating_system)} packages, zero findings.")
