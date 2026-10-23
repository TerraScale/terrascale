#!/usr/bin/env bash
# Skip obsolete queued/manual reruns, even if Fast passed for their exact tree.
set -euo pipefail

test "$#" -eq 2 || { echo "usage: $0 <owner/repo> <sha>" >&2; exit 2; }
repo=$1 sha=$2
head=$(gh api "repos/$repo/git/ref/heads/main" --jq .object.sha)
[[ $head =~ ^[a-f0-9]{40}$ ]] || { echo 'Cannot determine current main SHA' >&2; exit 1; }
if [[ $head == "$sha" ]]; then
  echo 'current=true' >> "$GITHUB_OUTPUT"
else
  echo "Skipping superseded main $sha; current main is $head"
  echo 'current=false' >> "$GITHUB_OUTPUT"
fi
