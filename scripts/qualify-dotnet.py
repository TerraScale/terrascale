"""Qualify the C# starter with exact installed packages and the real pinned binary."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ["package", "testing-package", "binary", "receipt"]:
        parser.add_argument("--" + name, required=True, type=Path)
    for name in ["sha256", "testing-sha256", "binary-sha256", "version"]:
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?", args.version):
        parser.error("version must be an exact NuGet version")
    for path, expected in [(args.package, args.sha256), (args.testing_package, args.testing_sha256),
                           (args.binary, args.binary_sha256)]:
        if not re.fullmatch(r"[0-9a-f]{64}", expected) or not path.is_file():
            parser.error("each artifact needs an existing file and a lowercase SHA-256")
        if digest(path) != expected:
            parser.error("artifact SHA-256 differs from the supplied identity")
    if not os.access(args.binary, os.X_OK):
        parser.error("real binary must be executable")
    revisions = []
    for path, package_id in [(args.package, "TerraScale.DotNet"), (args.testing_package, "TerraScale.DotNet.Testing")]:
        try:
            with zipfile.ZipFile(path) as package:
                nuspec = next(name for name in package.namelist() if name.endswith(".nuspec"))
                metadata = ET.fromstring(package.read(nuspec))
                values = {node.tag.split("}")[-1]: node.text for node in metadata.iter()}
                if values.get("id") != package_id or values.get("version") != args.version:
                    parser.error("package ID/version differs from the supplied identity")
                if f"lib/net10.0/{package_id}.dll" not in package.namelist():
                    parser.error("package lacks its .NET 10 assembly")
                repository = next((node for node in metadata.iter() if node.tag.split("}")[-1] == "repository"), None)
                revision = repository.get("commit") if repository is not None else None
                if not revision or not re.fullmatch(r"[0-9a-f]{40}", revision):
                    parser.error("package lacks an exact source revision")
                revisions.append(revision)
        except (zipfile.BadZipFile, ET.ParseError, StopIteration):
            parser.error("invalid SDK package")
    if args.receipt.exists():
        parser.error("receipt already exists; use a new evidence path")
    if revisions[0] != revisions[1]:
        parser.error("SDK and testing package source revisions differ")
    root = Path(__file__).resolve().parent.parent
    sources = ["examples/terrabase-dotnet/Program.cs", "examples/terrabase-dotnet/TerraBaseStarter.csproj",
               "scripts/qualification-dotnet/Qualification.cs", "scripts/qualification-dotnet/Qualification.csproj",
               "scripts/qualify-dotnet.py"]
    receipt = {"sdk_sha256": args.sha256, "testing_sha256": args.testing_sha256,
               "sdk_version": args.version, "producer_sha256": args.binary_sha256,
               "sdk_source_revision": revisions[0],
               "source_sha256": {name: digest(root / name) for name in sources}, "status": "FAIL"}
    try:
        with tempfile.TemporaryDirectory(prefix="terrascale-docs-csharp-") as scratch:
            directory = Path(scratch)
            for source in sources:
                destination = directory / source
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(root / source, destination)
            feed = directory / "feed"
            feed.mkdir()
            for source, name in [(args.package, "TerraScale.DotNet"), (args.testing_package, "TerraScale.DotNet.Testing")]:
                shutil.copyfile(source, feed / f"{name}.{args.version}.nupkg")
            config = directory / "NuGet.Config"
            config.write_text(f'''<configuration>
  <packageSources><clear />
    <add key="sdk" value="{escape(str(feed))}" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="sdk"><package pattern="TerraScale.DotNet*" /></packageSource>
    <packageSource key="nuget"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>''')
            env = dict(os.environ, NUGET_PACKAGES=str(directory / "packages"),
                       TERRABASE_BINARY=str(args.binary.resolve()), DOTNET_CLI_TELEMETRY_OPTOUT="1")
            project = directory / "scripts/qualification-dotnet/Qualification.csproj"
            commands = [["dotnet", "restore", str(project), f"-p:TerraBaseSdkPackageVersion={args.version}",
                         "--configfile", str(config)],
                        ["dotnet", "build", str(project), "--no-restore", "-c", "Release",
                         f"-p:TerraBaseSdkPackageVersion={args.version}"],
                        ["dotnet", str(project.parent / "bin/Release/net10.0/Qualification.dll")]]
            for phase, command in zip(["restore", "build", "real_binary"], commands):
                receipt["phase"] = phase
                # Never print arbitrary build/process output, credentials or server response bodies.
                result = subprocess.run(command, cwd=directory, env=env, capture_output=True, timeout=120, check=False)
                if result.returncode != 0:
                    raise RuntimeError(f"{phase} failed (exit {result.returncode}); qualification not passed")
                if phase == "real_binary" and result.stdout.strip() != b"PASS_REAL_TERRABASE_STARTER":
                    raise RuntimeError("real_binary did not produce the expected success marker")
            receipt["status"] = "PASS_REAL_TERRABASE_STARTER"
    except (OSError, subprocess.TimeoutExpired, RuntimeError) as error:
        receipt["failure"] = str(error) if isinstance(error, RuntimeError) else "tool unavailable or phase deadline exceeded"
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    with args.receipt.open("x", encoding="utf-8") as output:
        output.write(json.dumps(receipt, indent=2) + "\n")
    print(receipt["status"])
    return 0 if receipt["status"] == "PASS_REAL_TERRABASE_STARTER" else 1


if __name__ == "__main__":
    raise SystemExit(main())
