#!/usr/bin/env bash
# Run through ts-heavy-any on beta; never prints synthetic credentials.
set -euo pipefail
cd -- "$(dirname -- "$0")/../.."
out=$PWD/benchmark/bench/go-parity-live-20261028-a
mkdir "$out"
cp "$0" "$out/run.sh"
git rev-parse HEAD > "$out/source.txt"
git fetch -q origin fix/api-overhead-tenant
test "$(git rev-parse FETCH_HEAD)" = 3b00282251210b85acd998cbe74c7ef6927f18f0
git show FETCH_HEAD:benchmark/scripts/api-overhead-tenant.py > benchmark/scripts/api-overhead-tenant.py
sha256sum benchmark/scripts/api-overhead-tenant.py > "$out/tenant-source.sha256"
(cd benchmark && go build -trimpath -o "$out/api-bench-go" ./clients/go)
sha256sum "$out/api-bench-go" > "$out/binary.sha256"
previous=$HOME/remote-builds/terrascale/fix-api-request-overhead-7da853e0eb5b7bd285a10942b2ab433859daba2c3d0916c0faabaa6edeca75b1/benchmark/bench/https-merged-0018-quiet-2
cp "$previous/cpu-monitor.py" "$out/"
reserve_fds=()
for slot in 1 2 3 4; do
  exec {reserve_fd}>"/tmp/ts-heavy-$slot.lock"
  if flock -n "$reserve_fd"; then reserve_fds+=("$reserve_fd"); fi
done
if [[ ${#reserve_fds[@]} != 3 ]]; then
  echo 'Quiet reservation unavailable; no tenant created.' >&2; exit 73
fi
python3 -u "$out/cpu-monitor.py" "$$" > "$out/cpu-processes.jsonl" &
observer=$!
trap 'kill "$observer" 2>/dev/null || true; wait "$observer" 2>/dev/null || true' EXIT
plan='ssh -o BatchMode=yes delta python3 /home/mariogk/.local/state/perf-api-overhead/api-overhead-plan.py --ts-orch /home/mariogk/.local/state/perf-api-overhead/ts-orch --vault-id ocid1.vault.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljrlqoda2dl7r527hwngvx5k5j7lhfytswubkmx6rtg5f4mjtv4xzlq --netbird /home/mariogk/.local/state/perf-api-overhead/netbird-operator --known-hosts /home/mariogk/.local/state/perf-api-overhead/known_hosts --identity /home/mariogk/.ssh/homelab_workstation_ed25519 --node ts-gru-1'
{ date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } > "$out/host.txt"
export TERRABASE_GO_PARITY_OUT="$out"
python3 benchmark/scripts/api-overhead-tenant.py --plan-command "$plan" -- \
  bash -c 'exec "$TERRABASE_GO_PARITY_OUT/api-bench-go" parity --credentials "$TERRABASE_API_OVERHEAD_CREDENTIALS" --out "$TERRABASE_GO_PARITY_OUT/cells.jsonl"' \
  > "$out/run.log" 2>&1 || { tail -30 "$out/run.log"; exit 1; }
{ date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } >> "$out/host.txt"
kill "$observer"
wait "$observer" 2>/dev/null || true
trap - EXIT
python3 - "$out" <<'PY'
import json,statistics,sys
from pathlib import Path
p=Path(sys.argv[1]); rows=[json.loads(s) for s in (p/'cpu-processes.jsonl').read_text().splitlines()]
if not rows:raise SystemExit('Missing CPU evidence')
x=sorted(r['external_cpu'] for r in rows); p95=x[min(len(x)-1,int(.95*len(x)))];mean=statistics.mean(x)
builders={'go','compile','dotnet','cargo','rustc','pytest','testhost','cc1','ninja','make','csc','VBCSCompiler'}
offenders=[v for r in rows for v in r['external_top'] if v[0]>=10 and v[2] in builders]
quiet=mean<=25 and p95<=100 and not offenders
result=dict(samples=len(x),external_mean=mean,external_p95=p95,external_max=max(x),external_builders=offenders,quiet=quiet)
(p/'quiet-summary.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
if not quiet:raise SystemExit('Quiet control rejected; all evidence retained')
PY
echo 'Go regional parity control and synthetic cleanup completed.'
