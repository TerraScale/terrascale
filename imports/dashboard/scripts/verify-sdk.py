"""Check that the dashboard application consumes the recorded SDK bytes."""
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parents[1]
metadata = json.loads((root / "vendor/dotnet/provenance.json").read_text())
package = root / "vendor/dotnet" / metadata["file"]
assert hashlib.sha256(package.read_bytes()).hexdigest() == metadata["sha256"], "SDK package digest mismatch"
with zipfile.ZipFile(package) as archive:
    nuspecs = [name for name in archive.namelist() if name.endswith(".nuspec")]
    assert len(nuspecs) == 1, "SDK package must have one nuspec"
    nuspec = ET.fromstring(archive.read(nuspecs[0]))
    ns = {"n": "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"}
    identity = nuspec.find("n:metadata", ns)
    assert identity is not None
    assert identity.findtext("n:id", namespaces=ns) == metadata["package"]
    assert identity.findtext("n:version", namespaces=ns) == metadata["version"]
    repository = identity.find("n:repository", ns)
    assert repository is not None
    assert repository.get("url") == metadata["repository"]
    assert repository.get("commit") == metadata["sourceRevision"]
    assemblies = metadata["assemblySha256ByFramework"]
    assert set(assemblies) == {"net10.0", "net11.0"}, "SDK must qualify both supported frameworks"
    for framework, digest in assemblies.items():
        assert hashlib.sha256(archive.read(f"lib/{framework}/TerraScale.DotNet.dll")).hexdigest() == digest, "SDK assembly digest mismatch"
    if "contractSha256ByPath" in metadata:
        contracts = metadata["contractSha256ByPath"]
        packed = {name for name in archive.namelist() if name.startswith(("contracts/auth/", "contracts/terrabase/")) and not name.endswith("/")}
        assert packed and set(contracts) == packed, "SDK contract provenance must cover every packed contract"
        for path, digest in contracts.items():
            assert hashlib.sha256(archive.read(path)).hexdigest() == digest, "SDK contract bytes mismatch"
versions = ET.parse(root / "Directory.Packages.props")
pins = versions.findall('.//PackageVersion[@Include="TerraScale.DotNet"]')
assert len(pins) == 1 and pins[0].get("Version") == metadata["version"]
project = ET.parse(root / "src/TerraScale.Dashboard/TerraScale.Dashboard.csproj")
framework = project.findtext(".//TargetFramework") or ET.parse(root / "Directory.Build.props").findtext(".//TargetFramework")
assert framework in assemblies, "SDK must support the application's framework"
assert len(project.findall('.//PackageReference[@Include="TerraScale.DotNet"]')) == 1
assert not project.findall(".//ProjectReference"), "Application must consume SDK packages"
solution = ET.parse(root / "TerraScale.Dashboard.slnx")
assert [item.get("Path") for item in solution.iter("Project")] == [
    "src/TerraScale.Dashboard/TerraScale.Dashboard.csproj",
    "tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj"], "One application project and its E2E suite"
print("PASS: one application project and recorded ts-dotnet package identity/digest")
