#!/usr/bin/env bash
set -euo pipefail

# The CI/release runners are Linux amd64; cache bytes must match the pinned build.
expected="go$(tr -d '\n' < .go-version)"
test "$(go env GOVERSION)" = "$expected"
test "$(go env GOOS GOARCH)" = $'linux\namd64'
scanner_dir="$HOME/.cache/terrascale/govulncheck-v1.9.0-$expected"
mkdir -p "$scanner_dir"
if [[ ! -f "$scanner_dir/govulncheck" ]]; then
  GOBIN="$scanner_dir" CGO_ENABLED=0 GOOS=linux GOARCH=amd64 GOAMD64=v1 GOFLAGS='' GOWORK=off GOTOOLCHAIN=local \
    go install -trimpath golang.org/x/vuln/cmd/govulncheck@v1.9.0
fi
echo "46db5cf19985d26fb41dbd8e429b29269c27d72b6e2bf181168d223f3732e499  $scanner_dir/govulncheck" | sha256sum --check
chmod +x "$scanner_dir/govulncheck"
# Scanner dependencies and its embedded compiler are checked against today's advisories too.
"$scanner_dir/govulncheck" -mode=binary -scan=module -show=verbose "$scanner_dir/govulncheck"
echo "$scanner_dir" >> "$GITHUB_PATH"
