#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
out=${1:-"$root/benchmark/bench/$(date -u +%F)-shared-process-tenancy.md"}
work=$(mktemp -d)
trap 'status=$?; if [[ $status == 0 ]]; then rm -rf "$work"; else echo "benchmark failed; logs kept in $work" >&2; fi' EXIT
dotnet build "$root/tests/TerraBase.E2E/TerraBase.E2E.csproj" --configuration Release --no-restore
for run in 1 2 3; do
  TERRABASE_BENCH_SHARED_OUT="$work/runs.jsonl" \
    dotnet run --project "$root/tests/TerraBase.E2E" --configuration Release --no-build -- \
    --treenode-filter '/*/*/SharedProcessBenchmark/*' >"$work/run-$run.log" 2>&1 || { tail -50 "$work/run-$run.log" >&2; exit 1; }
done
python3 -I "$root/benchmark/scripts/shared-process-report.py" "$work/runs.jsonl" "$(git -C "$root" rev-parse --short HEAD)" "$(hostname -s)" >"$out"
cat "$out"
