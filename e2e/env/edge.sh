set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
source "$here/service.sh"
tools=${E2E_TOOLS:?E2E_TOOLS is the directory that caches the pinned tools}
pki=$state/pki
trust=${SSL_CERT_FILE:?SSL_CERT_FILE is the trust bundle that holds the run CA}
umask 077
rm -rf "$logs" "$pids" "$state/caddy" "$state/ports.json"
mkdir -p "$logs" "$pids" "$state/caddy"
start port-registry python3 "$here/ports.py" hold --out "$state/ports.json" --block --profile journey
until [ -s "$state/ports.json" ]; do
  kill -0 "$(cat "$pids/port-registry.pid")" 2> /dev/null || { echo "port registry exited before it assigned ports" >&2; cat "$logs/port-registry.log" >&2; exit 1; }
  sleep 0.05
done
eval "$(jq -r 'to_entries[] | "export E2E_PORT_\(.key)=\(.value)"' "$state/ports.json")"

edge_serves() { test "$(curl -s --max-time 2 -o /dev/null -w '%{http_code}' --cacert "$trust" https://status.terrascale.tech/)" = 404; }
pg_edge_serves() { openssl s_client -starttls postgres -connect "127.0.0.1:$(e2e_port postgres_edge)" -servername pg.terrascale.tech -verify_return_error -CAfile "$trust" < /dev/null; }

start mailpit "$tools/mailpit" --listen "127.0.0.1:$(e2e_port mailpit)" --smtp "127.0.0.1:$(e2e_port smtp)" \
  --smtp-tls-cert "$pki/edge.pem" --smtp-tls-key "$pki/edge.key" --smtp-require-starttls --smtp-auth-accept-any \
  --smtp-disable-rdns --disable-version-check
start client-proxy node "$here/client-proxy.mjs"
start dns node "$here/dns.mjs"
start webhook-receiver node "$here/webhook-receiver.mjs"
start cloudflare-sink node "$here/cloudflare-sink.mjs"
start stripe node "$here/stripe.mjs"
start r2 node "$here/r2.mjs"
start attack-protection node "$here/attack-protection.mjs"
start oauth2 node "$here/oauth2.mjs"
start sso node "$here/sso.mjs"
start pg-edge env E2E_EDGE_CERT="$pki/edge.pem" E2E_EDGE_KEY="$pki/edge.key" node "$here/pg-edge.mjs"
start caddy env XDG_DATA_HOME="$state/caddy" XDG_CONFIG_HOME="$state/caddy" E2E_EDGE_CERT="$pki/edge.pem" E2E_EDGE_KEY="$pki/edge.key" \
  "$tools/caddy" run --config "$here/Caddyfile" --adapter caddyfile
ready mailpit curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port mailpit)/readyz"
ready cloudflare-sink curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port cloudflare)/messages"
ready stripe curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port stripe)/log"
ready r2 curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port r2)/log"
ready oauth2 curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port oauth)/readyz"
ready caddy edge_serves
ready client-proxy curl -sS --max-time 2 --proxy "http://127.64.0.1:$(e2e_port client_proxy)" --noproxy '' --cacert "$trust" https://status.terrascale.tech/
ready pg-edge pg_edge_serves
