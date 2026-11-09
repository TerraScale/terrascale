#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet restore TerraScale.DotNet.slnx --locked-mode
dotnet build TerraScale.DotNet.slnx --configuration Release --no-restore
pids=()
for framework in net10.0 net11.0; do
    python3 ../../scripts/ci/fast-budget.py "sdk-dotnet-$framework" -- \
        dotnet run --project tests/TerraScale.DotNet.Tests --configuration Release --no-build --framework "$framework" -- &
    pids+=("$!")
    python3 ../../scripts/ci/fast-budget.py "sdk-dotnet-efcore-$framework" -- \
        dotnet run --project tests/TerraScale.EntityFrameworkCore.Tests --configuration Release --no-build --framework "$framework" -- &
    pids+=("$!")
done
status=0
for pid in "${pids[@]}"; do
    wait "$pid" || status=$?
done
exit "$status"
