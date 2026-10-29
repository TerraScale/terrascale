#!/usr/bin/env bash
# The same checked builds, packaging checks and suites as Dockerfile's test-* stages.
# Locked restore runs in its own CI step; only package downloads are cached, never test results.
set -euo pipefail
scripts/vendor/verify-turso-native.sh src/vendor/Turso.Data.Sqlite/native

case "$1:$2" in
  fast:libs)
    make -o restore fmt-check libs-build SOLUTION=libs.slnf CONFIGURATION=Release
    make -o restore libs-check libs-test CONFIGURATION=Release
    ;;
  fast:terrabase)
    make -o restore fmt-check terrabase-check SOLUTION=terrabase.slnf CONFIGURATION=Release
    dotnet format whitespace src/TerraBase.Analyzers.BuildCheck/TerraBase.Analyzers.BuildCheck.csproj --verify-no-changes --no-restore
    dotnet publish src/TerraBase --configuration Release --runtime linux-x64 --self-contained --no-restore --output "$RUNNER_TEMP/terrabase-publish"
    make -o restore terrabase-test-fast CONFIGURATION=Release
    scripts/vendor/test-turso-native-singlefile.sh
    make -j2 -o restore -o terrabase-build terrabase-e2e-local CONFIGURATION=Release
    ;;
  *)
    echo 'Expected fast and libs|terrabase' >&2
    exit 2
    ;;
esac
