#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# The repository pins its toolchain in rust-toolchain.toml. An ambient
# RUSTUP_TOOLCHAIN (this host's default is "stable") would silently
# override that pin and gate on the wrong toolchain — surfaced 2026-08-26
# when the GAP-F07 warden found pinned-only clippy findings the ambient
# override masked. Unset it so every cargo step below honors the pin.
unset RUSTUP_TOOLCHAIN

cargo fmt --all -- --check
cargo check --workspace --locked
# Dev-profile tests come first: `debug_assert!` contracts (frame-length
# and budget invariants) compile out under --release, so a release-only
# test run gates nothing about them. Surfaced by the SE-07 warden repair,
# where an off-by-one frame-header assertion was green in every release
# gate and deterministically red in dev.
cargo test --workspace --locked
cargo test --workspace --locked --release
cargo clippy --workspace --all-targets --all-features --locked -- -D warnings
cargo doc --workspace --no-deps --locked
python3 scripts/validate_parity.py --check
python3 scripts/test_parity_tooling.py
python3 scripts/test_v1_citations.py
python3 scripts/test_secret_scan.py
python3 scripts/check_license.py
# SE-15's standing benchmark-receipt discipline: every receipt under
# evidence/benchmarks/ carries the canonical objective-status line, and a
# `not met` status names its regression owner. Wired by the integrator
# (the SE-15 packet's write scope stopped at the checker itself); the
# guard only bridges the window before SE-15's scripts land on develop —
# once they exist here the check is mandatory and the miss fails loudly.
if [[ -f scripts/check_benchmark_receipts.py ]]; then
  python3 scripts/check_benchmark_receipts.py
  python3 scripts/test_benchmark_receipt_discipline.py
else
  echo "benchmark-receipt discipline UNRUN: SE-15 checkers not yet on this branch"
fi
./scripts/check_secrets.sh

v1_root="${TERRABASE_V1_ROOT:-}"
if [[ -z "$v1_root" ]]; then
  for candidate in "$repo_root/../TerraBase" "$repo_root/../../TerraBase"; do
    if [[ -d "$candidate" ]]; then
      v1_root="$candidate"
      break
    fi
  done
fi
if [[ -z "$v1_root" || ! -d "$v1_root" ]]; then
  echo "V1 citation audit UNRUN: set TERRABASE_V1_ROOT to the pinned TerraBase checkout" >&2
  exit 1
fi
workspace_root="${TERRASCALE_WORKSPACE_ROOT:-}"
if [[ -z "$workspace_root" ]]; then
  for candidate in "$repo_root/.." "$repo_root/../.."; do
    if [[ -d "$candidate/docs/specs" && -d "$candidate/TerraBase" ]]; then
      workspace_root="$candidate"
      break
    fi
  done
fi
if [[ -z "$workspace_root" ]]; then
  echo "V1 citation audit UNRUN: unable to resolve the TerraScale workspace root" >&2
  exit 1
fi
python3 scripts/validate_v1_citations.py \
  --mode strict \
  --v1-root "$v1_root" \
  --workspace-root "$workspace_root" \
  --gap-report "$repo_root/parity/v1-test-gaps.json"

if command -v cargo-deny >/dev/null 2>&1; then
  cargo deny check
else
  echo "cargo-deny unavailable: dependency/license/advisory check recorded as unrun"
fi
echo "TerraBaseV2 foundation checks passed"
