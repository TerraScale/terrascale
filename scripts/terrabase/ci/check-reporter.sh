#!/usr/bin/env bash
# Prove the Slow failure reporter works before it is needed: it renders, redacts
# secrets that span lines, keeps short logs whole, and stays within a small
# memory bound for huge logs and huge single lines. Only the test supplies a fake fj.
set -euo pipefail

repo=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
export GITHUB_SERVER_URL=${GITHUB_SERVER_URL:-https://git.terrascale.tech}
export GITHUB_REPOSITORY=${GITHUB_REPOSITORY:-TerraScale/terrabase}
export GITHUB_RUN_ID=${GITHUB_RUN_ID:-0} GITHUB_SHA=${GITHUB_SHA:-0000000000000000000000000000000000000000}
export FORGEJO_TOKEN=check-token
mkdir -p "$work/bin"
: > "$work/issues"
cat > "$work/bin/fj" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
work=$(dirname "$0")/..
shift 2 # -H HOST
case "$1 $2" in
  'auth add-token') cat >/dev/null ;;
  'issue search') cat "$work/issues" ;;
  'issue create')
    printf 'create\n' >> "$work/created"
    while [[ $1 != --body-file ]]; do shift; done
    printf '# Slow test suite failed\n\n'
    cat "$2"
    ;;
  'issue comment')
    printf '%s\n' "$3" >> "$work/commented"
    while [[ $1 != --body-file ]]; do shift; done
    cp "$2" "$work/comment.md"
    ;;
  *) echo 'unexpected fj command' >&2; exit 1 ;;
esac
SH
chmod 0700 "$work/bin/fj"
export PATH="$work/bin:$PATH"

report() { bash "$repo/scripts/ci/report-slow-failure.sh" "$1"; }
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
  bash "$repo/scripts/ci/report-slow-failure.sh" "$work/large.log"
grep -q '^failed Late' "$work/large.md" || fail 'a late failure was not reported'
grep -q 'line truncated' "$work/large.md" || fail 'a huge line was not truncated'

# fj isolates numbers and titles with U+2068/U+2069; select the first exact title.
printf '#\342\201\250%s\342\201\251: \342\201\250%s\342\201\251 (by fixture)\n' \
  41 'Slow test suite failed previously' \
  42 'Slow test suite failed' \
  43 'Slow test suite failed' > "$work/issues"
cp "$work/created" "$work/created-before-comment"
report "$work/secrets.log" > "$work/comment-result"
printf '%s#42\n' "$GITHUB_REPOSITORY" > "$work/expected-commented"
diff -u "$work/expected-commented" "$work/commented"
[[ $(head -n 1 "$work/comment.md") == 'Failed again.' ]] || fail 'repeat failure prefix missing'
grep -q '^failed Example' "$work/comment.md" || fail 'comment failure line missing'
grep -q 'after the secrets' "$work/comment.md" || fail 'comment context after a multi-line secret was lost'
! grep -q 'secret-one\|secret-two\|secret-three' "$work/comment.md" || fail 'a secret was commented'
diff -u "$work/created-before-comment" "$work/created"
echo 'reporter checks passed'
