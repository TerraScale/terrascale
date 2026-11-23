import xml.etree.ElementTree as ET
from pathlib import Path

root = Path(__file__).resolve().parents[3]
project = ET.parse(root / "src/TerraScale.Dashboard/TerraScale.Dashboard.csproj")
references = {item.get("Include") for item in project.findall(".//ProjectReference")}
assert references == {"../TerraScale.Endpoints/TerraScale.Endpoints.csproj",
                      "../TerraScale.Contracts/TerraScale.Contracts.csproj",
                      "../../sdk/dotnet/src/TerraScale.DotNet/TerraScale.DotNet.csproj"}, \
    "The dashboard references the endpoint infrastructure, the shared contracts and the .NET SDK"
packages = {item.get("Include") for item in project.findall(".//PackageReference")}
retired = sorted(name for name in packages if name.startswith(("Microsoft.EntityFrameworkCore", "MagicOnion", "Grpc.", "NATS.", "TerraScale.")))
assert not retired, f"The dashboard stores state through Dapper and DbUp over the .NET SDK, not {retired}"
assert {"Dapper", "Dapper.AOT", "dbup-core"} <= packages, "The dashboard stores state through Dapper.AOT and DbUp"
sdk = ET.parse(root / "sdk/dotnet/src/TerraScale.DotNet/TerraScale.DotNet.csproj")
assert sdk.findtext(".//PackageId") == "TerraScale.DotNet", "The official SDK package identity stays unchanged"
assert ET.parse(root / "Directory.Build.props").findtext(".//TargetFramework") == "net11.0"
solution = ET.parse(root / "TerraScale.slnx")
solution_projects = {item.get("Path") for item in solution.iter("Project")}
expected = {
    "sdk/dotnet/src/TerraScale.DotNet/TerraScale.DotNet.csproj",
    "src/TerraScale.Dashboard/TerraScale.Dashboard.csproj",
    "src/TerraScale.Contracts/TerraScale.Contracts.csproj",
    "src/TerraScale.Endpoints/TerraScale.Endpoints.csproj",
    "src/TerraScale.Endpoints.Generators/TerraScale.Endpoints.Generators.csproj",
}
assert expected <= solution_projects, "TerraScale.slnx must include the dashboard build closure"
print("PASS: dashboard stores state through Dapper.AOT and DbUp over the .NET SDK on .NET 11")
