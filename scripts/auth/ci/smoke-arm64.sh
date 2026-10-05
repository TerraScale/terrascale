#!/usr/bin/env bash
# Start the staged linux-arm64 executable under qemu-aarch64 user-mode emulation, with the
# arm64 variant of the Containerfile's digest-pinned chiseled runtime-deps image as its root
# (glibc, libstdc++, OpenSSL, CA certificates), and require /healthz to report this
# revision and /readyz to fail closed without a store. Startup runs the Argon2id self-test,
# so a healthy process also proves the bundled libsodium extracts and loads on arm64.
set -euo pipefail

binary=${1:-}
[[ -n $binary && $# -eq 1 ]] || { echo "usage: $0 BINARY" >&2; exit 2; }
[[ -x $binary ]] || { echo "$binary is not an executable" >&2; exit 1; }
qemu=$(command -v qemu-aarch64) || { echo "qemu-aarch64 is required" >&2; exit 1; }
binary=$(cd "$(dirname "$binary")" && pwd)/$(basename "$binary")

repo=$(cd "$(dirname "$0")/../.." && pwd)
revision=$(git -C "$repo" rev-parse HEAD)
base=$(awk '$1 == "FROM" {print $2; exit}' "$repo/Containerfile")
[[ $base == *@sha256:* ]] || { echo "the Containerfile base image is not pinned by digest" >&2; exit 1; }

work=$(mktemp -d)
container=
pid=
cleanup() {
  if [[ -n $pid ]]; then
    kill "$pid" 2>/dev/null || true
    # Give shutdown five seconds, then force it; reap before removing runtime files.
    for _ in {1..50}; do
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
  if [[ -n $container ]]; then podman rm "$container" >/dev/null 2>&1 || true; fi
  rm -rf -- "$work"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

# Pulling, creating and exporting never run the image, so the host needs no arm64 binfmt.
podman pull --quiet --platform linux/arm64 "$base" >/dev/null
# The base has no default command; /unused is only a placeholder for creation.
container=$(podman create --platform linux/arm64 "$base" /unused)
mkdir -- "$work/root" "$work/extract"
podman export "$container" | tar -x -C "$work/root"
[[ -e $work/root/lib/ld-linux-aarch64.so.1 ]] || { echo "the arm64 base image has no aarch64 loader" >&2; exit 1; }

port=$(python3 - <<'PY'
import socket
with socket.socket() as listener:
    listener.bind(('127.0.0.1', 0))
    print(listener.getsockname()[1])
PY
)
# Development settings without a reachable store, as in package-image.sh.
env -i DOTNET_BUNDLE_EXTRACT_BASE_DIR="$work/extract" \
  TS_AUTH_ENVIRONMENT=Development \
  TS_AUTH_LISTEN="http://127.0.0.1:$port" \
  TS_AUTH_PUBLIC_ORIGIN="http://127.0.0.1:$port" \
  TS_AUTH_TERRASCALE_API_URL=https://127.0.0.1:1 \
  TS_AUTH_TERRASCALE_API_KEY=ci-contract-key \
  TS_AUTH_DATABASE=00000000-0000-4000-8000-000000000000/ci-contract-db \
  TS_AUTH_MASTER_KEY=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA= \
  TS_AUTH_OPERATOR_KEY=ci-operator \
  "$qemu" -L "$work/root" "$binary" > "$work/log" 2>&1 &
pid=$!

# Emulated startup takes seconds to a few minutes on a busy runner.
ready=0
for _ in $(seq 1 240); do
  if curl --silent --fail --max-time 2 "http://127.0.0.1:$port/healthz" > "$work/health"; then ready=1; break; fi
  kill -0 "$pid" 2>/dev/null || break
  sleep 1
done
if [[ $ready != 1 ]]; then
  echo "the linux-arm64 executable did not become healthy under qemu-aarch64" >&2
  tail -n 20 "$work/log" >&2
  exit 1
fi
jq -e --arg revision "${revision:0:12}" '.version | endswith("+" + $revision)' "$work/health" >/dev/null ||
  { echo "the linux-arm64 executable does not report revision ${revision:0:12}" >&2; exit 1; }
status=$(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 30 "http://127.0.0.1:$port/readyz")
[[ $status == 503 ]] || { echo "/readyz answered $status without a store, not 503" >&2; exit 1; }
printf 'smoke-checked linux-arm64 ts-auth revision %s under qemu-aarch64\n' "${revision:0:12}"
