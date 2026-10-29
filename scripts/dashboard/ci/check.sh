#!/usr/bin/env bash
set -euo pipefail

# Restore/audit once before the no-restore build. Vulnerability data is fresh on every run.
make -f scripts/dashboard/Makefile security
make -f scripts/dashboard/Makefile lint
dotnet build scripts/dashboard/TerraScale.Dashboard.slnx --configuration Release --no-restore \
  -p:ContinuousIntegrationBuild=true -p:SourceRevisionId="$GITHUB_SHA"
python3 scripts/dashboard/ci/tree-proof.py --self-check
# Keep CI's package identical to the standalone Dockerfile's runtime contract.
test "$(grep '^FROM mcr.microsoft.com/dotnet/aspnet:' scripts/dashboard/Dockerfile)" = \
  "$(grep '^FROM mcr.microsoft.com/dotnet/aspnet:' scripts/dashboard/ci/Dockerfile.runtime)"
if grep -rniI 'fl[y]' .github/workflows | grep -viE 'fl[y]\.storage\.tigris\.dev|fl[y] storage'; then
  exit 1
fi
dotnet run --configuration Release --no-build --no-restore \
  --project tests/TerraScale.Dashboard.E2E/TerraScale.Dashboard.E2E.csproj -- \
  --treenode-filter '/*/*/*/*[Category!=Slow]' --report-trx \
  --results-directory "$PWD/test-results/tunit" --report-trx-filename fast.trx
