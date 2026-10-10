#!/usr/bin/env bash
# The workflow matrix is the single source of truth for browser shard count.
set -euo pipefail
workflow=${1:-.forgejo/workflows/ci.yml}
matrix=$(sed -n 's/^[[:space:]]*shard: \[\([^]]*\)\][[:space:]]*$/\1/p' "$workflow")
[[ $matrix =~ ^[0-9]+(,[[:space:]]*[0-9]+)*$ ]] || {
	echo 'expected one explicit browser shard matrix' >&2; exit 2;
}
IFS=, read -r -a indices <<< "$matrix"
for position in "${!indices[@]}"; do
	index=${indices[position]//[[:space:]]/}
	[[ $index == "$((position + 1))" ]] || {
		echo 'browser shard matrix must contain consecutive indices starting at 1' >&2; exit 2;
	}
done
printf '%s\n' "${#indices[@]}"
