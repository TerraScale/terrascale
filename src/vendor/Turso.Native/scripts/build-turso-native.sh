#!/usr/bin/env bash
set -euo pipefail
arch=${1:?usage: build-turso-native.sh <amd64|arm64> <output-directory>}
output=${2:?usage: build-turso-native.sh <amd64|arm64> <output-directory>}
case "$arch" in amd64|arm64) ;; *) echo 'Expected amd64 or arm64' >&2; exit 1 ;; esac
case "$(uname -m):$arch" in
  x86_64:amd64|aarch64:arm64) ;;
  *) echo 'Native publication requires a real host of the requested architecture; emulation is not accepted.' >&2; exit 1 ;;
esac
root=$(git rev-parse --show-toplevel)
source "$root/src/vendor/Turso.Native/turso-pin.env"
[[ $TURSO_COMMIT =~ ^[0-9a-f]{40}$ ]]
[[ $TURSO_SHA256 =~ ^[0-9a-f]{64}$ ]]
if command -v docker >/dev/null; then
  builder=docker
elif command -v podman >/dev/null; then
  builder=podman
else
  echo 'Docker or Podman is required to build the pinned native core' >&2
  exit 1
fi
"$builder" buildx build --platform "linux/$arch" --no-cache --target native \
  --file "$root/src/vendor/Turso.Native/scripts/Dockerfile.turso-native" \
  --output "type=local,dest=$output" "$root"
(cd "$output" && sha256sum --check SHA256SUMS-*)
"$builder" buildx build --platform "linux/$arch" --no-cache --target native \
  --file "$root/src/vendor/Turso.Native/scripts/Dockerfile.router-parser" \
  --output "type=local,dest=$output" "$root"
