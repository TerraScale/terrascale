#!/usr/bin/env bash
# Canonicalize the non-semantic protoc version line in all TBEP Go bindings.
# The stdlib Go helper uses directory descriptors and O_NOFOLLOW, so a symlink
# swap in an ancestor cannot redirect reads or renames after preflight.
set -euo pipefail

if [[ $# -ne 1 ]]; then
  printf 'usage: %s BINDING_ROOT\n' "$0" >&2
  exit 64
fi

GO111MODULE=off go run "$(dirname "$0")/../terrabase-go/cmd/normalize-generated-protoc-version/main.go" "$1"
