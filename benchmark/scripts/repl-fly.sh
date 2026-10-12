#!/usr/bin/env bash
# The replication benchmark on Fly, one load machine per region (gru, sjc, fra), against ts-terrabase. Run from the
# repository root after the benchmark credential is installed (repl-fly-tenant.sh install, run by a user):
#
#   FLY_API_TOKEN=$(flyctl auth token) make benchmark-repl-fly REPL_ARGS='--docs 1000000 --batch 16'
#
# Machines run in app ts-bench, which holds the Fly secret TS_BENCH_API_KEY and has no machines between runs. Every
# step that uses the key runs on a Fly machine, which reads it from its environment. This host only moves the state
# (collection and conflict ids), the partials (ids and hashes) and the report, and adds machine metrics with the
# read-only Fly token. All machines are destroyed on exit, whatever happens. BENCH_VM_SIZE picks the load machines' size.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
: "${FLY_API_TOKEN:?FLY_API_TOKEN must be set (flyctl auth token): machine metrics are read with it}"
bench_app=${BENCH_FLY_APP:-ts-bench}
terrabase=${BENCH_TERRABASE_APP:-ts-terrabase}
repo=${BENCH_GITHUB_REPO:-TerraScale/terrascale}
size=${BENCH_VM_SIZE:-shared-cpu-4x}
name=${BENCH_NAME:-repl-fly}
regions=(gru sjc fra)
work=$(mktemp -d)

die() { echo "repl-fly: $*" >&2; exit 1; }
cleanup() {
  local status=$? id
  for id in $(flyctl machine list --app "$bench_app" --json 2> /dev/null | jq -r '.[].id'); do
    flyctl machine destroy "$id" --app "$bench_app" --force > /dev/null 2>&1 || echo "WARNING: destroy machine $id of $bench_app by hand" >&2
  done
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

project=$(gh variable get TS_BENCH_FLY_PROJECT --repo "$repo" 2> /dev/null) || die "no benchmark tenant: run repl-fly-tenant.sh install first"
flyctl secrets list --app "$bench_app" --json | jq -e 'any(.[]; .Name == "TS_BENCH_API_KEY")' > /dev/null || die "the Fly secret TS_BENCH_API_KEY is missing on $bench_app"
[[ $(flyctl machine list --app "$bench_app" --json | jq length) == 0 ]] || die "$bench_app already has machines: another run is going, or one was left behind"

# The targets carry no key.
machines=$(flyctl machine list --app "$terrabase" --json | jq -c 'map({(.region): "\(.config.guest.cpu_kind)-cpu-\(.config.guest.cpus)x \(.config.guest.memory_mb) MB"}) | add')
jq -n --arg p "$project" --arg a "$terrabase" --argjson m "$machines" \
  '{kind: "fly", project: $p, database: "bench", app: $a, org: "terrascale", machines: $m,
    regions: (["gru", "sjc", "fra"] | map({(.): {url: "https://\($a).fly.dev", pin: true}}) | add)}' > "$work/targets.json"

cd "$root/benchmark"
commit=$(git rev-parse --short=12 HEAD)
CGO_ENABLED=0 go build -trimpath -o "$work/repl-bench" ./cmd/repl-bench
printf 'FROM alpine:3.22\nCOPY repl-bench /repl-bench\n' > "$work/Dockerfile"
printf 'app = "%s"\n' "$bench_app" > "$work/fly.toml"
label="bench-$commit-$(date -u +%Y%m%d%H%M%S)"
(cd "$work" && flyctl deploy --app "$bench_app" --config fly.toml --build-only --push --image-label "$label" --remote-only >&2)

# run_machine <role> <region> <shell command>: a detached machine with the targets (and the state, once it exists).
run_machine() {
  local files=(--file-literal "/work/targets.json=$(base64 -w0 "$work/targets.json")")
  [[ -f "$work/state.json" ]] && files+=(--file-literal "/work/state.json=$(base64 -w0 "$work/state.json")")
  flyctl machine run "registry.fly.io/$bench_app:$label" --app "$bench_app" --region "$2" --vm-size "$size" --name "bench-$1" \
    --restart no --autostop off --detach "${files[@]}" -- sh -c "$3" | sed -n 's/.*Machine ID: \([0-9a-f]*\).*/\1/p'
}
wait_for() { until flyctl ssh console --app "$bench_app" --machine "$1" -C "test -f $2" > /dev/null 2>&1; do sleep 15; done; }
args="--targets /work/targets.json --state /work/state.json --commit $commit"

echo "setup: collections and conflict documents" >&2
setup=$(run_machine setup gru "/repl-bench --step setup $args $* > /work/setup.log 2>&1; touch /work/done; sleep infinity")
wait_for "$setup" /work/done
flyctl ssh sftp get --app "$bench_app" --machine "$setup" /work/state.json "$work/state.json" >&2 || {
  flyctl ssh console --app "$bench_app" --machine "$setup" -C "cat /work/setup.log" >&2
  die "setup failed"
}
flyctl machine destroy "$setup" --app "$bench_app" --force > /dev/null

declare -A machine
began=$(date +%s)
for region in "${regions[@]}"; do
  load="/repl-bench --step load --only $region --partial /work/partial-$region.json $args $* > /work/load.log 2>&1; touch /work/loaded"
  # gru verifies once the other regions' partials arrive (this host uploads them, then the ready file).
  [[ $region == gru ]] && load+="; until [ -f /work/partials-ready ]; do sleep 5; done; /repl-bench --step verify --name $name --out /work/bench --partials /work/partial-gru.json,/work/partial-sjc.json,/work/partial-fra.json $args $* > /work/verify.log 2>&1; touch /work/verified"
  machine[$region]=$(run_machine "$region" "$region" "$load; sleep infinity")
  echo "load machine ${machine[$region]} in $region" >&2
done
for region in sjc fra; do
  wait_for "${machine[$region]}" /work/loaded
  flyctl ssh sftp get --app "$bench_app" --machine "${machine[$region]}" "/work/partial-$region.json" "$work/partial-$region.json" >&2
  flyctl ssh sftp put --app "$bench_app" --machine "${machine[gru]}" "$work/partial-$region.json" "/work/partial-$region.json" >&2
  flyctl machine destroy "${machine[$region]}" --app "$bench_app" --force > /dev/null
done
: > "$work/partials-ready"
flyctl ssh sftp put --app "$bench_app" --machine "${machine[gru]}" "$work/partials-ready" /work/partials-ready >&2
wait_for "${machine[gru]}" /work/verified
loaded=$(date +%s)
flyctl ssh console --app "$bench_app" --machine "${machine[gru]}" -C "tail -5 /work/verify.log" >&2 || true
mkdir -p "$work/bench"
for file in $(flyctl ssh console --app "$bench_app" --machine "${machine[gru]}" -C "ls /work/bench" 2> /dev/null | tr -d '\r'); do
  flyctl ssh sftp get --app "$bench_app" --machine "${machine[gru]}" "/work/bench/$file" "$work/bench/$file" >&2
done
flyctl machine destroy "${machine[gru]}" --app "$bench_app" --force > /dev/null

report=$(ls "$work"/bench/*.json | head -1) || die "verify wrote no report"
cp "$work"/bench/* "$root/benchmark/bench/"
"$work/repl-bench" --step resources --report "$root/benchmark/bench/$(basename "$report")"
echo "load machines: 3 x $size, $((loaded - began)) s from the first load start to the verified report (plus setup)" >&2
jq -e '.convergence.converged and .convergence.unknown == 0 and (.errors // [] | length == 0)' "$report" > /dev/null
