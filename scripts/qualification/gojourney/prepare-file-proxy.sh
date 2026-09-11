#!/bin/sh
set -eu
sdk_root=${1:-../../..}
sdk_root=$(CDPATH= cd -- "$sdk_root" && pwd)
revision=${2:-$(git -C "$sdk_root" rev-parse HEAD)}
revision=$(git -C "$sdk_root" rev-parse --verify "$revision^{commit}")
commit_time=$(git -C "$sdk_root" show -s --format=%cI "$revision")
timestamp=$(date -u -d "$commit_time" '+%Y%m%d%H%M%S')
short_revision=$(git -C "$sdk_root" rev-parse --short=12 "$revision")
default_version="v0.0.0-${timestamp}-${short_revision}"
version=${3:-$default_version}
case "$version" in v*) ;; *) echo 'version must begin with v' >&2; exit 2;; esac
test "$version" = "$default_version" || {
	echo "version $version does not identify revision $revision (want $default_version)" >&2
	exit 2
}
test "$(git -C "$sdk_root" show "$revision:go.mod" | sed -n 's/^module //p; 2q')" = 'terrascale.tech/sdk' || {
	echo "revision $revision has unexpected Go module path" >&2
	exit 2
}
out=$(pwd)/file-proxy
module_dir="$out/terrascale.tech/sdk/@v"
mkdir -p "$module_dir"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT HUP INT TERM
git -C "$sdk_root" archive --format=zip --prefix="terrascale.tech/sdk@$version/" "$revision" -- '*.go' go.mod go.sum > "$tmp/sdk.git.zip"
# Go module ZIP hashes cover files, not explicit directory entries. git
# archive emits both, so normalize the archive before placing it in GOPROXY.
python3 - "$tmp/sdk.git.zip" "$tmp/sdk.zip" <<'PY'
import sys
import zipfile

source, destination = sys.argv[1:]
with zipfile.ZipFile(source) as archive, zipfile.ZipFile(destination, "w") as output:
    for entry in archive.infolist():
        if entry.is_dir():
            continue
        output.writestr(entry, archive.read(entry))
PY
printf '{"Version":"%s","Time":"%s"}\n' "$version" "$(date -u -d "$commit_time" '+%Y-%m-%dT%H:%M:%SZ')" > "$module_dir/$version.info"
git -C "$sdk_root" show "$revision:go.mod" > "$module_dir/$version.mod"
test "$(sed -n 's/^module //p; 2q' "$module_dir/$version.mod")" = 'terrascale.tech/sdk' || exit 2
cp "$tmp/sdk.zip" "$module_dir/$version.zip"
sha256sum "$module_dir/$version.zip" | tee "$out/$version.zip.sha256"
test "$(sha256sum "$module_dir/$version.zip" | awk '{print $1}')" = "$(awk 'NF {print $1; exit}' "$out/$version.zip.sha256")"
printf '%s\n' "revision=$revision" "version=$version" "proxy=$out" > "$out/QUALIFICATION_SOURCE"
echo "GOPROXY=file://$out GOSUMDB=off go run . --phase seed"
