#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
name=${TERRABASE_S3_CONTAINER:-terrabase-s3mock-$$}
port=${TERRABASE_S3_PORT:-19090}
# Never remove an existing container: Podman refuses a name collision.
cid=$(podman run -d --name "$name" -p "127.0.0.1:$port:9090" docker.io/adobe/s3mock@sha256:4897c37314a72ae13509513905a102e7642de3854f52fcfc2b1c5e59bab41b7e)
cleanup() { podman rm -f "$cid" >/dev/null; }
trap cleanup EXIT
ready=false
for ((attempt=0; attempt<30; attempt++)); do
  if curl -fsS "http://127.0.0.1:${port}/" >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 1
done
if [[ "$ready" != true ]]; then
  podman logs "$cid"
  exit 1
fi
curl -fsS -X PUT "http://127.0.0.1:${port}/terrabase" >/dev/null
log=$(mktemp)
TERRABASE_MINIO_ENDPOINT="http://127.0.0.1:${port}" nice -n 10 cargo test -p terrabase --lib backup::object_store::tests::minio_round_trip_100k_wal_records -- --ignored --nocapture >"$log" 2>&1 &
test_pid=$!
peak_kib=0
while kill -0 "$test_pid" 2>/dev/null; do
  rss=$(ps -eo pid=,rss= | awk -v root="$test_pid" '$1 == root {print $2}')
  if [[ -n "$rss" && "$rss" -gt "$peak_kib" ]]; then peak_kib=$rss; fi
  sleep 0.1
done
wait "$test_pid"
cat "$log"
rm -f "$log"
echo "peak observed test RSS: ${peak_kib} KiB"
