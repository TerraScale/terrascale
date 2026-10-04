#!/usr/bin/env bash
# File a failed slow-suite run as a Forgejo issue with fj, or comment on the
# open issue with the same title. Run it as the last step of the slow job with
# `if: failure()`, after the suite wrote its output to the log given below.
#
#   scripts/ci/report-slow-failure.sh <slow-suite-log>
#
# The report carries the commit, run URL, environment, reproduction steps,
# failing test names and a redacted log tail. Suspected vulnerabilities belong
# in the private security channel, not in this public issue.
#
# Environment: FORGEJO_TOKEN (job token with issue write) and the GITHUB_*
# variables every Actions job gets. REPORT_DRY_RUN=1 prints instead of filing.
set -euo pipefail
export LC_ALL=C

test "$#" -eq 1 || { echo "usage: $0 <slow-suite-log>" >&2; exit 2; }
log=$1
: "${GITHUB_SERVER_URL:?}" "${GITHUB_REPOSITORY:?}" "${GITHUB_RUN_ID:?}" "${GITHUB_SHA:?}"
: "${TS_LIBS_REV:=unknown}"
title="Slow test suite failed"
host=${GITHUB_SERVER_URL#https://}
run_url="$GITHUB_SERVER_URL/$GITHUB_REPOSITORY/actions/runs/${GITHUB_RUN_NUMBER:-$GITHUB_RUN_ID}"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Tests only use synthetic credentials, but logs are still masked before they
# leave the runner. The whole log is redacted at once (quoted values may span
# lines or never close) before failures are extracted or the tail is taken, so
# truncation can never separate a secret from the key that marks it. A value may
# start on the line after its separator (pretty-printed JSON); an unquoted
# sensitive value is redacted to the end of its line. Excerpts are then capped
# in bytes so one huge line cannot produce an issue body Forgejo rejects. Every
# pattern is linear: keys start at a token boundary with bounded affixes.
redact() {
  python3 -c '
import re, sys
text = sys.stdin.read()
rules = [
    (r"\x1b\[[0-9;]*[A-Za-z]", ""),
    (r"((?<![A-Za-z0-9+.-])[A-Za-z][A-Za-z0-9+.-]{0,31}://)[^/@\s]{1,2048}@", r"\1[REDACTED]@"),
    (r"(?is)((?<![A-Za-z0-9_-])[A-Za-z0-9_-]{0,64}?(?:SECRET|TOKEN|AUTHORIZATION|PASSWORD|PASSWD|API[_-]KEY|MASTER[_-]KEY|SIGNING[_-]KEY|OPERATOR[_-]KEY|SESSION|COOKIE|DSN)[A-Za-z0-9_-]{0,64}[\"\x27]?[ \t]*[:=][ \t\r\n]*(?:(?:bearer|basic|digest|token)[ \t]+)?)"
     r"(?:\"(?:[^\"\\]|\\.)*(?:\"|\Z)|\x27(?:[^\x27\\]|\\.)*(?:\x27|\Z)|[^\r\n]*)", r"\1[REDACTED]"),
    (r"(?i)((?:proxy-)?authorization|set-cookie|cookie|x-api-key|api-key)([\"\x27]?[ \t]*[:=][ \t]*)[^\n]*", r"\1\2[REDACTED]"),
    (r"(?i)\b(bearer|basic|digest)([ \t]+)\S+", r"\1\2[REDACTED]"),
    (r"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+", "[REDACTED-JWT]"),
    (r"\b(sk|kub)_[A-Za-z0-9_-]{16,}", "[REDACTED]"),
    (r"`{3,}", "` ` `"),
]
for pattern, replacement in rules:
    text = re.sub(pattern, replacement, text)
sys.stdout.write(text)
'
}

# cap <bytes>: pass at most that many bytes through, then say what was cut.
cap() {
  python3 -c '
import sys
limit = int(sys.argv[1])
data = sys.stdin.buffer.read()
# Cut on a character boundary so the body stays valid UTF-8.
sys.stdout.buffer.write(data[:limit].decode("utf-8", errors="ignore").encode("utf-8"))
if len(data) > limit:
    sys.stdout.write(f"\n[truncated {len(data) - limit} bytes]\n")
' "$1"
}

if [[ -f $log ]]; then
  redact <"$log" >"$work/redacted.log"
else
  : >"$work/redacted.log"
fi

{
  echo "**Commit:** [\`$GITHUB_SHA\`]($GITHUB_SERVER_URL/$GITHUB_REPOSITORY/commit/$GITHUB_SHA) on \`${GITHUB_REF_NAME:-unknown}\` (${GITHUB_EVENT_NAME:-unknown})"
  echo "**Run:** $run_url"
  echo "**Environment:** runner \`${RUNNER_NAME:-unknown}\`, $(dotnet --version 2>/dev/null | sed 's/^/.NET SDK /' || echo '.NET SDK unknown')"
  echo
  echo "### Reproduce"
  echo '```sh'
  echo "git checkout $GITHUB_SHA"
  echo "# ts-libs revision from .forgejo/workflows/slow.yml (TS_LIBS_REV), checked out as <dir>/ts-libs:"
  echo "git -C <dir>/ts-libs checkout $TS_LIBS_REV"
  echo "TerraScaleRoot=<dir> nice -n 19 make test-slow CONFIGURATION=Release"
  echo '```'
  echo
  echo "### Failing tests"
  echo '```text'
  if [[ -f $log ]]; then
    # sed reads all of its input, so no upstream command dies of SIGPIPE under pipefail.
    grep -E '^failed |AssertionException|Expected |but (received|found)' "$work/redacted.log" | sed -n '1,60p' | cap 20000 || echo "(none recognized; see the log tail)"
  else
    echo "(the suite produced no log; see the run)"
  fi
  echo '```'
  echo
  echo "Expected: every slow test passes. Actual: the failures above."
  echo
  echo "<details><summary>Last 80 log lines (redacted)</summary>"
  echo
  echo '```text'
  tail -n 80 "$work/redacted.log" | cap 40000
  echo '```'
  echo "</details>"
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
