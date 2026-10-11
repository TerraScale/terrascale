#!/usr/bin/env bash
# End-to-end smoke of the REAL stack: this script boots the real ts-auth
# service (its own process, production startup validation) against a REAL
# compatible TerraBase member and drives every API family
# once over HTTP. Webhook delivery and signature are checked when a controlled
# public HTTPS sink is configured.
#
# Usage:
#   scripts/smoke-real-stack.sh            # member at https://127.0.0.1:8420, service on a free port
#   MEMBER_URL=... SERVICE_LISTEN=http://127.0.0.1:9090 scripts/smoke-real-stack.sh
#   WEBHOOK_SINK_URL=https://your-public-sink.example/hook \
#   WEBHOOK_SINK_CAPTURE_FILE=/path/to/sink-receipts.jsonl scripts/smoke-real-stack.sh
# The optional operator-owned sink writes JSON lines with path, headers, and
# body fields. Without both sink settings, webhook delivery is skipped.
set -uo pipefail

MEMBER_URL=${MEMBER_URL:-https://127.0.0.1:8420}
SERVICE_LISTEN=${SERVICE_LISTEN:-http://127.0.0.1:8480}
REPO_ROOT=$(cd "$(dirname "$0")/.." && pwd)
# Override SERVICE_BINARY to smoke a built worktree/pinned checkout.
SERVICE_BINARY=${SERVICE_BINARY:-$REPO_ROOT/artifacts/ci/container-publish/ts-auth}
WEBHOOK_SINK_URL=${WEBHOOK_SINK_URL:-}
WEBHOOK_SINK_CAPTURE_FILE=${WEBHOOK_SINK_CAPTURE_FILE:-}
FAILURES=0

pass() { echo "PASS  $1"; }
fail() { echo "FAIL  $1"; FAILURES=$((FAILURES + 1)); }

check() { # check <label> <expected> <actual>
    if [[ "$2" == "$3" ]]; then pass "$1"; else fail "$1 (expected [$2], got [$3])"; fi
}

cleanup() {
    [[ -n "${SERVICE_PID:-}" ]] && kill "$SERVICE_PID" 2>/dev/null
    wait 2>/dev/null
}
trap cleanup EXIT

# ---- 0. Preconditions ----------------------------------------------------------------

MEMBER_CODE=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$MEMBER_URL/" 2>/dev/null)
if [[ "$MEMBER_CODE" == "000" ]]; then
    echo "No TerraBase member at $MEMBER_URL."
    echo "Provide a running TerraBase document API member at MEMBER_URL."
    exit 2
fi

if [[ -n "$WEBHOOK_SINK_URL" || -n "$WEBHOOK_SINK_CAPTURE_FILE" ]]; then
    if [[ -z "$WEBHOOK_SINK_URL" || -z "$WEBHOOK_SINK_CAPTURE_FILE" ]]; then
        echo "Set both WEBHOOK_SINK_URL and WEBHOOK_SINK_CAPTURE_FILE for webhook delivery smoke."
        exit 2
    fi
    if [[ "$WEBHOOK_SINK_URL" != https://* || ! -f "$WEBHOOK_SINK_CAPTURE_FILE" ]]; then
        echo "Webhook smoke requires a controlled public HTTPS sink and an existing capture file."
        exit 2
    fi
    WEBHOOK_SMOKE=1
else
    WEBHOOK_SMOKE=0
    echo "SKIP webhook delivery/signature: set WEBHOOK_SINK_URL and WEBHOOK_SINK_CAPTURE_FILE for a controlled public HTTPS sink."
fi

# ---- 1. Boot the real service process ------------------------------------------------

OP_KEY="smoke-operator-$(head -c 12 /dev/urandom | base64 -w0 | tr -d '=+/')"
if [[ ! -x "$SERVICE_BINARY" ]]; then
    echo "ts-auth executable missing: $SERVICE_BINARY"
    echo "Build the self-contained executable from the repository root first:"
    echo "  scripts/ci/stage-release.sh artifacts/ci/container-publish"
    exit 2
fi

TS_AUTH_ENVIRONMENT=Development \
TS_AUTH_LISTEN="$SERVICE_LISTEN" \
TS_AUTH_PUBLIC_ORIGIN="$SERVICE_LISTEN" \
TS_AUTH_TERRASCALE_API_URL="$MEMBER_URL" \
TS_AUTH_TERRASCALE_API_KEY="${TS_AUTH_E2E_API_KEY:-dev}" \
TS_AUTH_DATABASE="${TS_AUTH_E2E_DATABASE:-00000000-0000-0000-0000-000000000000/ts-auth-dev}" \
TS_AUTH_MASTER_KEY="$(head -c 32 /dev/urandom | base64 -w0)" \
TS_AUTH_AUDIT_SIGNING_KEY="$(head -c 32 /dev/urandom | base64 -w0)" \
TS_AUTH_OPERATOR_KEY="$OP_KEY" \
"$SERVICE_BINARY" >/tmp/ts-auth-smoke-service.log 2>&1 &
SERVICE_PID=$!

SERVICE_UP=0
for _ in $(seq 1 120); do
    if curl -sf -o /dev/null "$SERVICE_LISTEN/healthz"; then SERVICE_UP=1; break; fi
    sleep 1
done
if [[ $SERVICE_UP -ne 1 ]]; then
    echo "Service never became healthy; log:"
    tail -30 /tmp/ts-auth-smoke-service.log
    exit 2
fi
pass "service boots against the real member and answers /healthz"

SLUG="smoke-$(head -c 4 /dev/urandom | base64 -w0 | tr -d '=+/' | tr 'A-Z' 'a-z')"
EMAIL="smoke-$(head -c 4 /dev/urandom | base64 -w0 | tr -d '=+/' | tr 'A-Z' 'a-z')@example.test"
PASSWORD="correct-horse-battery"

# ---- 2. Operator onboarding ----------------------------------------------------------

BOOTSTRAP_KEY="smoke-bootstrap-$(head -c 16 /dev/urandom | base64 -w0 | tr -d '=+/')"
INSTANCE=$(curl -sf -X POST "$SERVICE_LISTEN/v1/instances" \
    -H "Authorization: Bearer $OP_KEY" \
    -H "Idempotency-Key: $BOOTSTRAP_KEY" \
    -H 'Content-Type: application/json' \
    -d "{\"slug\":\"$SLUG\",\"name\":\"Smoke Test\"}")
if [[ -z "$INSTANCE" ]]; then fail "operator instance create"; exit 1; fi
API_KEY=$(jq -r .api_key <<<"$INSTANCE")
pass "operator creates instance + first sk_ key"

# ---- 3. Hosted UI: sign-up, session, account ------------------------------------------

SIGNUP=$(curl -s -D /tmp/smoke-signup-headers -o /dev/null -X POST "$SERVICE_LISTEN/ui/$SLUG/sign-up" \
    -H "Origin: $SERVICE_LISTEN" \
    --data-urlencode "email=$EMAIL" --data-urlencode "password=$PASSWORD" --data-urlencode "name=Smoke Tester")
COOKIE=$(grep -i '^set-cookie: ' /tmp/smoke-signup-headers | head -1 | sed 's/^[Ss]et-[Cc]ookie: //' | cut -d';' -f1 | tr -d '\r')
check "hosted sign-up sets a session cookie" "yes" "$([[ -n "$COOKIE" ]] && echo yes || echo no)"

ACCOUNT=$(curl -s -b "$COOKIE" "$SERVICE_LISTEN/ui/$SLUG/account")
check "account page renders the signed-in user" "yes" "$(grep -q "$EMAIL" <<<"$ACCOUNT" && echo yes || echo no)"

# ---- 4. Sign-out / sign-in ------------------------------------------------------------

curl -s -o /dev/null -b "$COOKIE" -c /tmp/smoke-cookies -X POST \
    -H "Origin: $SERVICE_LISTEN" "$SERVICE_LISTEN/ui/$SLUG/sign-out"
SIGNIN=$(curl -s -D /tmp/smoke-signin-headers -o /dev/null -c /tmp/smoke-cookies -X POST "$SERVICE_LISTEN/ui/$SLUG/sign-in" \
    -H "Origin: $SERVICE_LISTEN" \
    --data-urlencode "email=$EMAIL" --data-urlencode "password=$PASSWORD")
SIGNIN_COOKIE=$(grep -i '^set-cookie: ' /tmp/smoke-signin-headers | head -1 | sed 's/^[Ss]et-[Cc]ookie: //' | cut -d';' -f1 | tr -d '\r')
check "password sign-in re-establishes the session" "yes" "$([[ -n "$SIGNIN_COOKIE" ]] && echo yes || echo no)"
COOKIE="$SIGNIN_COOKIE"

WRONG=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$SERVICE_LISTEN/ui/$SLUG/sign-in" \
    -H "Origin: $SERVICE_LISTEN" \
    --data-urlencode "email=$EMAIL" --data-urlencode "password=wrong-password")
check "wrong password does not redirect as success" "yes" "$([[ "$WRONG" != 302 ]] && echo yes || echo no)"

# ---- 5. Management API ----------------------------------------------------------------

USERS=$(curl -sf -H "Authorization: Bearer $API_KEY" "$SERVICE_LISTEN/v1/$SLUG/users?limit=10")
check "management API lists users over the sk_ key" "yes" "$(grep -q "$EMAIL" <<<"$USERS" && echo yes || echo no)"

CREATED=$(curl -sf -X POST -H "Authorization: Bearer $API_KEY" -H 'Content-Type: application/json' \
    -d "{\"email\":\"api-user-$SLUG@example.test\",\"password\":\"api-user-pass-1\"}" \
    "$SERVICE_LISTEN/v1/$SLUG/users")
check "management API creates users" "yes" "$(jq -re '.email // empty' <<<"$CREATED" | grep -q api-user && echo yes || echo no)"

BRAND=$(curl -sf -X PATCH -H "Authorization: Bearer $API_KEY" -H 'Content-Type: application/json' \
    -d '{"logo_url":"https://cdn.example.test/logo.png","theme_color":"#123456"}' \
    "$SERVICE_LISTEN/v1/$SLUG/instance")
check "instance branding round-trips" "#123456" "$(jq -r .theme_color <<<"$BRAND")"

# ---- 6. OAuth 2.1 / OIDC provider -----------------------------------------------------

CLIENT=$(curl -sf -X POST -H "Authorization: Bearer $API_KEY" -H 'Content-Type: application/json' \
    -d '{"name":"Smoke App","redirect_uris":["https://app.example.test/callback"],"public":true}' \
    "$SERVICE_LISTEN/v1/$SLUG/oauth-clients")
CLIENT_ID=$(jq -r .client.clientId <<<"$CLIENT")
REDIRECT="https://app.example.test/callback"

VERIFIER=$(head -c 48 /dev/urandom | base64 -w0 | tr -d '=+/' | tr '+/' '-_')
CHALLENGE=$(printf '%s' "$VERIFIER" | openssl dgst -sha256 -binary | base64 -w0 | tr -d '=' | tr '+/' '-_')

AUTHZ=$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' -b "$COOKIE" \
    "$SERVICE_LISTEN/$SLUG/oauth/authorize?response_type=code&client_id=$CLIENT_ID&redirect_uri=$(python3 -c "import urllib.parse,sys;print(urllib.parse.quote(sys.argv[1],safe=''))" "$REDIRECT")&scope=$(python3 -c "import urllib.parse;print(urllib.parse.quote('openid profile email',safe=''))")&state=smoke-state&nonce=smoke-nonce&code_challenge=$CHALLENGE&code_challenge_method=S256")
CODE=$(sed -n 's/.*[?&]code=\([^&]*\).*/\1/p' <<<"$(cut -d' ' -f2- <<<"$AUTHZ")")
STATE_BACK=$(sed -n 's/.*[?&]state=\([^&]*\).*/\1/p' <<<"$(cut -d' ' -f2- <<<"$AUTHZ")")
check "authorize redirects with a code (PKCE S256)" "smoke-state" "$STATE_BACK"

TOKENS=$(curl -sf -X POST "$SERVICE_LISTEN/$SLUG/oauth/token" \
    -d "grant_type=authorization_code&code=$CODE&redirect_uri=$REDIRECT&client_id=$CLIENT_ID&code_verifier=$VERIFIER")
ACCESS=$(jq -r .access_token <<<"$TOKENS")
ID_TOKEN=$(jq -r .id_token <<<"$TOKENS")
check "token endpoint exchanges code for a three-segment JWT" "3" "$(awk -F. '{print NF}' <<<"$ACCESS")"
check "id_token issued for openid scope" "yes" "$([[ "$ID_TOKEN" != null && ${#ID_TOKEN} -gt 20 ]] && echo yes || echo no)"

USERINFO=$(curl -sf -H "Authorization: Bearer $ACCESS" "$SERVICE_LISTEN/$SLUG/oauth/userinfo")
check "userinfo answers for the minted access token" "$EMAIL" "$(jq -r .email <<<"$USERINFO")"

DISCOVERY=$(curl -sf "$SERVICE_LISTEN/$SLUG/.well-known/openid-configuration")
check "OIDC discovery advertises the issuer" "$SERVICE_LISTEN/$SLUG" "$(jq -r .issuer <<<"$DISCOVERY")"
JWKS=$(curl -sf "$SERVICE_LISTEN/$SLUG/.well-known/jwks.json")
check "JWKS publishes the instance signing keys" "yes" "$(jq -re 'if (.keys | length) > 0 then "yes" else "no" end' <<<"$JWKS")"

# ---- 7. SCIM directory sync -----------------------------------------------------------

DIRECTORY=$(curl -sf -X POST -H "Authorization: Bearer $API_KEY" -H 'Content-Type: application/json' \
    -d '{}' "$SERVICE_LISTEN/v1/$SLUG/directories")
DIR_ID=$(jq -r .id <<<"$DIRECTORY")
DIR_TOKEN=$(jq -r .token <<<"$DIRECTORY")
SCIM_USER=$(curl -s -D /tmp/smoke-scim-headers -o /dev/null -w '%{http_code}' -X POST "$SERVICE_LISTEN/scim/$DIR_ID/Users" \
    -H "Authorization: Bearer $DIR_TOKEN" -H 'Content-Type: application/scim+json' \
    -d "{\"schemas\":[\"urn:ietf:params:scim:schemas:core:2.0:User\"],\"userName\":\"scim-$SLUG@example.test\",\"emails\":[{\"value\":\"scim-$SLUG@example.test\",\"primary\":true}]}")
check "SCIM provision creates a user (201 + Location)" "201" "$SCIM_USER"
grep -qi '^location:' /tmp/smoke-scim-headers && pass "SCIM 201 carries Location header" || fail "SCIM 201 carries Location header"

# ---- 8. Webhook delivery with signature verification -----------------------------------

if [[ $WEBHOOK_SMOKE -eq 0 ]]; then
    echo "SKIP webhook delivery/signature (no controlled public HTTPS sink configured)"
else
    SINK_MARK=$(wc -l < "$WEBHOOK_SINK_CAPTURE_FILE")
    WH=$(curl -sf -X POST -H "Authorization: Bearer $API_KEY" -H 'Content-Type: application/json' \
        -d "$(jq -nc --arg url "$WEBHOOK_SINK_URL" '{url:$url,events:["user.*"]}')" \
        "$SERVICE_LISTEN/v1/$SLUG/webhooks")
    WH_SECRET=$(jq -r '.signing_secret // empty' <<<"$WH")
    check "webhook endpoint accepts controlled HTTPS sink" "yes" \
        "$([[ -n "$WH_SECRET" ]] && echo yes || echo no)"

    API_USERS_RESULT=$(curl -sS -w $'\n%{http_code}' \
        -H "Authorization: Bearer $API_KEY" \
        "$SERVICE_LISTEN/v1/$SLUG/users?limit=10")
    API_USERS_CURL_STATUS=$?
    API_USERS_HTTP_STATUS=${API_USERS_RESULT##*$'\n'}
    API_USERS_BODY=${API_USERS_RESULT%$'\n'*}

    API_USER_ID=""
    if [[ $API_USERS_CURL_STATUS -ne 0 ]]; then
        fail "webhook trigger user-list request failed (curl exit $API_USERS_CURL_STATUS, HTTP $API_USERS_HTTP_STATUS)"
    elif [[ "$API_USERS_HTTP_STATUS" != "200" ]]; then
        fail "webhook trigger user-list request returned HTTP $API_USERS_HTTP_STATUS"
    elif ! jq -e '(.data | type) == "array"' <<<"$API_USERS_BODY" >/dev/null 2>&1; then
        fail "webhook trigger user-list response did not contain a valid data array"
    else
        API_USER_ID=$(jq -r '[.data[] | select((.email? | type) == "string") | select(.email | startswith("api-user-")) | .id | select((type == "string") and (length > 0))] | first // empty' <<<"$API_USERS_BODY")
        if [[ -z "$API_USER_ID" ]]; then
            fail "webhook trigger user was not found in the API user-list response"
        fi
    fi
    WEBHOOK_TRIGGER_READY=0
    if [[ -n "$WH_SECRET" && -n "$API_USER_ID" ]]; then
        TRIGGER_HTTP_STATUS=$(curl -sS -o /dev/null -w '%{http_code}' -X PATCH \
            -H "Authorization: Bearer $API_KEY" \
            -H 'Content-Type: application/json' -d '{"name":"Renamed User"}' \
            "$SERVICE_LISTEN/v1/$SLUG/users/$API_USER_ID")
        TRIGGER_CURL_STATUS=$?
        if [[ $TRIGGER_CURL_STATUS -ne 0 ]]; then
            fail "webhook trigger update request failed (curl exit $TRIGGER_CURL_STATUS, HTTP $TRIGGER_HTTP_STATUS)"
        elif [[ "$TRIGGER_HTTP_STATUS" != "200" ]]; then
            fail "webhook trigger update request returned HTTP $TRIGGER_HTTP_STATUS"
        else
            WEBHOOK_TRIGGER_READY=1
        fi
    fi

    DELIVERY=""
    if [[ $WEBHOOK_TRIGGER_READY -eq 1 ]]; then
        for _ in $(seq 1 30); do
            DELIVERY=$(tail -n "+$((SINK_MARK + 1))" "$WEBHOOK_SINK_CAPTURE_FILE" | \
                jq -rc --arg user_id "$API_USER_ID" \
                'select((.body | fromjson? | .type) == "user.updated" and (.body | fromjson? | .data.user_id) == $user_id)' | tail -1)
            [[ -n "$DELIVERY" ]] && break
            sleep 1
        done
    fi
    if [[ $WEBHOOK_TRIGGER_READY -eq 1 ]]; then
        if [[ -z "$DELIVERY" ]]; then
            fail "webhook delivery reaches the controlled sink"
        else
            pass "webhook delivery reaches the controlled sink"
            SIGNATURE_OK=$(python3 -c "
import hashlib, hmac, json, sys
d = json.loads(sys.argv[1]); secret = sys.argv[2].encode()
headers = {k.lower(): v for k, v in d['headers'].items()}
ts = headers.get('x-tsauth-timestamp', '')
sig = headers.get('x-tsauth-signature', '')
expected = 'sha256=' + hmac.new(secret, (ts + '.' + d['body']).encode(), hashlib.sha256).hexdigest()
print('yes' if hmac.compare_digest(expected, sig) else 'no')" "$DELIVERY" "$WH_SECRET")
            check "webhook HMAC signature verifies" "yes" "$SIGNATURE_OK"
        fi
    fi
fi

# ---- 9. Passcodes, audit ------------------------------------------------------------------

PASSCODE=$(curl -sf -X POST "$SERVICE_LISTEN/ui/$SLUG/passcode/request" \
    -H "Origin: $SERVICE_LISTEN" -H 'Content-Type: application/json' \
    -d "{\"email\":\"$EMAIL\"}")
check "email passcode request answers ok (no enumeration)" "true" "$(jq -r .ok <<<"$PASSCODE")"

AUDIT=$(curl -sf -H "Authorization: Bearer $API_KEY" "$SERVICE_LISTEN/v1/$SLUG/audit-events?limit=50")
check "audit log records the smoke run's actions" "yes" "$(grep -q 'user.signed_up' <<<"$AUDIT" && echo yes || echo no)"

echo
if [[ $FAILURES -eq 0 ]]; then
    if [[ $WEBHOOK_SMOKE -eq 0 ]]; then
        echo "SMOKE RESULT: all executed checks passed; webhook delivery/signature skipped (service on $SERVICE_LISTEN, member $MEMBER_URL)"
    else
        echo "SMOKE RESULT: all checks passed (service on $SERVICE_LISTEN, member $MEMBER_URL)"
    fi
else
    echo "SMOKE RESULT: $FAILURES check(s) failed"
    exit 1
fi
