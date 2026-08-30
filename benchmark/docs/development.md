# Development contract

The workspace [GOAL.md](../../GOAL.md) is the product and architecture
authority. This repository's [GOAL.md](../GOAL.md) is the execution plan and
current evidence index; the [ADR index](adr/README.md) links its durable
decisions. Repository ADRs record decisions that are hard to reverse; a
`Proposed` ADR is not an accepted decision. Source, tests, and receipts
describe implementation status and must retain their honest limits.

The repository is pre-production. A focused green test or a deterministic
loopback receipt does not qualify OCI placement, WAN behavior, production
latency, compliance, or General Availability.

Do not use RavenDB or TerraBase V1 contracts, citations, parity rows, import
formats, or behavior as V2 acceptance criteria. Native schema migrations,
Partition movement, snapshots, backup, and restore remain part of V2.

Run focused checks while editing:

```bash
cargo fmt --all -- --check
cargo check --workspace --locked
cargo test --workspace --locked
cargo clippy --workspace --all-targets --all-features --locked -- -D warnings
cargo doc --workspace --no-deps --locked
python3 scripts/check_license.py
./scripts/check_secrets.sh
```

CI owns the full `scripts/check.sh` gate. Storage, durability, replication,
compaction, backup, and recovery changes also require the exclusive recovery
lane and a secret-safe Recovery Receipt. Performance claims require a valid
TerraBase.Benchmark receipt.

Protocol or generated-binding changes must run the schema-owner regeneration
and drift checks, plus the shared valid/error and hostile-input vectors. Keep
the schema and generated artifacts in their owning lane; do not hand-edit
generated output. Changes to a release invariant require a receipt naming the
source commit, exact command, environment, result, and scope limits.

Before describing a lane as production-ready, update
[the qualification matrix](evidence/qualification-gap-sweep.md), link the
new receipt, and recheck its status against the current checkout. Do not
silently upgrade `partial`, `missing`, or `blocked-external` evidence.
