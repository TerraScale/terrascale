#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
# Resolve real executables: a rustup proxy's path does not identify its selected compiler.
export RUSTC="$(rustup which --toolchain 1.98.1 rustc)"
export RUSTDOC="$(rustup which --toolchain 1.98.1 rustdoc)"
# Artifacts from another toolchain must never enter this SDK's Fast build graph.
export CARGO_TARGET_DIR="$PWD/target/1.98.1"
rustup run 1.98.1 cargo test --locked
