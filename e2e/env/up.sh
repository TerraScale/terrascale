set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
state=${E2E_STATE:?E2E_STATE is the run state directory}
build=$(cd "${E2E_BUILD:?E2E_BUILD holds the terrabase, ts-auth and dashboard builds}" && pwd)
tools=${E2E_TOOLS:?E2E_TOOLS is the directory that caches the pinned tools}
pki=$state/pki
logs=$state/logs
pids=$state/pids
trust=${SSL_CERT_FILE:?SSL_CERT_FILE is the trust bundle that holds the run CA}
umask 077
rm -rf "$logs" "$pids" "$state/terrabase" "$state/caddy"
mkdir -p "$logs" "$pids" "$state/terrabase" "$state/caddy"

random() { openssl rand -base64 "$1" | tr '+/' '-_' | tr -d '=\n'; }
peer_secret=$(openssl rand -hex 32)
terrabase_master_key=$(openssl rand -base64 32)
auth_master_key=$(openssl rand -base64 32)
auth_audit_signing_key=$(openssl rand -base64 32)
session_key=$(openssl rand -base64 32)
client_secret=$(random 36)
stripe_secret_key=sk_test_$(openssl rand -hex 24)
stripe_signing_secret=whsec_$(openssl rand -hex 24)
cloudflare_account=$(openssl rand -hex 16)
cloudflare_token=$(random 30)
smtp_username=$(openssl rand -hex 8)
smtp_password=$(random 30)
printf '%s\n' "$peer_secret" "$terrabase_master_key" "$auth_master_key" "$auth_audit_signing_key" "$session_key" "$client_secret" \
  "$stripe_secret_key" "$stripe_signing_secret" "$cloudflare_account" "$cloudflare_token" "$smtp_username" "$smtp_password" > "$state/secrets"

start() {
  local name=$1
  shift
  "$@" > "$logs/$name.log" 2>&1 &
  echo $! > "$pids/$name.pid"
}

ready() {
  local name=$1
  shift
  local pid
  pid=$(cat "$pids/$name.pid")
  until "$@" > /dev/null 2>&1; do
    if ! kill -0 "$pid" 2> /dev/null; then
      echo "$name exited before it was ready" >&2
      tail -n 60 "$logs/$name.log" >&2
      return 1
    fi
    sleep 0.1
  done
}

edge_serves() { test "$(curl -s -o /dev/null -w '%{http_code}' --cacert "$trust" https://status.terrascale.tech/)" = 404; }

start mailpit "$tools/mailpit" --listen 127.0.0.1:8025 --smtp 127.0.0.1:587 \
  --smtp-tls-cert "$pki/edge.pem" --smtp-tls-key "$pki/edge.key" --smtp-require-starttls --smtp-auth-accept-any \
  --smtp-disable-rdns --disable-version-check
start cloudflare-sink node "$here/cloudflare-sink.mjs"
start caddy env XDG_DATA_HOME="$state/caddy" XDG_CONFIG_HOME="$state/caddy" E2E_EDGE_CERT="$pki/edge.pem" E2E_EDGE_KEY="$pki/edge.key" \
  "$tools/caddy" run --config "$here/Caddyfile" --adapter caddyfile
start terrabase env DOTNET_ENVIRONMENT=Development TERRABASE_DATA_DIRECTORY="$state/terrabase" TERRABASE_PORT=8080 \
  TERRABASE_INTERNAL_PORT=8081 TERRABASE_METRICS_PORT=9091 TERRABASE_POSTGRES_PORT=6432 \
  TERRABASE_AUTH_ISSUER=https://auth.terrascale.tech/terrascale TERRABASE_AUTH_AUDIENCE=https://api.terrascale.tech \
  TERRABASE_PEER_SECRET="$peer_secret" TERRABASE_MASTER_KEY="$terrabase_master_key" \
  dotnet "$build/terrabase/terrabase.dll"
ready terrabase curl -fsS http://127.0.0.1:8080/readyz

auth=(TS_AUTH_TERRABASE_URL=http://127.0.0.1:8081 TS_AUTH_LISTEN=http://127.0.0.1:8090 TS_AUTH_METRICS_LISTEN=http://127.0.0.1:9092
  TS_AUTH_DASHBOARD_REDIRECT_URIS=https://dash.terrascale.tech/login/callback
  TS_AUTH_DASHBOARD_POST_LOGOUT_REDIRECT_URIS=https://dash.terrascale.tech/login
  TS_AUTH_DASHBOARD_CLIENT_SECRET="$client_secret" TS_AUTH_MASTER_KEY="$auth_master_key" TS_AUTH_AUDIT_SIGNING_KEY="$auth_audit_signing_key"
  TERRABASE_PEER_SECRET="$peer_secret" TS_REGION=gru TS_NODE=journey-1
  TS_AUTH_SMTP_HOST=smtp.email.sa-saopaulo-1.oci.oraclecloud.com TS_AUTH_SMTP_PORT=587
  TS_AUTH_SMTP_USERNAME="$smtp_username" TS_AUTH_SMTP_PASSWORD="$smtp_password"
  CLOUDFLARE_ACCOUNT_ID="$cloudflare_account" CLOUDFLARE_EMAIL_TOKEN="$cloudflare_token")
start ts-auth env "${auth[@]}" dotnet "$build/ts-auth/ts-auth.dll"
start dashboard env TS_AUTH_DASHBOARD_CLIENT_SECRET="$client_secret" TS_DASHBOARD_SESSION_ENCRYPTION_KEY="$session_key" \
  STRIPE_SECRET_KEY="$stripe_secret_key" STRIPE_WEBHOOK_SECRET="$stripe_signing_secret" TERRABASE_PEER_SECRET="$peer_secret" \
  TS_DASHBOARD_TERRABASE_URL=http://127.0.0.1:8081 TS_REGION=gru TS_NODE=journey-1 \
  dotnet "$build/dashboard/TerraScale.Dashboard.dll" --environment Production --urls http://127.0.0.1:8085 --contentRoot "$build/dashboard"

ready mailpit curl -fsS http://127.0.0.1:8025/readyz
ready cloudflare-sink curl -fsS http://127.0.0.1:8026/messages
ready caddy edge_serves
ready ts-auth curl -fsS http://127.0.0.1:8090/readyz
ready dashboard curl -fsS --cacert "$trust" https://dash.terrascale.tech/readyz
curl -fsS --cacert "$trust" https://dash.terrascale.tech/readyz
echo
