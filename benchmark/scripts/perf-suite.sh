#!/usr/bin/env bash
# Run through ts-heavy-any on a quiet dev host; evidence is append-only.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"
if [[ ${1:-} == report ]]; then
  evidence=${2:?evidence directory}
  status=0
  python3 benchmark/scripts/perf-report.py "$evidence/local" "${@:3}" || status=1
  python3 benchmark/scripts/perf-live-report.py "$evidence" || status=1
  cat "$evidence/local/summary.md" "$evidence/live-summary.md"
  exit "$status"
fi
if [[ ${1:-} == cleanup ]]; then
  shift
  exec bash benchmark/scripts/perf-live.sh "$@" cleanup
fi
if [[ ${1:-} == live ]]; then
  shift
  exec bash benchmark/scripts/perf-live.sh "$@"
fi
out=$(realpath -m "${1:-benchmark/bench/perf-$(date -u +%Y%m%dT%H%M%SZ)}")
exec python3 benchmark/scripts/perf-quiet.py "$out"
