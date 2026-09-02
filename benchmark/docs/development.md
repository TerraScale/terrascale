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
cargo check --manifest-path bench/Cargo.toml --locked
cargo deny check
```



CI owns the full gate (`.forgejo/workflows/ci.yml`). Storage, durability,
replication, compaction, backup, and recovery changes also require the
exclusive recovery lane and a secret-safe Recovery Receipt. Performance
claims require a lane receipt under `/benchmarks/` produced by
the engine-side adapter (`src/benchmarks/`) with both pinned references run.

Production paths must support asynchronous I/O and bounded multithreaded CPU
execution. Do not perform blocking filesystem, network, or process work on an
async executor thread. Put CPU-heavy work on a bounded pool and derive worker,
queue, batching, and memory limits from the resources admitted to the Member.
Avoid global locks, serial coordinators, busy waiting, unbounded task creation,
and fixed worker counts that prevent useful multicore scaling.

A performance-sensitive change must measure more than elapsed time. Its lane
receipt must report admitted CPUs, worker configuration, per-core utilization,
executor delay, queue depth, throughput, latency, memory, storage I/O, network
I/O, and any saturation or throttling. Run at several core counts on the same
native architecture and resource class. A change cannot claim hardware
efficiency when throughput does not scale for a parallel workload, cores sit
idle behind a serial bottleneck, or oversubscription breaks latency,
backpressure, fairness, determinism, durability, isolation, or recovery.

Protocol or generated-binding changes must run the schema-owner regeneration
and drift checks, plus the shared valid/error and hostile-input vectors. Keep
the schema and generated artifacts in their owning lane; do not hand-edit
generated output. Changes to a release invariant require a receipt naming the
source commit, exact command, environment, result, and scope limits.

Before describing a lane as production-ready, update
[the qualification matrix](evidence/qualification-gap-sweep.md), link the
new receipt, and recheck its status against the current checkout. Do not
silently upgrade `partial`, `missing`, or `blocked-external` evidence.
