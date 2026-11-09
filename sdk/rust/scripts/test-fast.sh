#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
. scripts/toolchain.sh
# Resolve real executables: a rustup proxy's path does not identify its selected compiler.
toolchain_use
# Artifacts from another toolchain must never enter this SDK's Fast build graph.
export CARGO_TARGET_DIR="$PWD/target/1.99.1"
cargo test --locked --no-run
python3 ../../scripts/ci/fast-budget.py sdk-rust --format rust -- cargo test --locked
