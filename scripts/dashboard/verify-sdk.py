"""The dashboard reaches TerraBase only over MagicOnion through the shared contracts, never the official SDK."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
project = ET.parse(root / "src/TerraScale.Dashboard/TerraScale.Dashboard.csproj")
references = {item.get("Include") for item in project.findall(".//ProjectReference")}
assert references == {"../TerraScale.TerraBase.Contracts/TerraScale.TerraBase.Contracts.csproj"}, \
    "The dashboard references only the shared TerraBase contracts"
assert not project.findall('.//PackageReference[@Include="TerraScale.DotNet"]'), "The dashboard does not use the official SDK"
assert not [path for path in (root / "src/TerraScale.Dashboard").rglob("*.cs")
            if not {"bin", "obj"}.intersection(path.parts) and "TerraScale.DotNet" in path.read_text()], \
    "The dashboard does not use the official SDK"
sdk = ET.parse(root / "sdk/dotnet/src/TerraScale.DotNet/TerraScale.DotNet.csproj")
assert sdk.findtext(".//PackageId") == "TerraScale.DotNet", "The official SDK package identity stays unchanged"
assert ET.parse(root / "Directory.Build.props").findtext(".//TargetFramework") == "net11.0"
solution = ET.parse(root / "scripts/dashboard/TerraScale.Dashboard.slnx")
assert [item.get("Path") for item in solution.iter("Project")] == [
    "../../src/TerraScale.Dashboard.Analyzers/TerraScale.Dashboard.Analyzers.csproj",
    "../../src/TerraScale.Dashboard/TerraScale.Dashboard.csproj",
    "../../src/TerraScale.TerraBase.Contracts/TerraScale.TerraBase.Contracts.csproj",
    "../../src/TerraScale.TerraBase.Rpc/TerraScale.TerraBase.Rpc.csproj",
    "../../tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj"], \
    "One application project, the shared TerraBase contracts and RPC services, its E2E suite and the binding analyzer"
print("PASS: dashboard reaches TerraBase over MagicOnion contracts on .NET 11")
