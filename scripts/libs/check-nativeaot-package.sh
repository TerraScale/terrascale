#!/usr/bin/env bash
# Run after packing; use `nice -n 19 bash scripts/check-nativeaot-package.sh` locally.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
feed="$(realpath "${1:-$root/artifacts/packages}")"
shopt -s nullglob
packages=("$feed"/TerraScale.Endpoints.*.nupkg)
if [ "${#packages[@]}" -ne 1 ]; then
  echo "Expected exactly one TerraScale.Endpoints .nupkg in $feed" >&2
  exit 1
fi
version=$(unzip -p "${packages[0]}" '*.nuspec' | sed -n 's:.*<version>\(.*\)</version>.*:\1:p')
test -n "$version"

# Outside the checkout: no inherited build props, central versions, or project references.
consumer=$(mktemp -d /tmp/ts-libs-nativeaot.XXXXXX)
trap 'rm -rf -- "$consumer"' EXIT
cp "$root/global.json" "$consumer/global.json"
export NUGET_PACKAGES="$consumer/packages"
cat > "$consumer/nuget.config" <<'EOF'
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="packed"><package pattern="TerraScale.Endpoints" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
EOF
# Let NuGet escape XML attribute characters in the filesystem path.
dotnet nuget add source "$feed" --name packed --configfile "$consumer/nuget.config"
cat > "$consumer/Consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <ILLinkTreatWarningsAsErrors>true</ILLinkTreatWarningsAsErrors>
    <IlcTreatWarningsAsErrors>true</IlcTreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TerraScale.Endpoints" Version="$version" />
  </ItemGroup>
</Project>
EOF
cat > "$consumer/Program.cs" <<'EOF'
using System.Text;
using System.Text.Json.Serialization;
using TerraScale.Endpoints;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, SmokeJsonContext.Default));
// This overload exists only when the generator bundled in the package loads.
builder.Services.AddTerraScaleEndpoints();

await using var app = builder.Build();
app.MapTerraScaleEndpoints();
app.Urls.Add("http://127.0.0.1:0");
await app.StartAsync();

using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
using var body = new StringContent("""{"value":"nativeaot"}""", Encoding.UTF8, "application/json");
using var response = await client.PostAsync("/echo", body);
response.EnsureSuccessStatusCode();
var actual = await response.Content.ReadAsStringAsync();
if (actual != """{"value":"nativeaot"}""")
{
    throw new InvalidOperationException($"Unexpected endpoint response: {actual}");
}

await app.StopAsync();
Console.WriteLine("NativeAOT package check passed: generated /echo endpoint returned the expected JSON.");

public sealed class EchoEndpoint : Endpoint<SmokeMessage, SmokeMessage>
{
    public override void Configure() => Post("/echo");
    public override ValueTask<SmokeMessage> HandleAsync(SmokeMessage request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request);
}

public sealed record SmokeMessage(string Value);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SmokeMessage))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;
EOF

cd "$consumer"
dotnet restore Consumer.csproj --runtime linux-x64 --configfile nuget.config --nologo
dotnet publish Consumer.csproj --configuration Release --runtime linux-x64 --no-restore --nologo --output publish
timeout 30s ./publish/Consumer
