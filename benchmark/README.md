# TerraBaseV2

TerraBaseV2 is the planned Rust implementation of the TerraBase engine. This
repository currently contains the buildable project foundation, parity
inventory schema, and evidence contracts. It does not contain database
behavior, V1 parity, a migration tool, a multi-region implementation, or a
production release.

The workspace goal and acceptance criteria live at
`../specs/GOAL.md`. The glossary is in `CONTEXT.md`.
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
cargo test --workspace --locked
cargo test --workspace --locked --release
cargo clippy --workspace --all-targets --all-features --locked -- -D warnings
cargo doc --workspace --no-deps --locked
python3 scripts/validate_parity.py --check
python3 scripts/test_parity_tooling.py
python3 scripts/test_v1_citations.py
python3 scripts/check_license.py
./scripts/check_secrets.sh
```

The V1 contract audit is a separate qualification input because it needs an
exact TerraBase V1 checkout. Set `TERRABASE_V1_ROOT` and run
`./scripts/check.sh` to execute the strict gate. Without that checkout the
gate reports `UNRUN` and fails. W2 contract review can use the non-release
audit command below, which records unresolved test obligations in
`parity/v1-test-gaps.json`:

```bash
python3 scripts/validate_v1_citations.py \
  --mode contract \
  --v1-root /path/to/TerraBase \
  --workspace-root /path/to/TerraScale \
  --gap-report parity/v1-test-gaps.json
```

The current extraction has 258 rows, 251 V1-backed rows, 267 source
citations, and 346 exact test citations. One row (PB-5012) keeps a test gap
adjudicated by parent decision PD-2026-08-24-02 with an open V2-side proof
obligation; the adjudication input is `parity/adjudications.json`. Those
numbers are accounting evidence, not a V2 parity claim.

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
