#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
python3 prepare.py
LAB_TOOLCHAIN="$HOME/.rustup/toolchains/1.99.1-x86_64-unknown-linux-gnu/bin"
export RUSTC="$LAB_TOOLCHAIN/rustc"
[[ "$("$RUSTC" --version)" == "rustc 1.99.1 "* ]]
export PATH="$LAB_TOOLCHAIN:$PATH"
export CARGO_BUILD_JOBS=6 CARGO_INCREMENTAL=0 RUSTFLAGS='--cfg=tokio_unstable'
"$LAB_TOOLCHAIN/cargo" build --manifest-path turso/Cargo.toml --profile lib-release --locked -p ts_sync_host
npm ci --no-audit --no-fund
sha256sum turso/target/lib-release/libts_sync_host.so
