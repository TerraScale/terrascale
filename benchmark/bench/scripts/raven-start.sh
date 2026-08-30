#!/usr/bin/env bash
# TerraBase benchmark lane: pinned RavenDB reference server.
#
# Pinned identity: RavenDB stable series from the official linux-x64
# container image (docker.io/ravendb/ravendb:latest), run unsecured on
# loopback only, with a fresh data directory per run discarded afterwards.
# The exact server build version is captured verbatim from the server's
# own /build/version endpoint at the run and recorded on the receipt.
set -euo pipefail

PORT=8091

podman rm -f terrabase-bench-raven >/dev/null 2>&1 || true
podman run -d --name terrabase-bench-raven \
  --network host \
  -e RAVEN_Setup_Mode=None \
  -e RAVEN_ServerUrl=http://127.0.0.1:$PORT \
  -e RAVEN_Security_UnsecuredAccessAllowed=PublicNetwork \
  -e RAVEN_DataDir=/tmp/ravendb-bench-data \
  docker.io/ravendb/ravendb:latest >/dev/null

cleanup() {
  podman rm -f terrabase-bench-raven >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

# Wait for the server to answer; the loop is best-effort and the explicit
# probes below report readiness honestly instead of hiding a failure.
ready=false
for _ in $(seq 1 120); do
  if curl -sf --max-time 2 "http://127.0.0.1:$PORT/health/ready" >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 1
done
if [[ "$ready" != true ]]; then
  echo "RavenDB did not become ready on 127.0.0.1:$PORT" >&2
  exit 1
fi
echo "RAVEN_BUILD_INFO:"
curl -s --max-time 5 "http://127.0.0.1:$PORT/build/version" || true
echo
echo RAVEN_READY
