set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
source "$here/service.sh"
tools=${E2E_TOOLS:?E2E_TOOLS is the directory that caches the pinned tools}
pki=$state/pki
trust=${SSL_CERT_FILE:?SSL_CERT_FILE is the trust bundle that holds the run CA}
umask 077
rm -rf "$logs" "$pids" "$state/caddy"
mkdir -p "$logs" "$pids" "$state/caddy"

edge_serves() { test "$(curl -s --max-time 2 -o /dev/null -w '%{http_code}' --cacert "$trust" https://status.terrascale.tech/)" = 404; }
pg_edge_serves() { openssl s_client -starttls postgres -connect 127.0.0.1:5432 -servername pg.terrascale.tech -verify_return_error -CAfile "$trust" < /dev/null; }

start mailpit "$tools/mailpit" --listen 127.0.0.1:8025 --smtp 127.0.0.1:587 \
  --smtp-tls-cert "$pki/edge.pem" --smtp-tls-key "$pki/edge.key" --smtp-require-starttls --smtp-auth-accept-any \
  --smtp-disable-rdns --disable-version-check
start webhook-receiver node "$here/webhook-receiver.mjs"
start cloudflare-sink node "$here/cloudflare-sink.mjs"
start stripe node "$here/stripe.mjs"
start r2 node "$here/r2.mjs"
start pg-edge env E2E_EDGE_CERT="$pki/edge.pem" E2E_EDGE_KEY="$pki/edge.key" node "$here/pg-edge.mjs"
start caddy env XDG_DATA_HOME="$state/caddy" XDG_CONFIG_HOME="$state/caddy" E2E_EDGE_CERT="$pki/edge.pem" E2E_EDGE_KEY="$pki/edge.key" \
  "$tools/caddy" run --config "$here/Caddyfile" --adapter caddyfile
ready mailpit curl -fsS --max-time 2 http://127.0.0.1:8025/readyz
ready cloudflare-sink curl -fsS --max-time 2 http://127.0.0.1:8026/messages
ready stripe curl -fsS --max-time 2 http://127.0.0.1:8027/log
ready r2 curl -fsS --max-time 2 http://127.0.0.1:8028/log
ready caddy edge_serves
ready pg-edge pg_edge_serves
