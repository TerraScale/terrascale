#!/usr/bin/env bash
set -euo pipefail

# Tests locate auth fixtures through source paths; normalize paths only when packing.
dotnet build TerraScale.DotNet.slnx --configuration Release -p:RestoreLockedMode=true
dotnet format TerraScale.DotNet.slnx --verify-no-changes --no-restore
dotnet restore scripts/check-nuget-vulnerabilities.cs --locked-mode
dotnet run --no-restore --file scripts/check-nuget-vulnerabilities.cs -- src/TerraScale.DotNet/TerraScale.DotNet.csproj tests/TerraScale.DotNet.Tests/TerraScale.DotNet.Tests.csproj
for framework in net10.0 net11.0; do
    dotnet run --project tests/TerraScale.DotNet.Tests --configuration Release --no-build --framework "$framework"
done
