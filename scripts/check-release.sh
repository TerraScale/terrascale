#!/usr/bin/env bash
set -euo pipefail

# Tests locate auth fixtures through source paths; normalize paths only when packing.
bash scripts/check-dependencies.sh
dotnet run --no-restore --file scripts/check-nuget-vulnerabilities.tests.cs
dotnet build TerraScale.DotNet.slnx --configuration Release -p:RestoreLockedMode=true
# The Release build above already fails on analyzer warnings; whitespace is the part of dotnet format it does not cover
# (the full dotnet format re-ran the analyzers: 38 of the job's 96 seconds).
dotnet format whitespace TerraScale.DotNet.slnx --verify-no-changes
for framework in net10.0 net11.0; do
    dotnet run --project tests/TerraScale.DotNet.Tests --configuration Release --no-build --framework "$framework"
done
