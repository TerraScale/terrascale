#!/usr/bin/env bash
# Run through ts-heavy-any on beta after the retained run-b tenant is reconciled.
set -euo pipefail
umask 077
cd -- "$(dirname -- "$0")/../.."
out=$PWD/benchmark/bench/go-parity-live-20261028-c
mkdir "$out"
cp "$0" "$out/run.sh"
git rev-parse HEAD > "$out/source.txt"
sha256sum benchmark/scripts/api-overhead-tenant.py > "$out/tenant-source.sha256"
export TS_PEERS_FILE=/tmp/perf-api-admitted-peers-20261028-0915.txt
test ! -e /tmp/api-overhead-26w5i0s8
test "$(stat -c %a "$TS_PEERS_FILE")" = 600
test "$(sha256sum "$TS_PEERS_FILE" | cut -d ' ' -f 1)" = 3cd3cdd8a4f8f8cba6ed01009c56061be06a0f3468e80c25b48309a4e406067c
cp "$TS_PEERS_FILE" "$out/peers.txt"
cp /tmp/perf-api-admitted-peers-20261028-0915.json "$out/peers-source.json"
(cd benchmark && go build -trimpath -o "$out/api-bench-go" ./clients/go)
sha256sum "$out/api-bench-go" > "$out/binary.sha256"
# This must fail before a tenant exists if the binary/output contract is wrong.
if "$out/api-bench-go" parity --credentials /nonexistent --out "$out/preflight-must-not-exist.jsonl" > "$out/preflight.log" 2>&1; then
  echo 'Invalid-credentials preflight unexpectedly succeeded.' >&2; exit 1
fi
test ! -e "$out/preflight-must-not-exist.jsonl"
grep -qx 'api-bench-go: parity credentials could not be loaded' "$out/preflight.log"
reserve_fds=()
for slot in 1 2 3 4; do
  exec {reserve_fd}>"/tmp/ts-heavy-$slot.lock"
  if flock -n "$reserve_fd"; then reserve_fds+=("$reserve_fd"); fi
done
if [[ ${#reserve_fds[@]} != 3 ]]; then
  echo 'Quiet reservation unavailable; no tenant created.' >&2; exit 73
fi
python3 -u benchmark/scripts/parity-host-cpu.py "$$" > "$out/cpu-processes.jsonl" &
observer=$!
trap 'kill "$observer" 2>/dev/null || true; wait "$observer" 2>/dev/null || true' EXIT
plan='ssh -o BatchMode=yes delta python3 /home/mariogk/.local/state/perf-api-overhead/api-overhead-plan.py --ts-orch /home/mariogk/.local/state/perf-api-overhead/ts-orch --vault-id ocid1.vault.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljrlqoda2dl7r527hwngvx5k5j7lhfytswubkmx6rtg5f4mjtv4xzlq --netbird /home/mariogk/.local/state/perf-api-overhead/netbird-operator --known-hosts /home/mariogk/.local/state/perf-api-overhead/known_hosts --identity /home/mariogk/.ssh/homelab_workstation_ed25519 --node ts-gru-1'
ssh -o BatchMode=yes delta test -x /home/mariogk/.local/state/perf-api-overhead/ts-orch
date -u +%FT%TZ > "$out/host.txt"
uptime >> "$out/host.txt"
cat /proc/pressure/cpu >> "$out/host.txt"
export TERRABASE_GO_PARITY_OUT="$out"
result=0
python3 benchmark/scripts/api-overhead-tenant.py --plan-command "$plan" -- \
  bash -c 'exec "$TERRABASE_GO_PARITY_OUT/api-bench-go" parity --credentials "$TERRABASE_API_OVERHEAD_CREDENTIALS" --out "$TERRABASE_GO_PARITY_OUT/cells.jsonl"' \
  > "$out/run.log" 2>&1 || result=$?
date -u +%FT%TZ >> "$out/host.txt"
uptime >> "$out/host.txt"
cat /proc/pressure/cpu >> "$out/host.txt"
kill "$observer"
wait "$observer" 2>/dev/null || true
trap - EXIT
python3 - "$out" <<'PY'
import json, statistics, sys
from pathlib import Path
p = Path(sys.argv[1])
rows = [json.loads(line) for line in (p / 'cpu-processes.jsonl').read_text().splitlines()]
if not rows:
    raise SystemExit('Missing CPU evidence')
x = sorted(row['external_cpu'] for row in rows)
builders = {'go', 'compile', 'dotnet', 'cargo', 'rustc', 'pytest', 'testhost', 'cc1', 'ninja', 'make', 'csc', 'VBCSCompiler'}
offenders = [v for row in rows for v in row['external_top'] if v[0] >= 10 and v[2] in builders]
summary = dict(samples=len(x), external_mean=statistics.mean(x), external_p95=x[min(len(x)-1, int(.95*len(x)))], external_max=max(x), external_builders=offenders)
summary['quiet'] = summary['external_mean'] <= 25 and summary['external_p95'] <= 100 and not offenders
(p / 'quiet-summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps(summary))
if not summary['quiet']:
    raise SystemExit('Quiet control rejected; all evidence retained')
PY
tail -12 "$out/run.log"
exit "$result"
