#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "usage: scripts/run-checked-rust-tests.sh <lane> [--allow-ignore <exact-runner::name>] [--skip <exact-name>] -- <cargo-test-arguments...>" >&2
  exit 2
}

[[ $# -ge 2 ]] || usage
lane=$1
shift
[[ "$lane" =~ ^[a-z0-9][a-z0-9._-]*$ ]] || usage

allowed_ignores=()
skips=()
approved_skips=(
  benchmarks::lane_run::tests::probe_lane_operation_costs
  benchmarks::lane_run::tests::probe_read_subcall_costs
  benchmarks::lane_run::tests::probe_write_durability_costs
  benchmarks::lane_run::tests::probe_concurrent_synced_writes
)
while [[ $# -gt 0 && $1 != -- ]]; do
  case "$1" in
    --allow-ignore)
      [[ $# -ge 2 ]] || usage
      allowed_ignores+=("$2")
      shift 2
      ;;
    --skip)
      [[ $# -ge 2 ]] || usage
      skips+=("$2")
      shift 2
      ;;
    *) usage ;;
  esac
done
[[ $# -ge 2 && $1 == -- ]] || usage
shift
cargo_arguments=("$@")

for cargo_argument in "${cargo_arguments[@]}"; do
  case "$cargo_argument" in
    --bench|--bench=*|--benches|--all-targets)
      echo "Rust test lane $lane refuses benchmark target selection: $cargo_argument" >&2
      exit 1
      ;;
  esac
done

for skip in "${skips[@]}"; do
  printf '%s\n' "${approved_skips[@]}" | grep -Fxq -- "$skip" || {
    echo "Rust test lane $lane requested an unapproved skip: $skip" >&2
    exit 1
  }
done

# The inventory parser consumes Cargo's stable plain-text protocol. Override
# any workflow-wide color preference so ANSI escapes cannot hide runners,
# doctests, or outcomes from the exact-inventory checks below.
export CARGO_TERM_COLOR=never

work=$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/terrabase-rust-tests.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && $work == */terrabase-rust-tests.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT

extract_inventory() {
  awk '
    function binary_lane(line, path) {
      sub(/^ +Running /, "", line)
      if (match(line, /\([^()]+\)$/)) {
        path = substr(line, RSTART + 1, RLENGTH - 2)
        sub(/^.*\//, "", path)
        sub(/-[0-9a-f]+$/, "", path)
        return "binary:" path
      }
      return "runner:" line
    }
    /^     Running / { current = binary_lane($0); next }
    /^   Doc-tests / {
      current = $0
      sub(/^ +Doc-tests /, "", current)
      current = "doctest:" current
      next
    }
    /: test$/ {
      name = $0
      sub(/: test$/, "", name)
      if (current == "") exit 91
      print current "\t" name
    }
  ' "$1"
}

extract_passes() {
  awk '
    function binary_lane(line, path) {
      sub(/^ +Running /, "", line)
      if (match(line, /\([^()]+\)$/)) {
        path = substr(line, RSTART + 1, RLENGTH - 2)
        sub(/^.*\//, "", path)
        sub(/-[0-9a-f]+$/, "", path)
        return "binary:" path
      }
      return "runner:" line
    }
    /^     Running / { current = binary_lane($0); next }
    /^   Doc-tests / {
      current = $0
      sub(/^ +Doc-tests /, "", current)
      current = "doctest:" current
      next
    }
    /^test .* \.\.\. ok$/ {
      name = $0
      sub(/^test /, "", name)
      sub(/ \.\.\. ok$/, "", name)
      if (current ~ /^doctest:/) sub(/ - (compile fail|compile|should panic)$/, "", name)
      if (current == "") exit 92
      print current "\t" name
    }
  ' "$1"
}

inventory_log=$work/inventory.log
ignored_log=$work/ignored.log
run_log=$work/run.log
inventory=$work/inventory.tsv
ignored=$work/ignored.tsv
expected=$work/expected.tsv
observed=$work/observed.tsv

cargo test "${cargo_arguments[@]}" -- --list --format terse >"$inventory_log" 2>&1
if grep -Eq ': benchmark$' "$inventory_log"; then
  echo "Rust test lane $lane includes a benchmark, which this verifier will not execute" >&2
  exit 1
fi
extract_inventory "$inventory_log" | LC_ALL=C sort >"$inventory"
[[ -s "$inventory" ]] || {
  echo "Rust test lane $lane discovered no tests" >&2
  exit 1
}

printf '%s\n' "${skips[@]}" | sed '/^$/d' | LC_ALL=C sort >"$work/requested-skips.txt"
for approved_skip in "${approved_skips[@]}"; do
  if awk -F '\t' -v skip="$approved_skip" '$2 == skip { found = 1 } END { exit !found }' "$inventory"; then
    grep -Fxq -- "$approved_skip" "$work/requested-skips.txt" || {
      echo "Rust test lane $lane selected an approved benchmark probe without excluding it: $approved_skip" >&2
      exit 1
    }
  fi
done

cargo test "${cargo_arguments[@]}" -- --ignored --list --format terse >"$ignored_log" 2>&1
extract_inventory "$ignored_log" | LC_ALL=C sort >"$ignored"

printf '%s\n' "${allowed_ignores[@]}" | sed '/^$/d' | LC_ALL=C sort >"$work/allowed-ignores.txt"
awk -F '\t' '{ print $1 "::" $2 }' "$ignored" | LC_ALL=C sort >"$work/observed-ignores.txt"
diff -u "$work/allowed-ignores.txt" "$work/observed-ignores.txt"

cp "$inventory" "$expected"
if [[ -s "$ignored" ]]; then
  grep -Fvx -f "$ignored" "$expected" >"$work/without-ignored.tsv"
  mv "$work/without-ignored.tsv" "$expected"
fi

libtest_arguments=()
for skip in "${skips[@]}"; do
  substring_count=$(awk -F '\t' -v skip="$skip" 'index($2, skip) > 0 { count += 1 } END { print count + 0 }' "$expected")
  exact_count=$(awk -F '\t' -v skip="$skip" '$2 == skip { count += 1 } END { print count + 0 }' "$expected")
  [[ $substring_count -eq 1 && $exact_count -eq 1 ]] || {
    echo "Rust test lane $lane skip is not one exact selected test: $skip" >&2
    exit 1
  }
  awk -F '\t' -v skip="$skip" '$2 != skip' "$expected" >"$work/without-skip.tsv"
  mv "$work/without-skip.tsv" "$expected"
  libtest_arguments+=(--skip "$skip")
done

cargo test "${cargo_arguments[@]}" -- "${libtest_arguments[@]}" 2>&1 | tee "$run_log"
extract_passes "$run_log" | LC_ALL=C sort >"$observed"
diff -u "$expected" "$observed"
echo "verified Rust test lane $lane: $(wc -l <"$observed") exact passes"
