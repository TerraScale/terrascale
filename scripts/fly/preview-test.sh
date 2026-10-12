#!/usr/bin/env bash
# Self-check for scripts/fly/preview.sh against a fake flyctl, curl and gh; CI runs it in the `test` job. Each container
# gets exactly its own secrets (ts-auth its two generated keys), and the reaper destroys previews of closed or merged PRs
# and previews older than 24 hours, keeping a preview whose PR state cannot be read.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin"
cat >"$tmp/bin/flyctl" <<'FAKE'
#!/usr/bin/env bash
echo "flyctl $*" >>"$STATE/calls"
case "$1 $2" in
  "apps list") jq -cn --arg apps "$(cat "$STATE/apps")" '$apps | split(" ") | map({Name: .})' ;;
  "volumes list") echo '[{"id": "vol_1"}]' ;;
  "storage destroy" | "apps destroy") ;;
  *) echo "fake flyctl: unexpected $*" >&2; exit 1 ;;
esac
FAKE
# The Machines API: GET lists an app's machines (created_at from $STATE/created-<app>), POST stores its body in $STATE/posted.
cat >"$tmp/bin/curl" <<'FAKE'
#!/usr/bin/env bash
url=${*: -1}
if [[ $* == *" -d "* ]]; then
  while [[ $1 != -d ]]; do shift; done
  printf '%s' "$2" >"$STATE/posted"
  echo '{"id": "m1", "state": "created"}'
  exit 0
fi
app=$(grep -oE 'ts-pr-[0-9]+' <<<"$url")
if [[ -e $STATE/created-$app ]]; then jq -cn --arg c "$(cat "$STATE/created-$app")" '[{id: "m1", created_at: $c}]'; else echo '[]'; fi
FAKE
# A PR's state from $STATE/pr-<n>; no file is a failed lookup.
cat >"$tmp/bin/gh" <<'FAKE'
#!/usr/bin/env bash
[[ -e $STATE/pr-$3 ]] || { echo "gh: lookup failed" >&2; exit 1; }
cat "$STATE/pr-$3"
FAKE
chmod +x "$tmp/bin/flyctl" "$tmp/bin/curl" "$tmp/bin/gh"
export PATH="$tmp/bin:$PATH" FLY_API_TOKEN=fake
fail() { echo "preview-test: $*" >&2; exit 1; }

export STATE=$tmp/machine
mkdir -p "$STATE"
"$here/preview.sh" machine 7 registry.fly.io/ts-pr-7:terrabase-x registry.fly.io/ts-pr-7:ts-auth-x >/dev/null
secrets() { jq -r --arg c "$1" '.config.containers[] | select(.name == $c) | [.secrets[]? | .name] | join(",")' "$STATE/posted"; }
[[ $(secrets ts-auth) == "TS_AUTH_MASTER_KEY,TS_AUTH_AUDIT_SIGNING_KEY" ]] || fail "ts-auth gets: $(secrets ts-auth)"
[[ $(secrets terrabase) == "AWS_ENDPOINT_URL_S3,AWS_REGION,BUCKET_NAME,AWS_ACCESS_KEY_ID,AWS_SECRET_ACCESS_KEY,TERRABASE_MASTER_KEY" ]] \
  || fail "terrabase gets: $(secrets terrabase)"
jq -e '[.config.containers[].secrets[]? | .env_var == .name] | all' "$STATE/posted" >/dev/null || fail "a secret is renamed"

export STATE=$tmp/reap
mkdir -p "$STATE"
now=$(date -u +%Y-%m-%dT%H:%M:%SZ) old=$(date -u -d '30 hours ago' +%Y-%m-%dT%H:%M:%SZ)
echo "ts-pr-1 ts-pr-2 ts-pr-3 ts-pr-4 ts-pr-5 ts-pr-6" >"$STATE/apps"
for n in 1 2 3 5; do echo "$now" >"$STATE/created-ts-pr-$n"; done
for n in 4 6; do echo "$old" >"$STATE/created-ts-pr-$n"; done
echo OPEN >"$STATE/pr-1"; echo CLOSED >"$STATE/pr-2"; echo MERGED >"$STATE/pr-3"; echo OPEN >"$STATE/pr-4"
"$here/preview.sh" reap >"$STATE/out"
destroyed=$(grep -oE '^flyctl apps destroy ts-pr-[0-9]+' "$STATE/calls" | grep -oE '[0-9]+$' | paste -sd, -)
[[ $destroyed == "2,3,4,6" ]] || fail "reap destroyed [$destroyed], expected 2,3,4,6: $(cat "$STATE/out")"
[[ $(grep -c '^flyctl storage destroy terrascale-ts-pr-' "$STATE/calls") == 4 ]] || fail "reap left a preview bucket"
echo "preview: all checks passed"
