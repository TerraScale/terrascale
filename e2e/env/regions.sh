set -euo pipefail
printf 'journey-1 gru 127.0.0.1\njourney-2 fra 127.0.0.2\n' > "$state/peers"
rm -rf "$state/terrabase-fra"
mkdir -p "$state/terrabase-fra"
cells=(DOTNET_ENVIRONMENT=Production TS_PEERS_FILE="$state/peers" TERRABASE_INTERNAL_PORT=8081
  TERRABASE_AUTH_ISSUER=https://auth.terrascale.tech/terrascale TERRABASE_AUTH_AUDIENCE=https://api.terrascale.tech
  TERRABASE_PEER_SECRET="$peer_secret" TERRABASE_MASTER_KEY="$terrabase_master_key"
  AWS_ENDPOINT_URL_S3=https://journey.backups.terrascale.tech/ AWS_REGION=auto BUCKET_NAME=journey-backups
  AWS_ACCESS_KEY_ID="$r2_access_key_id" AWS_SECRET_ACCESS_KEY="$r2_secret_access_key"
  TERRABASE_SHAPES_ACCESS_KEY_ID="$r2_access_key_id" TERRABASE_SHAPES_SECRET_ACCESS_KEY="$r2_secret_access_key"
  TERRABASE_SHAPES_CDN_API_TOKEN="$cloudflare_token"
  TERRABASE_FILES_S3_ENDPOINT=https://journey.r2.cloudflarestorage.com/ TERRABASE_FILES_S3_ACCESS_KEY_ID="$r2_access_key_id"
  TERRABASE_FILES_S3_SECRET_ACCESS_KEY="$r2_secret_access_key")
start terrabase env "${cells[@]}" TS_REGION=gru TS_NODE=journey-1 TERRABASE_DATA_DIRECTORY="$state/terrabase" \
  TERRABASE_PORT=8080 TERRABASE_METRICS_PORT=9091 TERRABASE_POSTGRES_PORT=6432 dotnet "$build/terrabase/terrabase.dll"
start terrabase-fra env "${cells[@]}" TS_REGION=fra TS_NODE=journey-2 TERRABASE_DATA_DIRECTORY="$state/terrabase-fra" \
  TERRABASE_PORT=8180 TERRABASE_METRICS_PORT=9191 TERRABASE_POSTGRES_PORT=6532 dotnet "$build/terrabase/terrabase.dll"
ready terrabase curl -fsS --max-time 2 http://127.0.0.1:8080/readyz
ready terrabase-fra curl -fsS --max-time 2 http://127.0.0.1:8180/readyz
