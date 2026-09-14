# TerraBase C# benchmark baseline

Status: **scaffold; no current C# benchmark receipt is admitted**.

The latest recorded validation snapshot is dated **2026-09-14** and tested
revision `89a7cb722e59586f34ea98ead772b368599f4c6a` (`main`). The shared
worktree has continued to change after that snapshot, so moving `HEAD` is not
a benchmark result. Every receipt must record its exact revision, dirty state,
and artifact digest. Runtime target:
.NET 11 RC, C# only.

The host path is not an end-to-end durable benchmark target: the default host
uses `InMemoryStorageProvider`, the path overload uses the JSON
`FileStorageProvider`, and `StorageEngine` is not wired into the host. The
MagicOnion package and contract interfaces do not constitute a live
TerraBase-to-TerraBase adapter or peer endpoint. Component rows below must keep
those boundaries explicit.

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
| Full-text lookup | `TerraBase.ZoneTree.FullTextSearch` | One parsed query against a fixed token corpus | Record tokenizer, corpus/token counts, query shape, and result count. |
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
in the receipt directory and add the reason here. The initial table remains
empty by design.

| Workload | Subject | Status | Samples | Ops/s | p50 (µs) | p95 (µs) | p99 (µs) | Dataset / payload | Durability | Receipt |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | --- | --- | --- |
| all listed workloads | — | pending | — | — | — | — | — | — | — | — |

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
delivery, external object-store recovery, or a production RPO/RTO/SLO. No
current receipt reports throughput, p50/p95/p99 latency, allocations, CPU/RAM,
write amplification, replication lag, subscription fan-out cost, or restore
time; the results table therefore remains `pending`.

## Run metadata

Complete one row for every benchmark batch. The values shown are required
fields, not measured defaults.

| Batch date (UTC) | Commit | Dirty tree | Artifact SHA-256 | Host / OS / architecture | CPU / memory envelope | Storage | Workload digest | Runner command | Receipt directory |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| — | — | — | — | — | — | — | — | — | — |

Do not add a ratio against PostgreSQL, RavenDB, an archived Rust build, or a
historical SQLite run until both sides have matching workload, boundary,
resource envelope, source identity, and retained receipts.
