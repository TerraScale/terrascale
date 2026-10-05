#!/usr/bin/env bash
# Fast suite: run the built benchmark executables as processes against real
# SQLite/Turso comparison engines. Results are smoke checks only; host
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
scorecard=$(executable local-engine local-engine-scorecard)
[[ -n $scorecard && -x $scorecard ]] || { echo "missing scorecard; run make local-engine-build first" >&2; exit 2; }

work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
failures=0
pass() { printf 'ok   %s\n' "$1"; }
fail() { printf 'FAIL %s\n' "$1" >&2; failures=$((failures + 1)); }

# Smoke benchmark: both local SQL engines run the same CRUD trace through the
# real bindings, then reopen, integrity-check and hash the final state.
# This pinned Rust checkout supplies historical Turso comparison sources only,
# never the current C# TerraBase server under acceptance.
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
