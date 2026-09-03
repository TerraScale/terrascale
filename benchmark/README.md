# TerraBase

TerraBase is TerraScale's multi-Region database engine and public API, written
in Rust (D-34): one member process serves `/api/v1` over axum and hosts the
engine; the private TBEP gRPC surface is control-only (placement, health,
drain, faults).

This checkout is pre-production. Local tests establish implementation facts,
not OCI qualification. `ts-orchestrator` owns native x86_64 and ARM64 runs,
GRU/SFO/FRA topology tests, failure injection, the thirty-day soak, and
promotion of the exact candidate. [GOAL.md](GOAL.md) records the exit gates and
current blockers.

## Runtime boundary

```text
customer SDK / dashboard
        -> public REST + reactive API over HTTPS (axum, D-34/D-44)
        -> TerraBase engine (one SQLite file per database, D-27)
orchestrator / CLI (control only)
        -> private protobuf/gRPC TBEP over HTTP/2 and production mTLS
```

The public surface is the REST API (with the reactive WebSocket transport
landing as S-04); TBEP carries no customer request path (D-02).

The engine owns storage, WAL, compaction, indexing, TQL query execution,
causality, CRDT merge, replication, repair, membership acceptance, backup,
restore, deletion, reactive state, and authoritative usage facts. A durable
engine opens one filesystem location through `Engine::open` and
`EngineConfig::durable`.

The production engine must run asynchronously across multiple threads and use
all CPUs admitted to the Member. Independent storage, network, replication,
and maintenance I/O must not block executor threads. Query execution,
indexing, compaction, repair, encoding, and other CPU-heavy work must use
bounded parallelism instead of serial hot paths or unbounded task spawning.
Runtime limits must account for available CPUs, memory, storage, and network
capacity while preserving deterministic results, durability, tenant
isolation, fairness, and backpressure.

Local commit acknowledgements mean:

```text
Buffered -> Written -> Synced
```

These states describe local storage progress. They do not imply Region-loss
durability. `GlobalDurable` must fail closed unless at least two active Regions
participate.

## Repository map

- `src/` contains the Rust engine.
- `crates/terrabase-tbep/` owns the protobuf schema, gRPC server, private host
  boundary, vectors, and transport qualification harness.
- `crates/terrabase-server/` is the production daemon composition. It is a
  release deliverable and remains unready until every configured security and
  runtime bound is enforced and the complete clustered host replaces the
  standalone engine host.
- `bench/` is the benchmark driver. Markdown history under
  `docs/evidence/benchmarks/` is the repository's comparison authority.
- `deploy/observability/` contains the private Prometheus template, alert
  rules, and canonical Grafana dashboard.
- `docs/adr/` contains accepted architecture decisions.
- `docs/evidence/` separates local lane facts from production qualification.

## Development gates

The root workspace:

```sh
cargo fmt --all -- --check
cargo check --workspace --locked
cargo test --workspace --locked
cargo clippy --workspace --all-targets --all-features --locked -- -D warnings
cargo doc --workspace --no-deps --locked
cargo deny --all-features --locked check
```

Standalone deliverables:

```sh
cargo test --manifest-path bench/Cargo.toml --locked
```

CI also checks protobuf generation drift, hostile input handling, architecture
rules, dashboard queries, benchmark-history drift, licenses, and advisories.
See [docs/development.md](docs/development.md) before changing code or evidence.
Performance-sensitive changes must also prove multicore scaling and report CPU
utilization, executor delay, queue depth, throughput, latency, and resource
use under the decision-grade policy in ADR 0026.

## Production evidence

The initial topology uses `GRU`, `SFO`, and `FRA`, with two active Members per
selected Region and Partition. Production TCP requires TLS 1.3 and mutual
workload authentication. Prometheus and Grafana remain private. Production
encryption uses OCI Vault and the key lifecycle in ADR 0030; local generated
keys are development-only and can never satisfy a production gate.

Do not turn a loopback test, synthetic benchmark, skipped live test, or local
fixture into a production claim. Candidate evidence must name the source,
artifact, configuration, topology, command, result, and known limits.

TerraBase is licensed under the [MIT License](LICENSE.md).
