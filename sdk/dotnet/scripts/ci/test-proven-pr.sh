#!/usr/bin/env bash
set -euo pipefail

temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT
export GITHUB_OUTPUT="$temporary/output" GITHUB_REPOSITORY=example/sdk
export GITHUB_SHA=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
head=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
tree=$(git rev-parse HEAD^{tree})
export PULLS JOBS RUNS FAIL_API=''
pulls=$(jq -cn --arg sha "$GITHUB_SHA" --arg head "$head" '[{
  merged_at: "2026-10-22", state: "closed", base: {ref: "main"},
  merge_commit_sha: $sha, head: {sha: $head}
}]')
jobs=$(jq -cn --arg marker "Checked tree $tree" '{jobs: [{
  name: "test", status: "completed", conclusion: "success",
  steps: [{name: $marker, status: "completed", conclusion: "success"}]
}]}')
runs=$(jq -cn --arg head "$head" '{workflow_runs: [{
  id: 123, event: "pull_request", head_sha: $head, status: "completed", conclusion: "success"
}]}')

# Exported functions intercept every API request; this test never contacts GitHub.
gh() {
  case "$2" in
    */pulls\?*) [[ "$FAIL_API" != pulls ]] && printf '%s\n' "$PULLS" ;;
    */actions/runs/123/jobs\?*) [[ "$FAIL_API" != jobs ]] && printf '%s\n' "$JOBS" ;;
    */actions/workflows/ci.yml/runs\?*) [[ "$FAIL_API" != runs ]] && printf '%s\n' "$RUNS" ;;
    *) return 1 ;;
  esac
}
export -f gh

check() {
  : > "$GITHUB_OUTPUT"
  bash scripts/ci/proven-pr.sh 2> "$temporary/stderr"
  [[ "$(cat "$GITHUB_OUTPUT")" == "proven=$1" ]] || {
    printf 'Proof self-check failed: %s\n' "$2" >&2
    exit 1
  }
}

PULLS=$pulls JOBS=$jobs RUNS=$runs check true 'exact successful PR tree'
for endpoint in pulls runs jobs; do
  PULLS=$pulls JOBS=$jobs RUNS=$runs FAIL_API=$endpoint check false "API unavailable: $endpoint"
done
PULLS='[]' JOBS=$jobs RUNS=$runs check false 'direct push'
PULLS='invalid JSON' JOBS=$jobs RUNS=$runs check false 'malformed evidence'
for mutation in '.[0].base.ref="other"' '.[0].merge_commit_sha="other"' \
  '.[0].merged_at=null' '.[0].state="open"' '.[0].head.sha="invalid"'; do
  PULLS=$(jq "$mutation" <<< "$pulls") JOBS=$jobs RUNS=$runs check false "$mutation"
done
for mutation in '.jobs=[]' '.jobs[0].name="other"' '.jobs[0].status="in_progress"' \
  '.jobs[0].conclusion="failure"' '.jobs[0].conclusion="skipped"' '.jobs[0].steps=[]' \
  '.jobs[0].steps[0].name="Checked tree other"' '.jobs[0].steps[0].status="queued"' \
  '.jobs[0].steps[0].conclusion="skipped"' '.jobs[0].steps[0].conclusion="failure"'; do
  PULLS=$pulls JOBS=$(jq "$mutation" <<< "$jobs") RUNS=$runs check false "$mutation"
done
for mutation in '.workflow_runs=[]' '.workflow_runs[0].head_sha="other"' \
  '.workflow_runs[0].event="push"' '.workflow_runs[0].status="in_progress"' \
  '.workflow_runs[0].conclusion="failure"'; do
  PULLS=$pulls JOBS=$jobs RUNS=$(jq "$mutation" <<< "$runs") check false "$mutation"
done
printf 'PR proof: 26 mocked API cases passed.\n'
