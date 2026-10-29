#!/usr/bin/env bash
set -euo pipefail

# Compile all public packages from an isolated consumer without service access.
go version
repo=$(pwd)
consumer=$(mktemp -d)
trap 'rm -rf "$consumer"' EXIT
cd "$consumer"
cat > go.mod <<'MOD'
module consumer.example/smoke

go 1.25.0

require terrascale.tech/sdk v0.0.0
MOD
go mod edit -replace "terrascale.tech/sdk=$repo"
cat > main.go <<'GO'
package main

import (
    _ "terrascale.tech/sdk"
    _ "terrascale.tech/sdk/tsauth"
    _ "terrascale.tech/sdk/v1"
)

func main() {}
GO
go build -mod=mod ./...
