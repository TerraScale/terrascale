#!/usr/bin/env bash
# Check that the actual shard patterns cover every listed test exactly once.
# Test listing skips application/browser startup; use the run's build flags.
set -euo pipefail

total=${1:?usage: check-e2e-shards.sh TOTAL [go test flags...]}
shift
[[ $total =~ ^[1-9][0-9]{0,8}$ ]] || { echo "invalid shard total: $total" >&2; exit 2; }
listing=$(go test "$@" -list . ./tests/... | awk '/^Test/')
[[ -n $listing ]] || { echo 'browser test listing was empty' >&2; exit 1; }
mapfile -t tests <<< "$listing"
((total <= ${#tests[@]})) || { echo 'more shards than browser tests' >&2; exit 2; }
patterns=()
for ((index=1; index<=total; index++)); do
	pattern=$(scripts/e2e-shard.sh "$index/$total" "$@")
	patterns+=("$pattern")
done
for test in "${tests[@]}"; do
	matches=0
	for pattern in "${patterns[@]}"; do
		if [[ $test =~ $pattern ]]; then matches=$((matches + 1)); fi
	done
	if ((matches != 1)); then
		echo "$test matches $matches shards; expected exactly one" >&2
		exit 1
	fi
done
# Invalid indices, leading zeros, and a shard with no tests must fail.
empty=$((${#tests[@]} + 1))
for invalid in 0/4 5/4 04/4 1/04 1/0 1/2/3 "$empty/$empty"; do
	if scripts/e2e-shard.sh "$invalid" "$@" >/dev/null 2>&1; then
		echo "invalid shard $invalid was accepted" >&2
		exit 1
	fi
done
printf 'Verified %s tests across %s shards, each exactly once; invalid shard inputs rejected.\n' "${#tests[@]}" "$total"
