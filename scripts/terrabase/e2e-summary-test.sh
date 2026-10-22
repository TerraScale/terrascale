#!/usr/bin/env bash
# Self-check for scripts/terrabase/e2e-summary.sh: a report keeps the categories apart and passes its own check, and
# the check refuses summaries that mix categories, lack the commit or patch identity, or do not add up.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
summary="$here/e2e-summary.sh"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
fail() { echo "e2e-summary-test: $*" >&2; exit 1; }

cat > "$tmp/results.jsonl" <<'JSON'
{"test":"Pin_a","category":"harness","detail":"","nodes":"no node (harness)","faults":"none"}
{"test":"Kill","category":"pass","detail":"","nodes":"separate OS processes","faults":"ProcessKill"}
{"test":"E1","category":"expected-failure","detail":"the CDC source (R3)","nodes":"in-process nodes","faults":"none"}
{"test":"E2","category":"expected-failure","detail":"the CDC source (R3)","nodes":"in-process nodes","faults":"none"}
{"test":"E10","category":"expected-failure","detail":"the changes feed (R8)","nodes":"in-process nodes","faults":"none"}
{"test":"Partition","category":"not-supported","detail":"R3; no local link","nodes":"in-process nodes","faults":"SimulatedPartition"}
{"test":"Outage","category":"skip","detail":"Outage test requires a live endpoint","nodes":"in-process nodes","faults":"NodeStop"}
JSON
"$summary" report "$tmp/results.jsonl" test > "$tmp/report.md" || fail "a run without failures must exit 0"
for line in '| actual pass | 1 |' '| harness check | 1 |' '| expected failure | 3 |' '| not supported | 1 |' '| skip | 1 |' '| fail | 0 |' '| total | 7 |' \
  '| `Kill` | actual pass | separate OS processes | ProcessKill |  |' '| `Partition` | not supported | in-process nodes | SimulatedPartition | R3; no local link |' \
  '| `Outage` | skip | in-process nodes | NodeStop | Outage test requires a live endpoint |' '| `E1` | expected failure | in-process nodes | none | the CDC source (R3) |'; do
  grep -qF -- "$line" "$tmp/report.md" || fail "report lacks: $line"
done
grep -qE '^- Commit: `[0-9a-f]{40}`; working tree: (clean|[0-9a-f]{64})$' "$tmp/report.md" || fail "report lacks the commit and patch identity"
"$summary" check "$tmp/report.md" > /dev/null || fail "a generated report must pass the check"
cp "$tmp/results.jsonl" "$tmp/results.jsonl.ok"
E2E_STACKED_ON=HEAD "$summary" report "$tmp/results.jsonl.ok" stacked > "$tmp/stacked.md" || fail "a stacked run must report"
grep -qE '^- Stacked on: `[0-9a-f]{40}` \(the harness commit' "$tmp/stacked.md" || fail "a stacked run must name its harness commit"
"$summary" check "$tmp/stacked.md" > /dev/null || fail "a stacked report must pass the check"

echo '{"test":"Broken","category":"fail","detail":"Failed: boom","nodes":"in-process nodes","faults":"none"}' >> "$tmp/results.jsonl"
if "$summary" report "$tmp/results.jsonl" > /dev/null; then fail "a run with a failure must exit 1"; fi

refused() {
  if "$summary" check "$tmp/bad.md" > /dev/null 2>&1; then fail "the check accepted: $1"; fi
}
sed 's/| skip | 1 |/| skip | 2 |/' "$tmp/report.md" > "$tmp/bad.md"; refused "counts that do not add up"
grep -v '| not supported |' "$tmp/report.md" > "$tmp/bad.md"; refused "a missing category"
grep -v '^- Commit:' "$tmp/report.md" > "$tmp/bad.md"; refused "no commit"
grep -v '^- Stacked on:' "$tmp/report.md" > "$tmp/bad.md"; refused "no stacked-on line"
sed 's/working tree: .*/working tree: dirty/' "$tmp/report.md" > "$tmp/bad.md"; refused "a patch that is not a sha256"
sed 's/^E2E run.*/E2E run: 40 successes/' "$tmp/report.md" > "$tmp/bad.md"; refused "a summary that reports successes"
echo "no block here" > "$tmp/bad.md"; refused "a file without a summary block"
grep -v '^| `E10`' "$tmp/report.md" > "$tmp/bad.md"; refused "a test missing from the per-test table"
sed 's/| `E1` | expected failure | in-process nodes |/| `E1` | expected failure | undeclared |/' "$tmp/report.md" > "$tmp/bad.md"; refused "a test without its nodes"
echo "e2e-summary-test: ok"
