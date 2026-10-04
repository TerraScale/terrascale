#!/usr/bin/env bash
# Prove the Slow failure reporter works before it is needed: it renders, redacts
# secrets that span lines, keeps short logs whole, and stays within a small
# memory bound for huge logs and huge single lines. Runs as a dry run.
set -euo pipefail

repo=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
export GITHUB_SERVER_URL=${GITHUB_SERVER_URL:-https://git.terrascale.tech}
export GITHUB_REPOSITORY=${GITHUB_REPOSITORY:-TerraScale/terrabase}
export GITHUB_RUN_ID=${GITHUB_RUN_ID:-0} GITHUB_SHA=${GITHUB_SHA:-0000000000000000000000000000000000000000}

report() { REPORT_DRY_RUN=1 bash "$repo/scripts/ci/report-slow-failure.sh" "$1"; }
fail() { echo "reporter check failed: $*" >&2; exit 1; }

printf '%s\n' 'failed Example (1s)' 'Authorization: Bearer secret-one' \
  'PASSWORD="' 'Expected secret-two' '"' '"api_key":' '  "secret-three"' 'after the secrets' > "$work/secrets.log"
report "$work/secrets.log" > "$work/secrets.md"
grep -q '^# Slow test suite failed' "$work/secrets.md" || fail 'no title'
grep -q '^failed Example' "$work/secrets.md" || fail 'failure line missing'
grep -q 'after the secrets' "$work/secrets.md" || fail 'context after a multi-line secret was lost'
! grep -q 'secret-one\|secret-two\|secret-three' "$work/secrets.md" || fail 'a secret was published'

printf '%s\n' 'Unhandled exception: connection refused' > "$work/short.log"
report "$work/short.log" > "$work/short.md"
grep -q 'Unhandled exception: connection refused' "$work/short.md" || fail 'a one-line log lost its only line'

# 64 MiB of noise and one 16 MiB matching line, all within 128 MiB of peak RSS.
# Process substitution keeps the early-closing producers out of pipefail.
{
  head -c 67108864 < <(yes 'noise line from a runaway test')
  printf '\nfailed Huge '
  tr '\0' 'x' < <(head -c 16777216 /dev/zero)
  printf '\nfailed Late (1s)\n'
} > "$work/large.log"
python3 "$repo/scripts/ci/peak-rss.py" 131072 "$work/large.md" \
  env REPORT_DRY_RUN=1 bash "$repo/scripts/ci/report-slow-failure.sh" "$work/large.log"
grep -q '^failed Late' "$work/large.md" || fail 'a late failure was not reported'
grep -q 'line truncated' "$work/large.md" || fail 'a huge line was not truncated'
echo 'reporter checks passed'
