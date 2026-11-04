#!/usr/bin/env bash
set -euo pipefail

[ "$#" -eq 1 ] || { echo "usage: qualify-release.sh <external-output-directory>" >&2; exit 2; }
cd "$(dirname "${BASH_SOURCE[0]}")/.."
repository=$(git rev-parse --show-toplevel)
output=$(realpath -m "$1")
case "$output/" in "$repository/"*) echo 'Release output must be outside the checkout' >&2; exit 2 ;; esac
mkdir -p "$output"
[ -z "$(find "$output" -mindepth 1 -maxdepth 1 -print -quit)" ] || { echo 'Release output must be empty' >&2; exit 2; }

export GOTOOLCHAIN=local
pin=$(tr -d '\n' < .go-version)
# mise resolves ambient go in this directory from go.mod (the module minimum);
# pin the build toolchain and the cached vulnerability scanner explicitly so
# every step, including the goreleaser post-build hook, sees the same tools.
scanner="$HOME/.cache/terrascale/govulncheck-v1.9.0-go$pin"
export GOROOT="$(mise where go@"$pin")"
export PATH="$GOROOT/bin:$scanner:$PATH"
test "$(go env GOVERSION)" = "go$pin"
latest=$(git tag --list 'sdk/go/v0.0.*' --merged HEAD --sort=-v:refname | head -n 1)
case "$latest" in
  sdk/go/v0.0.*) version="0.0.$((${latest##*.} + 1))" ;;
  *) echo 'No sdk/go/v0.0.X tag found in this history' >&2; exit 1 ;;
esac
export TS_GO_VERSION="$version"
test "$(go env GOVERSION)" = "go$pin"
workspace=$(mktemp -d)
trap 'rm -rf "$workspace"' EXIT
export GITHUB_PATH="$workspace/github.path"
: > "$GITHUB_PATH"
bash scripts/audit-dependencies.sh
test -z "$(gofmt -l .)"
go vet ./...
go test -race -count=1 ./...
# Invoke resolved tools directly: mise exec can prepend the system profile's Go
# ahead of the pinned PATH on dev hosts.
consumer_go_root=$(mise where go@1.25.0)
GOROOT="$consumer_go_root" PATH="$consumer_go_root/bin:$PATH" bash scripts/check-consumer.sh
"$(mise where goreleaser@2.19.0)/goreleaser" release --snapshot --clean
python3 scripts/test-release.py
cp dist/*.tar.gz dist/*.zip dist/checksums.txt dist/homebrew/Formula/ts.rb scripts/install.sh "$output/"
printf '%s\n' "$version" > "$output/version.txt"
