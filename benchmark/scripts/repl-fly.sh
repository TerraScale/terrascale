#!/usr/bin/env bash
# The replication benchmark on Fly: one temporary load machine per region (gru, sjc, fra) in a throwaway app, each
# writing its share to its own region of ts-terrabase; then verification from this host. The app and its machines are
# destroyed on exit, whatever happens. Run from the repository root (make benchmark-repl-fly):
#
#   TS_BENCH_FLY_TARGETS=/path/targets.json FLY_API_TOKEN=$(flyctl auth token) benchmark/scripts/repl-fly.sh [repl-bench flags]
#
# The targets file (0600, from benchmark/scripts/repl-fly-tenant.sh) holds the API key. It reaches the machines as a Fly
# secret of the throwaway app, never as a command-line argument. BENCH_VM_SIZE picks the load machines' size.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
targets=${TS_BENCH_FLY_TARGETS:?TS_BENCH_FLY_TARGETS must name the Fly targets file}
: "${FLY_API_TOKEN:?FLY_API_TOKEN must be set (flyctl auth token): the run reads machine metrics with it}"
org=${BENCH_FLY_ORG:-terrascale}
size=${BENCH_VM_SIZE:-shared-cpu-4x}
app="ts-bench-$(date -u +%Y%m%d%H%M%S)"
regions=(gru sjc fra)
work=$(mktemp -d)
created=0

cleanup() {
  status=$?
  if [[ $created == 1 ]]; then
    echo "destroying $app" >&2
    flyctl apps destroy "$app" --yes >&2 || echo "WARNING: destroy $app by hand: flyctl apps destroy $app --yes" >&2
  fi
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

cd "$root/benchmark"
CGO_ENABLED=0 go build -trimpath -o "$work/repl-bench" ./cmd/repl-bench
commit=$(git rev-parse --short=12 HEAD)
printf 'FROM alpine:3.22\nCOPY repl-bench /repl-bench\n' > "$work/Dockerfile"
printf 'app = "%s"\n' "$app" > "$work/fly.toml"

echo "setup: collections and conflict documents" >&2
"$work/repl-bench" --step setup --targets "$targets" --state "$work/state.json" "$@"

flyctl apps create "$app" --org "$org" >&2
created=1
flyctl secrets set --app "$app" --stage TARGETS_JSON="$(cat "$targets")" STATE_JSON="$(cat "$work/state.json")" > /dev/null
(cd "$work" && flyctl deploy --app "$app" --config fly.toml --build-only --push --image-label bench --remote-only >&2)

declare -A machine
began=$(date +%s)
for region in "${regions[@]}"; do
  machine[$region]=$(flyctl machine run "registry.fly.io/$app:bench" --app "$app" --region "$region" --vm-size "$size" \
    --name "load-$region" --restart no --autostop off --detach \
    --file-secret /work/targets.json=TARGETS_JSON --file-secret /work/state.json=STATE_JSON \
    -- sh -c "/repl-bench --step load --only $region --targets /work/targets.json --state /work/state.json --partial /work/partial.json --commit $commit $* > /work/load.log 2>&1; touch /work/done; sleep infinity" \
    | sed -n 's/.*Machine ID: \([0-9a-f]*\).*/\1/p')
  echo "load machine ${machine[$region]} in $region" >&2
done

for region in "${regions[@]}"; do
  until flyctl ssh console --app "$app" --machine "${machine[$region]}" -C "test -f /work/done" > /dev/null 2>&1; do sleep 15; done
  flyctl ssh console --app "$app" --machine "${machine[$region]}" -C "tail -3 /work/load.log" >&2 || true
  flyctl ssh sftp get --app "$app" --machine "${machine[$region]}" /work/partial.json "$work/$region.json" >&2
done
loaded=$(date +%s)

echo "verify: every region, from this host" >&2
status=0
"$work/repl-bench" --step verify --targets "$targets" --state "$work/state.json" --commit "$commit" --out "$root/benchmark/bench" \
  --partials "$work/gru.json,$work/sjc.json,$work/fra.json" \
  --not-tested "load machines: 3 x $size, $((loaded - began)) s from the first start to the last partial, plus one remote image build" \
  "$@" || status=$?
exit "$status"
