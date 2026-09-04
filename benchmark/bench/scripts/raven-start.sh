#!/usr/bin/env bash
# TerraBase benchmark lane: pinned RavenDB reference server.
#
# Pinned identity: an operator-supplied immutable digest of the official
# linux-x64 container image. Mutable tags (especially `latest`) are refused.
# The exact server build version is captured verbatim from `/build/version`.
set -euo pipefail

PORT=8091
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "$SCRIPT_DIR/resource-envelope.sh"
envelope_require

RAVEN_DIGEST="${TERRABASE_RAVEN_IMAGE_DIGEST:-}"
if [[ ! "$RAVEN_DIGEST" =~ ^[0-9a-fA-F]{64}$ ]]; then
  echo "raven-start: TERRABASE_RAVEN_IMAGE_DIGEST must be a 64-hex immutable digest; tags are forbidden" >&2
  exit 2
fi
RAVEN_IMAGE="docker.io/ravendb/ravendb@sha256:$RAVEN_DIGEST"
EVIDENCE_PATH="/tmp/terrabase-bench-raven-resource-$$.txt"

podman rm -f terrabase-bench-raven >/dev/null 2>&1 || true
podman run -d --name terrabase-bench-raven \
  --network host \
  --cpus 2 --memory "$TERRABASE_BENCH_ENVELOPE_MEMORY" \
  --cpuset-cpus "$(envelope_cpu_set)" \
  -e RAVEN_Setup_Mode=None \
  -e RAVEN_ServerUrl=http://127.0.0.1:$PORT \
  -e RAVEN_Security_UnsecuredAccessAllowed=PublicNetwork \
  -e RAVEN_DataDir=/tmp/ravendb-bench-data \
  "$RAVEN_IMAGE" >/dev/null

# Verify the container runtime retained the requested cgroup values. A
# benchmark cannot become complete merely because the HTTP endpoint answers.
envelope_podman terrabase-bench-raven

cleanup() {
  local pid
  pid="$(podman inspect --format '{{.State.Pid}}' terrabase-bench-raven 2>/dev/null || true)"
  if [[ "$pid" =~ ^[0-9]+$ && "$pid" != 0 ]]; then
    envelope_subject_evidence ravendb "$pid" "$EVIDENCE_PATH"
    echo "RAVEN_RESOURCE_EVIDENCE: $EVIDENCE_PATH"
  fi
  podman rm -f terrabase-bench-raven >/dev/null 2>&1 || true
}
trap cleanup EXIT
trap 'exit 143' INT TERM
# Hold mode: leave the pinned reference running for the lane runs that the
# documented se15 flow starts AFTER this script exits. The next start always
# removes the prior server first, so nothing leaks.
[[ "${TERRABASE_BENCH_HOLD:-}" != 1 ]] || trap - EXIT

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
curl -sf --max-time 5 "http://127.0.0.1:$PORT/build/version"
echo
echo "RAVEN_IMAGE_DIGEST: sha256:$RAVEN_DIGEST"
echo "RAVEN_TOPOLOGY: single-node"
echo "RAVEN_ENVELOPE: $TERRABASE_BENCH_ENVELOPE_SPEC"
RAVEN_PID="$(podman inspect --format '{{.State.Pid}}' terrabase-bench-raven)"
envelope_subject_evidence ravendb "$RAVEN_PID" "$EVIDENCE_PATH"
echo "RAVEN_RESOURCE_EVIDENCE: $EVIDENCE_PATH"
echo RAVEN_READY
