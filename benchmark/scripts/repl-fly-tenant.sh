#!/usr/bin/env bash
# The Fly benchmark credential: a dedicated TerraBase tenant for repl-bench and its API key. A user runs it (through
# scripts/handoff/morning-credentials.sh); agents never run it. Commands:
#
#   dry-run   check prerequisites and print what install would create; changes nothing
#   install   create the tenant once, then mint a key and hand it to Fly (re-running rotates the key)
#   status    what exists: ids, the Fly secret's digest, the key's expiry, benchmark collections
#   revoke    revoke the key in TerraBase and remove the Fly secret; the tenant and its data stay
#   cleanup   drop every bench-* collection (the benchmark documents) of the benchmark database
#
# Tenant: organization "TerraScale benchmark" (enterprise plan, so QoS does not throttle the load), project "bench",
# database "bench" (home gru, replicas sjc and fra). It is never the e2e tenant. The owner is a synthetic user id.
# Key: documents:read and documents:write, narrowed to the bench database, expiring after 30 days.
#
# The API key only travels through a pipe; the peer header uses a temporary owner-only file removed on exit:
#   Fly secret TS_BENCH_API_KEY on app ts-bench (an app with no machines between runs; the Fly benchmark driver runs its
#     load machines there, and they read the key from their environment)
#   GitHub repository variables (not secret): TS_BENCH_FLY_OWNER, TS_BENCH_FLY_ORGANIZATION, TS_BENCH_FLY_PROJECT,
#     TS_BENCH_FLY_DATABASE_ID, TS_BENCH_FLY_KEY_ID, TS_BENCH_FLY_KEY_EXPIRES
#
# It reaches TerraBase through `flyctl proxy` to gru's internal listener (port 8081, h2c, fly-local-6pn), where the
# platform operator acts as a named user (X-TerraBase-User-Id) and issues keys (POST /admin/api-keys).
set +x
set -euo pipefail
shopt -s inherit_errexit

command=${1:-}
app=${BENCH_TERRABASE_APP:-ts-terrabase}
bench_app=${BENCH_FLY_APP:-ts-bench}
org=${BENCH_FLY_ORG:-terrascale}
repo=${BENCH_GITHUB_REPO:-TerraScale/terrascale}
port=${BENCH_PROXY_PORT:-18081}
base="http://127.0.0.1:$port"
secret_name=TS_BENCH_API_KEY
key_days=30

die() { echo "repl-fly-tenant: $*" >&2; exit 1; }
need() { command -v "$1" > /dev/null || die "$1 is required"; }
# A ULID: 10 characters of Unix milliseconds, then 16 random ones (each byte % 32 is uniform: 256 is a multiple of 32).
ulid() {
  local alphabet=0123456789ABCDEFGHJKMNPQRSTVWXYZ ms out="" byte
  ms=$(date +%s%3N)
  for _ in {1..10}; do out=${alphabet:ms%32:1}$out; ms=$((ms / 32)); done
  for byte in $(od -An -N16 -tu1 /dev/urandom); do out+=${alphabet:byte%32:1}; done
  echo "$out"
}
variable() { gh variable get "$1" --repo "$repo" 2> /dev/null || true; }

open_proxy() {
  [[ ${TERRABASE_PEER_SECRET:-} =~ ^[0-9a-fA-F]{64}$ ]] || die "TERRABASE_PEER_SECRET must contain 64 hexadecimal characters"
  peer_header=$(mktemp)
  trap '[[ -z ${proxy:-} ]] || kill "$proxy" 2> /dev/null || true; rm -f -- "$peer_header"' EXIT
  chmod 600 "$peer_header"
  printf 'X-TerraBase-Peer-Credential: %s\n' "$TERRABASE_PEER_SECRET" > "$peer_header"
  local gru
  gru=$(flyctl dig AAAA "gru.$app.internal" --app "$app" --short 2> /dev/null | head -1)
  [[ -n "$gru" ]] || die "gru.$app.internal has no address: is gru running?"
  flyctl proxy "$port:8081" "$gru" --app "$app" > /dev/null 2>&1 &
  proxy=$!
  for _ in $(seq 1 30); do
    curl -q -s -o /dev/null --http2-prior-knowledge --max-redirs 0 -H "@$peer_header" "$base/" && return 0
    sleep 1
  done
  die "gru's internal listener did not answer through flyctl proxy"
}

# A call on the internal listener: as the owner when $owner is set (Management API), else as the operator.
internal() {
  local method=$1 path=$2
  shift 2
  local args=(-q -sS --fail-with-body --http2-prior-knowledge --max-redirs 0 -H "@$peer_header" -X "$method" -H 'content-type: application/json')
  [[ -n "${owner:-}" ]] && args+=(-H "X-TerraBase-User-Id: $owner")
  curl "${args[@]}" "$base$path" "$@"
}

load_ids() {
  owner=$(variable TS_BENCH_FLY_OWNER) organization=$(variable TS_BENCH_FLY_ORGANIZATION)
  project=$(variable TS_BENCH_FLY_PROJECT) database_id=$(variable TS_BENCH_FLY_DATABASE_ID) key_id=$(variable TS_BENCH_FLY_KEY_ID)
  key_expires=$(variable TS_BENCH_FLY_KEY_EXPIRES)
}

dry_run() {
  need flyctl; need gh; need curl; need jq
  flyctl auth whoami > /dev/null 2>&1 || die "flyctl is not logged in"
  gh auth status > /dev/null 2>&1 || die "gh is not logged in"
  load_ids
  echo "tenant:   ${organization:-(new)} 'TerraScale benchmark' / project ${project:-(new)} 'bench' / database 'bench' (gru; sjc, fra)"
  echo "key:      documents:read documents:write, database bench only, expires in $key_days days"
  echo "secret:   Fly secret $secret_name on app $bench_app (org $org); the previous key, if any, is revoked after the new one is set"
  echo "ids:      GitHub variables TS_BENCH_FLY_{OWNER,ORGANIZATION,PROJECT,DATABASE_ID,KEY_ID} on $repo"
  flyctl dig AAAA "gru.$app.internal" --app "$app" --short > /dev/null 2>&1 && echo "gru:      reachable over 6PN" || echo "gru:      NOT reachable (install would fail)"
  echo "dry run: nothing was changed"
}

install() {
  dry_run > /dev/null
  load_ids
  open_proxy
  if [[ -z "$organization" ]]; then
    owner=$(ulid) organization=$(ulid) project=$(ulid) database_id=$(ulid)
    internal POST /v1/management/commits -d "$(jq -cn --arg id "$(ulid)" --arg o "$organization" --arg p "$project" --arg d "$database_id" '{id: $id, ops: [
      {op: "create_organization", id: $o, name: "TerraScale benchmark"},
      {op: "set_plan", organization: $o, plan: "enterprise"},
      {op: "create_project", organization: $o, id: $p, name: "bench"},
      {op: "create_database", organization: $o, project: $p, name: "bench", id: $d, home_region: "gru", replica_regions: ["sjc", "fra"]}]}')" > /dev/null
    for name in OWNER:"$owner" ORGANIZATION:"$organization" PROJECT:"$project" DATABASE_ID:"$database_id"; do
      gh variable set "TS_BENCH_FLY_${name%%:*}" --repo "$repo" --body "${name#*:}"
    done
  fi
  flyctl apps list --org "$org" --json | jq -e --arg a "$bench_app" 'any(.[]; .Name == $a)' > /dev/null || flyctl apps create "$bench_app" --org "$org" > /dev/null
  local previous=$key_id expires
  expires=$(date -u -d "+$key_days days" +%Y-%m-%dT%H:%M:%SZ)
  # The response holds the secret: one pipe splits it, the id goes to a variable and the secret straight into Fly.
  internal POST /admin/api-keys -d "$(jq -cn --arg o "$organization" --arg p "$project" --arg d "$database_id" --arg e "$expires" \
    '{name: "repl-bench", scopes: ["documents:read", "documents:write"], organizationId: $o, projectId: $p, databaseId: $d, expiresAt: $e}')" \
    | jq -jer --arg n "$secret_name" '"\(.id)\n\($n)=\(.secret)\n"' \
    | { read -r key_id; gh variable set TS_BENCH_FLY_KEY_ID --repo "$repo" --body "$key_id"; flyctl secrets import --app "$bench_app" --stage > /dev/null; }
  gh variable set TS_BENCH_FLY_KEY_EXPIRES --repo "$repo" --body "$expires"
  if [[ -n "$previous" ]]; then
    owner=$owner internal DELETE "/v1/management/organizations/$organization/projects/$project/api-keys/$previous" > /dev/null || echo "revoke the previous key $previous by hand" >&2
  fi
  echo "installed: key expires $expires; Fly secret $secret_name on $bench_app"
}

status() {
  need flyctl; need gh; need curl; need jq
  load_ids
  [[ -n "$organization" ]] || { echo "not installed"; return 0; }
  echo "tenant:   organization $organization, project $project, database bench ($database_id), owner $owner"
  flyctl secrets list --app "$bench_app" --json 2> /dev/null | jq -r --arg n "$secret_name" '.[] | select(.Name == $n) | "secret:   \(.Name) digest \(.Digest) (\(.Status // "set"))"' || echo "secret:   app $bench_app not found"
  open_proxy
  owner=$owner internal GET "/v1/management/organizations/$organization/projects/$project/api-keys" \
    | jq -r --arg k "$key_id" --arg e "${key_expires:-unknown}" 'if any(.items[]; .id == $k) then "key:      \($k) active, expires \($e)" else "key:      \($k) NOT active (revoked or expired)" end'
  owner="" internal GET "/v1/projects/$project/databases/bench/collections" | jq -r '"data:     \([(.items // [])[] | select(.name | startswith("bench-"))] | length) bench-* collections"'
}

revoke() {
  need flyctl; need gh; need curl; need jq
  load_ids
  [[ -n "$key_id" ]] || die "no key recorded (TS_BENCH_FLY_KEY_ID is empty)"
  open_proxy
  owner=$owner internal DELETE "/v1/management/organizations/$organization/projects/$project/api-keys/$key_id" > /dev/null
  flyctl secrets unset "$secret_name" --app "$bench_app" --stage > /dev/null
  gh variable delete TS_BENCH_FLY_KEY_ID --repo "$repo"
  gh variable delete TS_BENCH_FLY_KEY_EXPIRES --repo "$repo"
  echo "revoked key $key_id and removed $secret_name from $bench_app; the tenant and its data stay (cleanup drops the data)"
}

cleanup() {
  need flyctl; need gh; need curl; need jq
  load_ids
  [[ -n "$project" ]] || die "not installed"
  open_proxy
  local dropped=0 name
  # The operator reaches the project's data routes without a key; only bench-* collections are touched.
  for name in $(owner="" internal GET "/v1/projects/$project/databases/bench/collections" | jq -r '(.items // [])[].name | select(startswith("bench-"))'); do
    owner="" internal DELETE "/v1/projects/$project/databases/bench/collections/$name" > /dev/null
    dropped=$((dropped + 1))
  done
  echo "dropped $dropped bench-* collections from database bench"
}

case "$command" in
  dry-run) dry_run ;;
  install) install ;;
  status) status ;;
  revoke) revoke ;;
  cleanup) cleanup ;;
  *) die "usage: repl-fly-tenant.sh dry-run|install|status|revoke|cleanup" ;;
esac
