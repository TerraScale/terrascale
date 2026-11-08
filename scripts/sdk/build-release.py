#!/usr/bin/env python3
"""Build an SDK's release artifacts for this checkout with its own release gates."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tomllib
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


def version_of(language, area, output):
    if language == "dotnet":
        return ET.parse(area / "src/TerraScale.DotNet/TerraScale.DotNet.csproj").findtext(".//Version")
    if language == "js":
        return json.loads((area / "package.json").read_text())["version"]
    if language == "python":
        return re.search(r'__version__ = "([^"]+)"', (area / "src/terrascale/_version.py").read_text())[1]
    if language == "rust":
        return tomllib.loads((area / "Cargo.toml").read_text())["package"]["version"]
    return (output / "version.txt").read_text().strip()


def main(language, destination):
    if language not in {"dotnet", "js", "go", "python", "rust"}:
        raise SystemExit("Unknown SDK")
    area = ROOT / "sdk" / language
    output = Path(destination).resolve()
    subprocess.run(["bash", str(area / "scripts/qualify-release.sh"), str(output)], cwd=ROOT, check=True)
    version = version_of(language, area, output)
    if not re.fullmatch(r"0\.0\.[0-9]+", version or ""):
        raise SystemExit("SDK versions must be 0.0.X")
    if not any(path.is_file() for path in output.iterdir()):
        raise SystemExit("The release build produced no artifacts")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as stream:
            stream.write(f"version={version}\n")
    print(f"Built {language} {version} artifacts in {output}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("usage: build-release.py <dotnet|js|go|python|rust> <output-directory>")
    main(*sys.argv[1:])
