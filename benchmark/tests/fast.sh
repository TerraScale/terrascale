#!/usr/bin/env bash
# Fast suite: run the built benchmark executables as processes against real
# engines and the v2 floor classifier. Results are smoke checks only; host
# timings here never fill a scoreboard row.
set -euo pipefail

command -v jq >/dev/null || { echo 'jq is required' >&2; exit 2; }

root=$(cd "$(dirname "$0")/.." && pwd)
# Ask Cargo, from each crate's own directory, for the executable it builds.
# This honours CARGO_TARGET_DIR, an explicit build target and crate-local
# configuration, and rebuilds a stale binary instead of testing it.
executable() {
  (cd "$root/$1" && cargo build --release --locked --bin "$2" --message-format=json-render-diagnostics) |
    jq -r --arg bin "$2" 'select(.reason == "compiler-artifact" and .target.name == $bin) | .executable // empty' |
    tail -n 1
}
floor=$(executable terra-rust throughput-floor)
scorecard=$(executable local-engine local-engine-scorecard)
for binary in "$floor" "$scorecard"; do
  [[ -n $binary && -x $binary ]] || { echo "missing $binary; run make build-fast first" >&2; exit 2; }
done

work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
failures=0
pass() { printf 'ok   %s\n' "$1"; }
fail() { printf 'FAIL %s\n' "$1" >&2; failures=$((failures + 1)); }

# pairs <case> <ops> <sqlite_ns> <terrabase_ns>... writes one input per call.
pairs() {
  local name=$1 case_id=$2 count=$3; shift 3
  jq -n --arg case "$case_id" --argjson count "$count" --argjson ns "$(printf '%s\n' "$@" | jq -s .)" '
    {schema_version: 1, matrix_version: "v2", case_id: $case, bootstrap_seed: 2026100101,
     pairs: [range(0; $count) as $i | {id: ($i + 1 | tostring),
       sqlite: {receipt: "s\($i).json", operations_completed: 1000, elapsed_ns: 1000000000},
       terrabase: {receipt: "t\($i).json", operations_completed: 1000, elapsed_ns: $ns[$i % ($ns | length)]}}]}' \
    > "$work/$name.json"
}

expect_status() {
  local name=$1 want=$2 got
  if ! "$floor" --input "$work/$name.json" --output "$work/$name.out.json" > "$work/$name.row"; then
    fail "throughput-floor $name exited nonzero"; return
  fi
  got=$(jq -r .status "$work/$name.out.json")
  if [[ $got == "$want" ]] && grep -q "| $want |" "$work/$name.row"; then
    pass "throughput-floor $name -> $want"
  else
    fail "throughput-floor $name: want $want, got $got"
  fi
}

# expect_reject <name> <diagnostic>: the classifier must fail validation with
# exit status 1 and the expected message, and write no report.
expect_reject() {
  local name=$1 diagnostic=$2 status=0
  "$floor" --input "$work/$name.json" --output "$work/$name.out.json" \
    > /dev/null 2> "$work/$name.err" || status=$?
  if (( status != 1 )); then
    fail "throughput-floor $name: want exit 1, got $status"
  elif ! grep -qF -- "$diagnostic" "$work/$name.err"; then
    fail "throughput-floor $name: missing diagnostic '$diagnostic'"
  elif [[ -e $work/$name.out.json ]]; then
    fail "throughput-floor wrote a report for rejected $name"
  else
    pass "throughput-floor rejects $name"
  fi
}

# TerraBase at 0.5x SQLite throughput in every pair: CI is [0.5, 0.5].
pairs meets M01/insert/c1/throughput 7 2000000000
expect_status meets MEETS_FLOOR
# Exactly 0.40x in every pair: CI is [0.40, 0.40], which meets the floor.
pairs at-floor M01/insert/c1/throughput 7 2500000000
expect_status at-floor MEETS_FLOOR
# 0.2x in every pair.
pairs below M01/insert/c1/throughput 7 5000000000
expect_status below BELOW_FLOOR
# Pairs alternate 0.2x and 1.0x, so the interval spans 0.40.
pairs spans M01/insert/c1/throughput 8 5000000000 1000000000
expect_status spans INCONCLUSIVE
# 2x SQLite is uncapped and meets the floor.
pairs faster M01/insert/c1/throughput 7 500000000
expect_status faster MEETS_FLOOR
if [[ -s $work/faster.out.json ]] && jq -e '.point_ratio > 1.9 and .ci95_lower > 1.9' "$work/faster.out.json" >/dev/null; then
  pass 'throughput-floor keeps ratios above 1.0'
else
  fail 'throughput-floor capped a ratio above 1.0'
fi
pairs too-few M01/insert/c1/throughput 6 2000000000
expect_reject too-few 'at least 7 paired trials are required'
pairs latency M01/insert/c1/p99 7 2000000000
expect_reject latency 'the floor applies only to throughput cases'
pairs unequal M01/insert/c1/throughput 7 2000000000
jq '.pairs[3].terrabase.operations_completed = 999' "$work/unequal.json" > "$work/unequal-work.json"
expect_reject unequal-work 'lanes completed different logical work'
jq '.matrix_version = "v1"' "$work/meets.json" > "$work/v1.json"
expect_reject v1 'only matrix v2 registers the throughput floor'
cp "$work/meets.out.json" "$work/meets.before.json"
status=0
"$floor" --input "$work/meets.json" --output "$work/meets.out.json" \
  > /dev/null 2> "$work/overwrite.err" || status=$?
if (( status == 1 )) && grep -qF 'create fresh report' "$work/overwrite.err" &&
   cmp -s "$work/meets.before.json" "$work/meets.out.json"; then
  pass 'throughput-floor refuses to overwrite a report'
else
  fail "throughput-floor overwrite: exit $status or report changed"
fi

# Smoke benchmark: both local SQL engines run the same CRUD trace through the
# real bindings, then reopen, integrity-check and hash the final state.
terrabase_rev=$(git -C "$root/../terrabase" rev-parse HEAD)
benchmark_rev=$(git -C "$root" rev-parse HEAD)
turso_rev=$(git -C "$root/../terrabase/crates/turso" rev-parse HEAD)
cpu=$(taskset -cp $$ | sed -E 's/.*: *([0-9]+).*/\1/')
for engine in sqlite turso; do
  lane="$work/lane-$engine"
  mkdir -p "$lane"
  if taskset -c "$cpu" python3 "$root/local-engine/host-manifest.py" \
       --output "$lane/environment.json" --database-parent "$lane" &&
     taskset -c "$cpu" "$scorecard" --engine "$engine" --cycles 50 --body-bytes 256 \
       --seed 1 --trial 1 --database "$lane/data.db" --output "$lane/results" \
       --environment-manifest "$lane/environment.json" \
       --benchmark-revision "$benchmark_rev" --terrabase-revision "$terrabase_rev" \
       --turso-revision "$turso_rev" > "$lane/stdout" 2> "$lane/stderr" &&
     jq -e '.status == "passed" and .operations_completed == 350 and .integrity_check == "ok" and .final_rows == 50' \
       "$lane/results/receipt.json" >/dev/null; then
    pass "local-engine $engine CRUD smoke"
  else
    cat "$lane/stderr" >&2 || true
    fail "local-engine $engine CRUD smoke"
  fi
done
hashes() {
  jq -er '[.trace_sha256, .final_state_sha256]
          | if all(type == "string" and test("^[0-9a-f]{64}$")) then join(" ") else error("missing hash") end' \
    "$1" 2>/dev/null
}
if sqlite_hashes=$(hashes "$work/lane-sqlite/results/receipt.json") &&
   turso_hashes=$(hashes "$work/lane-turso/results/receipt.json") &&
   [[ $sqlite_hashes == "$turso_hashes" ]]; then
  pass 'local-engine lanes agree on trace and final state'
else
  fail 'local-engine lanes lack or disagree on trace and final-state SHA-256'
fi

if (( failures > 0 )); then
  echo "$failures fast check(s) failed" >&2
  exit 1
fi
echo 'fast suite passed'
