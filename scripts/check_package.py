#!/usr/bin/env python3
"""Build and run a standalone NuGet consumer without SDK project references."""

from pathlib import Path
import hashlib
import json
import subprocess
import tempfile
import uuid


ROOT = Path(__file__).resolve().parents[1]


def run(*arguments: str, cwd: Path = ROOT) -> None:
    subprocess.run(arguments, cwd=cwd, check=True)


def main() -> None:
    manifest_path = ROOT / "contracts" / "v1" / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest_sha256 = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
    openapi_pin = next(
        artifact["blake3"]
        for artifact in manifest["artifacts"]
        if artifact["path"] == "openapi.json"
    )
    with tempfile.TemporaryDirectory(prefix="terrascale-dotnet-consumer-") as directory:
        workspace = Path(directory)
        feed = workspace / "feed"
        consumer = workspace / "consumer"
        consumer.mkdir()
        version = f"0.1.0-conformance.{uuid.uuid4().hex}"
        run("dotnet", "pack", "src/TerraScale.DotNet/TerraScale.DotNet.csproj",
            "--configuration", "Release", "--no-build", "--output", str(feed),
            f"-p:PackageVersion={version}")
        (consumer / "Consumer.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TerraScale.DotNet" Version="{version}" />
  </ItemGroup>
</Project>
""")
        (consumer / "Program.cs").write_text("""using System.Text.Json;
using TerraScale.DotNet;
using TerraScale.DotNet.Generated;

using var http = new HttpClient();
var client = new TerraScaleClient(http, new TerraScaleClientOptions
{
    BaseAddress = new Uri("https://api.terrascale.tech"),
    SessionConsistency = TerraScaleSessionConsistency.Automatic,
});
var parameters = TerraScaleSqlParameters.FromOrdinal(long.MaxValue, long.MinValue);
if (parameters["1"].GetInt64() != long.MaxValue || parameters["2"].GetInt64() != long.MinValue)
    throw new InvalidOperationException("Packaged SQL parameters lost 64-bit precision.");
if (client.Documents is null || client.Reactive is null || client.Management is null)
    throw new InvalidOperationException("Packaged client surfaces are missing.");
var catalog = JsonSerializer.Deserialize<TerraScaleRegionCatalog>("{\\\"regions\\\":[{\\\"code\\\":\\\"IAD\\\",\\\"name\\\":\\\"Ashburn\\\",\\\"country\\\":\\\"US\\\",\\\"latitude\\\":38.9,\\\"longitude\\\":-77,\\\"available\\\":true}],\\\"hosts\\\":{\\\"IAD\\\":\\\"iad.api.terrascale.tech\\\"}}") ?? throw new InvalidOperationException("Packaged region catalog did not deserialize.");
if (catalog.Regions.Count != 1 || catalog.Regions[0].Code != "IAD" || catalog.Hosts["IAD"] != "iad.api.terrascale.tech")
    throw new InvalidOperationException("Packaged region catalog lost the published regions/hosts wire.");
if (!string.Equals(TerraScaleContractPin.ManifestSha256, "{manifest_sha256}", StringComparison.Ordinal) || !string.Equals(TerraScaleContractPin.Blake3_OpenapiJson, "{openapi_pin}", StringComparison.Ordinal))
    throw new InvalidOperationException("Packaged ContractPin does not match the installed manifest.");
if (TerraScaleContractRoutes.Find("execute_sql").Method != "POST")
    throw new InvalidOperationException("Packaged generated contract is missing.");
Console.WriteLine("Standalone NuGet consumer passed.");
""".replace("{manifest_sha256}", manifest_sha256).replace("{openapi_pin}", openapi_pin))
        run("dotnet", "restore", "--source", str(feed), "--source", "https://api.nuget.org/v3/index.json",
            "--packages", str(workspace / "packages"), cwd=consumer)
        run("dotnet", "run", "--configuration", "Release", "--no-restore", cwd=consumer)


if __name__ == "__main__":
    main()
