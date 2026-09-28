#!/usr/bin/env bash
# Check out the ts-rust SDK revision pinned in TS_RUST_REV beside this
# repository, where the workspace's `path = "../ts-rust"` dependency expects
# it. The SDK carries its own rust-toolchain.toml, so RUSTUP_TOOLCHAIN (written
# to GITHUB_ENV for later steps) pins every rustup proxy, including rustc runs
# inside ../ts-rust, to this repository's toolchain.
set -euo pipefail

test "$#" -eq 0 || { echo "usage: $0" >&2; exit 2; }
[[ ${TS_RUST_REV:-} =~ ^[0-9a-f]{40}$ ]] || { echo "TS_RUST_REV must be a full commit SHA" >&2; exit 2; }
: "${ORG_READ_TOKEN:?org read token is required}"
: "${GITHUB_ENV:?GITHUB_ENV is required}"

repo=$(cd "$(dirname "$0")/../.." && pwd)
sdk="$repo/../ts-rust"

git init -q "$sdk"
git -C "$sdk" fetch -q --depth 1 \
  "https://oauth2:${ORG_READ_TOKEN}@git.terrascale.tech/TerraScale/ts-rust.git" "$TS_RUST_REV"
git -C "$sdk" checkout -q --detach FETCH_HEAD
test "$(git -C "$sdk" rev-parse HEAD)" = "$TS_RUST_REV"

toolchain=$(sed -nE 's/^channel = "([^"]+)"$/\1/p' "$repo/rust-toolchain.toml")
echo "RUSTUP_TOOLCHAIN=${toolchain:?rust-toolchain.toml has no channel}" >> "$GITHUB_ENV"
