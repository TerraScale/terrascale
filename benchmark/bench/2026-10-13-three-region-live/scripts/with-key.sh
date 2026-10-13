#!/usr/bin/env bash
# Runs a command with one test key in its inherited environment only. Usage: with-key.sh <cmd...>
# The key (documents:read/write on the run's project only, 24 h expiry) is minted through gru's operator API over the proxy on
# 18181, held only in this shell and its children's environment (never a file, argv, log or output), and revoked on exit,
# failure included. Its id and expiry (never the value) go to stderr for the resource ledger.
set -euo pipefail; set +x
here=$(cd "$(dirname "$0")" && pwd)
source "$here/tenant.env"; source "$here/machines.env"; export G S F
B=http://127.0.0.1:18181
keys=$B/v1/management/organizations/$org/projects/$project/api-keys
name="run-$(date -u +%Y%m%dT%H%M%S)"
issued=$(curl -sS --fail --http2-prior-knowledge -H 'content-type: application/json' -d "$(jq -cn --arg n "$name" --arg o "$org" --arg p "$project" \
  '{name: $n, scopes: ["documents:read", "documents:write"], organizationId: $o, projectId: $p, expiresAt: (now + 86400 | todate)}')" "$B/admin/api-keys")
id=$(jq -r .id <<< "$issued"); echo "key $name id $id expires $(jq -r .expiresAt <<< "$issued")" >&2
revoke() { for port in 18181 18182 18183; do curl -sS --fail --http2-prior-knowledge -H "X-TerraBase-User-Id: $owner" -X DELETE -o /dev/null "${keys/18181/$port}/$id" 2> /dev/null && { echo "key $name revoked via :$port" >&2; return; }; done; echo "REVOKE FAILED for key $id" >&2; }
trap revoke EXIT
TERRABASE_E2E_FLY_API_KEY=$(jq -r .secret <<< "$issued"); unset issued; export TERRABASE_E2E_FLY_API_KEY TERRABASE_E2E_FLY_PROJECT=$project OWNER=$owner
"$@"
