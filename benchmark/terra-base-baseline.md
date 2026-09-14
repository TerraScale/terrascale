# TerraBase C# benchmark baseline

Status: **scaffold; no current C# benchmark receipt is admitted**.

Evidence cutoff: **2026-09-13**. Source baseline:
`8cc4526b65ea2682c4356f493033f2f35d0f060a` (`main`). Runtime target: .NET 11,
C# only. The tables below are intentionally empty until a runner records the
source, artifact, host, workload, and operation boundary for the same run.

The old Rust benchmark corpus is archived under `bench/rust/` in its source
history. It is not a baseline for this C# runtime. The C# cutover also removed
the SQLite, MessagePack, and TBEP paths used by earlier experiments, so their
numbers cannot be copied into this report.

## Admission rules

A row becomes `complete` only when its receipt records all of the following:

| Field | Required value |
| --- | --- |
| Source identity | Git commit, clean/dirty state, and a source tree or release artifact SHA-256. |
| Runtime | .NET SDK/runtime version, OS, architecture, GC mode, and build configuration. |
| Subject | Provider or component name and exact operation boundary. |
| Host envelope | CPU model/count or cgroup limit, memory limit, storage medium, and relevant I/O pressure. |
| Workload | Seed, dataset size, payload size, operation count, concurrency, warm-up, and workload digest. |
| Durability | In-memory, file snapshot, transactional provider, or other explicit mode. |
| Samples | At least five measured samples after warm-up, with p50, p95, and p99 per sample. |
| Receipt | A retained machine-readable result path and command/exit status. |

Percentiles are reported per sample and summarized as medians; samples are not
pooled into a synthetic percentile. A missing measurement is `—`, never zero.
An error or incompatible setup is `failed` or `invalid` with its reason and
receipt path. It never becomes a performance claim.

Use the repository's lowest available CPU priority for build and benchmark
commands. The current solution checks are documented in
[`docs/development.md`](../docs/development.md). A benchmark must name any
additional runner command rather than implying that a solution test is a
throughput measurement.

## Workload definitions

These rows align to APIs that exist in the current source. A future index,
reactive, replication, or backup implementation may add a row with its own
operation boundary and admission evidence.

| Workload | Subject | Operation boundary | Required setup |
| --- | --- | --- | --- |
| Point read | `InMemoryStorageProvider` or `FileStorageProvider` | One `ReadAsync` for an existing key | Warm provider; record file size and cache state. |
| Conditional upsert | Same providers | One `UpsertAsync` with `DocumentPrecondition.Version` | Existing key and fixed JSON body; report conflict rate separately. |
| Delete/recreate | Same providers | One delete followed by one missing-precondition upsert | Record version watermark behavior and operation latency separately. |
| Collection scan | Same providers | One complete `ScanAsync` for one tenant/collection | Record row count, returned bytes, and cancellation policy. |
| Replication apply | `LeaderlessReplicationEngine` | One `Apply` for clean, duplicate, deferred, and concurrent records | Separate each outcome; include body and canonical-byte sizes. |
| Snapshot fetch | `SyncTransport` | One `GetSnapshotAsync` response parse | Record response bytes and server endpoint; this measures client transport only. |
| SSE parse | `SyncTransport` | One complete SSE event or event stream | Record event bytes and event count; it is not reactive server latency. |
| Backup artifact | `FileBackupReplica` or `PresignedHttpBackupReplica` | One `PutAsync` including hash verification/retries | Record artifact bytes, destination, attempt count, and restore verification status. |

## Results summary

Fill one row per admitted subject and workload. Keep rows for failed or
invalid attempts in the receipt directory and add the reason to the table.

| Workload | Subject | Status | Samples | Ops/s | p50 (µs) | p95 (µs) | p99 (µs) | Dataset / payload | Durability | Receipt |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | --- | --- | --- |
| point-read | InMemoryStorageProvider | pending | — | — | — | — | — | — | in-memory | — |
| point-read | FileStorageProvider | pending | — | — | — | — | — | — | file snapshot | — |
| conditional-upsert | InMemoryStorageProvider | pending | — | — | — | — | — | — | in-memory | — |
| conditional-upsert | FileStorageProvider | pending | — | — | — | — | — | — | file snapshot | — |
| delete-recreate | FileStorageProvider | pending | — | — | — | — | — | — | file snapshot | — |
| collection-scan | InMemoryStorageProvider | pending | — | — | — | — | — | — | in-memory | — |
| collection-scan | FileStorageProvider | pending | — | — | — | — | — | — | file snapshot | — |
| replication-apply-clean | LeaderlessReplicationEngine | pending | — | — | — | — | — | — | in-memory | — |
| replication-apply-merge | LeaderlessReplicationEngine | pending | — | — | — | — | — | — | in-memory | — |
| snapshot-fetch | SyncTransport | pending | — | — | — | — | — | — | network | — |
| sse-parse | SyncTransport | pending | — | — | — | — | — | — | network | — |
| backup-upload | FileBackupReplica | pending | — | — | — | — | — | — | artifact | — |
| backup-upload | PresignedHttpBackupReplica | pending | — | — | — | — | — | — | object store | — |

## Correctness and durability evidence

Performance rows must link to the focused correctness evidence for the same
source state. The current repository already has these checks:

| Area | Current evidence | What it proves |
| --- | --- | --- |
| Local versions and recovery | [`tests/TerraBase.Tests/Program.cs`](../tests/TerraBase.Tests/Program.cs) | Conditional writes, deterministic scans, close/reopen, temporary snapshot recovery, corrupt input rejection, and monotone delete/recreate versions. |
| Causal replication | [`tests/TerraBase.Api.Tests/ProductionPrimitivesTests.cs`](../tests/TerraBase.Api.Tests/ProductionPrimitivesTests.cs) | Dependency deferral, canonical-byte fast path, convergence under reordered concurrent records, and TLS requirement for sync transport. |
| Host boundary | [`tests/TerraBase.Api.Tests/MetricsEndpointTests.cs`](../tests/TerraBase.Api.Tests/MetricsEndpointTests.cs) | Loopback metrics and lifecycle/readiness responses. |

These tests do not qualify multi-Region durability, reactive visibility,
automatic indexes, backup recovery, or a production SLO. Add a separate
receipt when those state owners exist.

## Run metadata

Complete this table with every benchmark batch. The values shown are the
required fields, not measured defaults.

| Batch date (UTC) | Commit | Artifact SHA-256 | Host / OS / architecture | CPU / memory envelope | Storage | Workload digest | Runner command | Receipt directory |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| — | — | — | — | — | — | — | — | — |

No ratio against PostgreSQL, RavenDB, the archived Rust implementation, or a
historical SQLite build should be added until both sides have matching
workload, boundary, resource envelope, source identity, and retained receipts.

