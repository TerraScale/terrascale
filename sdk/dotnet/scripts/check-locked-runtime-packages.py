"""Audit exact RID-only NuGet packages that dotnet list omits from normal targets."""
import json
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def run(*args, **kwargs):
    return subprocess.run(args, cwd=ROOT, check=True, **kwargs)


def main():
    locks = run("git", "ls-files", "*packages.lock.json", capture_output=True, text=True).stdout.splitlines()
    packages = {}
    for lock in locks:
        targets = json.loads((ROOT / lock).read_text())["dependencies"]
        ordinary = {(name.lower(), entry["resolved"])
                    for target, entries in targets.items() if "/" not in target
                    for name, entry in entries.items() if entry["type"] != "Project"}
        for target, entries in targets.items():
            if "/" not in target:
                continue
            for name, entry in entries.items():
                if entry["type"] == "Project" or (name.lower(), entry["resolved"]) in ordinary:
                    continue
                key = (name.lower(), entry["resolved"])
                expected = entry["contentHash"]
                if key in packages and packages[key] != expected:
                    raise SystemExit(f"Conflicting locked content hashes: {key}")
                packages[key] = expected

    if not packages:
        print("No additional RID-only locked NuGet packages.")
        return
    # These are audit-only projections of real locked packages, never compiled
    # or shipped. Separate projects preserve multiple locked versions of an ID.
    with tempfile.TemporaryDirectory(prefix="ts-dotnet-runtime-audit-") as directory:
        projects = []
        for index, ((name, version), expected) in enumerate(sorted(packages.items())):
            folder = Path(directory) / str(index)
            folder.mkdir()
            project = folder / "RuntimePackages.csproj"
            root = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
            properties = ET.SubElement(root, "PropertyGroup")
            ET.SubElement(properties, "TargetFramework").text = "net11.0"
            ET.SubElement(properties, "TreatWarningsAsErrors").text = "true"
            ET.SubElement(ET.SubElement(root, "ItemGroup"), "PackageReference", Include=name)
            central = ET.Element("Project")
            ET.SubElement(ET.SubElement(central, "PropertyGroup"), "ManagePackageVersionsCentrally").text = "true"
            ET.SubElement(ET.SubElement(central, "ItemGroup"), "PackageVersion", Include=name, Version=f"[{version}]")
            ET.ElementTree(central).write(folder / "Directory.Packages.props", encoding="unicode")
            ET.ElementTree(root).write(project, encoding="unicode")
            run("dotnet", "restore", str(project), "--use-lock-file",
                "--configfile", str(ROOT / "NuGet.config"))
            restored = json.loads((folder / "packages.lock.json").read_text())["dependencies"]["net11.0"]
            entry = next(value for key, value in restored.items() if key.lower() == name)
            if entry["resolved"] != version or entry["contentHash"] != expected:
                raise SystemExit(f"Restored package differs from committed runtime lock: {name} {version}")
            print(f"Auditing locked RID package: {name} {version}", flush=True)
            projects.append(str(project))
        run("dotnet", "run", "--no-restore", "--file",
            str(ROOT / "scripts/check-nuget-vulnerabilities.cs"), "--", *projects)


if __name__ == "__main__":
    main()
