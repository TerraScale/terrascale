#!/usr/bin/env bash
# Run twice with separate output directories to prove byte-for-byte reproducibility.
set -euo pipefail
arch=${1:?usage: build-turso-native.sh <amd64|arm64> <output-directory>}
output=${2:?usage: build-turso-native.sh <amd64|arm64> <output-directory>}
case "$arch" in amd64|arm64) ;; *) echo 'Expected amd64 or arm64' >&2; exit 1 ;; esac
case "$(uname -m):$arch" in
  x86_64:amd64|aarch64:arm64) ;;
  *) echo 'Native publication requires a real host of the requested architecture; emulation is not accepted.' >&2; exit 1 ;;
esac
root=$(git rev-parse --show-toplevel)
if command -v docker >/dev/null; then
  builder=docker
elif command -v podman >/dev/null; then
  builder=podman
else
  echo 'Docker or Podman is required to build the pinned native core' >&2
  exit 1
fi
"$builder" buildx build --platform "linux/$arch" --no-cache --target native \
  --file "$root/scripts/vendor/Dockerfile.turso-native" \
  --output "type=local,dest=$output" "$root"
(cd "$output" && sha256sum --check SHA256SUMS-*)
"$builder" buildx build --platform "linux/$arch" --no-cache --target native \
  --file "$root/scripts/vendor/Dockerfile.router-parser" \
  --output "type=local,dest=$output" "$root"
