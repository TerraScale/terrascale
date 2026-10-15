#!/usr/bin/env bash
# Builds the tree and runs E2E test classes, one log each: e2e.sh <log dir> <class>...  (on a ts-heavy host:
#   ts-heavy-any bash benchmark/bench/2026-10-14-replication-vtest/scripts/e2e.sh /tmp/v2v5 WaveAE2ETests --repo terrascale --branch <b>)
# With TERRABASE_E2E_FLY_* exported (vtest.sh env, under vtest.sh with-key) the same classes run against ts-vtest-tb.
# A class written as Class/Test runs that test only.
set -uo pipefail
out=${1:?log dir}; shift; mkdir -p "$out"
echo "commit $(git rev-parse HEAD) host $(hostname) $(date -u +%FT%TZ) fly=${TERRABASE_E2E_FLY_URL:-local} faults=${TERRABASE_E2E_FLY_OUTAGE:-0}"
make fmt-check terrabase-build CONFIGURATION=Release > "$out/build.log" 2>&1 || { tail -40 "$out/build.log"; exit 1; }
status=0
for c in "$@"; do
  log=$out/${c//\//-}.log; filter="/*/*/$c/*"; [[ $c == */* ]] && filter="/*/*/$c*"
  echo "== $c $(date -u +%FT%TZ)"
  env -u ACTIONS_RUNTIME_TOKEN -u ACTIONS_RESULTS_URL dotnet run --project tests/TerraBase.E2E -c Release --no-build -- \
    --treenode-filter "$filter" --output detailed --no-ansi > "$log" 2>&1 || status=1
  grep -E "V2 |V5 |replication latency|^\s*(failed|passed|skipped) |total:|failed:|succeeded:|skipped:" "$log" | cut -c1-600
  echo "log $log sha256 $(sha256sum "$log" | cut -d' ' -f1)"
done
exit $status
