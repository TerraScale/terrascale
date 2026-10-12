#!/usr/bin/env bash
# The e2e acceptance summary, built from the run's own per-test outcomes (tests/TerraBase.E2E/Outcomes.cs), never from a
# pass count. A strict expected failure "passes" in TUnit but proves nothing about the feature, so the categories stay
# apart: actual pass (a feature scenario held), harness check, expected failure (with its slice), not supported (a hook or
# feature not built yet), skip (with its reason) and fail. Every test's line says whether its nodes were separate OS
# processes or in one host and which faults it injected (a simulated partition is not a network fault). Every summary
# names the exact commit, the working tree's patch identity and the harness commit it is stacked on.
#
#   e2e-summary.sh report <results.jsonl> [label]   markdown summary on stdout; exit 1 if any test failed
#                                                   (E2E_STACKED_ON=<sha> names the harness commit the branch sits on)
#   e2e-summary.sh check <file.md>...               fail if a summary block lacks a category, its commit or patch
#                                                   identity, has counts that do not add up, or reports "successes"
set -euo pipefail
# The repository the script lives in, whatever the caller's directory (CI runs from the workspace above it).
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
git() { command git -C "$root" "$@"; }

patch_identity() {
  local diff untracked
  diff=$(git diff HEAD --binary)
  untracked=$(cd "$root" && command git ls-files --others --exclude-standard -z | xargs -0 -r sha256sum)
  if [[ -z "$diff" && -z "$untracked" ]]; then
    echo clean
  else
    printf '%s\n%s' "$diff" "$untracked" | sha256sum | cut -d' ' -f1
  fi
}

report() {
  local results=$1 label=${2:-local}
  [[ -s "$results" ]] || { echo "e2e-summary: $results holds no outcomes" >&2; exit 2; }
  count() { jq -s --arg c "$1" 'map(select(.category == $c)) | length' "$results"; }
  local pass harness expected unsupported skip fail total
  pass=$(count pass) harness=$(count harness) expected=$(count expected-failure) unsupported=$(count not-supported) skip=$(count skip) fail=$(count fail)
  total=$(jq -s length "$results")
  echo "<!-- e2e-summary -->"
  echo "E2E run ($label), $(date -u +%Y-%m-%dT%H:%MZ) on $(hostname)"
  echo
  echo "- Commit: \`$(git rev-parse HEAD)\`; working tree: $(patch_identity)"
  if [[ -n "${E2E_STACKED_ON:-}" ]]; then
    echo "- Stacked on: \`$(git rev-parse "$E2E_STACKED_ON")\` (the harness commit this candidate sits on)"
  else
    echo "- Stacked on: none (merge base with origin/main \`$(git merge-base HEAD origin/main 2> /dev/null || git rev-parse HEAD)\`)"
  fi
  echo
  echo "| Category | Tests |"
  echo "| --- | ---: |"
  echo "| actual pass | $pass |"
  echo "| harness check | $harness |"
  echo "| expected failure | $expected |"
  echo "| not supported | $unsupported |"
  echo "| skip | $skip |"
  echo "| fail | $fail |"
  echo "| total | $total |"
  echo
  echo "| Test | Outcome | Nodes | Faults | Waits on / reason |"
  echo "| --- | --- | --- | --- | --- |"
  jq -rs 'def outcome: {"pass": "actual pass", "harness": "harness check", "expected-failure": "expected failure", "not-supported": "not supported", "skip": "skip", "fail": "fail"}[.category] // .category;
    sort_by(.category, .test)[] | "| `\(.test)` | \(outcome) | \(.nodes // "undeclared") | \(.faults // "undeclared") | \(.detail | gsub("\\|"; "/") | gsub("\n"; " ")) |"' "$results"
  echo "<!-- /e2e-summary -->"
  [[ "$fail" == 0 ]]
}

check() {
  local file status=0
  for file in "$@"; do
    # Each summary block (between the markers) is checked on its own.
    if ! awk -v file="$file" '
      /<!-- e2e-summary -->/ { inside = 1; blocks++; delete n; commit = 0; stacked = 0; rows = 0; next }
      /<!-- \/e2e-summary -->/ {
        inside = 0
        for (c in want) if (!(c in n)) { printf "%s: summary %d lacks the %s count\n", file, blocks, c; bad = 1 }
        if (!commit) { printf "%s: summary %d lacks \"Commit: `<sha>`; working tree: clean|<sha256>\"\n", file, blocks; bad = 1 }
        if (!stacked) { printf "%s: summary %d lacks \"Stacked on: ...\"\n", file, blocks; bad = 1 }
        if (n["total"] != n["actual pass"] + n["harness check"] + n["expected failure"] + n["not supported"] + n["skip"] + n["fail"]) {
          printf "%s: summary %d counts do not add up to its total\n", file, blocks; bad = 1 }
        if (rows != n["total"]) { printf "%s: summary %d lists %d tests for a total of %d\n", file, blocks, rows, n["total"]; bad = 1 }
        next
      }
      inside && /^\| `/ {
        rows++
        if ($0 ~ /undeclared/) { printf "%s: summary %d: a test does not declare its nodes and faults: %s\n", file, blocks, $0; bad = 1 }
        next }
      inside && /^\| (actual pass|harness check|expected failure|not supported|skip|fail|total) \| [0-9]+ \|$/ {
        split($0, f, "|"); gsub(/^ +| +$/, "", f[2]); gsub(/ /, "", f[3]); n[f[2]] = f[3] + 0; next }
      inside && /^- Commit: `[0-9a-f]{7,40}`; working tree: (clean|[0-9a-f]{64})$/ { commit = 1 }
      inside && /^- Stacked on: (`[0-9a-f]{7,40}` \(|none \(merge base with origin\/main `[0-9a-f]{7,40}`\))/ { stacked = 1 }
      inside && tolower($0) ~ /succe(ss|ssful|eded)/ { printf "%s: summary %d says \"%s\": report the categories, not successes\n", file, blocks, $0; bad = 1 }
      BEGIN { split("actual pass,harness check,expected failure,not supported,skip,fail,total", w, ","); for (i in w) want[w[i]] = 1 }
      END {
        if (inside) { printf "%s: summary %d is not closed\n", file, blocks; bad = 1 }
        if (!blocks) { printf "%s: no e2e summary block\n", file; bad = 1 }
        exit bad }' "$file"; then
      status=1
    fi
  done
  return "$status"
}

case "${1:-}" in
  report) shift; report "$@" ;;
  check) shift; check "$@" ;;
  *) echo "usage: e2e-summary.sh report <results.jsonl> [label] | check <file.md>..." >&2; exit 2 ;;
esac
