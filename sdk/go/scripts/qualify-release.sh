#!/usr/bin/env bash
set -euo pipefail

[ "$#" -eq 1 ] || { echo "usage: qualify-release.sh <external-output-directory>" >&2; exit 2; }
cd "$(dirname "${BASH_SOURCE[0]}")/.."
repository=$(git rev-parse --show-toplevel)
output=$(realpath -m "$1")
case "$output/" in "$repository/"*) echo 'Release output must be outside the checkout' >&2; exit 2 ;; esac
mkdir -p "$output"
[ -z "$(find "$output" -mindepth 1 -maxdepth 1 -print -quit)" ] || { echo 'Release output must be empty' >&2; exit 2; }

export GOTOOLCHAIN=local TS_GO_VERSION=0.0.11
test "$(go env GOVERSION)" = "go$(tr -d '\n' < .go-version)"
workspace=$(mktemp -d)
trap 'rm -rf "$workspace"' EXIT
export GITHUB_PATH="$workspace/github.path"
bash scripts/audit-dependencies.sh
test -z "$(gofmt -l .)"
go vet ./...
go test -race -count=1 ./...
mise exec go@1.25.0 -- bash scripts/check-consumer.sh
mise exec goreleaser@2.19.0 -- goreleaser release --snapshot --clean
python3 scripts/test-release.py
cp dist/*.tar.gz dist/*.zip dist/checksums.txt dist/homebrew/Formula/ts.rb scripts/install.sh "$output/"
