#!/usr/bin/env bash
# Publish ts-auth as a self-contained single-file executable for RID
# (linux-x64 by default, or linux-arm64) and stage it as DIST_DIR/ts-auth.
# The linux-x64 build is the one the Containerfile copies; the linux-arm64
# build is the native release asset (scripts/auth/ci/publish-binary.sh).
# TS_AUTH_REVISION, reported in the /healthz and /readyz version, is the first
# 12 characters of the source commit. The executable needs only glibc,
# libstdc++, OpenSSL and CA certificates, which the chiseled runtime-deps base
# image provides.
set -euo pipefail

dist=${1:-}
rid=${2:-linux-x64}
[[ -n $dist && $# -ge 1 && $# -le 2 ]] || { echo "usage: $0 DIST_DIR [linux-x64|linux-arm64]" >&2; exit 2; }
# ELF e_machine (offset 18, little-endian) the RID must produce, so a build
# that silently targeted another architecture fails here.
case $rid in
  linux-x64) machine=3e00 ;;
  linux-arm64) machine=b700 ;;
  *) echo "unsupported runtime identifier: $rid" >&2; exit 2 ;;
esac

repo=$(cd "$(dirname "$0")/../../.." && pwd)
cd "$repo"

revision=$(git rev-parse HEAD)
[[ -z ${FORGEJO_SHA:-} || $revision == "$FORGEJO_SHA" ]] ||
  { echo "checkout $revision is not the event revision $FORGEJO_SHA" >&2; exit 1; }

out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
TS_AUTH_REVISION=${revision:0:12} dotnet publish src/TerraScale.Auth/TerraScale.Auth.csproj \
  --configuration Release --runtime "$rid" --self-contained true --nologo \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none --output "$out"

binary="$out/ts-auth"
[[ -x $binary ]] || { echo "publish produced no ts-auth executable" >&2; exit 1; }
file_type=$(od -An -tx1 -N4 "$binary" | tr -d ' \n')
[[ $file_type == 7f454c46 ]] || { echo "$binary is not an ELF executable" >&2; exit 1; }
file_machine=$(od -An -tx1 -j18 -N2 "$binary" | tr -d ' \n')
[[ $file_machine == "$machine" ]] ||
  { echo "$binary has ELF machine $file_machine, not $machine for $rid" >&2; exit 1; }

mkdir -p -- "$dist"
install -m 0755 -- "$binary" "$dist/ts-auth"
printf 'staged self-contained %s ts-auth revision %s in %s\n' "$rid" "${revision:0:12}" "$dist"
