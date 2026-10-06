#!/usr/bin/env bash
# Post-deploy smoke for the dashboard's own sign-in (TS-582, see "Sign-in
# design (do not revert)" in docs/development.md): GET /login must render the
# dashboard's form, not redirect to ts-auth's hosted page, and a password
# sign-in for an unknown email must reach ts-auth and be refused as wrong
# credentials, not throttled.
#
# Usage: scripts/smoke-native-login.sh https://dash.terrascale.tech
set -euo pipefail

base=${1:?usage: smoke-native-login.sh BASE_URL}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
jar=$work/cookies
page=$work/login.html

fail() {
	echo "::error::native sign-in smoke failed: $*" >&2
	exit 1
}

status=$(curl -sS --retry 6 --retry-delay 2 --retry-all-errors --connect-timeout 10 --max-time 20 \
	-c "$jar" -o "$page" -w '%{http_code}' "$base/login") || fail "GET /login did not answer"
[ "$status" = 200 ] || fail "GET /login answered $status, want 200 with the dashboard's form (303 is ts-auth's hosted page)"
for marker in 'type="password"' 'href="/login/code' 'href="/login/sso'; do
	grep -qF "$marker" "$page" || fail "GET /login has no $marker"
done
csrf=$(sed -nE 's/.*name="_csrf" value="([^"]+)".*/\1/p' "$page" | head -n 1)
[ -n "$csrf" ] || fail "GET /login has no CSRF token"

email="deploy-smoke-$(date +%s)-$RANDOM@example.invalid"
status=$(curl -sS --connect-timeout 10 --max-time 20 -b "$jar" -c "$jar" -o /dev/null -w '%{http_code}' \
	-H "Origin: $base" --data-urlencode "_csrf=$csrf" --data-urlencode "email=$email" \
	--data-urlencode "password=deploy smoke, not a password" "$base/login") || fail "POST /login did not answer"
[ "$status" = 401 ] || fail "password sign-in for an unknown email answered $status, want 401 (ts-auth refused it); 429 is the throttle, 503 ts-auth"

echo "native sign-in smoke passed: GET /login renders the form, an unknown email is refused with 401"
