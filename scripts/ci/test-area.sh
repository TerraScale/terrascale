#!/usr/bin/env bash
set -euo pipefail
scripts/vendor/verify-turso-native.sh src/vendor/Turso.Data.Sqlite/native

case "$1:$2" in
  fast:libs)
    make -o restore fmt-check libs-build SOLUTION=libs.slnf CONFIGURATION=Release
    make -o restore libs-check libs-test CONFIGURATION=Release
    ;;
  build:terrabase|fast:terrabase)
    make -o restore fmt-check SOLUTION=terrabase.slnf CONFIGURATION=Release
    # Fast shards consume these three executables. Slow builds its independent graph in its own job; building it here
    # also compiles ts-auth through its interoperability fixture and contributes no required Fast coverage.
    for project in TerraBase.Fast Turso.Data.Sqlite.Tests TerraBase.E2E; do
      dotnet build "tests/$project/$project.csproj" --configuration Release --no-restore
    done
    make -o restore -o terrabase-build terrabase-check CONFIGURATION=Release
    dotnet format whitespace src/TerraBase.Analyzers.BuildCheck/TerraBase.Analyzers.BuildCheck.csproj --verify-no-changes --no-restore
    dotnet publish src/TerraBase --configuration Release --runtime linux-x64 --self-contained --no-restore --output "$RUNNER_TEMP/terrabase-publish"
    scripts/vendor/test-turso-native-singlefile.sh
    [[ $1 == build ]] && exit 0
    make -o restore terrabase-test-fast CONFIGURATION=Release
    make -j2 -o restore -o terrabase-build terrabase-e2e-local CONFIGURATION=Release
    ;;
  *)
    echo 'Expected fast libs|terrabase or build terrabase' >&2
    exit 2
    ;;
esac
