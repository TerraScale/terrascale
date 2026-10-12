#!/usr/bin/env bash
# The Fly e2e suite's credential (TERRABASE_E2E_FLY_API_KEY) in GitHub Actions secrets. A HUMAN runs it, through
# scripts/handoff/morning-credentials.sh; agents never run it. TerraBase mints the key inside the Fly org (operator endpoint
# on gru's internal listener, reached through `flyctl proxy`), and the key goes from that response straight into
# `gh secret set` through a pipe: never printed, logged or written to disk.
#
#   dry-run   check prerequisites and print what install would do; changes nothing
#   install   ensure the e2e org/project/database, mint the key, install it (re-running rotates: the new key replaces the
#             secret, older ci-e2e keys are revoked)
#   status    what exists: secret names in GitHub, the e2e tenant, its keys (ids and expiry only)
#   revoke    revoke every ci-e2e key of the e2e project and delete the secret; the tenant stays
#
# Tenant: organization "TerraScale e2e" (enterprise plan; owner = a fixed synthetic user id, so reruns find it), project
# "e2e", database "e2e" (home gru, replicas sjc and fra). Never the benchmark tenant.
# Key: a TerraBase API key, documents:read + documents:write, narrowed to the e2e project, expiring after 90 days.
set -euo pipefail
shopt -s inherit_errexit
set +x

readonly REPO=TerraScale/terrascale
readonly APP=ts-terrabase
readonly PORT=18082
readonly BASE="http://127.0.0.1:$PORT"
readonly OWNER=01M4E2E0000000000000000000
readonly ORGANIZATION="TerraScale e2e"
readonly PROJECT=e2e
readonly DATABASE=e2e
readonly KEY_NAME=ci-e2e
readonly KEY_DAYS=90
readonly SECRET=TERRABASE_E2E_FLY_API_KEY

die() { echo "e2e-credential: $*" >&2; exit 1; }
need() { command -v "$1" > /dev/null || die "$1 is required"; }

prerequisites() {
  for tool in flyctl curl jq gh; do need "$tool"; done
  flyctl auth whoami > /dev/null 2>&1 || die "flyctl is not logged in"
  gh auth status > /dev/null 2>&1 || die "gh is not logged in"
}

# gru's internal listener: h2c, no credential (Fly's private network is the trust boundary).
open_proxy() {
  flyctl proxy "$PORT:8081" "gru.$APP.internal" --app "$APP" > /dev/null 2>&1 &
  proxy=$!
  trap 'kill "$proxy" 2> /dev/null || true' EXIT
  local _
  for _ in $(seq 1 30); do
    [[ $(curl -s -m 2 --http2-prior-knowledge "$BASE/healthz" | jq -r '.region // empty' 2> /dev/null) == gru ]] && return 0
    sleep 1
  done
  die "gru's TerraBase internal listener did not answer through flyctl proxy"
}

# The deployed TerraBase must know API-key expiry (terrascale#81). A past expiry on a non-existent organization creates
# nothing: a build with expiry refuses the expiry, an older one refuses the organization.
check_expiry() {
  curl -s --http2-prior-knowledge -H 'content-type: application/json' \
    -d '{"name":"probe","scopes":["documents:read"],"organizationId":"00000000000000000000000000","expiresAt":"2000-01-01T00:00:00Z"}' \
    "$BASE/admin/api-keys" | grep -q expiresAt || die "the deployed TerraBase does not support API-key expiry yet (terrascale#81)"
}

management() { curl -sS --fail --http2-prior-knowledge -H "X-TerraBase-User-Id: $OWNER" -H 'content-type: application/json' "$@"; }

find_ids() {
  local m="$BASE/v1/management/organizations"
  organization=$(management "$m" | jq -r --arg n "$ORGANIZATION" '[.items[] | select(.name == $n)][0].id // empty')
  project=""
  [[ -z $organization ]] || project=$(management "$m/$organization/projects" | jq -r --arg n "$PROJECT" '[.items[] | select(.name == $n)][0].id // empty')
}

keys_path() { echo "$BASE/v1/management/organizations/$organization/projects/$project/api-keys"; }

dry_run() {
  prerequisites
  echo "tenant:   organization 'TerraScale e2e' (owner $OWNER, enterprise) / project 'e2e' / database 'e2e' (gru; sjc, fra)"
  echo "key:      TerraBase API key '$KEY_NAME': documents:read documents:write, project e2e only, expires in $KEY_DAYS days"
  echo "secret:   GitHub Actions secret $SECRET on $REPO (piped from TerraBase's response; never shown)"
  echo "ids:      GitHub Actions secrets TERRABASE_E2E_FLY_PROJECT, TERRABASE_E2E_FLY_DATABASE (not secret)"
  echo "rotation: install again; older '$KEY_NAME' keys are revoked after the new one is set"
  echo "dry run: nothing was changed"
}

install() {
  prerequisites
  open_proxy
  check_expiry
  local m="$BASE/v1/management/organizations" previous expires
  find_ids
  if [[ -z $organization ]]; then
    organization=$(management -d "$(jq -cn --arg n "$ORGANIZATION" '{name: $n}')" "$m" | jq -er .id)
    management -X PUT -o /dev/null -d '{"plan":"enterprise"}' "$m/$organization/plan"
  fi
  if [[ -z $project ]]; then
    project=$(management -d "$(jq -cn --arg n "$PROJECT" '{name: $n}')" "$m/$organization/projects" | jq -er .id)
  fi
  if ! management "$m/$organization/projects/$project/databases" | jq -e --arg n "$DATABASE" 'any(.items[]; .name == $n)' > /dev/null; then
    management -o /dev/null -d "$(jq -cn --arg n "$DATABASE" '{name: $n, replicaRegions: ["sjc", "fra"]}')" "$m/$organization/projects/$project/databases"
  fi
  previous=$(management "$(keys_path)" | jq -r --arg n "$KEY_NAME" '.items[] | select(.name == $n) | .id')
  expires=$(date -u -d "+$KEY_DAYS days" +%Y-%m-%dT%H:%M:%SZ)
  # The key goes from TerraBase's response into GitHub through the pipe only.
  curl -sS --fail --http2-prior-knowledge -H 'content-type: application/json' \
    -d "$(jq -cn --arg n "$KEY_NAME" --arg o "$organization" --arg p "$project" --arg e "$expires" \
      '{name: $n, scopes: ["documents:read", "documents:write"], organizationId: $o, projectId: $p, expiresAt: $e}')" \
    "$BASE/admin/api-keys" | jq -jer .secret | gh secret set "$SECRET" --repo "$REPO"
  gh secret set TERRABASE_E2E_FLY_PROJECT --repo "$REPO" --body "$project"
  gh secret set TERRABASE_E2E_FLY_DATABASE --repo "$REPO" --body "$DATABASE"
  local id
  for id in $previous; do
    management -X DELETE -o /dev/null "$(keys_path)/$id" || echo "revoke the previous key $id by hand" >&2
  done
  echo "installed: $SECRET (key '$KEY_NAME', never shown, expires $expires); project $project, database $DATABASE"
}

status() {
  prerequisites
  open_proxy
  find_ids
  [[ -n $organization && -n $project ]] || { echo "tenant:   not created"; return 0; }
  echo "tenant:   organization $organization, project $project, database $DATABASE, owner $OWNER"
  management "$(keys_path)" | jq -r --arg n "$KEY_NAME" '[.items[] | select(.name == $n)] | if length == 0 then "keys:     none" else (.[] | "key:      \(.id) created \(.createdAt)") end'
}

revoke() {
  prerequisites
  open_proxy
  find_ids
  if [[ -n $organization && -n $project ]]; then
    local id
    for id in $(management "$(keys_path)" | jq -r --arg n "$KEY_NAME" '.items[] | select(.name == $n) | .id'); do
      management -X DELETE -o /dev/null "$(keys_path)/$id"
      echo "revoked key $id"
    done
  fi
  gh secret delete "$SECRET" --repo "$REPO" 2> /dev/null || true
  echo "revoked: $SECRET removed; the e2e job fails until a new key is installed (the tenant stays)"
}

case "${1:-}" in
  dry-run) dry_run ;;
  install) install ;;
  status) status ;;
  revoke) revoke ;;
  *) die "usage: install-ci-credential.sh dry-run|install|status|revoke" ;;
esac
