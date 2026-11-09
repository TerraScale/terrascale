#!/usr/bin/env bash
set -euo pipefail
scripts/vendor/verify-turso-native.sh src/vendor/Turso.Data.Sqlite/native

case "$1:$2" in
  build:libs|fast:libs)
    make -o restore fmt-check libs-build SOLUTION=libs.slnf CONFIGURATION=Release
    make -o restore -o libs-build libs-check CONFIGURATION=Release
    [[ $1 == build ]] && exit 0
    make -o restore libs-test CONFIGURATION=Release
    ;;
  build:terrabase|fast:terrabase)
    publish_pid=
    publish_log=$(mktemp)
    build_logs=$(mktemp -d)
    if [[ $1 == fast ]]; then
      dotnet publish src/TerraBase --configuration Release --runtime linux-x64 --self-contained --no-restore -p:PublishReadyToRun=false --output "$RUNNER_TEMP/terrabase-publish" >"$publish_log" 2>&1 &
      publish_pid=$!
    fi
    cleanup() {
      if [[ -n $publish_pid ]]; then kill "$publish_pid" 2>/dev/null || true; fi
      rm -f "$publish_log"
      rm -rf "$build_logs"
    }
    trap cleanup EXIT
    make -o restore fmt-check SOLUTION=terrabase.slnf CONFIGURATION=Release
    build_pids=()
    for project in TerraBase.Fast Turso.Data.Sqlite.Tests TerraBase.E2E; do
      dotnet build "tests/$project/$project.csproj" --configuration Release --no-restore >"$build_logs/$project.log" 2>&1 &
      build_pids+=("$!")
    done
    build_status=0
    for pid in "${build_pids[@]}"; do wait "$pid" || build_status=1; done
    if [[ $build_status -ne 0 ]]; then
      cat "$build_logs"/*.log
      exit 1
    fi
    make -o restore -o terrabase-build terrabase-check CONFIGURATION=Release
    dotnet format whitespace src/TerraBase.Analyzers.BuildCheck/TerraBase.Analyzers.BuildCheck.csproj --verify-no-changes --no-restore
    scripts/vendor/test-turso-native-singlefile.sh
    if [[ -n $publish_pid ]]; then
      wait "$publish_pid" || { publish_status=$?; publish_pid=; cat "$publish_log"; exit "$publish_status"; }
      publish_pid=
    fi
    [[ $1 == build ]] && exit 0
    make -o restore terrabase-test-fast CONFIGURATION=Release
    make -j2 -o restore -o terrabase-build terrabase-e2e-local CONFIGURATION=Release
    ;;
  *)
    echo 'Expected fast libs|terrabase or build libs|terrabase' >&2
    exit 2
    ;;
esac
