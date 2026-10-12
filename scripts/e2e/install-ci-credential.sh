#!/usr/bin/env bash
# Installs the Fly e2e suite's credential (TERRABASE_E2E_FLY_API_KEY) into GitHub Actions secrets. A HUMAN runs this on
# their own machine; agents never run it. TerraBase mints the key inside the Fly org (operator endpoint on gru's internal
# listener, reached through `flyctl proxy`), and the key goes from that response straight into `gh secret set` through a
# pipe: never printed, logged or written to disk.
#
#   scripts/e2e/install-ci-credential.sh --dry-run  # check the preconditions and print the plan; changes nothing
#   scripts/e2e/install-ci-credential.sh            # ensure the e2e org/project/database, mint the key, install it
#   scripts/e2e/install-ci-credential.sh --revoke   # revoke every "ci-e2e" key of the e2e project, delete the secret
#
# Key: a TerraBase API key of project "e2e" (org "TerraScale e2e"), scopes documents:read + documents:write, 90-day
# expiry. Rotate before it expires by running the script again (a new key replaces the secret), then revoke the old id.
# Needs: flyctl (logged in to org terrascale), curl with HTTP/2, jq, gh (access to TerraScale/terrascale).
set -euo pipefail
set +x

readonly REPO=TerraScale/terrascale
readonly APP=ts-terrabase
readonly PORT=18081
readonly BASE="http://127.0.0.1:$PORT"
# The e2e organization's owner: a synthetic user id with no ts-auth account, so the organization is found again on reruns.
readonly OWNER=01M4E2E0000000000000000000
readonly ORGANIZATION="TerraScale e2e"
readonly PROJECT=e2e
readonly DATABASE=e2e
readonly KEY_NAME=ci-e2e
readonly LIFETIME_DAYS=90

mode=install
case "${1:-}" in
  "") ;;
  --dry-run) mode=dry-run ;;
  --revoke) mode=revoke ;;
  *) echo "usage: $0 [--dry-run|--revoke]" >&2; exit 2 ;;
esac

for tool in flyctl curl jq gh; do
  command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 1; }
done
gh auth status >/dev/null 2>&1 || { echo "gh is not logged in (gh auth login)" >&2; exit 1; }

# gru's internal listener (h2c, no credential: Fly's private network is the trust boundary).
flyctl proxy "$PORT:8081" "gru.$APP.internal" -a "$APP" >/dev/null 2>&1 &
proxy=$!
trap 'kill "$proxy" 2>/dev/null || true' EXIT
for _ in $(seq 1 30); do
  curl -s -m 2 --http2-prior-knowledge -o /dev/null "$BASE/healthz" && break
  sleep 1
done
health=$(curl -s -m 5 --http2-prior-knowledge "$BASE/healthz" || true)
echo "TerraBase gru internal listener: ${health:-unreachable}"
[[ $(jq -r '.region // empty' <<<"$health" 2>/dev/null) == gru ]] || { echo "Precondition not met: gru's TerraBase must answer." >&2; exit 1; }

# Precondition: the deployed TerraBase knows API-key expiry (terrascale#81). A past expiry on a non-existent organization
# creates nothing: a build with expiry refuses the expiry, an older one refuses the organization.
probe=$(curl -s --http2-prior-knowledge -H 'Content-Type: application/json' \
  -d '{"name":"probe","scopes":["documents:read"],"organizationId":"00000000000000000000000000","expiresAt":"2000-01-01T00:00:00Z"}' \
  "$BASE/admin/api-keys")
if ! grep -q expiresAt <<<"$probe"; then
  echo "Precondition not met: the deployed TerraBase does not support API-key expiry yet (needs terrascale#81)." >&2
  exit 1
fi

if [[ $mode == dry-run ]]; then
  cat <<PLAN
Plan (nothing changed):
1. Ensure organization "$ORGANIZATION" (owner $OWNER, enterprise plan), project "$PROJECT" and database "$DATABASE"
   (home gru, replicas sjc, fra) through gru's internal listener.
2. Mint TerraBase API key "$KEY_NAME" (documents:read, documents:write, expires in $LIFETIME_DAYS days) and pipe it into
   gh secret set TERRABASE_E2E_FLY_API_KEY -R $REPO
3. Set TERRABASE_E2E_FLY_PROJECT and TERRABASE_E2E_FLY_DATABASE=$DATABASE (not secret).
Revoke with: $0 --revoke
PLAN
  exit 0
fi

management() { curl -sS --fail --http2-prior-knowledge -H "X-TerraBase-User-Id: $OWNER" -H 'Content-Type: application/json' "$@"; }
M="$BASE/v1/management/organizations"

organization=$(management "$M" | jq -r --arg name "$ORGANIZATION" '.items[] | select(.name == $name) | .id' | head -1)
if [[ -z $organization ]]; then
  [[ $mode == install ]] || { echo "No organization \"$ORGANIZATION\": nothing to revoke."; gh secret delete TERRABASE_E2E_FLY_API_KEY -R "$REPO" || true; exit 0; }
  organization=$(management -d "$(jq -n --arg name "$ORGANIZATION" '{name: $name}')" "$M" | jq -er .id)
  management -X PUT -o /dev/null -d '{"plan":"enterprise"}' "$M/$organization/plan"
fi
project=$(management "$M/$organization/projects" | jq -r --arg name "$PROJECT" '.items[] | select(.name == $name) | .id' | head -1)
if [[ -z $project ]]; then
  [[ $mode == install ]] || { echo "No project \"$PROJECT\": nothing to revoke."; gh secret delete TERRABASE_E2E_FLY_API_KEY -R "$REPO" || true; exit 0; }
  project=$(management -d "$(jq -n --arg name "$PROJECT" '{name: $name}')" "$M/$organization/projects" | jq -er .id)
fi
keys="$M/$organization/projects/$project/api-keys"

if [[ $mode == revoke ]]; then
  for id in $(management "$keys" | jq -r --arg name "$KEY_NAME" '.items[] | select(.name == $name) | .id'); do
    management -X DELETE -o /dev/null "$keys/$id"
    echo "Revoked API key $id"
  done
  gh secret delete TERRABASE_E2E_FLY_API_KEY -R "$REPO" || true
  echo "Done: the e2e job fails until a new key is installed."
  exit 0
fi

if ! management "$M/$organization/projects/$project/databases" | jq -e --arg name "$DATABASE" '.items[] | select(.name == $name)' >/dev/null; then
  management -o /dev/null -d "$(jq -n --arg name "$DATABASE" '{name: $name, replicaRegions: ["sjc", "fra"]}')" "$M/$organization/projects/$project/databases"
fi

expires=$(date -u -d "+$LIFETIME_DAYS days" +%Y-%m-%dT%H:%M:%SZ)
# The key goes from TerraBase's response into GitHub through the pipe only.
curl -sS --fail --http2-prior-knowledge -H 'Content-Type: application/json' \
  -d "$(jq -n --arg name "$KEY_NAME" --arg organization "$organization" --arg project "$project" --arg expires "$expires" \
    '{name: $name, scopes: ["documents:read", "documents:write"], organizationId: $organization, projectId: $project, expiresAt: $expires}')" \
  "$BASE/admin/api-keys" | jq -jer .secret | gh secret set TERRABASE_E2E_FLY_API_KEY -R "$REPO"
gh secret set TERRABASE_E2E_FLY_PROJECT -R "$REPO" --body "$project"
gh secret set TERRABASE_E2E_FLY_DATABASE -R "$REPO" --body "$DATABASE"
echo "Installed TERRABASE_E2E_FLY_API_KEY (key \"$KEY_NAME\", never shown, expires $expires), _PROJECT=$project, _DATABASE=$DATABASE."
echo "Older \"$KEY_NAME\" keys still work until they expire; list them with the management API and revoke the old ids, or run --revoke and reinstall."
