#!/usr/bin/env bash
# Run through ts-heavy-any on delta, which is also the operator host; never prints synthetic credentials.
set -euo pipefail
umask 077
cd -- "$(dirname -- "$0")/../.."
test "$(hostname -s)" = delta
out=$PWD/benchmark/bench/go-parity-live-20261028-d
mkdir "$out"
cp "$0" "$out/run.sh"
operator=/home/mariogk/.local/state/perf-api-overhead
plan_sha=4989a8aa57b8de3a108e98e2acd7e2ac43fb18bb23bb189c45d6d42345b2eb54
peers_sha=3cd3cdd8a4f8f8cba6ed01009c56061be06a0f3468e80c25b48309a4e406067c
{
  echo "head $(git rev-parse HEAD)"
  echo "tree-clients-go $(git rev-parse HEAD:benchmark/clients/go)"
  echo "tree-internal $(git rev-parse HEAD:benchmark/internal)"
  echo "go $(go version)"
} > "$out/source.txt"
sha256sum benchmark/go.mod benchmark/go.sum benchmark/scripts/api-overhead-tenant.py benchmark/scripts/api-overhead-plan.py \
  benchmark/scripts/parity-host-cpu.py "$operator/api-overhead-plan.py" > "$out/source.sha256"
# The installed operator helper must be the reviewed classifier tracked here; never overwrite it.
test "$(sha256sum < "$operator/api-overhead-plan.py" | cut -d ' ' -f 1)" = "$plan_sha"
test "$(sha256sum < benchmark/scripts/api-overhead-plan.py | cut -d ' ' -f 1)" = "$plan_sha"
test -x "$operator/ts-orch"
export TS_PEERS_FILE=/tmp/perf-api-admitted-peers-20261028-live-d.txt
test "$(stat -c %a "$TS_PEERS_FILE")" = 600
test "$(sha256sum < "$TS_PEERS_FILE" | cut -d ' ' -f 1)" = "$peers_sha"
cp "$TS_PEERS_FILE" "$out/peers.txt"
cp /tmp/perf-api-admitted-peers-20261028-live-d.json "$out/peers-source.json"
cp /tmp/perf-api-auth-region-state-20261028-live-d.json "$out/auth-region-state.json"
# No retained tenant may exist before this run owns a new one.
if find /tmp -maxdepth 1 -type d -name 'api-overhead-*' | grep -q .; then
  echo 'Retained tenant directory present; no tenant created.' >&2; exit 74
fi
(cd benchmark && go build -trimpath -o "$out/api-bench-go" ./clients/go)
sha256sum "$out/api-bench-go" > "$out/binary.sha256"
go version -m "$out/api-bench-go" | grep -E 'terrascale.tech/sdk|TerraScale/ts-go' > "$out/sdk.txt"
grep -q 'v0.0.0-20261028013942-07ffb51e1d66' "$out/sdk.txt"
# This must fail before a tenant exists if the binary/output contract is wrong.
if "$out/api-bench-go" parity --credentials /nonexistent --out "$out/preflight-must-not-exist.jsonl" > "$out/preflight.log" 2>&1; then
  echo 'Invalid-credentials preflight unexpectedly succeeded.' >&2; exit 1
fi
test ! -e "$out/preflight-must-not-exist.jsonl"
grep -qx 'api-bench-go: parity credentials could not be loaded' "$out/preflight.log"
for check in healthz readyz; do
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "https://auth.terrascale.tech/$check")
  echo "$(date -u +%FT%TZ) public auth $check $code" >> "$out/auth-ready.txt"
  test "$code" = 200
done
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
plan="python3 $operator/api-overhead-plan.py --ts-orch $operator/ts-orch --vault-id ocid1.vault.oc1.sa-saopaulo-1.ffvnrldqaaexa.abtxeljrlqoda2dl7r527hwngvx5k5j7lhfytswubkmx6rtg5f4mjtv4xzlq --netbird $operator/netbird-operator --known-hosts $operator/known_hosts --identity /home/mariogk/.ssh/homelab_workstation_ed25519 --node ts-gru-1"
{ date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } > "$out/host.txt"
export TERRABASE_GO_PARITY_OUT="$out"
result=0
python3 benchmark/scripts/api-overhead-tenant.py --plan-command "$plan" -- \
  bash -c 'exec "$TERRABASE_GO_PARITY_OUT/api-bench-go" parity --credentials "$TERRABASE_API_OVERHEAD_CREDENTIALS" --out "$TERRABASE_GO_PARITY_OUT/cells.jsonl"' \
  > "$out/run.log" 2>&1 || result=$?
{ date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } >> "$out/host.txt"
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
grep -E '^(Synthetic|API overhead|Cleanup|api-bench-go)' "$out/run.log" | sed -E 's#private directory: .*#private directory: <redacted>#' || true
echo "tenant-exit $result"
exit "$result"
