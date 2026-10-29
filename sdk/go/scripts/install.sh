#!/bin/sh
# Install a published TerraScale CLI into ~/.local/bin without sudo.
set -eu
fail() { printf 'ts installer: %s\n' "$*" >&2; exit 1; }
[ "$#" -le 1 ] || fail 'usage: install.sh [0.0.X]'
release=${1:-latest}
if [ "$release" != latest ]; then
  printf '%s\n' "$release" | LC_ALL=C grep -Eq '^0\.0\.(0|[1-9][0-9]*)$' || fail 'version must be 0.0.X'
fi
case $(uname -s) in
  Linux) platform=linux ;;
  Darwin) platform=darwin ;;
  *) fail 'supported systems: Linux and macOS; download the Windows zip from GitHub Releases' ;;
esac
case $(uname -m) in
  x86_64|amd64) arch=amd64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) fail 'supported architectures: amd64 and arm64' ;;
esac
command -v curl >/dev/null 2>&1 || fail 'curl is required'
command -v tar >/dev/null 2>&1 || fail 'tar is required'
if command -v sha256sum >/dev/null 2>&1; then
  checksum=sha256sum
elif command -v shasum >/dev/null 2>&1; then
  checksum=shasum
else
  fail 'sha256sum or shasum is required'
fi
base=https://github.com/TerraScale/terrascale/releases
if [ "$release" = latest ]; then
  # Other monorepo areas also release; select only stable Go SDK tags.
  release=$(curl --proto '=https' --tlsv1.2 -fsSL 'https://api.github.com/repos/TerraScale/terrascale/releases?per_page=100' |
    tr ',' '\n' |
    sed -n 's/.*"tag_name":[[:space:]]*"sdk\/go\/v\(0\.0\.[0-9][0-9]*\)".*/\1/p' |
    sort -t. -k3,3n | tail -n 1)
  printf '%s\n' "$release" | LC_ALL=C grep -Eq '^0\.0\.(0|[1-9][0-9]*)$' || fail 'no published Go SDK 0.0.X release found'
fi
base=$base/download/sdk/go/v$release
archive=ts_${platform}_${arch}.tar.gz
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
trap 'exit 1' HUP INT TERM
curl --proto '=https' --tlsv1.2 -fsSL "$base/$archive" -o "$tmp/$archive"
curl --proto '=https' --tlsv1.2 -fsSL "$base/checksums.txt" -o "$tmp/checksums.txt"
expected=$(awk -v name="$archive" '$2 == name { print $1 }' "$tmp/checksums.txt")
[ "${#expected}" -eq 64 ] || fail 'missing or invalid archive checksum'
case $expected in *[!0-9a-fA-F]*) fail 'invalid archive checksum' ;; esac
if [ "$checksum" = sha256sum ]; then
  actual=$(sha256sum "$tmp/$archive")
else
  actual=$(shasum -a 256 "$tmp/$archive")
fi
[ "$expected" = "${actual%% *}" ] || fail 'checksum mismatch; nothing installed'
# Extract only the binary, never arbitrary paths from the archive.
tar -xzOf "$tmp/$archive" ts > "$tmp/ts"
[ -s "$tmp/ts" ] || fail 'archive contains no binary'
chmod 755 "$tmp/ts"
bin=$HOME/.local/bin
mkdir -p "$bin"
# Rename on the destination filesystem so a failed copy preserves the old binary.
staged=$(mktemp "$bin/.ts.XXXXXXXX")
trap 'rm -rf "$tmp"; rm -f "$staged"' EXIT
cp "$tmp/ts" "$staged"
chmod 755 "$staged"
mv -f "$staged" "$bin/ts"
printf 'Installed ts %s to %s/ts\nAdd %s to PATH if needed.\n' "$release" "$bin" "$bin"
