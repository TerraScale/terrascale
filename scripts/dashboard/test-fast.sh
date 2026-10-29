#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
dotnet restore scripts/dashboard/TerraScale.Dashboard.slnx --locked-mode
dotnet build scripts/dashboard/TerraScale.Dashboard.slnx --configuration Release --no-restore
dotnet run --project tests/TerraScale.Dashboard.E2E --configuration Release --no-build -- \
  --treenode-filter '/*/*/*/*[Category!=Slow]' --minimum-expected-tests 1
