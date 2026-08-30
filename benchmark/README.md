# TerraBase

TerraBase is the native Rust engine and protocol workspace for the
TerraScale database product. It is under active development and is not a
production or General Availability release. The repository contains meaningful
correctness evidence, but its deterministic and loopback lanes do not qualify
OCI placement, WAN behavior, production SLOs, or a thirty-day soak.

The release plan, evidence limits, and open work are in
[GOAL.md](GOAL.md). The 40-scenario qualification matrix is
[docs/evidence/qualification-gap-sweep.md](docs/evidence/qualification-gap-sweep.md).
Accepted architecture decisions are indexed in [docs/adr/README.md](docs/adr/README.md); a decision with
`Proposed` status is not an implementation or release authorization.

## Repository shape

The root package and Rust library target are `terrabase` (`src/lib.rs`); the
rename cutover completed 2026-08-30 and the previous dependency-alias window
is closed. Its public engine modules
cover authorization, backup and restore, deletion, documents
and transactions, families, indexes, queries/TQL, regions, relationships,
replication, sessions, sharding, storage, streaming, tiers, usage, and the
platform/protocol adapter seams. The Cargo workspace also contains the native
causal, CRDT, sync, conformance, member-transport, cluster,
replication-core, and TBEP crates.

The durable engine path is `Engine::open` with an `EngineConfig::durable`
location. Local commit acknowledgement is explicit:

```text
Buffered -> Written -> Synced
```

The names describe local storage progress; they do not promise replication,
backup, or production durability. WAL/LSM, compaction, snapshots, backup,
restore, replication, and recovery code each retain their own limits and
receipts.

The intended product boundary is:

```text
public clients / SDKs
        -> TerraScale.Database service in TerraScale.Internals (product gateway and authorization boundary)
        -> private TBEP
        -> TerraBase engine and durable storage
```

The repository-local `host/`, `cli/`, and `studio/` trees are migration-source
and development/conformance lanes. Their documentation is explicit about
their current limitations; the trusted-header local host and direct TBEP mode
must not become a public customer boundary. `TerraScale.Internals` permanently
owns the Database service source. The standalone `TerraScale.Database`
repository is transitional and may be retired only after provenance, generated
binding parity, supported-operation cutover, and rollback evidence are accepted.
The accepted runtime ownership decision is recorded in [ADR 0034](docs/adr/0034-sole-production-gateway-and-host-retirement.md).

## Protocols and trust boundaries

Private TBEP is owned by `crates/terrabase-tbep` and its registry, vectors,
server, and transport tests. The public reactive sync contract is documented
under `docs/sync/` and is separate from private TBEP. Protocol generation,
hostile-input coverage, cross-language conformance, and migration evidence are
release work even where a schema or focused test already exists. See ADR 0025,
ADR 0024, and the protocol rows in the qualification matrix.

The Rust library denies unsafe code and validates bounded identifiers, payloads,
versions, generations, authorization evidence, and durability outcomes. The
repository-local host currently uses a trusted local context gate rather than
customer authentication. Production authentication, grants, key management,
and OCI placement belong to the product gateway and control-plane owners; no
local test is evidence that those production controls are complete.

## Current evidence posture

The latest matrix refresh is tied to the dated `develop` audit baseline
`4075908` (2026-08-29), not a promise about a later branch tip, and
distinguishes lane-scoped correctness from production qualification. It records
lane evidence for native storage, replication, recovery, sync, region parsing,
model checking, and deletion-domain invariants, while retaining explicit
`partial`, `missing`, and `blocked-external` statuses for work that is not
implemented or cannot be proven on this host. In particular, no benchmark
receipt in this repository is a production latency or throughput claim.

The dated audit baseline also carries the implementation work for the private
protocol schema; generated artifacts remain governed by their schema owner and
must pass regeneration, drift, hostile-decode, and cross-language checks before
the protocol can be called production-ready.

## Development

Read [docs/development.md](docs/development.md) before changing code or
evidence. The normal local checks are:

```bash
cargo fmt --all -- --check
cargo check --workspace --locked
cargo test --workspace --locked
cargo clippy --workspace --all-targets --all-features --locked -- -D warnings
cargo doc --workspace --no-deps --locked
cargo check --manifest-path bench/Cargo.toml --locked
cargo deny check
```

CI owns the complete gate (`.forgejo/workflows/ci.yml`). Recovery, replication,
compaction, backup, and performance work has additional lane and receipt
requirements described by the relevant ADR and evidence document. Do not
rewrite a receipt from memory: record the exact command, commit, environment,
result, and honest limits.

## Documentation map

- [GOAL.md](GOAL.md) — repository production plan, dependencies, decisions,
  acceptance evidence, risks, and exit gates.
- [docs/adr/README.md](docs/adr/README.md) — durable architecture decisions; `Proposed` remains
  proposed until its owner accepts it.
- [docs/evidence](docs/evidence/) — lane receipts and qualification limits.
- [docs/development.md](docs/development.md) — contributor and validation
  contract.
- [host/README.md](host/README.md), [cli/README.md](cli/README.md), and
  [studio/README.md](studio/README.md) — migration-source and UI lane
  contracts, not a production authorization to expose those surfaces.
