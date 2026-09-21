#!/usr/bin/env bash
# End-to-end smoke of the REAL stack: this script boots the real ts-auth
# service (its own process, production startup validation) against a REAL
# TerraBase member (tools/TerraBaseDevMember) and drives every API family
# once over HTTP, including a signature-verified webhook delivery.
#
# Usage:
#   scripts/smoke-real-stack.sh            # member at http://127.0.0.1:8420, service on a free port
#   MEMBER_URL=... SERVICE_LISTEN=http://127.0.0.1:9090 scripts/smoke-real-stack.sh
set -uo pipefail

MEMBER_URL=${MEMBER_URL:-http://127.0.0.1:8420}
SERVICE_LISTEN=${SERVICE_LISTEN:-http://127.0.0.1:8480}
REPO_ROOT=$(cd "$(dirname "$0")/.." && pwd)
# Override SERVICE_PROJECT to smoke a worktree/pinned checkout instead of this repo.
SERVICE_PROJECT=${SERVICE_PROJECT:-$REPO_ROOT/src/TerraScale.Auth}
FAILURES=0

pass() { echo "PASS  $1"; }
fail() { echo "FAIL  $1"; FAILURES=$((FAILURES + 1)); }

check() { # check <label> <expected> <actual>
    if [[ "$2" == "$3" ]]; then pass "$1"; else fail "$1 (expected [$2], got [$3])"; fi
}

cleanup() {
    [[ -n "${SERVICE_PID:-}" ]] && kill "$SERVICE_PID" 2>/dev/null
    [[ -n "${SINK_PID:-}" ]] && kill "$SINK_PID" 2>/dev/null
    wait 2>/dev/null
}
trap cleanup EXIT

# ---- 0. Preconditions ----------------------------------------------------------------

MEMBER_CODE=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$MEMBER_URL/" 2>/dev/null)
if [[ "$MEMBER_CODE" == "000" ]]; then
    echo "No TerraBase member at $MEMBER_URL."
    echo "Start one:  dotnet run --project tools/TerraBaseDevMember"
    exit 2
fi

# ---- 1. Boot the real service process ------------------------------------------------

SINK_PORT=$(( (RANDOM % 20000) + 20000 ))
SINK_FILE=$(mktemp)
python3 - "$SINK_PORT" "$SINK_FILE" <<'PY' &
import json, sys
from http.server import BaseHTTPRequestHandler, HTTPServer

port, sink_file = int(sys.argv[1]), sys.argv[2]

class Sink(BaseHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        with open(sink_file, "a") as f:
            f.write(json.dumps({"path": self.path,
                                "headers": {k: v for k, v in self.headers.items()},
                                "body": body.decode()}) + "\n")
        self.send_response(200)
        self.end_headers()
    def log_message(self, *_):
        pass

HTTPServer(("127.0.0.1", port), Sink).serve_forever()
PY
SINK_PID=$!

OP_KEY="smoke-operator-$(head -c 12 /dev/urandom | base64 -w0 | tr -d '=+/')"
METRICS_KEY="smoke-metrics-$(head -c 12 /dev/urandom | base64 -w0 | tr -d '=+/')"
SERVICE_ARGS=(--project "$SERVICE_PROJECT" -c Release)
[[ -n "${TERRASCALE_SDK_PROJECT:-}" ]] && SERVICE_ARGS+=(-p:TerraScaleSdkProject="$TERRASCALE_SDK_PROJECT")

TS_AUTH_LISTEN="$SERVICE_LISTEN" \
TS_AUTH_PUBLIC_ORIGIN="$SERVICE_LISTEN" \
TS_AUTH_TERRASCALE_API_URL="$MEMBER_URL" \
TS_AUTH_TERRASCALE_API_KEY="${TS_AUTH_E2E_API_KEY:-dev}" \
TS_AUTH_DATABASE="${TS_AUTH_E2E_DATABASE:-ts-auth-dev}" \
TS_AUTH_MASTER_KEY="$(head -c 32 /dev/urandom | base64 -w0)" \
TS_AUTH_AUDIT_SIGNING_KEY="$(head -c 32 /dev/urandom | base64 -w0)" \
TS_AUTH_OPERATOR_KEY="$OP_KEY" \
TS_AUTH_METRICS_KEY="$METRICS_KEY" \
dotnet run "${SERVICE_ARGS[@]}" >/tmp/ts-auth-smoke-service.log 2>&1 &
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

INSTANCE=$(curl -sf -X POST "$SERVICE_LISTEN/v1/instances" \
    -H "Authorization: Bearer $OP_KEY" \
    -H 'Content-Type: application/json' \
    -d "{\"slug\":\"$SLUG\",\"name\":\"Smoke Test\"}")
if [[ -z "$INSTANCE" ]]; then fail "operator instance create"; exit 1; fi
API_KEY=$(jq -r .api_key <<<"$INSTANCE")
pass "operator creates instance + first sk_ key"

# ---- 3. Hosted UI: sign-up, session, account ------------------------------------------

SIGNUP=$(curl -s -D /tmp/smoke-signup-headers -o /dev/null -X POST "$SERVICE_LISTEN/ui/$SLUG/sign-up" \
    --data-urlencode "email=$EMAIL" --data-urlencode "password=$PASSWORD" --data-urlencode "name=Smoke Tester")
COOKIE=$(grep -i '^set-cookie: ' /tmp/smoke-signup-headers | head -1 | sed 's/^[Ss]et-[Cc]ookie: //' | cut -d';' -f1 | tr -d '\r')
check "hosted sign-up sets a session cookie" "yes" "$([[ -n "$COOKIE" ]] && echo yes || echo no)"

ACCOUNT=$(curl -s -b "$COOKIE" "$SERVICE_LISTEN/ui/$SLUG/account")
check "account page renders the signed-in user" "yes" "$(grep -q "$EMAIL" <<<"$ACCOUNT" && echo yes || echo no)"

# ---- 4. Sign-out / sign-in ------------------------------------------------------------

curl -s -o /dev/null -b "$COOKIE" -c /tmp/smoke-cookies -X POST "$SERVICE_LISTEN/ui/$SLUG/sign-out"
SIGNIN=$(curl -s -D /tmp/smoke-signin-headers -o /dev/null -c /tmp/smoke-cookies -X POST "$SERVICE_LISTEN/ui/$SLUG/sign-in" \
    --data-urlencode "email=$EMAIL" --data-urlencode "password=$PASSWORD")
SIGNIN_COOKIE=$(grep -i '^set-cookie: ' /tmp/smoke-signin-headers | head -1 | sed 's/^[Ss]et-[Cc]ookie: //' | cut -d';' -f1 | tr -d '\r')
check "password sign-in re-establishes the session" "yes" "$([[ -n "$SIGNIN_COOKIE" ]] && echo yes || echo no)"
COOKIE="$SIGNIN_COOKIE"

WRONG=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$SERVICE_LISTEN/ui/$SLUG/sign-in" \
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

WH=$(curl -sf -X POST -H "Authorization: Bearer $API_KEY" -H 'Content-Type: application/json' \
    -d "{\"url\":\"http://127.0.0.1:$SINK_PORT/hook\",\"events\":[\"user.*\"]}" \
    "$SERVICE_LISTEN/v1/$SLUG/webhooks")
WH_SECRET=$(jq -r .signing_secret <<<"$WH")

API_USER_ID=$(curl -sf -H "Authorization: Bearer $API_KEY" "$SERVICE_LISTEN/v1/$SLUG/users?limit=10" | jq -re '.data[] | select(.email | startswith("api-user-")) | .id' | head -1)
[[ -n "$API_USER_ID" ]] && curl -sf -o /dev/null -X PATCH -H "Authorization: Bearer $API_KEY" -H 'Content-Type: application/json' \
    -d '{"name":"Renamed User"}' "$SERVICE_LISTEN/v1/$SLUG/users/$API_USER_ID"

DELIVERY=""
for _ in $(seq 1 30); do
    DELIVERY=$(grep '"path": "/hook"' "$SINK_FILE" 2>/dev/null | tail -1)
    [[ -n "$DELIVERY" ]] && break
    sleep 1
done
if [[ -z "$DELIVERY" ]]; then
    fail "webhook delivery reaches the sink"
else
    pass "webhook delivery reaches the sink"
    SIGNATURE_OK=$(python3 -c "
import hashlib, hmac, json, sys
d = json.loads(sys.argv[1]); secret = sys.argv[2].encode()
ts = d['headers'].get('X-TsAuth-Timestamp', '')
sig = d['headers'].get('X-TsAuth-Signature', '')
expected = 'sha256=' + hmac.new(secret, (ts + '.' + d['body']).encode(), hashlib.sha256).hexdigest()
print('yes' if hmac.compare_digest(expected, sig) else 'no')" "$DELIVERY" "$WH_SECRET")
    check "webhook HMAC signature verifies" "yes" "$SIGNATURE_OK"
fi

# ---- 9. Billing guard, passcodes, audit ------------------------------------------------

BILLING=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "Authorization: Bearer $API_KEY" \
    -H 'Content-Type: application/json' -d '{"plan":"pro"}' "$SERVICE_LISTEN/v1/$SLUG/billing/checkout")
check "billing answers 503 without Stripe configured" "503" "$BILLING"

PASSCODE=$(curl -sf -X POST "$SERVICE_LISTEN/ui/$SLUG/passcode/request" \
    -H 'Content-Type: application/json' -d "{\"email\":\"$EMAIL\"}")
check "email passcode request answers ok (no enumeration)" "true" "$(jq -r .ok <<<"$PASSCODE")"

AUDIT=$(curl -sf -H "Authorization: Bearer $API_KEY" "$SERVICE_LISTEN/v1/$SLUG/audit-events?limit=50")
check "audit log records the smoke run's actions" "yes" "$(grep -q 'user.signed_up' <<<"$AUDIT" && echo yes || echo no)"

echo
if [[ $FAILURES -eq 0 ]]; then
    echo "SMOKE RESULT: all checks passed (service on $SERVICE_LISTEN, member $MEMBER_URL)"
else
    echo "SMOKE RESULT: $FAILURES check(s) failed"
    exit 1
fi
