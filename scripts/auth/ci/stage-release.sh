#!/usr/bin/env bash
# Publish ts-auth as a self-contained single-file linux-x64 executable and
# stage it as DIST_DIR/ts-auth, the path the Containerfile copies.
# TS_AUTH_REVISION, reported in the /healthz and /readyz version, is the first
# 12 characters of the source commit. The executable needs only glibc,
# libstdc++, OpenSSL and CA certificates, which the chiseled runtime-deps base
# image provides.
set -euo pipefail

dist=${1:-}
[[ -n $dist && $# -eq 1 ]] || { echo "usage: $0 DIST_DIR" >&2; exit 2; }

repo=$(cd "$(dirname "$0")/../.." && pwd)
cd "$repo"

revision=$(git rev-parse HEAD)
[[ -z ${FORGEJO_SHA:-} || $revision == "$FORGEJO_SHA" ]] ||
  { echo "checkout $revision is not the event revision $FORGEJO_SHA" >&2; exit 1; }

out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
TS_AUTH_REVISION=${revision:0:12} dotnet publish src/TerraScale.Auth/TerraScale.Auth.csproj \
  --configuration Release --runtime linux-x64 --self-contained true --nologo \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none --output "$out"

binary="$out/ts-auth"
[[ -x $binary ]] || { echo "publish produced no ts-auth executable" >&2; exit 1; }
file_type=$(od -An -tx1 -N4 "$binary" | tr -d ' \n')
[[ $file_type == 7f454c46 ]] || { echo "$binary is not an ELF executable" >&2; exit 1; }

mkdir -p -- "$dist"
install -m 0755 -- "$binary" "$dist/ts-auth"
printf 'staged self-contained linux-x64 ts-auth revision %s in %s\n' "${revision:0:12}" "$dist"
