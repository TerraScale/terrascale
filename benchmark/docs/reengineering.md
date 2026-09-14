# TerraBase reengineering record

This note records the boundary between the current C# implementation and the
multi-Region database design described by the ADRs. It is intentionally dated:
the source and tests in this repository are authoritative, while an ADR or a
research result is a design input until the corresponding runtime and evidence
land.

The evidence cutoff for this note is **2026-09-13**, at commit
`8cc4526b65ea2682c4356f493033f2f35d0f060a`. The current product is a .NET 11
C# storage and observability library. It contains an abstract storage port,
in-memory and file-backed providers, a deterministic in-memory replication
engine, HTTPS sync transport, backup destination primitives, and a loopback
health/metrics host.

## Status vocabulary

| Status | Meaning in this note |
| --- | --- |
| Implemented | Present in the current source and covered by a focused test or an explicit source contract. |
| Primitive | A reusable boundary exists, but scheduling, persistence, or end-to-end policy is absent. |
| Design target | Described by an accepted ADR or research adaptation; current source does not provide the guarantee. |
| Removed | Present in an earlier implementation, then deliberately deleted during the current cutover. |
| Unmeasured | No current benchmark receipt proves a performance or production claim. |

This vocabulary prevents a design statement such as “GlobalDurable waits for
all Regions” from being read as a property of the local C# engine. The current
engine does not expose that durability class.

## Why the design was adapted

The September 13 cutover reduced TerraBase to a C# library with replaceable
storage and local observability. Commit `953daa41` removed the engine's SQLite
specific coupling and commit `d06859c8` repositioned the host around storage,
health, and metrics. Commit `8cc4526b` then removed stale MessagePack wording.
The old Rust benchmark records were moved under `bench/rust/` by
`8dc722f7`; they are historical evidence for a different implementation.

The implementation choices below retain useful research invariants while
discarding assumptions that would make a local primitive appear to be a
distributed database.

| Research or earlier assumption | Adaptation in the current repository | Status and boundary |
| --- | --- | --- |
| COPS causal+ dependency checks | `ReplicationRecord` carries a per-origin `Context`; `LeaderlessReplicationEngine.Apply` returns `Deferred` until every dependency is present. | Implemented in one in-memory engine. There is no durable frontier, authenticated member transport, or Region repair path. |
| Occult-style session causality | The replication frontier is exposed as a copy and can be used by a caller to track progress. | Design target. There is no session token, wait/redirect policy, or stale-replica error in the current public API. |
| UNISTORE-style mixing of durable classes | ADR 0035 keeps the fail-closed rule for a future `GlobalDurable` class. | Design target. The current providers have no cross-Region acknowledgement path. |
| Delta-state CRDTs and deterministic reconstruction | Concurrent records merge with an HLC/origin tie-break, remove-wins tombstones, and sorted object keys. Clean causal records can retain canonical UTF-8 bytes without parsing. | Implemented as a small document-level merge primitive. It is not the native datatype and canonical codec suite specified by ADR 0022. |
| DBSP incremental view maintenance and IntersectionCC | ADR 0044 limits the proposed live view subset and promises a consistent cut per view. | Design target. No query planner, materialized view, dependency index, or subscription server exists in the current source. |
| Automatic query-shaped indexes | ADR 0010 defines an index catalog and lifecycle with causal visibility. | Design target. `ScanAsync` is the current query surface and performs a deterministic key-ordered collection scan. |
| Object-store and Litestream-style backups | `IBackupReplica` accepts a caller-supplied SHA-256; the file adapter writes a verified temporary artifact before rename, and the HTTPS adapter retries transient PUT failures. | Primitive. There is no snapshot scheduler, WAL stream, manifest, restore workflow, or backup freshness proof. |
| SQLite as the durable source of truth | The engine depends on `IStorageProvider`; the current durable provider is a versioned JSON snapshot file. | Removed from the current runtime. SQLite history and claims must not be used as current guarantees. |
| TBEP/MessagePack as the engine protocol | The protocol project and package were removed during the cutover. Sync transport uses HTTPS JSON, SSE text, and WSS. | Removed. No MessagePack, TBEP, FlatBuffers, or protobuf wire contract is implemented in this branch. |

## Consistency and transaction boundaries

The current consistency boundary is one provider operation. There is no
transaction that spans the document provider, replication engine, sync
transport, metrics, or a backup destination.

| Boundary | Current behavior | Failure behavior |
| --- | --- | --- |
| One local read or scan | `IStorageProvider` returns an immutable `Document`; scans are snapshotted and ordered by document id for the tenant and collection. | Cancellation is observed; a disposed provider throws. |
| One local upsert/delete | The provider checks `DocumentPrecondition` (`Any`, `Missing`, or `Version`) while holding its provider lock. Versions are monotone per key, including delete/recreate cycles. | A stale precondition returns `Conflict`; deleting an absent key returns `NotFound`; no body mutation occurs. |
| File durability | `FileStorageProvider` serializes a complete format-versioned snapshot to a same-directory temporary file, flushes it with `WriteThrough`, and replaces the primary file. A valid temporary snapshot can recover a corrupt primary. | Invalid JSON, null documents, duplicate keys, stale watermarks, and unsupported format versions fail closed. A failed write restores in-memory state. |
| Batch mutation | `TerraBaseEngine.ApplyBatchAsync` requires `ITransactionalStorageProvider` and otherwise throws `NotSupportedException`. Neither built-in provider currently implements that capability. | There is no current all-or-nothing batch guarantee for `TerraBaseEngine.Open()` or `Open(path)`. |
| Replication apply | `LeaderlessReplicationEngine` serializes apply under its own lock, rejects zero/blank dots, defers missing dependencies, drops duplicates, and merges concurrent records. Its frontier and operation log live only in process memory. | A restart loses the replication state. A deferred record is returned to the caller for repair; no repair worker is attached. |
| HTTPS sync | `SyncTransport` fetches JSON snapshots, parses SSE events, and opens WSS. It does not own a server-side view or commit boundary. | Non-HTTPS base URIs are rejected. HTTP status errors and cancellation propagate. |
| Backup upload | A backup adapter is called independently with an artifact stream and expected hash. It is not invoked by a document write. | File uploads delete `.partial` files on failure. HTTPS retries only transient response classes up to the configured attempt count. |

The transaction contract in `StorageBatchContracts.cs` is therefore a
capability boundary, not a promise made by every provider. Any provider that
implements it must evaluate all preconditions and durable writes in one
transaction and return no committed writes on `Conflict` or `NotFound`.

## Binary, persistence, and wire formats

There is currently no required TerraBase binary persistence or member wire
format in the C# runtime.

| Surface | Current representation | Current guarantee |
| --- | --- | --- |
| File provider | UTF-8 JSON from `System.Text.Json`, with `FormatVersion = 1`, `Documents`, and optional `VersionWatermarks`. Document bodies are stored as JSON text inside the snapshot record. | The provider validates the schema and ordering used when writing. This is a local file format, not a cross-language protocol. |
| Replication record | A C# record containing key, `(Origin, Sequence)` dot, context map, signed HLC value, delete flag, and either `JsonElement` or optional `ReadOnlyMemory<byte>` canonical body. | The byte fast path avoids a parse for clean causal apply. It has no frame header, codec negotiation, checksum, or authentication in this class. |
| Snapshot sync | HTTPS `application/json` response. | JSON parsing is bounded by the caller's stream and cancellation token; snapshot versioning is a server concern not defined by `SyncTransport`. |
| SSE sync | `text/event-stream`; `id`, `event`, and one or more `data` lines become an `SseEvent`. | The parser preserves event data and IDs. It does not resume or verify a durable revision log. |
| WSS sync | WebSocket frames opened from an HTTPS base URI. | TLS and bearer-header setup are enforced; frame schema is not defined here. |
| Backup artifact | Opaque caller-provided bytes plus a lowercase hexadecimal SHA-256 string. | The file adapter hashes the bytes before rename. The HTTPS adapter sends the hash as `x-content-sha256` and relies on the object-store response. |

ADR 0022 names future codecs `terrabase-native/1` and
`terrabase-snapshot/1`, with canonical encoding and BLAKE3 rules. Those names
are design targets, not codecs present in the current C# project. A future
implementation must provide versioned headers, canonicalization rules,
allocation limits, corruption behavior, independent golden vectors, and a
migration decision before advertising either format.

## Indexing, reactive delivery, and backup guarantees

The accepted ADRs describe a larger product surface. The current evidence is
deliberately narrower.

| Capability | Current source | Guarantee that can be claimed today | Design target / missing evidence |
| --- | --- | --- | --- |
| Indexing | No index catalog or index implementation. `ScanAsync` filters a provider snapshot and sorts by document id. | Deterministic collection scan for the selected tenant and collection. | ADR 0010's auto-index lifecycle needs catalog, build, causal publication, retirement, crash recovery, and benchmark receipts. |
| Reactive queries | `SyncTransport` is a client transport/parser only. | HTTPS snapshot fetch, SSE parsing, and WSS connection setup. | ADR 0044's view cuts, Z-set deltas, authorization generation, resume/reset, and backpressure need a server and conformance tests. |
| Replication | In-memory causal record application and export/import of the local operation log. | Dependency deferral, duplicate suppression, deterministic concurrent merge, and local compaction of superseded records. | Durable publication journal, authenticated Region transport, anti-entropy, membership, failover, and loss testing are absent. |
| Backups | `FileBackupReplica` and `PresignedHttpBackupReplica`. | Individual artifact upload checks input and, for the file adapter, verifies bytes before promotion. | Backup creation, immutable manifests, retention, restore verification, PITR, freshness metrics, and RPO/RTO evidence are absent. The engine initializes backup health as `Unknown`. |

No current write acknowledgement means `RegionalDurable`,
`GlobalDurable`, or `BackupDurable`. A local file write is durable only at the
file provider boundary after the snapshot replacement returns successfully; it
does not imply a backup or a second Region.

## Evidence map and dates

| Date | Evidence | What it establishes |
| --- | --- | --- |
| 2026-08-26 | [InstantDB survey](competitive/evidence/instantdb-survey-2026-08-26.md) and [Convex survey](competitive/evidence/convex-survey-2026-08-26.md) | Dated public competitor observations used as research input. They establish competitor claims, not TerraBase behavior. |
| 2026-08-29 | [ADR 0022](adr/0022-native-causal-model-and-crdt-algebra.md), [ADR 0033](adr/0033-production-slos-oci-placement-and-operability.md), and [ADR 0035](adr/0035-one-region-global-durable-semantics.md) | Native causal/codec design, production durability targets, and the fail-closed future `GlobalDurable` rule. |
| 2026-09-03 | [ADR 0044](adr/0044-sync-and-replication-engines.md) | Accepted research adaptation for the future replication and sync engines; its invariants still need runtime qualification. |
| 2026-09-13 | `953daa41`, `d06859c8`, and `8cc4526b` | SQLite-specific runtime coupling and MessagePack/TBEP references were removed or narrowed; the current C# library and documentation were refreshed. |
| 2026-09-13 | [storage tests](../tests/TerraBase.Tests/Program.cs), [production primitive tests](../tests/TerraBase.Api.Tests/ProductionPrimitivesTests.cs), and [host tests](../tests/TerraBase.Api.Tests/MetricsEndpointTests.cs) | Focused evidence for local storage recovery/versioning, replication/sync primitives, and loopback observability. |

The dated benchmark scaffold in
[`../bench/terra-base-baseline.md`](../bench/terra-base-baseline.md) is the
only place to add current performance numbers. A historical Rust receipt, an
old SQLite run, a unit-test duration, or a local SQL probe cannot be promoted
to a current C# service benchmark without matching source identity, workload,
resource envelope, operation boundary, and receipt metadata.

## Reengineering guardrails

Future changes should preserve these distinctions:

1. Add persistence before describing a frontier, journal, or replication
   acknowledgement as durable.
2. Add a provider transaction implementation before exposing batch atomicity
   through `TerraBaseEngine`; do not infer it from the capability interface.
3. Add the index and view state owners before publishing query or reactive
   guarantees; transport parsing alone is not reactive maintenance.
4. Add snapshot manifests, restore checks, and freshness evidence before
   describing an upload destination as a backup system.
5. Keep binary codec names, public wire contracts, and benchmark identities
   versioned and tied to the source state that produced them.

