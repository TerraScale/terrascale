"""Build a clean SDK revision twice and qualify the exact installed package bytes."""
import hashlib
import html
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def run(*command, cwd=ROOT, env=None, capture=False, data=None):
    return subprocess.run(command, cwd=cwd, env=env, input=data, check=True,
                          stdout=subprocess.PIPE if capture else None).stdout


def digest(data):
    return hashlib.sha256(data).hexdigest()


def main(destination):
    if run("git", "status", "--porcelain", capture=True).strip():
        raise SystemExit("Commit the SDK changes before qualifying its exact clean revision.")
    output = Path(destination).resolve()
    if output.is_relative_to(ROOT):
        raise SystemExit("Choose an artifact output directory outside the source worktree.")
    revision = run("git", "rev-parse", "HEAD", capture=True).decode().strip()
    tree = run("git", "rev-parse", "HEAD^{tree}", capture=True).decode().strip()
    timestamp = run("git", "show", "-s", "--format=%ct", revision, capture=True).decode().strip()
    archive = run("git", "archive", "--format=tar", revision, capture=True)
    version = ET.parse(ROOT / "src/TerraScale.DotNet/TerraScale.DotNet.csproj").findtext(".//Version")
    name = f"TerraScale.DotNet.{version}.nupkg"
    packages = []
    with tempfile.TemporaryDirectory(prefix="ts-dotnet-package-") as temporary:
        temporary = Path(temporary)
        for index in range(2):
            source = temporary / f"source-{index}"
            source.mkdir()
            run("tar", "-xf", "-", "-C", str(source), data=archive)
            env = dict(os.environ, NUGET_PACKAGES=str(temporary / f"nuget-{index}"),
                       NUGET_HTTP_CACHE_PATH=str(temporary / f"http-{index}"), SOURCE_DATE_EPOCH=timestamp)
            project = "src/TerraScale.DotNet/TerraScale.DotNet.csproj"
            run("dotnet", "restore", project, "--locked-mode", cwd=source, env=env)
            run("dotnet", "pack", project, "--configuration", "Release", "--no-restore",
                "-p:ContinuousIntegrationBuild=true", f"-p:SourceRevisionId={revision}",
                f"-p:RepositoryCommit={revision}", f"-p:PathMap={source}=/_/", "--output", "package",
                cwd=source, env=env)
            packages.append(source / "package" / name)
        first, second = [package.read_bytes() for package in packages]
        if first != second:
            output.mkdir(parents=True, exist_ok=True)
            for index, package in enumerate(packages):
                shutil.copy(package, output / f"unqualified-build-{index}.zip")
            raise SystemExit("Two clean builds produced different package bytes; package is not qualified.")
        with zipfile.ZipFile(packages[0]) as package:
            contracts = {}
            for contract in sorted((ROOT / "contracts").rglob("*")):
                if not contract.is_file() or contract.relative_to(ROOT / "contracts").parts[0] not in ("auth", "terrabase"):
                    continue
                path = contract.relative_to(ROOT).as_posix()
                source_bytes = contract.read_bytes()
                if package.read(path) != source_bytes:
                    raise SystemExit(f"Packed contract bytes differ from source: {path}")
                contracts[path] = digest(source_bytes)
            assemblies = {framework: digest(package.read(f"lib/{framework}/TerraScale.DotNet.dll"))
                          for framework in ("net10.0", "net11.0")}
            nuspec = ET.fromstring(package.read("TerraScale.DotNet.nuspec"))
            namespace = {"n": "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"}
            repository = nuspec.find("n:metadata/n:repository", namespace)
            if repository is None or repository.get("commit") != revision:
                raise SystemExit("The package repository commit does not identify the qualified source.")
        consumer = temporary / "consumer"
        shutil.copytree(ROOT / "tests/TerraScale.DotNet.PackageConsumer", consumer,
                        ignore=shutil.ignore_patterns("bin", "obj", "packages.lock.json"))
        shutil.copy(ROOT / "global.json", consumer / "global.json")
        (consumer / "NuGet.config").write_text(f'''<configuration><packageSources><clear/>
          <add key="sdk" value="{html.escape(str(packages[0].parent), quote=True)}"/>
          <add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
          <packageSourceMapping><packageSource key="sdk"><package pattern="TerraScale.DotNet"/></packageSource>
          <packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>''')
        env = dict(os.environ, TerraScaleDotNetVersion=version,
                   NUGET_PACKAGES=str(temporary / "consumer-packages"),
                   NUGET_HTTP_CACHE_PATH=str(temporary / "consumer-http"))
        run("dotnet", "restore", "--use-lock-file", cwd=consumer, env=env)
        restored = Path(env["NUGET_PACKAGES"]) / "terrascale.dotnet" / version / name.lower()
        if digest(restored.read_bytes()) != digest(first):
            raise SystemExit("The installed consumer restored different SDK bytes.")
        checker = str(ROOT / "scripts/check-nuget-vulnerabilities.cs")
        run("dotnet", "restore", checker, "--locked-mode", env=env)
        audit = run("dotnet", "run", "--no-restore", "--file", checker, "--",
                    str(consumer / "TerraScale.DotNet.PackageConsumer.csproj"), env=env, capture=True)
        for framework in ("net10.0", "net11.0"):
            run("dotnet", "run", "--configuration", "Release", "--no-restore", "--framework", framework,
                f"-p:TerraScaleDotNetVersion={version}", "--", version, revision, cwd=consumer, env=env)
        output.mkdir(parents=True, exist_ok=True)
        target = output / name
        if target.exists() and target.read_bytes() != first:
            raise SystemExit("Artifact destination already contains different bytes for this package identity.")
        target.write_bytes(first)
        (output / "consumer-nuget-audit.log").write_bytes(audit)
        shutil.copy(consumer / "packages.lock.json", output / "consumer.packages.lock.json")
        metadata = {
            "package": "TerraScale.DotNet", "version": version,
            "repository": "https://github.com/TerraScale/ts-dotnet", "sourceRevision": revision,
            "sourceTree": tree, "sourceArchiveSha256": digest(archive), "file": name,
            "sourceDateEpoch": int(timestamp),
            "sha256": digest(first), "assemblySha256ByFramework": assemblies,
            "contractSha256ByPath": contracts,
            "sdk": json.loads((ROOT / "global.json").read_text())["sdk"]["version"],
            "qualification": "Two clean locked builds produced identical package bytes; installed-package graph passed the fail-closed transitive NuGet audit and HTTP fake consumer on net10.0 and net11.0. Locally built, not published or live-service qualified."
        }
        (output / "provenance.json").write_text(json.dumps(metadata, indent=2) + "\n")
        print(json.dumps(metadata, indent=2))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: pack-reproducible.py <artifact-directory-outside-worktree>")
    main(sys.argv[1])
