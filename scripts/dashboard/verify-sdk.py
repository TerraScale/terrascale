"""Keep dashboard and its official SDK on the same source revision."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
project_path = root / "src/TerraScale.Dashboard/TerraScale.Dashboard.csproj"
project = ET.parse(project_path)
references = project.findall('.//ProjectReference[@Include="../../sdk/dotnet/src/TerraScale.DotNet/TerraScale.DotNet.csproj"]')
assert len(references) == 1, "Dashboard must consume the SDK source from its own monorepo revision"
assert not project.findall('.//PackageReference[@Include="TerraScale.DotNet"]'), "Do not restore a different SDK revision from a package feed"
sdk = ET.parse(root / "sdk/dotnet/src/TerraScale.DotNet/TerraScale.DotNet.csproj")
assert sdk.findtext(".//PackageId") == "TerraScale.DotNet", "The official SDK package identity stays unchanged"
assert ET.parse(root / "Directory.Build.props").findtext(".//TargetFramework") == "net11.0"
solution = ET.parse(root / "scripts/dashboard/TerraScale.Dashboard.slnx")
assert [item.get("Path") for item in solution.iter("Project")] == [
    "../../src/TerraScale.Dashboard.Analyzers/TerraScale.Dashboard.Analyzers.csproj",
    "../../src/TerraScale.Dashboard/TerraScale.Dashboard.csproj",
    "../../tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj"], "One application project, its E2E suite and the generated-binding analyzer"
print("PASS: dashboard uses the official in-repo SDK source and .NET 11")
