#!/usr/bin/env bash
set -Eeuo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sdk_dir="$root/node_modules/.cache/ts-typescript"
sdk_commit="d1021878d0d0dfeb5639ea3dd0f1ed7f0a9eee19"
sdk_remote="${TERRASCALE_TYPESCRIPT_SDK_REPOSITORY:-ssh://git@git.terrascale.tech/TerraScale/ts-typescript.git}"

if [[ -n "${TERRASCALE_TYPESCRIPT_SDK_ENTRY:-}" ]]; then
  [[ -f "$TERRASCALE_TYPESCRIPT_SDK_ENTRY" ]] || {
    echo "TypeScript SDK entry does not exist: $TERRASCALE_TYPESCRIPT_SDK_ENTRY" >&2
    exit 1
  }
  exit 0
fi

if [[ ! -d "$sdk_dir/.git" ]] ||
  [[ "$(git -C "$sdk_dir" rev-parse HEAD)" != "$sdk_commit" ]]; then
  rm -rf -- "$sdk_dir"
  mkdir -p "$sdk_dir"
  git -C "$sdk_dir" init --quiet
  git -C "$sdk_dir" remote add origin "$sdk_remote"
  git -C "$sdk_dir" fetch --quiet --depth=1 origin "$sdk_commit"
  git -C "$sdk_dir" checkout --quiet --detach FETCH_HEAD
fi

if [[ -f "$sdk_dir/dist/index.js" ]] &&
  [[ -f "$sdk_dir/.dashboard-build-complete" ]]; then
  exit 0
fi

npm --prefix "$sdk_dir" ci --no-audit --no-fund
# The pinned upstream build includes an integration-only test that imports its
# own unpublished package export before dist exists. Omit that test from this
# dashboard runtime build; the public SDK modules still compile unchanged.
cat > "$sdk_dir/tsconfig.dashboard-e2e.json" <<'EOF'
{
  "extends": "./tsconfig.build.json",
  "exclude": ["dist", "node_modules", "src/integration/auth-authority.test.ts"]
}
EOF
"$sdk_dir/node_modules/.bin/tsc" -p "$sdk_dir/tsconfig.dashboard-e2e.json"
touch "$sdk_dir/.dashboard-build-complete"
