"""The dashboard stores its state through the TerraBase EF Core provider and reaches TerraBase's product APIs over MagicOnion."""
import xml.etree.ElementTree as ET
from pathlib import Path

root = Path(__file__).resolve().parents[2]
project = ET.parse(root / "src/TerraScale.Dashboard/TerraScale.Dashboard.csproj")
references = {item.get("Include") for item in project.findall(".//ProjectReference")}
assert references == {"../TerraScale.TerraBase.Contracts/TerraScale.TerraBase.Contracts.csproj",
                      "../TerraScale.Endpoints/TerraScale.Endpoints.csproj",
                      "../../sdk/dotnet/src/TerraScale.EntityFrameworkCore/TerraScale.EntityFrameworkCore.csproj"}, \
    "The dashboard references the shared TerraBase contracts, endpoint infrastructure and the TerraBase EF Core provider"
assert not project.findall('.//PackageReference[@Include="TerraScale.DotNet"]'), "The dashboard takes the SDK through the EF Core provider project"
sdk = ET.parse(root / "sdk/dotnet/src/TerraScale.DotNet/TerraScale.DotNet.csproj")
assert sdk.findtext(".//PackageId") == "TerraScale.DotNet", "The official SDK package identity stays unchanged"
assert ET.parse(root / "Directory.Build.props").findtext(".//TargetFramework") == "net11.0"
solution = ET.parse(root / "scripts/dashboard/TerraScale.Dashboard.slnx")
assert [item.get("Path") for item in solution.iter("Project")] == [
    "../../src/TerraBase/TerraBase.csproj",
    "../../src/TerraScale.Dashboard.Analyzers/TerraScale.Dashboard.Analyzers.csproj",
    "../../src/TerraScale.Dashboard/TerraScale.Dashboard.csproj",
    "../../src/TerraScale.Dashboard.EntityFrameworkDesign/TerraScale.Dashboard.EntityFrameworkDesign.csproj",
    "../../src/TerraScale.TerraBase.Contracts/TerraScale.TerraBase.Contracts.csproj",
    "../../src/TerraScale.TerraBase.Rpc/TerraScale.TerraBase.Rpc.csproj",
    "../../tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj"], \
    "TerraBase for the E2E suite, one application project, its EF design host, the shared TerraBase contracts and RPC services, its E2E suite and the binding analyzer"
print("PASS: dashboard stores state through the TerraBase EF Core provider on .NET 11")
