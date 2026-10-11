#!/usr/bin/env bash
# Exits 0 once the Fast suites (check `test`) passed for exactly the tree of <sha>, the main commit about to deploy.
# A squash merge of an up-to-date pull request has its head's tree, so that head's `test` result counts at once;
# otherwise this waits for main's own `test` run. Needs GH_TOKEN with checks:read and pull-requests:read.
#
#   scripts/fly/fast-gate.sh <owner/repo> <sha>
set -euo pipefail

test "$#" -eq 2 || { echo "usage: $0 <owner/repo> <sha>" >&2; exit 2; }
repo=$1 sha=$2

tree() { gh api "repos/$repo/commits/$1" --jq .commit.tree.sha; }
# The latest `test` check run of a commit: "<status> <conclusion>".
test_run() { gh api "repos/$repo/commits/$1/check-runs?check_name=test" --jq '.check_runs | max_by(.started_at) | "\(.status) \(.conclusion)"'; }

want=$(tree "$sha")
for head in $(gh api "repos/$repo/commits/$sha/pulls" --jq '.[].head.sha'); do
  if [[ $(tree "$head") == "$want" && $(test_run "$head") == "completed success" ]]; then
    echo "Fast passed on pull request head $head, same tree as $sha"
    exit 0
  fi
done

echo "No passed pull request head has this tree; waiting for main's own test run"
for _ in $(seq 120); do
  case $(test_run "$sha" 2>/dev/null || true) in
    "completed success") echo "Fast passed on $sha"; exit 0 ;;
    completed*) echo "Fast did not pass on $sha" >&2; exit 1 ;;
  esac
  sleep 10
done
echo "Timed out waiting for test on $sha" >&2
exit 1
