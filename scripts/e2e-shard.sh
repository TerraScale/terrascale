#!/usr/bin/env bash
# Prints the go test -run pattern that selects shard INDEX of TOTAL among the
# browser tests. Tests are dealt round-robin in listing order, so the tests of
# one file, which tend to cost alike, spread over every shard. Any further
# arguments are go test flags; pass the run's own flags so the listing builds
# the same test binary the run then reuses from the build cache.
set -euo pipefail

shard=${1:?usage: e2e-shard.sh INDEX/TOTAL [go test flags...]}
shift
if [[ ! $shard =~ ^([1-9][0-9]{0,8})/([1-9][0-9]{0,8})$ ]]; then
	echo "e2e-shard: shard $shard is not INDEX/TOTAL with 1 <= INDEX <= TOTAL" >&2
	exit 2
fi
index=${BASH_REMATCH[1]}
total=${BASH_REMATCH[2]}
if ((index > total)); then
	echo "e2e-shard: shard $shard has INDEX greater than TOTAL" >&2
	exit 2
fi

tests=$(go test "$@" -list . ./tests/... | awk '/^Test/')
selected=$(awk -v index_="$index" -v total="$total" \
	'(NR - 1) % total == index_ - 1 { printf "%s%s", sep, $0; sep = "|" }' <<<"$tests")
if [[ -z $selected ]]; then
	echo "e2e-shard: shard $shard selected no tests" >&2
	exit 3
fi
printf '^(%s)$\n' "$selected"
