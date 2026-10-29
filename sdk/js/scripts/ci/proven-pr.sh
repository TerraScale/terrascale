#!/usr/bin/env bash
set -euo pipefail

proven=false
trap 'printf "proven=%s\n" "$proven" >> "$GITHUB_OUTPUT"' EXIT

# Missing or unavailable evidence reruns CI instead of skipping it.
main_tree=$(git rev-parse HEAD^{tree}) || exit 0
pulls=$(gh api "repos/$GITHUB_REPOSITORY/commits/$GITHUB_SHA/pulls?per_page=100") || exit 0
heads=$(jq -er --arg sha "$GITHUB_SHA" '
  .[] | select(.merged_at != null and .state == "closed"
    and .base.ref == "main" and .merge_commit_sha == $sha) | .head.sha
' <<< "$pulls") || exit 0

while IFS= read -r head; do
  [[ "$head" =~ ^[0-9a-f]{40}$ ]] || continue
  runs=$(gh api "repos/$GITHUB_REPOSITORY/actions/workflows/sdk-js-ci.yml/runs?event=pull_request&head_sha=$head&status=success&per_page=1") || exit 0
  run_id=$(jq -er --arg sha "$head" '.workflow_runs[0]
    | select(.event == "pull_request" and .head_sha == $sha
      and .status == "completed" and .conclusion == "success") | .id' <<< "$runs") || continue
  [[ "$run_id" =~ ^[0-9]+$ ]] || continue
  jobs=$(gh api "repos/$GITHUB_REPOSITORY/actions/runs/$run_id/jobs?per_page=100") || exit 0
  # The final marker attests the synthetic merge tree that actually passed checks.
  if jq -e --arg marker "Checked tree $main_tree" 'any(.jobs[];
    .name == "verify" and .status == "completed" and .conclusion == "success"
    and any(.steps[]; .name == $marker and .status == "completed"
      and .conclusion == "success"))' <<< "$jobs" > /dev/null; then
    proven=true
    break
  fi
done <<< "$heads"
