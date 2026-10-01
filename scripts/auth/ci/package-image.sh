#!/usr/bin/env bash
# Package the binary staged by scripts/ci/stage-release.sh into the
# distroless Containerfile image localhost/terrascale/ts-auth:<commit>, then
# exercise the packaged binary and composed router without a live TerraBase:
# /healthz reports this revision, /openapi/v1.json serves the contract, and
# readiness fails closed.
set -euo pipefail

test "$#" -eq 0 || { echo "usage: $0" >&2; exit 2; }

repo=$(cd "$(dirname "$0")/../.." && pwd)
cd "$repo"

source_commit=$(git rev-parse HEAD)
image="$(bash scripts/ci/image-ref.sh)"
test -s artifacts/ci/container-publish/ts-auth
podman build --pull=missing --platform linux/amd64 --tag "$image" --file Containerfile .
test "$(podman image inspect "$image" --format '{{.Architecture}}')" = amd64
test "$(podman image inspect "$image" --format '{{.Os}}')" = linux

port=$(python3 - <<'PY'
import socket
with socket.socket() as listener:
    listener.bind(('127.0.0.1', 0))
    print(listener.getsockname()[1])
PY
)
container="ts-auth-ci-${source_commit:0:12}"
podman run --detach --rm --name "$container" --network host \
  --env TS_AUTH_ENVIRONMENT=Development \
  --env "TS_AUTH_LISTEN=http://127.0.0.1:${port}" \
  --env "TS_AUTH_PUBLIC_ORIGIN=http://127.0.0.1:${port}" \
  --env TS_AUTH_TERRASCALE_API_URL=http://127.0.0.1:1 \
  --env TS_AUTH_TERRASCALE_API_KEY=ci-contract-key \
  --env TS_AUTH_DATABASE=ci-contract-db \
  --env TS_AUTH_MASTER_KEY=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA= \
  --env TS_AUTH_OPERATOR_KEY=ci-operator \
  --env TS_AUTH_METRICS_KEY=ci-metrics \
  "$image"
trap 'podman stop "$container" >/dev/null 2>&1 || true' EXIT
ready=0
for _ in $(seq 1 30); do
  if curl --silent --fail --max-time 2 "http://127.0.0.1:${port}/healthz" >/dev/null; then ready=1; break; fi
  sleep 1
done
if test "$ready" != 1; then podman logs "$container"; exit 1; fi
curl --silent --fail "http://127.0.0.1:${port}/healthz" |
  jq -e --arg revision "${source_commit:0:12}" '.version | endswith("+" + $revision)'
curl --silent --fail "http://127.0.0.1:${port}/openapi/v1.json" |
  jq -e '.info.title == "TerraScale Auth API" and .paths["/healthz"] != null'
test "$(curl --silent --output /dev/null --write-out '%{http_code}' "http://127.0.0.1:${port}/readyz")" = 503
printf 'packaged and smoke-checked %s\n' "$image"
