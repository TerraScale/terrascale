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
    assert f'lib/{metadata["targetFramework"]}/TerraScale.DotNet.dll' in archive.namelist()
    if "assemblySha256" in metadata:
        assert hashlib.sha256(archive.read(f'lib/{metadata["targetFramework"]}/TerraScale.DotNet.dll')).hexdigest() == metadata["assemblySha256"], "SDK assembly digest mismatch"
versions = ET.parse(root / "Directory.Packages.props")
pins = versions.findall('.//PackageVersion[@Include="TerraScale.DotNet"]')
assert len(pins) == 1 and pins[0].get("Version") == metadata["version"]
project = ET.parse(root / "src/TerraScale.Dashboard/TerraScale.Dashboard.csproj")
assert len(project.findall('.//PackageReference[@Include="TerraScale.DotNet"]')) == 1
assert not project.findall(".//ProjectReference"), "Application must consume SDK packages"
solution = ET.parse(root / "TerraScale.Dashboard.slnx")
assert [item.get("Path") for item in solution.iter("Project")] == [
    "src/TerraScale.Dashboard/TerraScale.Dashboard.csproj",
    "tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj"], "One application project and its E2E suite"
print("PASS: one application project and recorded ts-dotnet package identity/digest")
