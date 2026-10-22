#!/usr/bin/env bash
set -euo pipefail

# Tests locate auth fixtures through source paths; normalize paths only when packing.
dotnet build TerraScale.DotNet.slnx --configuration Release -p:RestoreLockedMode=true
# The Release build above already fails on analyzer warnings; whitespace is the part of dotnet format it does not cover
# (the full dotnet format re-ran the analyzers: 38 of the job's 96 seconds).
dotnet format whitespace TerraScale.DotNet.slnx --verify-no-changes
dotnet restore scripts/check-nuget-vulnerabilities.cs --locked-mode
dotnet run --no-restore --file scripts/check-nuget-vulnerabilities.cs -- src/TerraScale.DotNet/TerraScale.DotNet.csproj tests/TerraScale.DotNet.Tests/TerraScale.DotNet.Tests.csproj
for framework in net10.0 net11.0; do
    dotnet run --project tests/TerraScale.DotNet.Tests --configuration Release --no-build --framework "$framework"
done
