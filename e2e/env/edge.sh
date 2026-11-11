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

start mailpit "$tools/mailpit" --listen 127.0.0.1:8025 --smtp 127.0.0.1:587 \
  --smtp-tls-cert "$pki/edge.pem" --smtp-tls-key "$pki/edge.key" --smtp-require-starttls --smtp-auth-accept-any \
  --smtp-disable-rdns --disable-version-check
start cloudflare-sink node "$here/cloudflare-sink.mjs"
start stripe node "$here/stripe.mjs"
start caddy env XDG_DATA_HOME="$state/caddy" XDG_CONFIG_HOME="$state/caddy" E2E_EDGE_CERT="$pki/edge.pem" E2E_EDGE_KEY="$pki/edge.key" \
  "$tools/caddy" run --config "$here/Caddyfile" --adapter caddyfile
ready mailpit curl -fsS --max-time 2 http://127.0.0.1:8025/readyz
ready cloudflare-sink curl -fsS --max-time 2 http://127.0.0.1:8026/messages
ready stripe curl -fsS --max-time 2 http://127.0.0.1:8027/log
ready caddy edge_serves
