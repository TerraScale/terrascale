#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

cargo fmt --all -- --check
cargo check --workspace --locked
cargo test --workspace --locked --release
cargo clippy --workspace --all-targets --all-features --locked -- -D warnings
cargo doc --workspace --no-deps --locked
python3 scripts/validate_parity.py --check
python3 scripts/test_parity_tooling.py
python3 scripts/test_v1_citations.py
python3 scripts/test_secret_scan.py
python3 scripts/check_license.py
./scripts/check_secrets.sh

if command -v cargo-deny >/dev/null 2>&1; then
  cargo deny check
else
  echo "cargo-deny unavailable: dependency/license/advisory check recorded as unrun"
fi
echo "TerraBaseV2 foundation checks passed"
