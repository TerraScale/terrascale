# TerraBaseV2

TerraBaseV2 is the planned Rust implementation of the TerraBase engine. This
repository currently contains the buildable project foundation, parity
inventory schema, and evidence contracts. It does not contain database
behavior, V1 parity, a migration tool, a multi-region implementation, or a
production release.

The workspace goal and acceptance criteria live at
`../specs/2026-08-23-terrabase-v2-goal.md`. The glossary is in `CONTEXT.md`.
Contract facts live in the domain-owned fragments under `parity/contracts/`.
Mutable V2 status and Proof IDs live in the disjoint state overlays under
`parity/overlays/`. The canonical `parity/inventory.json` is generated output.
Workers update fragments or overlays, then the generator owns the inventory
write.

## Local checks

The pinned Rust toolchain is in `rust-toolchain.toml`. Run the complete local
foundation checks with:

```bash
./scripts/check.sh
```

The individual checks are useful while editing:

```bash
cargo fmt --all -- --check
cargo check --workspace --locked
cargo test --workspace --locked --release
cargo clippy --workspace --all-targets --all-features --locked -- -D warnings
cargo doc --workspace --no-deps --locked
python3 scripts/validate_parity.py --check
python3 scripts/test_parity_tooling.py
python3 scripts/check_license.py
./scripts/check_secrets.sh
```

Optional dependency and vulnerability checks use `cargo-deny` when it is
installed. The CI workflow records the check as unavailable when it is not.
No global tools or credentials are required for the foundation checks.

The parity command applies the repository's supported Draft 2020-12 schema
rules, checks pinned V1 source identity, duplicate IDs, overlay references and
status transitions, and compares generated output byte-for-byte. If the
optional Python `jsonschema` package is unavailable, direct Draft 2020-12
validation is reported as unrun; the local checks still require the supported
rules. Regenerate the canonical file only with:

```bash
python3 scripts/validate_parity.py --generate
```

## Development status

The parity inventory deliberately marks seeded V1 capability families as
`unresolved`. A row becomes `implemented` only after the V1 contract is
evidenced and the matching V2 tests and Proof IDs exist. The benchmark lane
must compare a reviewed V2 source identity with a comparable TerraBase.Benchmark
baseline before anyone can claim that V2 is faster.

The repository is private and its distribution license is pending. The
`LicenseRef-TerraScale-Pending` metadata is a release gate, not runtime
licensing. Do not publish artifacts until that gate is resolved.
