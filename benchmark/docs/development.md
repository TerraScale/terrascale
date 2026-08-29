# Development contract

The workspace [GOAL.md](../../GOAL.md) is the product and architecture
authority. Repository ADRs record decisions that are hard to reverse. Source,
tests, and receipts describe implementation status.

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
