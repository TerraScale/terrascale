#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "usage: scripts/run-checked-go-tests.sh <lane> <module-directory> [--race] [--tags <tags>] [--allow-no-tests <package>] -- <package-pattern...>" >&2
  exit 2
}

[[ $# -ge 4 ]] || usage
lane=$1
module_directory=$2
shift 2
[[ "$lane" =~ ^[a-z0-9][a-z0-9._-]*$ ]] || usage
[[ -d "$module_directory" && ! -L "$module_directory" ]] || usage

go_arguments=(-mod=readonly -count=1)
allowed_no_tests=()
while [[ $# -gt 0 && $1 != -- ]]; do
  case "$1" in
    --race)
      go_arguments+=(-race)
      shift
      ;;
    --tags)
      [[ $# -ge 2 ]] || usage
      go_arguments+=(-tags "$2")
      shift 2
      ;;
    --allow-no-tests)
      [[ $# -ge 2 ]] || usage
      allowed_no_tests+=("$2")
      shift 2
      ;;
    *) usage ;;
  esac
done
[[ $# -ge 2 && $1 == -- ]] || usage
shift
package_patterns=("$@")
for package_pattern in "${package_patterns[@]}"; do
  [[ -n "$package_pattern" && "$package_pattern" != -* ]] || {
    echo "Go test lane $lane accepts package patterns only after --: $package_pattern" >&2
    exit 1
  }
done
go_arguments+=('-bench=^$')

work=$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/terrabase-go-tests.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && $work == */terrabase-go-tests.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT
export GOENV=off
export GOWORK=off
export GOFLAGS=
unset GOCACHEPROG
export GOCACHE=$work/gocache

discovery=$work/discovery.json
execution=$work/execution.json
(
  cd "$module_directory"
  go test "${go_arguments[@]}" -list '^(Test|Example|Fuzz)' -json "${package_patterns[@]}"
) | tee "$discovery"

jq -r 'select(.Action == "start") | .Package' "$discovery" | LC_ALL=C sort -u >"$work/packages.txt"
[[ -s "$work/packages.txt" ]] || {
  echo "Go test lane $lane discovered no packages" >&2
  exit 1
}
jq -r '
  select(.Action == "output" and (.Output | test("^(Test|Example|Fuzz)[^\\n]*\\n$"))) |
  [.Package, (.Output | rtrimstr("\n"))] | @tsv
' "$discovery" | LC_ALL=C sort >"$work/expected-tests.tsv"
[[ -s "$work/expected-tests.tsv" ]] || {
  echo "Go test lane $lane discovered no tests or examples" >&2
  exit 1
}
cut -f1 "$work/expected-tests.tsv" | LC_ALL=C sort -u >"$work/discovered-test-packages.txt"
comm -23 "$work/packages.txt" "$work/discovered-test-packages.txt" >"$work/no-tests.txt"
printf '%s\n' "${allowed_no_tests[@]}" | sed '/^$/d' | LC_ALL=C sort >"$work/allowed-no-tests.txt"
diff -u "$work/allowed-no-tests.txt" "$work/no-tests.txt"

(
  cd "$module_directory"
  go test "${go_arguments[@]}" -json "${package_patterns[@]}"
) | tee "$execution"

jq --slurp --exit-status '
  ([.[] | select(has("Test"))] | group_by([.Package, .Test]) |
    all(.[];
      ([.[] | select(.Action == "run")] | length) == 1 and
      ([.[] | select(.Action == "pass")] | length) == 1 and
      ([.[] | select(.Action == "fail" or .Action == "skip")] | length) == 0)) and
  ([.[] | select(.Action == "fail" and (has("Test") | not))] | length) == 0
' "$execution" >/dev/null

jq -r 'select(.Action == "run" and has("Test") and (.Test | contains("/") | not)) | [.Package, .Test] | @tsv' \
  "$execution" | LC_ALL=C sort >"$work/observed-tests.tsv"
diff -u "$work/expected-tests.tsv" "$work/observed-tests.tsv"

comm -23 "$work/packages.txt" "$work/no-tests.txt" >"$work/test-packages.txt"
jq -r 'select((.Action == "pass" or .Action == "skip") and (has("Test") | not)) | [.Package, .Action] | @tsv' \
  "$execution" | LC_ALL=C sort >"$work/package-terminals.tsv"
cut -f1 "$work/package-terminals.tsv" | uniq -d >"$work/duplicate-package-terminals.txt"
[[ ! -s "$work/duplicate-package-terminals.txt" ]] || {
  echo "Go test lane $lane observed duplicate package terminal outcomes" >&2
  exit 1
}
cut -f1 "$work/package-terminals.tsv" | LC_ALL=C sort >"$work/terminal-packages.txt"
diff -u "$work/packages.txt" "$work/terminal-packages.txt"
awk -F '\t' 'FILENAME == ARGV[1] { no_tests[$1] = 1; next } !($1 in no_tests) && $2 != "pass" { exit 1 }' \
  "$work/no-tests.txt" "$work/package-terminals.tsv"

echo "verified Go test lane $lane: $(wc -l <"$work/observed-tests.tsv") exact tests across $(wc -l <"$work/packages.txt") packages"
