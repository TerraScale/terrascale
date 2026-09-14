# TerraBase C# benchmark baseline

Status: **partial local receipts; distributed qualification remains pending**.

The latest correctness snapshot is dated **2026-09-14** at revision
`409cd9765a22382a90be63e3f6e417601e36821e` (`main`). It is not itself a
benchmark receipt. Every performance receipt must record its exact revision, dirty state,
and artifact digest. Runtime target: .NET 10 with SDK 10.0.401, C# 14 only.

The default host requires `TERRABASE_DATA_PATH` and uses the durable
`StorageEngine`; `ZONETREE` is an explicit alternative backend. Component rows
below must still keep local-provider, host, and TerraBase-to-TerraBase network
boundaries explicit.

Historical Rust and SQLite measurements belong to earlier source states and
are excluded. The current private inter-node surface uses keyed MessagePack,
but those contract tests and the JSON/SSE/WSS sync client are not throughput
receipts.

Research labels do not change benchmark identity: AIM is an ICDE 2023 paper
with an arXiv posting dated 2026-05-29; RIB is a SIGMOD 2026 paper and is not
implemented (the current scorer uses an internal variance proxy); Bf-Tree is a
PVLDB 17 (2024) index that is absent from this source tree; DBSP is a PVLDB 16
(2023) paper; and Enzyme is a SIGMOD 2026 paper (arXiv v1 2026-03-29, v2
2026-04-30). Their experimental or production results are not TerraBase
receipts.

## Admission rules

A row becomes `complete` only when its receipt records all of the following:

| Field | Required value |
| --- | --- |
| Source identity | Git commit, clean/dirty state, and source tree or release artifact SHA-256. |
| Runtime | .NET SDK/runtime, OS, architecture, GC mode, and build configuration. |
| Subject | Provider/component name and exact operation boundary. |
| Host envelope | CPU model/count or cgroup limit, memory limit, storage medium, and relevant I/O pressure. |
| Workload | Seed, dataset size, payload size, operation count, concurrency, warm-up, and workload digest. |
| Durability | In-memory, JSON snapshot, synced WAL/segment, object store, or another explicit mode. |
| Samples | At least five measured samples after warm-up, with p50, p95, and p99 per sample. |
| Receipt | Retained machine-readable result path plus command and exit status. |

Percentiles are reported per sample and summarized as medians; samples are not
pooled into a synthetic percentile. A missing measurement is `—`, never zero.
An error or incompatible setup is `failed` or `invalid` with its reason and
receipt path. It never becomes a performance claim.

Use the repository's lowest available CPU priority for build and benchmark
commands, for example `nice -n 19 dotnet ...`. The solution checks in
[`docs/development.md`](../docs/development.md) are correctness checks, not a
throughput measurement. A benchmark must name any additional runner command.

## Workload definitions

Each row names the smallest operation boundary that a future runner must time.
Where a component is only a primitive, the row measures that primitive and
must not be labeled as end-to-end database performance.

| Workload | Subject | Operation boundary | Required setup and split |
| --- | --- | --- | --- |
| Point read | `InMemoryStorageProvider`, `FileStorageProvider`, `StorageEngine` | One `ReadAsync` for an existing key | Warm provider; separate memory, JSON snapshot, and synced WAL/segment modes. |
| Conditional upsert | Same providers | One `UpsertAsync` with `DocumentPrecondition.Version` | Fixed JSON body; report successful and conflict outcomes separately. |
| Delete/recreate | `FileStorageProvider`, `StorageEngine` | One delete followed by one missing-precondition upsert | Report version-watermark behavior and each operation latency. |
| Collection scan | Same providers | One complete `ScanAsync` for one tenant/collection | Fixed row count and payload size; record returned bytes and cancellation policy. |
| Atomic batch | `StorageEngine` | One `ApplyBatchAsync` containing N upserts/deletes | Record N, one-WAL-frame mode, conflict rate, sequence range, and post-reopen correctness. |
| WAL reopen | `StorageEngine` / `FileDurableLog` | Close, reopen, and read the committed key set | Fixed WAL bytes and record count; report replay time separately from write latency. |
| Compaction | `StorageEngine` / `LocalStorageSegmentStore` | One `CompactAsync` after a fixed mutation history | Record live records, tombstones, WAL bytes, segment bytes, tier, and checksum verification. |
| Transaction commit | `TransactionCoordinator` over `StorageEngine` | Stage reads/mutations and one `CommitAsync` | Split `Snapshot`, `Serializable` success, and serializable read conflict. |
| Index definition | `IndexDefinition` | Construct and validate one static index definition | Split equality/range/composite/covering/partial/full-text definitions. This is metadata cost. |
| Auto-index analysis | `AutomaticWorkloadAnalyzer` / candidate scorer | Analyze a bounded observation window and derive/score candidates | Fixed query-shape count, observation capacity, field sketch, and candidate count. |
| Auto-index lifecycle | `AutomaticIndexCoordinator` | Select, invisible build, backfill, shadow verify, activate, rollback, retire | Use a recorded backend implementation; fake-backend tests are correctness evidence only. |
| ZoneTree point lookup | `TerraBase.ZoneTree` | One point read after open/warm-up | Record tree options, key/value sizes, segment count, and persistence mode. |
| Full-text lookup | `TerraBase.ZoneTree` (public namespace `ZoneTree.FullTextSearch`) | One parsed query against a fixed token corpus | Record tokenizer, corpus/token counts, query shape, and result count. |
| Incremental filter | `IncrementalView` | Apply one committed transaction to a filter view | Fixed before/after images and view row count; report `Applied`, `Duplicate`, and `Conflict` separately. |
| Incremental join | `IncrementalView` | Apply one endpoint change to a two-collection join | Fixed join fanout and affected-pair count. |
| Incremental aggregate | `IncrementalView` | Apply one grouped aggregate update | Fixed group cardinality and affected groups. |
| Fallback view reset | `IncrementalView` | Apply one unsupported-plan transaction and rebuild | Record source rows and complete replacement rows; this is a reset cost. |
| Reactive subscribe | `ReactiveSubscriptionHub` | Attach one subscriber and receive its initial snapshot | Record query result size, dependency count, authorization scope, and retained revisions. |
| Reactive publish | `ReactiveSubscriptionHub` | Publish one affected `ReactiveChangeSet` to M subscribers | Split no-op, delta, reset, backpressure, and authorization-revoked outcomes. |
| Reactive resume | `ReactiveSubscriptionHub` | Attach using a retained `tsr1` checkpoint | Split retained delta replay from expired-checkpoint reset. |
| Replication apply | `LeaderlessReplicationEngine` | Apply one clean, duplicate, deferred, or concurrent `ReplicationRecord` | Separate each outcome; include body and canonical-byte sizes. |
| Journal append | `ReplicationJournal` | Append one row and update the contiguous frontier | Split in-order, out-of-order, duplicate, and same-dot conflict. |
| Anti-entropy plan | `AntiEntropyEngine` | Fingerprint two journals and create a missing/divergent range plan | Fixed origins, range leaf width, row count, and divergence rate. |
| Anti-entropy reconcile | `AntiEntropyEngine` | Copy source rows into the destination journal | Record rows copied, duplicate rows, and final frontier. |
| Replication chunking | `ResumableReplicationStore` | Create and stream one bounded replication stream | Record max records, max bytes, retained chunks, checksum verification, and resume position. |
| Full backup | `BackupManager` / `InMemoryBackupObjectStore` | Capture, upload artifact, and commit one full manifest | Record snapshot bytes, SHA-256, encryption mode, attempts, and manifest commit latency. |
| Incremental backup | `BackupManager` | Encode and commit one chained incremental log | Record log entries, bytes, base boundary, retries, and chain depth. |
| Backup restore/PITR | `BackupManager` / `InMemoryBackupRestoreTarget` | Verify chain, restore snapshot, replay through target time | Record chain length, applied entries, plaintext bytes, and observed recovery time. |
| Backup encryption | `AesGcmBackupEncryption` | Encrypt and decrypt a fixed artifact | Record plaintext/ciphertext bytes, key size, and hash verification. |
| MessagePack contract | `InterNodeRequestEnvelope` / replication contract | Serialize and deserialize one private inter-node envelope | Record payload size and schema version; do not call this a network benchmark. |
| JSON/SSE client parsing | `SyncTransport` | Parse one snapshot response or SSE event stream | Record response/event bytes and parser-only time; exclude server latency. |

## Results summary

Fill one row per admitted subject and workload. Keep failed or invalid attempts
in the receipt directory and add the reason here. The rows below are the first
admitted local receipts; they do not represent end-to-end distributed
performance.

| Workload | Subject | Status | Samples | Ops/s | p50 (µs) | p95 (µs) | p99 (µs) | Allocated bytes/op | CPU % | RAM after bytes | Bytes written | Write amplification | Dataset / payload | Durability | Receipt |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | --- |
| Point read | InMemoryStorageProvider | complete | 5 | 260,010.40 | 1.87 | 10.05 | 22.32 | 896.88 | 6.50 | 63,873,024 | — | — | 100 / 128 B | in-memory | [`receipt.json`](receipts/20260914-5b5d1767-memory-point-read/receipt.json) |
| Conditional upsert | StorageEngine | complete | 5 | 40.41 | 23,816.13 | 30,250.68 | 36,456.07 | 8,713,948.64 | 3.07 | 98,619,392 | 67,153 | 9.46 | 100 / 128 B | synced WAL | [`receipt.json`](receipts/20260914-5b5d1767-storage-point-write/receipt.json) |
| Point read | InMemoryStorageProvider | complete | 5 | 269,832.70 | 1.96 | 10.16 | 19.43 | 899.44 | 5.79 | 63,795,200 | — | — | 100 / 128 B | in-memory | [`receipt.json`](receipts/20260914-409cd976-memory-point-read/receipt.json) |
| Conditional upsert | StorageEngine | complete | 5 | 40.66 | 24,282.18 | 30,333.18 | 35,801.61 | 8,714,390.40 | 3.05 | 98,693,120 | 67,158 | 9.46 | 100 / 128 B | synced WAL | [`receipt.json`](receipts/20260914-409cd976-storage-point-write/receipt.json) |
| all other listed workloads | — | pending | — | — | — | — | — | — | — | — | — | — | — | — | — |

## Correctness and durability evidence

Performance rows must link to correctness evidence for the same source state.
The current focused checks cover these local invariants:

| Area | Current evidence | What it proves |
| --- | --- | --- |
| Local storage and WAL | [`StorageEngineTests.cs`](../tests/TerraBase.Tests/StorageEngineTests.cs) and [`ProductionReengineeringTests.cs`](../tests/TerraBase.Tests/ProductionReengineeringTests.cs) | WAL replay, torn-tail recovery, checksum rejection, atomic batches, compaction/tombstones, close/reopen, snapshot recovery, and version watermarks. |
| Transactions | [`TransactionTests.cs`](../tests/TerraBase.Tests/TransactionTests.cs) | Atomic batch receipt, serializable read-set conflict, local strict referential checks, and deferred cross-shard checks. |
| Indexing | [`AutomaticIndexingTests.cs`](../tests/TerraBase.Api.Tests/AutomaticIndexingTests.cs) | Bounded deterministic candidates, tenant budget isolation, invisible lifecycle, shadow activation, rollback, and grace-period retirement. |
| Incremental views | [`IncrementalViewTests.cs`](../tests/TerraBase.Tests/IncrementalViewTests.cs) | Atomic watermarking, before-image conflicts, duplicate handling, join/aggregate maintenance, and fallback reset. |
| Replication and transport | [`ProductionPrimitivesTests.cs`](../tests/TerraBase.Api.Tests/ProductionPrimitivesTests.cs), [`ReplicationDeterministicTests.cs`](../tests/TerraBase.Tests/ReplicationDeterministicTests.cs), and [`InterNodeContractTests.cs`](../tests/TerraBase.Tests/InterNodeContractTests.cs) | Causal dependency deferral, canonical-byte convergence, anti-entropy/partition decisions, TLS requirement, bounded contracts, and MessagePack round trips. |
| Backups | [`BackupTests.cs`](../tests/TerraBase.Tests/BackupTests.cs) | Full/incremental chain creation, resumable in-memory upload, PITR, plaintext/encrypted hashes, and AES-GCM restore. |

These checks do not qualify multi-Region durability, durable replication across
restart, a concrete MagicOnion adapter, a real index backend, reactive network
delivery, external object-store recovery, or a production RPO/RTO/SLO. The
current receipts measure only local point reads and synced point writes; rows
for replication lag, subscription fan-out cost, restore time, and distributed
durability remain `pending`.

## Run metadata

Complete one row for every benchmark batch. The values shown are required
fields, not measured defaults.

| Batch date (UTC) | Commit | Dirty tree | Artifact SHA-256 | Host / OS / architecture | CPU / memory envelope | Storage | Workload digest | Runner command | Receipt directory |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 2026-09-14 | `5b5d1767d16211794020a111eeb5467bd73b0e5f` | true (`.codex/` untracked) | `c7d202a8b3d790d92711c864a42c47a2ca65862465560ce7fda0bcb274ec66ac` | NixOS 26.05 / x64 | Intel Xeon E5-2660 v4, 46 logical CPUs, 20.9 GB available | local filesystem; in-memory and synced WAL | see receipt JSON | `nice -n 19 dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll ...` | [`bench/receipts`](receipts) |
| 2026-09-14 | `409cd9765a22382a90be63e3f6e417601e36821e` | true (`.codex/` untracked) | `3574c8760d16d03a8dab9c488bd129d1ec30bb6c03bc99064d56b63b95625907` | NixOS 26.05 / x64 | Intel Xeon E5-2660 v4, 46 logical CPUs, 20.9 GB available | local filesystem; in-memory and synced WAL | see receipt JSON | `nice -n 19 dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll ...` | [`bench/receipts`](receipts) |

Do not add a ratio against PostgreSQL, RavenDB, an archived Rust build, or a
historical SQLite run until both sides have matching workload, boundary,
resource envelope, source identity, and retained receipts.
