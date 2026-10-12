#!/usr/bin/env bash
# Installs the CI credential of the Fly end-to-end suite (tests/TerraBase.E2E) into GitHub Actions secrets. A HUMAN runs
# this on their own machine: the API key goes from ts-auth's response straight into `gh secret set` through a pipe and is
# never printed, logged or written to disk. No agent runs it.
#
#   scripts/e2e/install-ci-credential.sh            # sign in, ensure the e2e project and database, mint the key, install it
#   scripts/e2e/install-ci-credential.sh --dry-run  # check the preconditions and print the plan; changes nothing
#   scripts/e2e/install-ci-credential.sh --revoke   # sign in, revoke every "ci-e2e" key of the e2e project, delete the secret
#
# The key: a ts-auth project API key of project "e2e" in organization "TerraScale e2e" (yours), scopes databases:read,
# databases:write, documents:read, documents:write, no expiry. The suite exchanges it for 15-minute project tokens.
# Needs: curl, jq, gh (logged in with access to TerraScale/terrascale), and the dashboard client secret in
# TS_AUTH_DASHBOARD_CLIENT_SECRET (asked for, hidden, when unset): the headless sign-in API serves only that client.
set -euo pipefail
set +x

readonly AUTH=https://ts-auth.fly.dev
readonly TERRABASE=https://ts-terrabase.fly.dev
readonly REPO=TerraScale/terrascale
readonly ORGANIZATION="TerraScale e2e"
readonly PROJECT=e2e
readonly DATABASE=e2e
readonly KEY_NAME=ci-e2e
# ts-auth's dashboard client (OidcIssuer.DashboardClientId).
readonly DASHBOARD_CLIENT=01M4EQ2FRJ8S8B29E8TQVWNW4J
readonly SCOPES='["databases:read","databases:write","documents:read","documents:write"]'

mode=install
case "${1:-}" in
  "") ;;
  --dry-run) mode=dry-run ;;
  --revoke) mode=revoke ;;
  *) echo "usage: $0 [--dry-run|--revoke]" >&2; exit 2 ;;
esac

for tool in curl jq gh; do
  command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 1; }
done
gh auth status >/dev/null 2>&1 || { echo "gh is not logged in (gh auth login)" >&2; exit 1; }

# Precondition: ts-auth is ready in every region, so sign-in and storage work.
ready=1
for region in gru sjc fra; do
  status=$(curl -s -o /dev/null -w '%{http_code}' -m 15 -H "fly-prefer-region: $region" "$AUTH/readyz" || true)
  echo "ts-auth /readyz in $region: $status"
  [[ $status == 200 ]] || ready=0
done
if [[ $ready != 1 ]]; then
  echo "Precondition not met: ts-auth must answer /readyz 200 in gru, sjc and fra before the credential can be minted." >&2
  [[ $mode == dry-run ]] || exit 1
fi

if [[ $mode == dry-run ]]; then
  cat <<PLAN
Plan (nothing changed):
1. Sign in to ts-auth with your email and the code it mails you (headless API, dashboard client).
2. Ensure organization "$ORGANIZATION" with project "$PROJECT", and TerraBase database "$DATABASE" in it.
3. Mint project API key "$KEY_NAME" (scopes $SCOPES, no expiry) and pipe it into
   gh secret set TERRABASE_E2E_FLY_API_KEY -R $REPO
4. Set TERRABASE_E2E_FLY_PROJECT and TERRABASE_E2E_FLY_DATABASE=$DATABASE (not secret; ci.yml sets the URL).
Revoke later with: $0 --revoke
PLAN
  exit 0
fi

secret=${TS_AUTH_DASHBOARD_CLIENT_SECRET:-}
if [[ -z $secret ]]; then
  read -rsp "Dashboard client secret (hidden): " secret
  echo
fi
read -rp "Your email: " email
basic=$(printf '%s:%s' "$DASHBOARD_CLIENT" "$secret" | base64 -w0)
unset secret

pending=()
read -rp "Do you already have a TerraScale account for this email? [Y/n] " existing
if [[ ${existing,,} == n* ]]; then
  # Sign-up: the account exists once the mailed code is redeemed with this sign-up's pending token.
  read -rsp "New password, 8+ characters (hidden): " password
  echo
  pending_token=$(curl -sS --fail -H "Authorization: Basic $basic" -H 'Content-Type: application/json' \
    -d "$(jq -n --arg email "$email" --arg password "$password" '{email: $email, password: $password, ip_address: "127.0.0.1"}')" \
    "$AUTH/terrascale/headless/v1/sign-up" | jq -er .pending_token)
  unset password
  pending=(--data-urlencode "pending_token=$pending_token")
else
  curl -sS --fail -o /dev/null -H "Authorization: Basic $basic" -H 'Content-Type: application/json' \
    -d "$(jq -n --arg email "$email" '{email: $email, ip_address: "127.0.0.1"}')" "$AUTH/terrascale/headless/v1/email-code/start"
fi
read -rsp "Code from the email (hidden): " code
echo
user_token=$(curl -sS --fail -H "Authorization: Basic $basic" \
  --data-urlencode 'grant_type=urn:terrascale:params:oauth:grant-type:email-code' --data-urlencode "email=$email" \
  --data-urlencode "code=$code" --data-urlencode 'ip_address=127.0.0.1' --data-urlencode 'scope=openid email' "${pending[@]}" \
  "$AUTH/terrascale/oauth/token" | jq -er .access_token)
unset code pending_token

api() { curl -sS --fail -H "Authorization: Bearer $user_token" -H 'Content-Type: application/json' "$@"; }

organization=$(api "$AUTH/v1/terrascale/organizations" | jq -r --arg name "$ORGANIZATION" '.items[] | select(.name == $name) | .id' | head -1)
if [[ -z $organization ]]; then
  [[ $mode == install ]] || { echo "No organization \"$ORGANIZATION\": nothing to revoke." >&2; exit 0; }
  organization=$(api -d "$(jq -n --arg name "$ORGANIZATION" '{name: $name}')" "$AUTH/v1/terrascale/organizations" | jq -er .id)
fi
project=$(api "$AUTH/v1/terrascale/organizations/$organization/projects" | jq -r --arg name "$PROJECT" '.items[] | select(.name == $name) | .id' | head -1)
if [[ -z $project ]]; then
  [[ $mode == install ]] || { echo "No project \"$PROJECT\": nothing to revoke." >&2; exit 0; }
  project=$(api -d "$(jq -n --arg name "$PROJECT" '{name: $name}')" "$AUTH/v1/terrascale/organizations/$organization/projects" | jq -er .id)
fi
keys="$AUTH/v1/terrascale/organizations/$organization/projects/$project/api-keys"

if [[ $mode == revoke ]]; then
  for id in $(api "$keys" | jq -r --arg name "$KEY_NAME" '.items[] | select(.name == $name) | .id'); do
    api -X DELETE -o /dev/null "$keys/$id"
    echo "Revoked API key $id"
  done
  gh secret delete TERRABASE_E2E_FLY_API_KEY -R "$REPO" || true
  echo "Done: the e2e job fails until a new key is installed."
  exit 0
fi

# The database lives in TerraBase: create it with a short-lived project token (409 when it exists).
project_token=$(curl -sS --fail -H "Authorization: Basic $basic" \
  --data-urlencode 'grant_type=urn:ietf:params:oauth:grant-type:token-exchange' --data-urlencode "subject_token=$user_token" \
  --data-urlencode 'subject_token_type=urn:ietf:params:oauth:token-type:access_token' --data-urlencode "organization=$organization" \
  --data-urlencode "project=$project" --data-urlencode 'scope=databases:read databases:write' \
  "$AUTH/terrascale/oauth/token" | jq -er .access_token)
status=$(curl -sS -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $project_token" -H 'Content-Type: application/json' \
  -d "{\"name\":\"$DATABASE\"}" "$TERRABASE/v1/projects/$project/databases")
unset project_token
[[ $status == 201 || $status == 409 ]] || { echo "Creating the e2e database answered HTTP $status" >&2; exit 1; }

# The key goes from the response into GitHub through the pipe only.
api -d "$(jq -n --arg name "$KEY_NAME" --argjson scopes "$SCOPES" '{name: $name, scopes: $scopes}')" "$keys" \
  | jq -jer .key | gh secret set TERRABASE_E2E_FLY_API_KEY -R "$REPO"
gh secret set TERRABASE_E2E_FLY_PROJECT -R "$REPO" --body "$project"
gh secret set TERRABASE_E2E_FLY_DATABASE -R "$REPO" --body "$DATABASE"
unset user_token basic
echo "Installed: TERRABASE_E2E_FLY_API_KEY (key \"$KEY_NAME\", never shown), _PROJECT=$project, _DATABASE=$DATABASE."
echo "Revoke with: $0 --revoke"
