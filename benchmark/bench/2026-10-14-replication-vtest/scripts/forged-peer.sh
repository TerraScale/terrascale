#!/usr/bin/env bash
# V5 forged peer (A1): from the operator's WireGuard peer on the org's 6PN (flyctl proxy), send each machine's internal
# listener requests with no peer credential and with a wrong one (random, generated here; never the real secret):
#   read  GET /catalog-gate                        (an internal read)
#   write POST /v1/management/organizations        (an operator write as a forged user)
#   rpc   terrabase.v1.Replication/Digest          (a peer gRPC, empty request)
# and the machine's terrabase_peer_authentication_requests counters before and after. Refused = 401 / gRPC Unauthenticated (16).
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd); source "$here/../state.env"; A=ts-vtest-tb
wrong=$(openssl rand -hex 32)
counters() { curl -sS -m 10 --http2-prior-knowledge "http://127.0.0.1:$1/metrics" | grep -E '^terrabase_peer_authentication_requests' | tr '\n' ' '; }
port=18190
for pair in gru=$G sjc=$S fra=$F; do
  r=${pair%%=*} m=${pair#*=}; port=$((port + 1))
  flyctl proxy "$port:8081" "$m.vm.$A.internal" -a $A > /dev/null 2>&1 & pid=$!
  for _ in $(seq 1 30); do curl -s -m 2 -o /dev/null --http2-prior-knowledge "http://127.0.0.1:$port/healthz" && break; sleep 1; done
  echo "== $r $m $(date -u +%FT%TZ) revision $(curl -sS -m 10 --http2-prior-knowledge "http://127.0.0.1:$port/healthz" | jq -r .revision)"
  echo "counters before: $(counters $port)"
  for cred in none wrong; do
    h=(); [[ $cred == wrong ]] && h=(-H "X-TerraBase-Peer-Credential: $wrong")
    read_code=$(curl -sS -m 10 -o /dev/null -w '%{http_code}' --http2-prior-knowledge "${h[@]}" "http://127.0.0.1:$port/catalog-gate")
    write=$(curl -sS -m 10 -w ' HTTP %{http_code}' --http2-prior-knowledge "${h[@]}" -H "X-TerraBase-User-Id: 01J0F0RGED0000000000000000" \
      -H 'content-type: application/json' -d "{\"name\":\"forged-$cred-$r\"}" "http://127.0.0.1:$port/v1/management/organizations" | jq -Rr '.' | cut -c1-200)
    rpc=$(printf '\0\0\0\0\0' | curl -sS -m 10 -o /dev/null -D - --http2-prior-knowledge "${h[@]}" -H 'content-type: application/grpc' -H 'te: trailers' \
      --data-binary @- "http://127.0.0.1:$port/terrabase.v1.Replication/Digest" | grep -iE '^(HTTP|grpc-status)' | tr -d '\r' | tr '\n' ' ')
    echo "credential=$cred read=$read_code write=[$write] rpc=[$rpc]"
  done
  echo "counters after: $(counters $port)"
  kill $pid 2> /dev/null; wait $pid 2> /dev/null
done
