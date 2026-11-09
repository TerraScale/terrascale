#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
scripts/vendor/verify-turso-native.sh src/vendor/Turso.Data.Sqlite/native >/dev/null 2>&1 || scripts/vendor/download-turso-native.sh src/vendor/Turso.Data.Sqlite/native
dotnet restore scripts/dashboard/TerraScale.Dashboard.slnx --locked-mode
dotnet build scripts/dashboard/TerraScale.Dashboard.slnx --configuration Release --no-restore
scripts/ci/release-only.sh
dotnet run --project tests/TerraScale.Dashboard.E2E --configuration Release --no-build -- \
  --treenode-filter '/*/*/*/*[Category!=Slow]' --minimum-expected-tests 1
