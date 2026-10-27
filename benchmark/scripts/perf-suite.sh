#!/usr/bin/env bash
# Run through ts-heavy-any on a quiet dev host; evidence is append-only.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"
if [[ ${1:-} == live ]]; then
  shift
  exec bash benchmark/scripts/perf-live.sh "$@"
fi
out=$(realpath -m "${1:-benchmark/bench/perf-$(date -u +%Y%m%dT%H%M%SZ)}")
mkdir "$out"
python3 - "$out/metadata.json" <<'PY'
import datetime, json, os, pathlib, platform, subprocess, sys
data = dict(sha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
            main_sha=subprocess.check_output(['git', 'rev-parse', 'origin/main'], text=True).strip(),
            host=platform.node(), platform=platform.platform(), cpus=os.cpu_count(),
            at=datetime.datetime.now(datetime.timezone.utc).isoformat(), load_before=os.getloadavg(),
            dotnet=subprocess.check_output(['dotnet', '--version'], text=True).strip(),
            cpu_model=next(line.strip() for line in pathlib.Path('/proc/cpuinfo').read_text().splitlines() if line.startswith('model name')),
            cpu_pressure_before=pathlib.Path('/proc/pressure/cpu').read_text())
pathlib.Path(sys.argv[1]).write_text(json.dumps(data, indent=2) + '\n')
PY
bash scripts/vendor/verify-turso-native.sh src/vendor/Turso.Data.Sqlite/native || bash scripts/vendor/download-turso-native.sh
export TERRABASE_VS_TURSO_LANES=turso,turso-cdc,engine,api,api-query,api-batch,sql,pg,hrana,wire,tcp-echo
export TERRABASE_VS_TURSO_WORKLOADS=read,write
export TERRABASE_VS_TURSO_REPEATS=3 TERRABASE_VS_TURSO_SECONDS=5 TERRABASE_VS_TURSO_CONCURRENCY=1,16
bash benchmark/dotnet/VsTurso/run.sh "$out/local.raw" >"$out/run.log" 2>&1 &
job=$!
trap 'kill "$job" 2>/dev/null || true' EXIT
while kill -0 "$job" 2>/dev/null; do
  python3 - "$out/load.jsonl" <<'PY'
import json, os, pathlib, sys, time
with open(sys.argv[1], 'a') as stream:
    stream.write(json.dumps(dict(at=time.time(), load=os.getloadavg(),
                                cpu=pathlib.Path('/proc/stat').read_text().splitlines()[0],
                                pressure=pathlib.Path('/proc/pressure/cpu').read_text())) + '\n')
PY
  sleep 5
done
status=0
wait "$job" || status=$?
trap - EXIT
python3 benchmark/scripts/perf-report.py "$out" || status=1
cat "$out/summary.md"
exit "$status"
