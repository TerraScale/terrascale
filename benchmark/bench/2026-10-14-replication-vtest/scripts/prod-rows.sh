#!/usr/bin/env bash
# The V2/V5 rows production can prove without a fault (V5 R1 remote-ack from each origin, V2 S2 drop/re-create), harvested
# from main CI's e2e job after a deploy: prod-rows.sh <CI run id>. That job runs them against ts-terrabase in the bound
# e2e project (databases it creates and drops); no machine is touched. Fault rows stay on ts-vtest-tb at the same SHA.
# Writes benchmark/bench/<run date>-replication-v-prod.{md,json}; exits 1 unless every V row is an actual pass on Fly.
set -euo pipefail
run=${1:?CI run id}; here=$(cd "$(dirname "$0")" && pwd); bench=$(cd "$here/../.." && pwd)
meta=$(gh run view "$run" --json headSha,createdAt,url,jobs)
sha=$(jq -r .headSha <<< "$meta"); day=$(jq -r ".createdAt[:10]" <<< "$meta")
jq -e '.jobs[] | select(.name == "deploy") | .conclusion == "success"' <<< "$meta" > /dev/null || { echo "run $run did not deploy" >&2; exit 1; }
dir=$(mktemp -d); trap 'rm -rf "$dir"' EXIT
gh run download "$run" -n "replication-e2e-$sha" -D "$dir"
results=$(find "$dir" -name e2e-results.jsonl); log=$(find "$dir" -name replication-e2e.log)
live=$(curl -sS -m 20 https://ts-terrabase.fly.dev/healthz | jq -c "{region,revision,storageFormat}" || echo null)
rows='select(.test | test("^(A_201_is_remote_durable_and_another_region_serves_it_at_once|Shape_clients_follow_a_collection_drop_and_re_create)"))
  | . + {row: (if (.test | startswith("A_201")) then "V5 R1" else "V2 S2" end), passed: (.category == "pass" and .nodes == "Fly machines")}'
out=$bench/$day-replication-v-prod
jq -s --arg sha "$sha" --arg url "$(jq -r .url <<< "$meta")" --argjson live "$live" --arg log "$(sha256sum "$log" | cut -d' ' -f1)" \
  --arg results "$(sha256sum "$results" | cut -d' ' -f1)" --arg measured "$(grep -E 'V2 |V5 ' "$log" | cut -c1-400 || true)" \
  "{commit: \$sha, run: \$url, live: \$live, sha256: {\"replication-e2e.log\": \$log, \"e2e-results.jsonl\": \$results},
    measured: (\$measured | split(\"\n\") | map(select(length > 0))), rows: [.[] | $rows]}" "$results" > "$out.json"
{
  echo "# Replication V2/V5 production rows, $day"
  echo
  echo "Commit \`$sha\` on ts-terrabase (gru, sjc, fra), CI run $(jq -r .run "$out.json"); live at harvest: \`$live\`."
  echo "Rows from the e2e job's \`e2e-results.jsonl\` (sha256 \`$(jq -r '.sha256["e2e-results.jsonl"]' "$out.json")\`). Fault rows run on ts-vtest-tb."
  echo
  echo "| Row | Test | Nodes | Result |"; echo "| --- | --- | --- | --- |"
  jq -r '.rows[] | "| \(.row) | `\(.test)` | \(.nodes) | \(if .passed then "PASS" else "NOT PASS: \(.category) \(.detail | gsub("\n"; " ") | .[:200])" end) |"' "$out.json"
  echo; echo "Measured lines:"; echo; jq -r '.measured[] | "    " + .' "$out.json"
} > "$out.md"
echo "$out.md"
jq -e '(.rows | length) == 4 and all(.rows[]; .passed)' "$out.json" > /dev/null
