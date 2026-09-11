#!/bin/sh
set -eu
sdk_root=${1:-../../..}
sdk_root=$(CDPATH= cd -- "$sdk_root" && pwd)
revision=${2:-$(git -C "$sdk_root" rev-parse HEAD)}
version=${3:-v0.0.0-20260910000829-658d897f69a5}
case "$version" in v*) ;; *) echo 'version must begin with v' >&2; exit 2;; esac
out=$(pwd)/file-proxy
module_dir="$out/terrascale.tech/sdk/@v"
mkdir -p "$module_dir"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT HUP INT TERM
git -C "$sdk_root" archive --format=zip --prefix="terrascale.tech/sdk@$version/" "$revision" -- '*.go' go.mod go.sum > "$tmp/sdk.zip"
printf '{"Version":"%s","Time":"2026-09-10T00:08:29Z"}\n' "$version" > "$module_dir/$version.info"
git -C "$sdk_root" show "$revision:go.mod" > "$module_dir/$version.mod"
cp "$tmp/sdk.zip" "$module_dir/$version.zip"
sha256sum "$module_dir/$version.zip" | tee "$out/$version.zip.sha256"
test "$(sha256sum "$module_dir/$version.zip" | awk '{print $1}')" = "$(awk 'NF {print $1; exit}' "$out/$version.zip.sha256")"
printf '%s\n' "revision=$revision" "version=$version" "proxy=$out" > "$out/QUALIFICATION_SOURCE"
echo "GOPROXY=file://$out GOSUMDB=off go run . --phase seed"
