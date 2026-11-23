set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
source "$here/service.sh"
build=$(cd "${E2E_BUILD:?E2E_BUILD holds the terrabase, ts-platform, ts-auth and dashboard builds}" && pwd)
trust=${SSL_CERT_FILE:?SSL_CERT_FILE is the trust bundle that holds the run CA}
umask 077
rm -rf "$state/terrabase"
mkdir -p "$state/terrabase"

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
r2_access_key_id=$(openssl rand -hex 16)
r2_secret_access_key=$(openssl rand -hex 32)
printf '%s\n' "$r2_access_key_id" "$r2_secret_access_key" > "$state/r2"
printf '%s\n' "$peer_secret" "$terrabase_master_key" "$auth_master_key" "$auth_audit_signing_key" "$session_key" "$client_secret" \
  "$stripe_secret_key" "$stripe_signing_secret" "$cloudflare_account" "$cloudflare_token" "$smtp_username" "$smtp_password" \
  "$r2_access_key_id" "$r2_secret_access_key" > "$state/secrets"
if [ -n "${GITHUB_ACTIONS:-}" ]; then sed 's/^/::add-mask::/' "$state/secrets"; fi

node "$here/callers.mjs" "$state"
export TERRABASE_CALLERS_FILE="$state/callers.json"

identity=(TERRABASE_INTERNAL_PORT="$(e2e_port internal)" TERRABASE_AUTH_ISSUER=https://auth.terrascale.tech/terrascale TERRABASE_AUTH_AUDIENCE=https://api.terrascale.tech
  TERRABASE_PEER_SECRET="$peer_secret" TERRABASE_MASTER_KEY="$terrabase_master_key")
if [ "${E2E_REGIONS:-1}" = 2 ]; then
  {
    printf 'journey-1 gru 127.0.0.1 %s %s\n' "$(e2e_port quic)" "$(e2e_port internal)"
    printf 'journey-2 fra 127.0.0.2 %s %s\n' "$(e2e_port quic_fra)" "$(e2e_port internal)"
    if [ "${E2E_NODES_PER_REGION:-1}" = 2 ]; then
      printf 'journey-3 gru 127.0.0.3 %s %s\n' "$(e2e_port quic_gru2)" "$(e2e_port internal)"
      printf 'journey-4 fra 127.0.0.4 %s %s\n' "$(e2e_port quic_fra2)" "$(e2e_port internal)"
    fi
  } > "$state/peers"
  rm -rf "$state/terrabase-fra"
  mkdir -p "$state/terrabase-fra"
  cells=(DOTNET_ENVIRONMENT=Production TS_PEERS_FILE="$state/peers" "${identity[@]}"
    AWS_ENDPOINT_URL_S3=https://journey.backups.terrascale.tech/ AWS_REGION=auto BUCKET_NAME=journey-backups
    AWS_ACCESS_KEY_ID="$r2_access_key_id" AWS_SECRET_ACCESS_KEY="$r2_secret_access_key")
  start terrabase env "${cells[@]}" TS_REGION=gru TS_NODE=journey-1 TERRABASE_DATA_DIRECTORY="$state/terrabase" TERRABASE_PORT="$(e2e_port terrabase)" \
    TERRABASE_METRICS_PORT="$(e2e_port metrics)" TERRABASE_POSTGRES_PORT="$(e2e_port postgres)" TERRABASE_QUIC_PORT="$(e2e_port quic)" dotnet "$build/terrabase/terrabase.dll"
  ready terrabase curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port terrabase)/readyz"
  node "$here/caller-peer-probe.mjs" "$state" > "$state/caller-peer-proof"
  start terrabase-fra env "${cells[@]}" TS_REGION=fra TS_NODE=journey-2 TERRABASE_DATA_DIRECTORY="$state/terrabase-fra" TERRABASE_PORT="$(e2e_port terrabase_fra)" \
    TERRABASE_METRICS_PORT="$(e2e_port metrics_fra)" TERRABASE_POSTGRES_PORT="$(e2e_port postgres_fra)" TERRABASE_QUIC_PORT="$(e2e_port quic_fra)" dotnet "$build/terrabase/terrabase.dll"
  if [ "${E2E_NODES_PER_REGION:-1}" = 2 ]; then
    rm -rf "$state/terrabase-gru-2" "$state/terrabase-fra-2"
    start terrabase-gru-2 env "${cells[@]}" TS_REGION=gru TS_NODE=journey-3 TERRABASE_DATA_DIRECTORY="$state/terrabase-gru-2" TERRABASE_PORT="$(e2e_port terrabase_gru2)" \
      TERRABASE_METRICS_PORT="$(e2e_port metrics_gru2)" TERRABASE_POSTGRES_PORT="$(e2e_port postgres_gru2)" TERRABASE_QUIC_PORT="$(e2e_port quic_gru2)" dotnet "$build/terrabase/terrabase.dll"
    start terrabase-fra-2 env "${cells[@]}" TS_REGION=fra TS_NODE=journey-4 TERRABASE_DATA_DIRECTORY="$state/terrabase-fra-2" TERRABASE_PORT="$(e2e_port terrabase_fra2)" \
      TERRABASE_METRICS_PORT="$(e2e_port metrics_fra2)" TERRABASE_POSTGRES_PORT="$(e2e_port postgres_fra2)" TERRABASE_QUIC_PORT="$(e2e_port quic_fra2)" dotnet "$build/terrabase/terrabase.dll"
    ready terrabase-gru-2 curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port terrabase_gru2)/readyz"
    ready terrabase-fra-2 curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port terrabase_fra2)/readyz"
  fi
  ready terrabase curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port terrabase)/readyz"
  ready terrabase-fra curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port terrabase_fra)/readyz"
else
  start terrabase env DOTNET_ENVIRONMENT=Development "${identity[@]}" TS_REGION=gru TS_NODE=journey-1 TERRABASE_DATA_DIRECTORY="$state/terrabase" \
    TERRABASE_PORT="$(e2e_port terrabase)" TERRABASE_METRICS_PORT="$(e2e_port metrics)" TERRABASE_POSTGRES_PORT="$(e2e_port postgres)" \
    TERRABASE_QUIC_PORT="$(e2e_port quic)" dotnet "$build/terrabase/terrabase.dll"
  ready terrabase curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port terrabase)/readyz"
  node "$here/caller-peer-probe.mjs" "$state" > "$state/caller-peer-proof"
  curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port metrics)/metrics" > "$state/backup-disabled.metrics"
  awk '/^terrabase_backup_disabled(\{[^}]*\})? 1$/ {found=1; print} END {exit !found}' "$state/backup-disabled.metrics"
  [ "$(curl -sS --max-time 2 -o "$state/backup-disabled.json" -w '%{http_code}' "http://127.0.0.1:$(e2e_port terrabase)/backupz")" = 503 ]
fi

start ts-platform env TS_PLATFORM_PORT="$(e2e_port platform)" TS_PLATFORM_METRICS_PORT="$(e2e_port platform_metrics)" \
  TS_PLATFORM_TERRABASE_URL="http://127.0.0.1:$(e2e_port internal)" TS_PLATFORM_TERRABASE_QUIC="journey-1.gru@127.0.0.1:$(e2e_port quic)" \
  TS_REGION=gru TS_NODE=journey-platform-1 TERRABASE_PEER_SECRET="$peer_secret" TERRABASE_MASTER_KEY="$terrabase_master_key" \
  TERRABASE_AUTH_ISSUER=https://auth.terrascale.tech/terrascale TERRABASE_AUTH_AUDIENCE=https://api.terrascale.tech \
  TERRABASE_FILES_S3_ENDPOINT=https://journey.r2.cloudflarestorage.com/ TERRABASE_FILES_S3_ACCESS_KEY_ID="$r2_access_key_id" \
  TERRABASE_FILES_S3_SECRET_ACCESS_KEY="$r2_secret_access_key" \
  TERRABASE_SHAPES_ENDPOINT=https://6cd3f3639e928077593d9b470765efeb.r2.cloudflarestorage.com/ TERRABASE_SHAPES_BUCKET=ts-terrabase-shapes-prod \
  TERRABASE_SHAPES_ACCESS_KEY_ID="$r2_access_key_id" TERRABASE_SHAPES_SECRET_ACCESS_KEY="$r2_secret_access_key" \
  TERRABASE_SHAPES_CDN_HOST=shapes-ts-terrabase-shapes-prod.terrascale.tech TERRABASE_SHAPES_CDN_ZONE=4cc466e19478c116fae7dc445a6d0911 \
  TERRABASE_SHAPES_CDN_API_TOKEN="$cloudflare_token" dotnet "$build/ts-platform/ts-platform.dll"
ready ts-platform curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port platform)/readyz"

auth=(TS_AUTH_TERRABASE_URL="http://127.0.0.1:$(e2e_port internal)" TS_AUTH_LISTEN="http://127.0.0.1:$(e2e_port auth)" TS_AUTH_METRICS_LISTEN="http://127.0.0.1:$(e2e_port auth_metrics)"
  TS_AUTH_TURNSTILE_SITE_KEY=1x00000000000000000000AA TS_AUTH_TURNSTILE_SECRET_KEY=1x0000000000000000000000000000000AA
  TS_AUTH_DASHBOARD_REDIRECT_URIS=https://dash.terrascale.tech/login/callback
  TS_AUTH_DASHBOARD_POST_LOGOUT_REDIRECT_URIS=https://dash.terrascale.tech/login
  TS_AUTH_DASHBOARD_CLIENT_SECRET="$client_secret" TS_AUTH_MASTER_KEY="$auth_master_key" TS_AUTH_AUDIT_SIGNING_KEY="$auth_audit_signing_key"
  TERRABASE_PEER_SECRET="$peer_secret" TS_REGION=gru TS_NODE=journey-1
  TS_AUTH_SMTP_HOST=smtp.email.sa-saopaulo-1.oci.oraclecloud.com TS_AUTH_SMTP_PORT=587
  TS_AUTH_SMTP_USERNAME="$smtp_username" TS_AUTH_SMTP_PASSWORD="$smtp_password"
  CLOUDFLARE_ACCOUNT_ID="$cloudflare_account" CLOUDFLARE_EMAIL_TOKEN="$cloudflare_token")
start ts-auth env "${auth[@]}" dotnet "$build/ts-auth/ts-auth.dll"
timeout --kill-after=10 "$boot_seconds" env "${auth[@]}" dotnet "$build/ts-auth/ts-auth.dll" provision-signing-key > "$logs/provision-signing-key.log" 2>&1 \
  || { echo "ts-auth provision-signing-key failed or was still running after $boot_seconds s (exit status $?)" >&2; tail -n 40 "$logs/provision-signing-key.log" >&2; exit 1; }
start dashboard env TS_AUTH_DASHBOARD_CLIENT_SECRET="$client_secret" TS_DASHBOARD_SESSION_ENCRYPTION_KEY="$session_key" \
  STRIPE_SECRET_KEY="$stripe_secret_key" STRIPE_WEBHOOK_SECRET="$stripe_signing_secret" TERRABASE_PEER_SECRET="$peer_secret" \
  TS_DASHBOARD_TERRABASE_URL="http://127.0.0.1:$(e2e_port internal)" TS_DASHBOARD_PLATFORM_URL="http://127.0.0.1:$(e2e_port platform)" \
  TS_REGION=gru TS_NODE=journey-1 \
  dotnet "$build/dashboard/TerraScale.Dashboard.dll" --environment Production --urls "http://127.0.0.1:$(e2e_port dashboard)" --contentRoot "$build/dashboard"

ready ts-auth curl -fsS --max-time 2 "http://127.0.0.1:$(e2e_port auth)/readyz"
ready dashboard curl -fsS --max-time 2 --cacert "$trust" https://dash.terrascale.tech/readyz
curl -fsS --max-time 2 --cacert "$trust" https://dash.terrascale.tech/readyz
echo
curl -fsS --max-time 2 --cacert "$trust" https://auth.terrascale.tech/readyz
echo
