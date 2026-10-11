#!/usr/bin/env bash
# File a failed slow-suite run as a Forgejo issue with fj, or comment on the
# open issue with the same title. Run it as the last step of the slow job with
# `if: failure()`, after the suite wrote its output to the log given below.
#
#   scripts/ci/report-slow-failure.sh <slow-suite-log>
#
# The report carries the commit, run URL, environment, reproduction steps,
# expected/actual behaviour and the names of the failing tests. It contains no
# log text at all: test output can hold arbitrary values, and no redactor can
# prove free text safe. The complete log stays in the linked Forgejo run, behind
# the repository's own permissions. Suspected vulnerabilities belong in the
# private security channel, not in this public issue.
#
# Environment: FORGEJO_TOKEN (job token with issue write) and the GITHUB_*
# variables every Actions job gets. REPORT_DRY_RUN=1 prints instead of filing.
set -euo pipefail
export LC_ALL=C

test "$#" -eq 1 || { echo "usage: $0 <slow-suite-log>" >&2; exit 2; }
log=$1
: "${GITHUB_SERVER_URL:?}" "${GITHUB_REPOSITORY:?}" "${GITHUB_RUN_ID:?}" "${GITHUB_SHA:?}"
: "${TS_DOTNET_REV:=unknown}"
title="Slow test suite failed"
host=${GITHUB_SERVER_URL#https://}
run_url="$GITHUB_SERVER_URL/$GITHUB_REPOSITORY/actions/runs/${GITHUB_RUN_NUMBER:-$GITHUB_RUN_ID}"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Failing tests by method name only, and only names of real slow-suite test
# methods (read from the test sources), so arbitrary output that merely looks
# like "failed <identifier>" can never reach the issue.
repo=$(cd "$(dirname "$0")/../.." && pwd)
inventory="$work/test-inventory"
grep -rhoE 'public async Task [A-Za-z_][A-Za-z0-9_]*\(' "$repo/tests/TerraScale.Auth.Slow" --include='*.cs' |
  sed -E 's/public async Task ([A-Za-z0-9_]+)\(/\1/' | sort -u >"$inventory" || true
failing_tests() {
  [[ -f $log && -s $inventory ]] || return 0
  LC_ALL=C grep -aoE '^failed [A-Za-z_][A-Za-z0-9_]{0,200}' "$log" | cut -c8- | grep -Fxf "$inventory" | sort | uniq -c |
    awk '{ printf "- %s%s\n", $2, ($1 > 1 ? " (" $1 " cases)" : "") }' | head -n 100 || true
}

{
  echo "**Commit:** [\`$GITHUB_SHA\`]($GITHUB_SERVER_URL/$GITHUB_REPOSITORY/commit/$GITHUB_SHA) on \`${GITHUB_REF_NAME:-unknown}\` (${GITHUB_EVENT_NAME:-unknown})"
  echo "**Run:** $run_url"
  echo "**Environment:** runner \`${RUNNER_NAME:-unknown}\`, $(dotnet --version 2>/dev/null | sed 's/^/.NET SDK /' || echo '.NET SDK unknown')"
  echo
  echo "### Reproduce"
  echo '```sh'
  echo "git checkout $GITHUB_SHA"
  echo "# ts-dotnet revision from .forgejo/workflows/slow.yml (TS_DOTNET_REV), checked out under <dir>:"
  echo "#   git -C <dir>/ts-dotnet checkout $TS_DOTNET_REV"
  echo "TerraScaleRoot=<dir> nice -n 19 make test-slow CONFIGURATION=Release"
  echo '```'
  echo
  echo "### Failing tests"
  tests=$(failing_tests)
  if [[ -n $tests ]]; then printf '%s\n' "$tests"; else echo "- (none recognized; see the run)"; fi
  echo
  echo "Expected: every slow test passes. Actual: the tests above failed."
  echo
  echo "The full output, including assertion messages, is in the run log: $run_url"
  echo
  echo "_Filed by scripts/ci/report-slow-failure.sh. The slow suite never blocks merges or releases; close this issue once it is fixed._"
} >"$work/body.md"

if [[ ${REPORT_DRY_RUN:-} == 1 ]]; then
  printf '# %s\n\n' "$title"
  cat "$work/body.md"
  exit 0
fi

: "${FORGEJO_TOKEN:?FORGEJO_TOKEN is required}"
command -v fj >/dev/null || { echo "fj is not installed on this runner" >&2; exit 1; }
# fj keeps its login under XDG_DATA_HOME. Point it at this script's temporary
# directory so the job token never lands in a reused home directory and
# concurrent jobs cannot overwrite each other's login; the EXIT trap removes it.
export XDG_DATA_HOME="$work/fj-data" XDG_CONFIG_HOME="$work/fj-config"
printf '%s' "$FORGEJO_TOKEN" | fj -H "$host" auth add-token >/dev/null

# fj prints "#<n>: <title> (by <user>)" with Unicode isolation marks.
number=$(fj -H "$host" issue search -r "$GITHUB_REPOSITORY" -s open "$title" |
  sed -E 's/\xe2\x81\xa8|\xe2\x81\xa9//g' |
  sed -nE "s/^#([0-9]+): ${title} \(by .*\)$/\1/p" | sed -n '1p')

if [[ -n $number ]]; then
  { echo "Failed again."; echo; cat "$work/body.md"; } >"$work/comment.md"
  fj -H "$host" issue comment "$GITHUB_REPOSITORY#$number" --body-file "$work/comment.md"
  echo "Commented on $GITHUB_SERVER_URL/$GITHUB_REPOSITORY/issues/$number"
else
  fj -H "$host" issue create -r "$GITHUB_REPOSITORY" --no-template --body-file "$work/body.md" "$title"
fi
