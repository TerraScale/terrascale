#!/usr/bin/env bash
# USER-RUN (Q-E2E-KEY = A): revokes the synthetic-tenant TerraBase API key "e2e-live" on ts-terrabase-e2e and deletes its
# local file. No secret is read or sent: revocation names the key id, and the internal listener trusts the operator header.
# It starts the three stopped machines (their catalog gates open by pulling from each other), revokes, then stops them again.
set -euo pipefail; set +x
app=ts-terrabase-e2e machines=(286513ead77708 83706ef7171428 28744161a1e048)
org=01M4S1SSGNFZ1TSBXN6NSKTQR6 project=01M4S1SSWF0J66P0R5EVRTFEPG owner=01M4E2E0000000000000000000
keys=http://127.0.0.1:18181/v1/management/organizations/$org/projects/$project/api-keys
flyctl machine start "${machines[@]}" -a "$app"
flyctl proxy 18181:8081 "${machines[0]}.vm.$app.internal" -a "$app" > /dev/null 2>&1 & proxy=$!
trap 'kill $proxy 2> /dev/null || true; flyctl machine stop "${machines[@]}" -a "$app"' EXIT
for _ in $(seq 1 60); do
  [[ $(curl -s -m 3 --http2-prior-knowledge http://127.0.0.1:18181/catalog-gate | jq -r .open 2> /dev/null) == true ]] && break
  sleep 2
done
mg() { curl -sS --fail --http2-prior-knowledge -H "X-TerraBase-User-Id: $owner" "$@"; }
for id in $(mg "$keys" | jq -r '.items[] | select(.name == "e2e-live") | .id'); do
  mg -X DELETE -o /dev/null "$keys/$id" && echo "revoked key $id"
done
echo "remaining e2e-live keys: $(mg "$keys" | jq '[.items[] | select(.name == "e2e-live")] | length')"
rm -f -- /home/mariogk/Projects/TerraScale/.worktrees/e2e-live-scratch/api-key && echo "deleted the local key file"
