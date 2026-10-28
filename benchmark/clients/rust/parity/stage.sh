#!/usr/bin/env bash
# Stages the parity runner into a new clean ts-rust v0.0.5 checkout and builds it with that
# commit's own Cargo.lock and dependencies. Needs a rustup cargo (rust-toolchain.toml: 1.98.1).
set -euo pipefail
rev=38a33829dd7099f22783ba79d8b187450328a59b
here=$(cd "$(dirname "$0")" && pwd)
dir=${1:?usage: stage.sh NEW-DIR}
[ ! -e "$dir" ] || { echo "stage.sh: $dir already exists" >&2; exit 2; }

git clone -q --no-checkout https://github.com/TerraScale/ts-rust.git "$dir"
git -C "$dir" checkout -q --detach "$rev"
if [ "$(git -C "$dir" rev-parse HEAD)" != "$rev" ] || [ -n "$(git -C "$dir" status --porcelain)" ]; then
  echo "stage.sh: $dir is not a clean checkout of $rev" >&2
  exit 1
fi
mkdir "$dir/examples/api_parity"
cp "$here"/*.rs "$dir/examples/api_parity/"
cd "$dir"
export TS_PARITY_SDK_REV=$rev
# The SDK's committed toolchain pin wins over any inherited override.
RUSTUP_TOOLCHAIN=$(sed -n 's/^channel = "\(.*\)"$/\1/p' rust-toolchain.toml)
export RUSTUP_TOOLCHAIN
cargo --version
rustc --version
rustfmt --edition 2021 --check examples/api_parity/main.rs
cargo clippy --locked --example api_parity -- -D warnings
cargo clippy --locked --example api_parity --tests -- -D warnings
cargo test --locked --example api_parity
cargo build --locked --release --example api_parity
# Only the staged example may differ from the pinned commit.
[ "$(git status --porcelain)" = "?? examples/api_parity/" ] || { git status --porcelain >&2; exit 1; }
sha256sum Cargo.lock examples/api_parity/*.rs target/release/examples/api_parity
"$here/cli-control.sh" target/release/examples/api_parity
