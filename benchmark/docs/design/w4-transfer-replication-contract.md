# W4 transfer and replication contract: streaming, replication, backup/import, and logical migration

Status: proposed design contract for W4, recorded 2026-08-24. Parent
adjudication of the decision records in this document is required before any
W4 behavior packet is dispatched.

This document turns the W4 wave into rules that builders and tests can use.
It covers the Streaming, Replication, and Backup/Import seams, including the
Logical Migration path. It mirrors how
[`docs/design/w3-core-contract.md`](w3-core-contract.md) governed W3: one
authoritative design record, one declaration-only preparation packet, then
behavior packets with verbatim red-test lists and exact path ownership.

It does not claim that any TerraBaseV2 streaming, replication, backup, or
migration behavior has been implemented or qualified. Every parent-gated
choice in this document — the replication algorithm, consistency and
acknowledgement defaults, the conflict policy, production topology limits, and
the optional/legacy transfer dispositions — is recorded as a decision with
options, evidence, and a recommendation marked **proposal only**. The parent
adjudicates at gate time. No builder may treat a recommendation in this
document as an accepted decision.

The governing records are the workspace charter identified as
`workspace-root:specs/GOAL.md` (the parent resolves
that identifier to an absolute path in assignment metadata; a builder must not
infer a path from its worktree location), the
[W3 core contract](w3-core-contract.md), the
[W2 contract extraction](../parity/w2-contract-extraction.md), and the
[W4 row mapping](../parity/w4-row-mapping.md). The pinned V1 source identity
remains commit `fa99b9257776dbe625e6a2388c74c9ce916637ee`.

## Scope and entry conditions

- Base commit: `46bccd5` on `develop` (W3 complete: storage/engine, families,
  query/index, protocol; 171 tests; both gates GO on W3-3).
- W4 builds on these frozen W3 contracts and does not restate or weaken them:
  - *Storage publication order* (W3 contract, "Storage publication
    ordering"): validate, encode, prepare, apply-with-marker, durability step,
    durable `ReadyToPublish` ledger sync, atomic pointer publication,
    post-publish syncs, durable `Published` ledger sync, lock release, then
    the immutable `CommitPublicationEvent`.
  - *Commit outcome and durable resolution* (W3 contract, "Commit outcome and
    durable resolution"): `CommitOutcome` is `Committed | Rejected | Unknown`;
    `DurableResolution` is `Committed | Rejected | Pending`; `Unknown` retains
    the original `OperationId` and is resolved through the operation ledger.
  - *Read snapshots and index generations* (W3 contract, "Read snapshots and
    index generations"): `ReadSnapshot` is immutable and tenant-scoped;
    `IndexGeneration` is monotonic within one namespace and persisted epoch.
  - *Local durability vocabulary* (W3 contract, "Local durability"):
    `Buffered`, `Written`, `Synced` describe local storage only and are never
    substituted for `Acknowledgement Level` or `Convergence`.
  - *Family seam* (W3 contract, "Data Family contract"): closed typed family
    requests/results; family values live in the reserved collection
    `terrabase.families`; family writes ride an Engine `Transaction`.
  - *Protocol seam* (W3 contract, "Protocol and Platform contract"): closed
    `PublicOperation` registry, opaque `CallerContext`, no grant crossing
    Protocol or Engine.
  - *CommitReceipt stays local* (W3 contract, "Public type ownership"):
    `CommitReceipt` contains only `OperationId`, `StorageSequence`, and
    `LocalDurability`. W4 reaffirms this; see "Streaming contract" and
    "Replication contract" below.

- Existing W3 code this contract anchors on (all read at `46bccd5`):
  `src/storage/mod.rs` (`StoragePort`, `CommitPublicationEvent`),
  `src/engine/mod.rs` (`Engine`, `CommitPublicationSink`,
  `snapshot_documents`), `src/families/state.rs` (`RESERVED_COLLECTION`),
  `src/adapters/protocol/mod.rs`, `src/types.rs`, `src/operations.rs`.
- Non-goals for W4: no V1 wire-compatibility claim (V1 transfer protocols are
  parity obligations, not a wire target); no Platform authorization or MCP
  boundary work (W5 owns `src/adapters/platform/**` and PB-3042/PB-6036); no
  move of the Engine/Platform/control-plane boundary; no sharding
  implementation (the sharding rows stay later work); no speed claims; no
  parity status changes.

Entry evidence the parent checks before dispatching W4-0: the W3-3 integrated
tip is recorded, the four baseline checks are green on it, and the decision
records below are adjudicated or explicitly deferred with a recorded limit.

## Decision summary

The selected design shape is a proposal. Like W3's hybrid rule, callers get
small typed interfaces and the difficult ordering, checkpoint, transport, and
recovery rules stay behind them.

| Seam | Proposed interface | Main reason | What stays behind the seam |
| --- | --- | --- | --- |
| Streaming | Borrowed `StreamStore<'engine>` with cursor-based batches, subscription checkpoints, and bounded buffers | One ordering and checkpoint rule can govern document, query, time-series, family, and change streams | Buffering policy, slow-consumer handling, resend state, subscription storage, event filtering |
| Replication | `RegionId`/`MemberId`/`TopologyView` inputs, `AcknowledgementLevel` request, `ReplicationOutcome` with `AcknowledgementReport`, `Convergence`, and `ReplicationReceipt` | The charter's Acknowledgement Level and Convergence vocabulary becomes observable without exposing transport or leader state | Transport, message codec, leader or shard state, anti-entropy internals, membership transitions |
| Backup/Import | Manifest-verified `BackupManifest`/`BackupArchive` and `MigrationManifest` with restartable, idempotent logical records | A manifest that binds exact bytes and order makes restore and Logical Migration auditable | Archive codec, incremental chain internals, staging directories, provider adapters |

No interface in this document is a decision about the replication algorithm,
consistency or acknowledgement defaults, conflict policy, production topology,
or optional/legacy dispositions. Those are the decision records in
"Replication and multi-region contract" and "Open decisions and limits".

## Problem frame and constraints

W4 has three behavior seams with different failure costs. Streaming must
deliver ordered, bounded, cancellable reads and subscriptions without
acknowledging data a consumer never processed. Replication must make
`Acknowledgement Level` and `Convergence` observable across Regions and
Members without turning a local receipt into a remote claim. Backup and
Logical Migration must bind exact input and output bytes to manifests so an
interrupted or partial transfer can never report success.

The shared constraints, extending W3's:

1. The core stays synchronous and runtime-neutral. Streaming, replication,
   and backup may use blocking local I/O behind bounded adapters; none may
   require Tokio or an executor.
2. Every mutating operation keeps its caller-supplied `OperationId`.
   Delivery, replay, and resend are at-least-once with stable identity;
   duplicates are identifiable, never silently applied twice.
3. `Buffered`, `Written`, and `Synced` remain local. `Acknowledgement Level`
   describes which replication members confirmed an operation; `Convergence`
   is the contract-defined replicated state observation. A local `Synced`
   receipt never implies either.
4. Streams read only immutable `ReadSnapshot` state or already-published
   `CommitPublicationEvent` facts. A live, uncommitted `Transaction` is never
   observable through any stream.
5. Replication's data plane lives in the Engine. Provisioning, tenant
   onboarding, routing, lifecycle, and external authentication policy remain
   the Platform/Orchestrator control plane, outside this repository.
6. Backup archives and migration manifests bind ordered member names,
   lengths, and SHA-256 digests. Restore and import verify before any target
   is materialized.
7. Raw bytes, WAL frames, file offsets, manifest paths, private host paths,
   credentials, and customer payloads never cross a public W4 seam.
8. W4 fault and recovery scenarios run in the exclusive recovery lane
   (charter, "Recovery and durability contract") and produce secret-safe
   Replication or Recovery Receipts. Ordinary focused tests support Proof IDs
   for their observable behavior only.

Dependencies by category (codebase-design vocabulary):

- In-process: cursor arithmetic, checkpoint state, acknowledgement
  aggregation, conflict classification, manifest construction and
  verification, loss-matrix computation.
- Local-substitutable: the file system, clock, checksum implementation, and
  the in-process member/link doubles used by the synthetic fault lane.
- Ports and adapters: approved Protocol/Platform integration (W5), backup
  destination providers (later), replication transport between Members
  (crate-private; the qualification lane replaces it with controlled links).
- True external: cloud/FTP backup destinations, queue brokers, and SQL
  providers. They are later waves and are not exercised by W4 packets.

## Public type ownership

The visibility rule is exact, mirroring W3: the types in the table below are
declared `pub` (with private fields where validation matters) in their W4-0
module and later implemented by the owning behavior packet. The types in the
crate-private table are declared `pub(crate)` only when another V2 module
needs them, and are never re-exported from `src/lib.rs`. Codec, transport,
archive, and leader-state types are plain private declarations.

### Streaming (`src/streaming/`, W4-0 declarations, W4-STREAM implementation)

| Type | Visibility and ownership | Required facts |
| --- | --- | --- |
| `StreamStore<'engine>` | `pub`; W4-0 `src/streaming/mod.rs`, then W4-STREAM | Engine-borrowing acquisition path via a proposed `Engine::streams()`; no independent durable or in-memory production constructor. |
| `StreamSource` | `pub` | Closed enum: `Documents(DocumentStreamSelector)`, `Query(QueryPlan` binding`)`, `TimeSeries(TimeSeriesStreamRange)`, `Family(FamilyStreamSelector)`. Typed fields only; no raw query text, route, or generic bytes. |
| `DocumentStreamSelector` | `pub` | id, prefix, match and exclusion selectors with typed validation errors (PB-6007). |
| `StreamCursor` | `pub` | Opaque resumable position bound to one stream source and Engine identity; comparable for equality only. |
| `StreamBatch` | `pub` | Bounded items plus the next cursor and a terminal `StreamCompletion`; the batch limit is part of the request. |
| `StreamItem` | `pub` | Closed enum over documents, query rows, time-series entries, and family values; no storage handles. |
| `StreamCompletion` | `pub` | `EndOfStream`, `Cancelled`, `Failed(StreamError)`, `SlowConsumer` are distinct terminal observations. |
| `StreamError` | `pub` | Typed categories: `EngineClosed`, `SnapshotEngineMismatch`, `TenantMismatch`, `InvalidSource`, `InvalidCursor`, `SlowConsumer`, `Deadline`, `Cancelled`. No paths, payloads, or transport detail. |
| `ChangeEvent` | `pub` | One committed change: kind, tenant scope, collection/document identity, `OperationId`, `StorageSequence`. Content-bearing payloads are fetched separately; the event itself stays small (PB-6002). |
| `ChangeKind` | `pub` | Closed V1 event-shape set: document, index, counter, time-series, operation, database changes. |
| `ChangeFilter` | `pub` | Typed document/index/family filters; no untrusted script execution in W4 (see decision D-5). |
| `SubscriptionSpec` | `pub` | Named subscription identity, source/filters, batch bound, acknowledgement policy (PB-6003). |
| `SubscriptionBatch` | `pub` | Bounded batch with its `SubscriptionCheckpoint`; opened once, then acknowledged (PB-6003/6004). |
| `SubscriptionCheckpoint` | `pub` | Opaque durable position; advances only at the acknowledgement seam. |
| `SubscriptionAcknowledgement` | `pub` | Caller acknowledgement carrying the checkpoint it confirms. |
| `SubscriptionStatus` | `pub` | `Opening`, `Active`, `AwaitingAcknowledgement`, `Reconnecting`, `Closed(status)`, with explicit close reason (PB-6005). |
| `StreamPosition` | `pub` | Public observation of a cursor: storage sequence and source kind, without internal offsets. |

### Replication (`src/replication/`, W4-0 declarations, W4-REPLICATION implementation)

| Type | Visibility and ownership | Required facts |
| --- | --- | --- |
| `RegionId`, `MemberId` | `pub` | Validated logical topology identifiers, exactly the names the W3 contract reserved for this seam. `Display` is the canonical identifier. |
| `TopologyView` | `pub` | Caller-supplied membership input: Regions, Members, Failure Domain grouping, and per-range ownership under the adjudicated algorithm. Immutable snapshot of a membership view. |
| `AcknowledgementLevel` | `pub` | The lattice proposed in decision D-2 (e.g. `Local`, `RegionQuorum`, `MajorityRegions`, `AllRegions`); the exact set is parent-gated. |
| `AcknowledgementReport` | `pub` | The observed level, the Region/Member set that contributed, and the observation time. It names members explicitly (PB-5026, PB-5042). |
| `Convergence` | `pub` | The contract-defined converged-state observation for an operation identity: which Members hold it, at which acknowledgement state, observed when (PB-5043). |
| `ReplicationOutcome` | `pub` | `Acknowledged(AcknowledgementReport)`, `Timeout { operation_id, requested: AcknowledgementLevel, observed: AcknowledgementReport }`, `Failed(ReplicationError)`. Timeout is distinct from local-commit failure and carries the partial-ack observation — which Members had durably recorded when the deadline hit (PB-5026; a D-2 parameter, see below). |
| `ReplicationStatus` | `pub` | Health observations (assigned, active, responsive, stale, unavailable, degraded, unknown) reported separately from convergence state (PB-5032). |
| `ReplicationReceipt` | `pub` | Secret-safe record of a qualification scenario: scenario identity, topology, fault classes, acknowledgement facts, convergence outcome, measured limits. No document content (PB-5042..PB-5047). |
| `ConflictRecord` | `pub` | Content-free conflict observation: identities, vectors or clocks, and the recorded policy outcome. No conflicting document payloads (PB-5022). |
| `ReplicationCursor` | `pub` | Durable per-member replication progress bound to storage sequence; resumes after restart (PB-5033). |
| `ReplicationConfig` | `pub` | Validated topology and policy request input; it does not authorize, provision, or route (control plane owns that). |
| `ReplicationError` | `pub` | Typed categories: `MemberUnavailable`, `QuorumUnreachable`, `RegionLost`, `ConflictDetected`, `NotReplicated`, `InvalidTopology`, `EngineClosed`, `Deadline`, `Cancelled`. |

### Backup, import, and Logical Migration (`src/backup/`, `src/migration/`; W4-0 declarations, W4-BACKUP implementation)

| Type | Visibility and ownership | Required facts |
| --- | --- | --- |
| `BackupManifest` | `pub` | Immutable recovery-point identity plus ordered archive names, lengths, and SHA-256 digests (PB-6010, PB-6015, PB-5013..5015). |
| `BackupArchive` | `pub` | One staged archive with its digest and member counts; archive internals stay private. |
| `BackupKind` | `pub` | `Full` and `Incremental { base: recovery-point identity }`; snapshot-mode is a later disposition (PB-6010). |
| `BackupPlan` | `pub` | Validated request: source snapshot boundary, kind, item selection, throttling bound. |
| `BackupProgress` | `pub` | Non-secret counts, current step, and skipped-item reporting; a green status cannot hide loss (PB-6021, PB-6035 engine side). |
| `BackupResult` | `pub` | Terminal result carrying the manifest and a `Recovery Receipt` reference. |
| `RestorePlan` | `pub` | Manifest identity, clean target location, verification options; refuses an existing unrelated target (PB-6016). |
| `RestoreResult` | `pub` | Restored identity, counts, checksum verdicts, and explicit limits. |
| `BackupError` | `pub` | Typed categories: `ManifestInvalid`, `ChecksumMismatch`, `ChainIncomplete`, `TargetNotEmpty`, `SourceUnavailable`, `Throttled`, `Cancelled`, `EngineClosed`. |
| `MigrationManifest` | `pub` | Verified V1-export identity: format version, record counts, and per-item digests; validated before any write (PB-6022, charter "Recovery and durability contract"). |
| `MigrationRecord` | `pub` | One logical input record with its kind and identity; physical V1 layout never crosses this type. |
| `MigrationOptions` | `pub` | Item selection, revision policy, batch and throttle bounds. |
| `MigrationReport` | `pub` | Converted counts, per-category loss matrix, resume position, and verification verdicts. |
| `MigrationLossMatrix` | `pub` | Explicit converted/dropped table with a reason per dropped category (see "Loss matrix"). |
| `MigrationError` | `pub` | Typed categories: `ManifestInvalid`, `RecordCorrupt`, `RecordTruncated`, `UnsupportedVersion`, `NotResumable`, `TargetIncompatible`, `Cancelled`. |

`CommitReceipt` is unchanged and stays local-only. No W4 type may add a
Region, Member, `AcknowledgementLevel`, `Convergence`, or `ReplicationReceipt`
field to it, and no W4 code may imply remote acknowledgement from a local
commit. `Engine::resolve_commit` and the operation ledger remain the only
local resolution path.

### Crate-private ownership

| Type or module | Owner | Rule |
| --- | --- | --- |
| stream buffers, slow-consumer queues, resend state, subscription storage | `src/streaming/` | Bounded, test-observable through the public seam only |
| replication transport, message codec, leader/shard state, anti-entropy workers, membership transition engine | `src/replication/` | Never re-exported; the qualification lane substitutes controlled in-process links |
| archive codec, incremental chain internals, staging directory management, destination adapter trait | `src/backup/` | Archive bytes never leave the module; destination providers are later adapters |
| `FamilyRecordInput` | `src/backup/mod.rs`, declared by W4-0, owned by W4-BACKUP | `pub(crate)` only: the typed family-record input that W4-CA-5 produces from the reserved collection, so the families seam can feed backup and migration without a public type or a families-owned backup type; never re-exported from `src/lib.rs` |
| migration iteration state, V1 record parsers, resume journal | `src/migration/` | Physical V1 layout parsing stays private; public types are logical records |
| `FaultPoint` arming and the publication-sink registry | `src/storage/` and `src/engine/` | Test-only fault arming stays `cfg(test)`; production-visible subscription is the proposed collision action W4-CA-3 |

No public W4 method may return a byte slice, WAL frame, file offset, manifest
path, archive path, transport frame, leader term, or private host path as a
substitute for a typed contract.

## W4 declaration preparation packet (W4-0)

One small executable preparation packet precedes behavior work, mirroring
W3-0. The parent dispatches `W4-0-DECLARATION-PREP` in worktree
`terrabasev2-w4-type-prep` (branch `fleet/w4-type-prep`), based on the
integrated W4 design tip. The dispatch records the exact tip by running
`git rev-parse` on the design branch after this document is integrated.

W4-0 owns exactly this declaration-only tree for the preparation commit:

```text
src/
├── lib.rs                      (collision action: add four roots + exports)
├── streaming/mod.rs
├── replication/mod.rs
├── backup/mod.rs
└── migration/mod.rs
tests/public_exports_w4.rs      (new file; tests/public_exports.rs untouched)
```

Each `mod.rs` contains only the module documentation, the type declarations
listed in the ownership tables above (public types from the public tables,
plus the crate-private `FamilyRecordInput` in `src/backup/mod.rs` for
W4-CA-5), and named validated constructors with
`todo!("w4 implementation packet")` bodies where a body is required for
compilation. `src/lib.rs` gains exactly these additions:

```rust
pub mod streaming;
pub mod replication;
pub mod backup;
pub mod migration;

pub use backup::{
    BackupArchive, BackupError, BackupKind, BackupManifest, BackupPlan,
    BackupProgress, BackupResult, RestorePlan, RestoreResult,
};
pub use migration::{
    MigrationError, MigrationLossMatrix, MigrationManifest, MigrationOptions,
    MigrationRecord, MigrationReport,
};
pub use replication::{
    AcknowledgementLevel, AcknowledgementReport, ConflictRecord, Convergence,
    MemberId, RegionId, ReplicationConfig, ReplicationCursor, ReplicationError,
    ReplicationOutcome, ReplicationReceipt, ReplicationStatus, TopologyView,
};
pub use streaming::{
    ChangeEvent, ChangeFilter, ChangeKind, DocumentStreamSelector, StreamBatch,
    StreamCompletion, StreamCursor, StreamError, StreamItem, StreamPosition,
    StreamSource, StreamStore, SubscriptionAcknowledgement, SubscriptionBatch,
    SubscriptionCheckpoint, SubscriptionSpec, SubscriptionStatus,
};
```

The root never re-exports a transport, codec, leader-state, archive-internals,
or anti-entropy type. `Engine::streams()` (proposed collision action W4-CA-1
below) is the only production acquisition path for `StreamStore`.

The declaration-only packet starts red with these four public-surface tests in
`tests/public_exports_w4.rs`:

- `w4_public_exports_compile_with_declaration_only_tree`;
- `streaming_store_borrows_the_engine_and_prevents_shutdown_while_live`;
- `commit_receipt_stays_local_only_in_the_w4_surface`;
- `w4_roots_reexport_no_transport_codec_or_leader_state`.

`StreamStore<'engine>` is the only engine-borrowing handle type W4-0 declares:
replication and backup operations acquire the Engine per operation inside
their behavior packets, so no `ReplicationStore`/`BackupStore` handle is
declared here. The borrow test is scoped to the declared surface
accordingly; it makes no claim about undeclared handle types.

These tests inspect only the compile/export and lifetime surface. They do not
claim that any streaming, replication, backup, or migration behavior exists.

### Integrator collision actions required before W4 behavior packets

`src/lib.rs`, `src/types.rs`, `src/operations.rs`, `src/engine/**`,
`src/storage/**`, `src/families/**`, `tests/public_exports.rs`, Cargo files,
CI, generated parity output, and overlays are frozen for W4 builders. The
following collision actions are proposed for parent assignment; a W4 builder
cannot edit any of these files.

| ID | File | Exact proposed change | Why a builder cannot do it |
| --- | --- | --- | --- |
| W4-CA-1 | `src/lib.rs` | Add the four `pub mod` roots and the export block above. | Frozen for behavior packets; only the parent-dispatched W4-0 preparation (or the integrator) may edit it. |
| W4-CA-2 | `src/engine/mod.rs` | Add `pub fn streams(&self) -> StreamStore<'_>` returning the borrowed store (mirrors `Engine::queries`/`Engine::families`). | `src/engine/**` is outside every W4 packet's write scope. |
| W4-CA-3 | `src/engine/mod.rs` (and `src/storage/mod.rs` only if the event type moves) | Replace the single-slot, `#[cfg(test)]` `set_publication_sink` with a production-visible, multi-subscriber registry: `pub(crate) fn subscribe_publications(&self, sink: CommitPublicationSink) -> SubscriptionGuard` where the guard detaches on drop. The existing index sink and the streaming subscription both register. Sinks are non-blocking and bounded: the emit call runs synchronously on the committing thread, so a sink must not block, must not take a storage lock, and must not grow an unbounded queue — a slow or overflowing sink is dropped or reported through its own bounded channel, never allowed to delay or block the publication path. | Streaming needs `CommitPublicationEvent` deltas in production; the current seam is test-only and single-slot, and `src/engine/**` is frozen to W4 builders. Absorbs the queued W3-2 integrator catalog action (see the warning below). |
| W4-CA-4 | `src/storage/mod.rs`, `src/storage/file.rs`, `src/storage/ledger.rs`, `src/engine/mod.rs` | Extend the crate-private `StoragePort` with a durable publication-history read for replication catch-up and backup boundaries. The read carries the logical write set itself, not a digest: `fn commits_after(&self, after: StorageSequence, limit: u32) -> Vec<PublishedCommit>` in global storage-sequence order across tenants, with `PublishedCommit { tenant: TenantId, operation_id: OperationId, sequence: StorageSequence, writes: Vec<PublishedWrite> }` and `enum PublishedWrite { Put { document: Document }, Delete { id: DocumentId, version_at_delete: u64 } }` — deletes must be reconstructable, and `read_document_at` cannot distinguish a delete from a never-existed id, so ids/digests alone are insufficient. A tenant enumeration `fn committed_tenants(&self) -> Vec<TenantId>` and a tenant-to-sequence mapping entry `fn tenant_sequence(&self, tenant: &TenantId, at: StorageSequence) -> Option<StorageSequence>` complete the seam, because `ReplicationCursor` is a global `StorageSequence` while tenant data is per-tenant. | Replication resume and backup boundaries need ordered, reconstructable committed history; `src/storage/**` and `src/engine/**` are frozen to W4 builders. Verification obligations for the integrator are recorded below the table. |
| W4-CA-5 | `src/families/mod.rs` | Re-export a typed family-record enumeration for backup and migration: `pub(crate) fn snapshot_family_records(scope: &TenantScope, snapshot: &ReadSnapshot) -> Result<Vec<crate::backup::FamilyRecordInput>, FamilyError>` implemented in `src/families/` against the reserved collection `terrabase.families`. `FamilyRecordInput` is declared crate-private in `src/backup/mod.rs` by W4-0 (see the crate-private table). | `src/families/**` is outside W4-BACKUP's write scope; the reserved collection layout is family-private. |
| W4-CA-6 | `tests/public_exports_w4.rs` (new) | Created by W4-0 as its own file so the shared `tests/public_exports.rs` stays untouched. | The shared export test is integrator-owned after W3-0. |
| W4-CA-7 | `src/engine/mod.rs`, `src/storage/mod.rs`, `src/storage/file.rs` | Add a bounded windowed snapshot read so streaming batches never materialize a whole tenant: `pub(crate) fn snapshot_documents_from(&self, scope: &TenantScope, snapshot: &ReadSnapshot, after: Option<DocumentId>, limit: u32) -> Result<Vec<Document>, EngineError>` on `Engine`, backed by a StoragePort windowed read `fn documents_at_from(&self, sequence: StorageSequence, tenant: &TenantId, after: Option<&DocumentId>, limit: u32) -> Vec<Document>` in canonical key order after `after`. Cursor binding needs no new seam: `StreamStore<'engine>` borrows the Engine, and `Engine::identity()` / `ReadSnapshot::engine_identity()` are already `pub(crate)`. | `src/engine/**` and `src/storage/**` are frozen to W4 builders; the only existing read (`snapshot_documents`) materializes every tenant document per call. |

W4-CA-3 warning (label and scope). The charter's ledger records a queued
W3-2 integrator action for an engine-owned index catalog; `IndexCatalog::sink`
is currently dead code in production pending that action. W4-CA-3 supersedes
and absorbs it: wiring the publication sink makes index maintenance
production-live, which is a change beyond the streaming scope, so the parent
must execute the combined action (registry plus index-catalog wiring) as one
named integrator action rather than two. The W4-CA-n labels exist to
disambiguate these from the W3-2 collision actions recorded in the charter
ledger.

W4-CA-4 verification obligations (inspector facts; the integrator must verify
each before executing the action):

1. The W3 ledger records no `document_ids` — a ledger-derived read cannot
   reconstruct the write set from `ReadyToPublish`/`Published` records alone;
   recoverability must come from the WAL/root chain (or a ledger schema
   extension, which is a larger collision action).
2. The ledger index is keyed `(tenant, operation)` and iterated in canonical
   key order, not storage-sequence order; a sequence-ordered
   `commits_after` needs a different iteration path or a secondary index.
3. Torn-tail truncation at open and W3-permitted ledger compaction bound the
   servable history window; `commits_after` must report the window's start
   (the oldest servable sequence) so replication and backup can detect a
   cursor older than servable history and fall back to a full catch-up or
   backup rather than silently skipping a gap.

Fault injection for the W4 fault lanes needs no `src/storage/**` change: the
three-region lane controls members and links at the replication level, and
member restart reuses the existing Engine reopen path. Storage-level
`FaultPoint` arming stays `cfg(test)` inside the storage crate.

## Streaming contract

### Sources and ordering

`StreamStore<'engine>` is obtained only from a durable Engine through
`Engine::streams()` (W4-CA-2). Its one batch contract is
`next_batch(cursor, bound) -> StreamBatch`: each batch is bounded by the
caller's item limit, ends with the next `StreamCursor`, and reports a terminal
`StreamCompletion` exactly once at the end of the stream.

Ordering is per source and is part of the public contract:

- Document streams (`PB-6007`): canonical key order within a collection; id,
  prefix, match, and exclusion selectors are validated before the first batch.
  Missing documents, invalid selectors, and malformed continuation cursors are
  typed `StreamError` values, never silently skipped.
- Query streams (`PB-6008`): the compiled `QueryPlan`'s ordering; the batch
  reports the `IndexGeneration` it used and honors the plan's staleness
  policy.
- Time-series streams (`PB-6009`): timestamp order within the requested range,
  preserving tag and value shape, rollups, deleted and empty ranges.
- Family streams (`PB-3002`, `PB-3014`): the family seam's typed bounded
  readers, wrapped so the same cursor/backpressure rules apply.
- Change and subscription streams (`PB-6002`..`PB-6006`): strictly by
  `StorageSequence`; no event is delivered before its commit is published.

### The never-committed read rule

Streams read only two things: immutable `ReadSnapshot` state and
already-published `CommitPublicationEvent` facts (via W4-CA-3). A live
`Transaction`'s staged writes, an in-flight commit between marker and pointer
publication, and any rejected or `Unknown` operation are never observable
through any stream. This is the streaming restatement of W3's
"publication visibility" rule and is red-tested at the seam.

### Production read path

Snapshot-bound stream sources (documents, queries, time-series, families) read
through `Engine::read_snapshot` and then the bounded windowed read of W4-CA-7
(`snapshot_documents_from`), which serves one bounded batch per call in
canonical key order. Streams do not route through the existing
`snapshot_documents` engine helper: it materializes every document of the
tenant per call, so its cost is O(tenant documents) per batch — a stated limit
that would make the bounded-memory red test
(`stream_memory_stays_bounded_when_the_consumer_never_drains`) dishonest for
large tenants. W4-CA-7 is therefore a hard prerequisite for that test: until
it executes, the test stays red, and no streaming packet may quiet it by
shrinking the fixture.

Change and subscription streams read only already-published
`CommitPublicationEvent` facts via the W4-CA-3 registry and the bounded
buffers described next.

### Backpressure, cancellation, and bounded memory

- Every buffer in the streaming path is bounded by configuration. When a
  consumer is slower than production, the stream reports
  `StreamCompletion::SlowConsumer` (or `StreamError::SlowConsumer` on a
  blocking call) instead of growing without bound (PB-6000 risk).
- Streaming never blocks an Engine commit. Publication events are consumed
  after storage locks release (W3 order step 10); a slow or stuck subscriber
  can slow itself, never the writer.
- Cancellation closes the stream promptly, releases its buffers, and reports
  `Cancelled`. The W3-3 handoff's cancellation-liveness finding is carried
  here: a cancelled stream must not hold a lock, a buffer, or a background
  reservation indefinitely.
- Memory bounds are proven at the public seam: a bounded number of items is
  observable per batch under a deliberately slow consumer.

### Checkpoint, acknowledgement, and resend (subscriptions)

Mirroring PB-6003/6004/6005 and the charter's subscription rows:

1. A subscription opens with a `SubscriptionSpec` and enters a negotiated
   `SubscriptionStatus`. Batches are bounded by the spec's max items.
2. The server records the batch boundary before waiting for the client's
   `SubscriptionAcknowledgement`; durable subscription state advances only at
   the acknowledgement seam.
3. Delivery is at-least-once. After reconnect or resend, items may be
   redelivered; every item carries stable identity (`OperationId` plus
   `StorageSequence`) so duplicates are identifiable by the consumer.
4. Unacknowledged items enter resend state; a reconnect never skips them and
   never advances the checkpoint without the acknowledgement.
5. Acknowledge failures, changed subscription state, and concurrent updates
   return typed errors without silently advancing the checkpoint.
6. Failover responsibility (a second worker, a dropped member) preserves the
   durable checkpoint; exactly one active consumer owns a subscription at a
   time. Cross-member subscription failover qualification belongs to
   W4-REPLICATION's lane (PB-6033, PB-6005).

Filters (PB-6006) are typed `ChangeFilter` values in W4. Filtered or skipped
items do not silently advance a checkpoint: a filter drop is reported in the
batch's progress facts. Projection and script criteria beyond typed filters
are bound by decision D-5 and are not implemented in W4-STREAM without parent
adjudication.

### W4-STREAM red tests

Each starts red at the public `StreamStore` seam:

- `engine_streams_returns_store_bound_to_durable_engine`;
- `stream_store_borrow_prevents_engine_shutdown_while_live`;
- `document_stream_preserves_key_order_and_result_shape`;
- `document_stream_selectors_support_id_prefix_match_and_exclusion`;
- `document_stream_missing_documents_and_invalid_selectors_return_typed_errors`;
- `query_stream_reports_the_index_generation_it_used`;
- `time_series_stream_preserves_timestamp_tag_and_value_order`;
- `family_stream_reuses_the_family_seams_bounded_readers`;
- `stream_cursors_resume_exactly_where_the_previous_batch_ended`;
- `stream_batches_honor_the_requested_bound_under_a_slow_consumer`;
- `stream_memory_stays_bounded_when_the_consumer_never_drains`;
- `stream_backpressure_never_blocks_an_engine_commit`;
- `stream_cancellation_closes_promptly_without_leaking_a_lock_or_buffer`;
- `changes_subscription_observes_only_committed_publication_events`;
- `changes_events_carry_operation_identity_and_storage_sequence_in_order`;
- `changes_never_expose_an_uncommitted_or_rejected_transaction`;
- `subscription_batches_are_bounded_and_acknowledged_at_the_checkpoint_seam`;
- `subscription_checkpoint_does_not_advance_before_acknowledgement`;
- `unacknowledged_subscription_items_are_resent_after_reconnect`;
- `duplicate_subscription_delivery_is_identifiable_and_at_least_once`;
- `subscription_reconnect_preserves_the_durable_checkpoint`;
- `subscription_close_reports_an_explicit_terminal_status`;
- `stream_sources_reject_a_snapshot_from_another_engine_or_tenant`;
- `stream_filters_report_drops_without_advancing_the_checkpoint`.

These tests observe ordering, bounded memory, cancellation liveness,
checkpoint durability, and duplicate identity at the public seam. They make no
V1 parity claim and create no receipt.

## Replication and multi-region contract

This section records the parent-gated decisions first. Everything after them
is contract text conditioned on the adjudicated outcomes; where a fact depends
on a decision, the decision ID is named. No recommendation below is an
accepted decision.

### Decision D-1: replication algorithm (parent adjudication required)

Evidence: the pinned V1 behavior is a two-layer design — a Raft-style
consensus (Rachis) owns cluster topology and database records
(PB-5016..PB-5020), while document data replicates asynchronously between
members with change vectors, conflict records, and opt-in write assurance
(PB-5000, PB-5021..PB-5026). W3's accepted storage is a single-writer,
immutable-root engine: one Engine per location, one publication order, a
durable operation ledger, and `CommitPublicationEvent` after lock release.

| Option | How it works | Evidence for | Evidence against |
| --- | --- | --- | --- |
| A. Single leader per data range with cross-Region quorum commit | One Member owns each range's write path; commits acknowledge once a quorum of Regions durably records the operation; followers catch up asynchronously via the publication history (W4-CA-4) | Maps directly onto W3's single-writer publication order and operation ledger; keeps `AcknowledgementLevel` meaningful (the quorum is observable); follower catch-up is the existing anti-entropy direction; failover is an explicit, testable transition | Leader failover needs a fencing story (PB-6033 old-owner fencing); write affinity to one Region per range |
| B. Leaderless quorum writes | Any Member accepts a write; a quorum of Regions stores it; read repair and vector clocks reconcile | No failover window for writes; matches some Dynamo-style deployments | Every write path becomes a conflict path; contradicts W3's single-writer storage design; `AcknowledgementLevel` semantics blur with read repair; far larger red-test surface before any claim |
| C. Full consensus protocol for the data plane (Raft-style log per range) | One replicated log per range; commit = log commit | Strongest ordering; V1's own cluster layer is Raft-style, so the shape is familiar | Reimplements W3's ledger+publication at a second layer; highest complexity and test cost; V1 itself does not run document data through Rachis |
| D. V1-style asynchronous multi-leader with change vectors only | Every member accepts writes; replication converges in the background | Closest to observed V1 document behavior; V1's post-commit waits (PB-5026) are level-shaped observations atop this model | No commit-time acknowledgement exists to observe — V1's waits happen after the local commit; no single agreed order per document; no bounded loss window for a lost Region — so `RegionQuorum`-class commit-time levels and the charter's region-loss-after-acknowledgement scenario cannot be offered honestly; PB-5042 requires observable levels |

Recommendation (proposal only): Option A — single leader per data range with
quorum acknowledgement across Regions and asynchronous follower catch-up —
because it is the only option that both preserves W3's accepted publication
order unchanged and can support the charter's full Acknowledgement Level
lattice and region-loss scenarios honestly. Option C remains the recorded
fallback if the fault lane shows A's failover fencing cannot be made safe.
Parent adjudication required; PB-5000, PB-5016..PB-5020 stay unresolved until
then.

Fencing (D-1 parameter, parent adjudication required). Option A names fencing
as its open cost. The candidate mechanisms are:

- epoch/term checked at follower apply — every follower rejects a replication
  batch whose epoch is below the highest it has seen (the shape of V1's
  monotonic terms, PB-5017);
- admission-time topology-ownership validation — a Member accepts a write only
  while its current `TopologyView` names it owner of the target range, so a
  deposed leader's new writes fail admission locally without any cross-Member
  round trip;
- conflict-policy disposition — no hard fence; a deposed leader's writes are
  ordinary concurrent writes resolved by D-3 after healing.

Recommendation (proposal only): admission-time ownership validation plus
apply-time epoch checks. Admission fencing stops new writes immediately and
locally; apply-time epoch checks catch in-flight batches a follower might
otherwise accept. Conflict-policy disposition alone is rejected as a proposal
because it lets a deposed leader keep minting writes that will all become
conflicts.

Old-leader writes after deposition (D-1 × D-3, parent adjudication
required). A deposed leader may have committed writes locally after losing
leadership but before observing the deposition. Their disposition must be
explicit: fenced-and-discarded (they never existed outside the deposed
Member) or retained and surfaced as `ConflictRecord` history under D-3 after
healing. Discarding silently contradicts the W3 local-receipt contract — a
`Local`-level commit was honestly reported to its caller; retaining makes
every post-deposition local write a potential conflict. Recommendation
(proposal only): retain as content-free conflict history. The choice is
recorded here because it changes what
`isolated_concurrent_writes_converge_deterministically_after_healing` must
assert under D-3.

Option C fallback trigger (proposal only). Option C is escalated at the
W4-REPLICATION gate — not before — if the lane tests
`old_owner_is_fenced_after_leader_transition` and its fault variants (leader
loss, partition healing, duplicate delivery of a deposed leader's batch)
cannot pass with the adjudicated fencing mechanism. That gate reports the
failing mechanism and its evidence; the parent then re-adjudicates D-1 toward
Option C before the packet may claim any PB-5016..PB-5020 progress.

### Decision D-2: consistency and acknowledgement defaults (parent adjudication required)

Proposed lattice (names follow the CONTEXT glossary; the exact set and
defaults are parent-gated):

| Level | Meaning when reported | Commit-time cost |
| --- | --- | --- |
| `Local` | The local storage publication rule completed (the W3 `CommitReceipt` fact); no remote member confirmed anything | Local only |
| `RegionQuorum` | A quorum of Members in the write's home Region durably recorded the operation | One Region's round trips |
| `MajorityRegions` | A majority of Regions each durably recorded the operation through a Region quorum | Cross-Region round trips |
| `AllRegions` | Every Region in the `TopologyView` durably recorded the operation | All Regions; least available |

Evidence: V1's default is a local save with replication running
asynchronously; `WaitForReplicationAfterSaveChanges`/`WaitForReplicas`/
`Majority` are explicit opt-ins reported separately from the mutation result
(PB-5026, PB-5000). Recommendation (proposal only): default `Local` for V1
parity, all stronger levels explicit per operation, timeout reported as
`ReplicationOutcome::Timeout` distinct from any local commit failure. Parent
adjudication required; PB-5026, PB-5042, PB-5043 stay unresolved until then.

Contributing threshold (D-2 parameter, parent adjudication required): a
Member contributes to any level above `Local` only after its own local
publication of the operation reaches `LocalDurability::Synced`. W3's
loss-window table makes `Synced` the only level with a durable-recoverability
claim — `Written` and `Buffered` may lose bytes after a fault, so counting
them would let an acknowledgement overstate durability. Recommendation
(proposal only): `Synced`; the exact threshold is parent-gated with the rest
of D-2.

Timeout partial-ack observation (D-2 parameter): `ReplicationOutcome::Timeout`
carries the partial-ack `AcknowledgementReport` naming which Members had
durably recorded the operation when the deadline hit, so a timeout is an
honest partial fact rather than an unknown. Whether the partial observation
is also retained for `Failed` outcomes is part of the same parent-gated D-2
parameter set.

Per-level visibility rules (conditioned on D-2): a read observes a replicated
operation only after the observing Member's storage published it; no
`AcknowledgementLevel` implies read-your-writes at another Region unless that
Region is named in the report. `Convergence` is reported as its own
observation (`Convergence` type), never inferred from an acknowledgement.

### Decision D-3: conflict policy (parent adjudication required)

| Option | How conflicts resolve | Evidence for | Evidence against |
| --- | --- | --- | --- |
| Last-writer-wins with bounded clocks | A hybrid logical clock (or equivalent bounded ordering) picks one deterministic winner; losers are observable as `ConflictRecord` history | Matches V1's observed deterministic outcome for disconnected writers (PB-5034: three separated writers converge, one survives); simple, testable | Clock skew bounds must be stated; silently discards a loser's content unless history is kept |
| First-committer-wins | First accepted write wins; later concurrent writes fail with a typed conflict error | No silent data loss | Diverges from V1's observed automatic convergence; turns partitions into visible failures |
| Deterministic merge | A typed, per-family merge function combines concurrent writes | Preserves both sides for families like counters (which V1 merges by construction, PB-5029) | No general merge exists for documents; needs per-type policy and much larger test surface |
| Application-resolved with tombstones | Conflicts park as conflict records; the application resolves; tombstones prevent stale resurrection | Most auditable; V1 exposes conflict reads/writes returning a conflict error (PB-5022) | Default convergence disappears; conflicts block reads until resolved |

Recommendation (proposal only): last-writer-wins by bounded hybrid logical
clock for documents, with an observable, content-free `ConflictRecord`
history, per-family deterministic merge where the family already defines one
(counters, time-series), and an application resolution path as a later
addition — the closest honest match to the pinned V1 observations. Parent
adjudication required; PB-5022, PB-5034, PB-5045 stay unresolved until then.

Two bounded-clock parameters are part of D-3 and parent-gated (proposal only;
no numeric value is selected in this document):

- the skew bound B and its derivation. B must be derived from the deployment's
  provisioned clock discipline (for example an NTP or PTP tolerance) plus a
  stated safety margin; a raw number without that derivation is not a
  decision. The charter's cross-Region timing bounds (D-4) bound the same
  envelope and must be decided together with B.
- the behavior when a peer's timestamp exceeds the local clock by more than B.
  The options are: reject the write with a typed error; clamp the observation
  to local-clock + B; or classify the pair as a conflict and route it through
  the conflict policy. Recommendation (proposal only): classify as a
  conflict, because clamping silently reorders causality and rejecting turns
  a clock fault into a data outage; the conflict path already has an
  observable, auditable outcome.

### Decision D-4: production topology limits (parent adjudication required)

The qualification lane below uses three synthetic Regions. Production limits —
maximum Regions and Members per Region, placement and failure-domain
assumptions, cross-Region timing bounds, and whether `AllRegions` is offered —
are parent-gated (charter risk ledger). No W4 artifact may state a production
topology limit. PB-5044 and PB-5048 stay unresolved.

The D-2 lattice degenerates at small Region counts, and D-4 must fix the
canonical reading (parent-gated): with one Region, `RegionQuorum`,
`MajorityRegions`, and `AllRegions` coincide; with two Regions,
`MajorityRegions` equals `AllRegions`. The parent should state whether a
degenerate request is normalized to one canonical level name, reported under
the requested name with a note, or rejected as an invalid request for that
topology. Recommendation (proposal only): normalize to the weakest distinct
name the topology actually distinguishes and record the normalization in the
`AcknowledgementReport`, so reports stay comparable across topologies.

### Decisions D-5..D-7 (restated from the risk ledger)

- D-5 transfer transform scripts: V1 smuggler and subscription scripts
  (PB-6020, PB-6006) are untrusted input. W4 implements typed filters only; a
  script execution mechanism (sandbox, step budget, cancellation) needs a
  parent decision. No script runs in W4 packets. The two rows are partially
  marked: their typed portions proceed in W4-STREAM/W4-BACKUP.
- D-6 optional and legacy transfer dispositions: ETL, queue sink, and
  relational migration provider matrices (PB-6023..PB-6030) need explicit
  evidence-backed disposition; they are mapped to later waves, not silently
  dropped.
- D-7 sharding scope: V2 sharding (PB-5036..PB-5041, PB-5048, PB-6034) is
  outside W4; its placement and orchestration decisions remain open.

### Membership, failure domains, and the engine/control-plane split

`TopologyView` is an input: the Platform/Orchestrator control plane owns
provisioning, placement, routing, lifecycle, membership policy, and
control-plane replication of topology itself. The Engine's replication module
owns the data plane: applying a `TopologyView`, replicating committed
operations between Members, aggregating `AcknowledgementReport` facts,
reporting `Convergence` and `ReplicationStatus`, exposing `ConflictRecord`
and `ReplicationCursor` facts, and producing `ReplicationReceipt` artifacts in
the qualification lane. The Engine never provisions, authenticates, or routes;
the control plane never writes data. A `RegionId`/`MemberId` set is data-plane
identity, not authorization — tenant isolation is enforced at every public
seam exactly as in W3.

A `Failure Domain` groups Members whose correlated loss is one failure. The
lane below treats each synthetic Region as one failure domain plus one
intra-region member fault domain.

### Anti-entropy, replay, rejoin, and duplicates

- Replication delivery is at-least-once with stable operation identity;
  applying a duplicate is idempotent at the storage seam (W3's
  `OperationId`/ledger contract makes replay resolution deterministic).
- A lagging or restarted Member resumes from its durable `ReplicationCursor`
  (backed by W4-CA-4's publication history), validates storage identity and
  format before rejoining, catches up, and does not duplicate committed
  mutations (PB-5033, PB-5047).
- Anti-entropy repair is a background convergence mechanism observable only
  through `ReplicationStatus` and `Convergence` reports; it never blocks a
  local commit.
- A rejoining Member becomes eligible for reads and acknowledgement
  contribution only after its manifest/version and cursor validation pass
  (PB-5047). A stale or incompatible member is reported, not admitted.

### Three-Region synthetic fault lane

The charter's multi-region qualification matrix runs in a synthetic,
in-process, deterministic lane owned by W4-REPLICATION (with W4-BACKUP's
restore scenarios sharing the exclusive lane under parent scheduling):

- Topology: three Regions `r-alpha`, `r-beta`, `r-gamma`; two Members per
  Region; each Region is one failure domain; every Member is a real durable
  Engine in a temporary directory. Links are controlled in-process doubles
  that can drop, delay, duplicate, reorder, or fully partition traffic per
  direction.
- Fault classes exercised: link delay and reorder; link drop and duplicate;
  full Region partition with isolated concurrent writes; Member restart
  (process exit and reopen); leader loss (under D-1 A/C); entire Region loss
  after an acknowledged write; healing after partition; replay of duplicated
  delivery; rejoin of a restarted Member.
- Charter matrix mapping (every row is a red test or a lane scenario below):
  one Region/one Member; one Region/multiple Members under delay; two
  Regions/healthy links; member loss before acknowledgement; Region loss
  after acknowledged write; delayed and reordered replication; member rejoin;
  tenant-isolation probe.
- Measured definitions (honest measurement, no numbers until measured):
  - RPO: the set of operations acknowledged at level L but absent after the
    recorded fault, reported as a count and a storage-sequence window — never
    a time guess.
  - RTO: the interval from fault injection to the first post-fault successful
    operation satisfying the contract's availability condition for the
    surviving topology, measured in lane time and stated with its clock.
  - Convergence: the interval from the last healing event to the first
    `Convergence` observation naming every surviving Member, plus the
    condition checked.
  - Replication lag: the storage-sequence distance between a Member's
    `ReplicationCursor` and the source's published sequence at observation
    time — a sequence distance, not a latency claim.
- Every lane run produces a secret-safe `ReplicationReceipt` (or
  `RecoveryReceipt` for rejoin/restore scenarios) naming scenario identity,
  topology, faults, acknowledgement facts, convergence outcome, measured
  RPO/RTO/convergence/lag with their definitions, and explicit limits. Failed
  or invalid runs are retained with their reason and support no claim.

### W4-REPLICATION red tests

Each starts red at the public replication seam (scenario tests run in the
exclusive lane; rows marked D-x are blocked until that decision is
adjudicated — the marker names every decision whose adjudication the test's
assertions need):

- `local_commit_receipt_contains_no_region_or_member_facts`;
- `replication_request_names_an_explicit_acknowledgement_level` (D-2);
- `acknowledgement_report_names_the_regions_and_members_that_contributed` (D-2);
- `acknowledgement_timeout_is_distinct_from_local_commit_failure` (D-2);
- `single_region_single_member_write_reads_back_and_survives_restart`;
- `one_region_multiple_members_reach_region_quorum_under_member_delay` (D-2);
- `two_regions_healthy_links_achieve_cross_region_acknowledgement_and_read_visibility` (D-2);
- `member_loss_before_acknowledgement_returns_a_typed_error_not_silent_success`;
- `region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit` (D-2, D-4);
- `delayed_and_reordered_replication_replays_idempotently_to_the_contract_outcome`;
- `duplicate_delivery_applies_each_operation_once`;
- `member_rejoin_checks_identity_and_catches_up_without_duplicates`;
- `restarted_member_resumes_from_its_durable_replication_cursor`;
- `anti_entropy_repairs_a_lagging_member_after_a_gap`;
- `convergence_report_names_members_operation_identity_and_observation_time` (D-2);
- `replication_status_reports_health_separately_from_convergence`;
- `isolated_concurrent_writes_converge_deterministically_after_healing` (D-3);
- `conflicted_writes_produce_the_recorded_policy_outcome_and_a_content_free_conflict_record` (D-3);
- `leader_loss_stops_new_commits_until_a_new_leader_is_confirmed` (D-1);
- `old_owner_is_fenced_after_leader_transition` (D-1);
- `tenant_isolation_probe_denies_cross_tenant_replication_and_is_observable`;
- `replication_receipt_is_secret_safe_and_names_scenario_faults_and_limits`;
- `three_region_fault_lane_reports_measured_rpo_rto_convergence_and_lag`.

The last test is the lane harness asserting the receipt shape and the
measurement definitions; it reports measured values as artifacts, never as
claims beyond the lane.

## Backup, import, and logical migration contract

### Backup manifests and archives

- A `BackupPlan` fixes a source boundary (a `ReadSnapshot`'s storage
  sequence), a `BackupKind`, item selection, and a throttle bound. Execution
  produces ordered `BackupArchive` members and one `BackupManifest` binding
  the immutable recovery-point identity, archive names, lengths, and SHA-256
  digests (PB-6010, PB-6015, PB-5013..PB-5015).
- An incremental backup names its base recovery point; its manifest records
  the journal-range boundary it covers. A restore consumes the complete
  ordered chain and rejects a missing, reordered, truncated, or corrupt member
  before materializing anything (PB-6010, PB-5014).
- Manifest and archive-name validation rejects path traversal, in-progress
  manifests, and identity mismatches before any target is created (PB-6015).
- Snapshot-mode backup and cloud/FTP destinations are later dispositions; W4
  implements the local destination and the typed destination seam only
  (PB-6012 mapping note in the row mapping).
- Backup admission allows one job at a time per source; a concurrent attempt
  is a typed `BackupError`. Throttling (`MaxReadOpsPerSecond`-equivalent)
  limits source reads without changing archive order or dropping records
  (PB-6017). Retries at archive and manifest seams are idempotent; an
  uncertain result cannot mint two recovery-point identities.

### Restore with clean-restore verification

- A `RestorePlan` names a manifest identity and a clean target location;
  restore refuses a non-empty or unrelated target (PB-6016).
- Execution verifies every digest and length, materializes the target,
  reopens it as a real durable Engine, and verifies counts and identity by
  reading back representative and boundary values — a clean-restore
  verification, not a trust-me copy.
- Cancellation leaves either a recoverable partial target clearly marked
  unusable or an explicit failure; it never leaves a target that opens as a
  valid but incomplete database.
- Every restore run in the fault lane produces a `RecoveryReceipt` with exact
  source identity, format version, manifest identity, fault or interruption
  class, restored/missing values, duplicate handling, checksum verdicts, and
  limits.

### Logical migration from a V1 export

The charter's Logical Migration rule governs: input and output are checked
against a manifest; the program never claims V2 opens V1 files directly.

- A `MigrationManifest` binds the V1-export identity: format version, record
  counts per item type, and per-segment digests. It is validated before any
  write (PB-6022).
- Migration reads logical `MigrationRecord` values (documents, revisions,
  tombstones, attachments, counters, time-series values and deleted ranges,
  identities, compare-exchange — per PB-6019 selection), transforms them under
  an explicit contract, and writes V2 data exclusively through the Engine
  transaction seam into the V2 Storage Format. Migration never writes storage
  files directly.
- Restartable and idempotent: each family of records tracks its last durable
  position (PB-6021). An interrupted run resumes from that boundary; running
  the same input against the same target state produces the same result —
  repeated records are applied idempotently by `OperationId`, and the report
  states the duplicate policy used.
- Verification: converted counts and checksums are verified against the
  manifest before success is reported; a truncated or corrupt input record is
  a typed `MigrationError`, never a skipped item.
- Success reports the explicit `MigrationLossMatrix`; success never means
  "everything in V1 arrived".

### Loss matrix

| Category | Disposition | Reason |
| --- | --- | --- |
| Documents and revisions | Convert through the Engine seam | Logical record with a V2 contract |
| Referenced attachments (metadata and content) | Convert through the family seam: attachment metadata plus content bytes via the family bounded reader, written through the Engine transaction seam | A referenced attachment is a Data Family record with an explicit contract; PB-6019 requires an explicit per-type disposition. If the W4-BACKUP packet proves content transport exceeds its scope, this category is recorded as deferred with a count and reason in `MigrationReport` — never silently dropped |
| Tombstones and deleted ranges | Convert where a consumer needs them; otherwise record as dropped with counts | Deletion evidence matters only for consumers that can still act on it; retention policy is part of `MigrationOptions` |
| Counters and time-series values | Convert (family records in the reserved collection) | Data Family records with explicit contracts |
| Identities and compare-exchange values | Convert | Logical state with observable contracts |
| V1 storage-internal structures (journals, page layout, headers, scratch, compaction state) | Drop | Physical V1 layout is not a V2 Storage Format concept; V2 writes its own format |
| Unreferenced blobs/orphaned attachments | Drop, counted | No logical referent; V1 evidence does not guarantee referents |
| Platform-owned configuration, connection strings, credentials, ongoing-task definitions | Drop, recorded as Platform-owned | Control-plane records are Platform scope (PB-6031/PB-6032/W5); the engine does not import authorization or secrets |
| Index definitions | Re-derive from documents or record as dropped, per `MigrationOptions` | V2 indexes publish from storage publication; V1 index binaries are not portable |

Every dropped category appears in `MigrationReport` with a count and its
reason. Nothing is dropped silently (PB-6021, PB-6018).

### Secret-safe evidence rules

Backup contents, migration inputs, credentials, connection strings, customer
data, and private host paths never enter any receipt, test output, or
documentation artifact. Receipts carry identities, counts, digests, and
verdicts only. The existing secret-scan gate covers W4 evidence paths.

### W4-BACKUP red tests

Each starts red at the public backup/migration seam:

- `full_backup_writes_a_manifest_with_ordered_digests_and_counts`;
- `backup_restore_into_a_clean_target_reopens_and_reads_back_boundary_values`;
- `restore_rejects_a_tampered_checksum_before_materializing_a_target`;
- `restore_rejects_a_missing_or_reordered_incremental_chain_member`;
- `restore_refuses_a_non_empty_or_unrelated_target`;
- `backup_includes_family_records_from_the_reserved_collection`;
- `backup_progress_reports_skipped_items_without_hiding_loss`;
- `backup_cancellation_leaves_no_usable_partial_recovery_point`;
- `backup_admits_one_job_and_rejects_a_concurrent_attempt_with_a_typed_error`;
- `backup_throttling_limits_reads_without_reordering_or_dropping_records`;
- `manifest_validation_rejects_path_traversal_and_in_progress_manifests`;
- `migration_validates_the_v1_export_manifest_before_any_write`;
- `migration_writes_only_through_the_engine_transaction_seam`;
- `migration_is_idempotent_for_the_same_input_and_target_state`;
- `interrupted_migration_resumes_from_its_last_durable_record`;
- `migration_reports_an_explicit_loss_matrix_with_reasons_and_counts`;
- `migration_verifies_counts_and_checksums_before_reporting_success`;
- `migration_rejects_a_truncated_or_corrupt_input_record`;
- `migration_rejects_an_unsupported_manifest_version`;
- `backup_and_restore_preserve_tenant_scopes_and_reject_cross_tenant_targets`;
- `recovery_receipt_records_source_identity_faults_and_limits_without_secrets`;
- `logical_migration_makes_no_direct_v1_file_open_claim`.

## Row mapping

All 86 W4-facing rows — 37 transfer rows (PB-6000..PB-6036) and 49
replication-storage rows (PB-5000..PB-5048) — are mapped to a first owner in
the [W4 row mapping](../parity/w4-row-mapping.md). Summary:

| Owner | Rows | Count |
| --- | --- | --- |
| W4-STREAM | PB-6000, PB-6002..PB-6009 | 9 |
| W4-REPLICATION | PB-5000, PB-5016..PB-5035, PB-5042..PB-5047, PB-6033 | 28 |
| W4-BACKUP | PB-6001, PB-6010..PB-6022, PB-5013..PB-5015 | 17 |
| W5 Platform Adapter | PB-6031, PB-6032, PB-6035, PB-6036 | 4 |
| Later waves | PB-5001..PB-5012, PB-5036..PB-5041, PB-5048, PB-6023..PB-6030, PB-6034 | 28 |
| Total | 86 | |

Of the 54 W4-owned rows (86 minus 4 W5 and 28 later-wave rows), 31 of the 86
total rows carry an explicit unresolved-parent-decision marker (D-1: 6; D-2:
3; D-3: 3; D-4: 1; D-5: 2 partial rows; D-6: 8; D-7: 8). Fifteen of the
marked rows are W4-owned and sixteen are later-wave rows. Every one of the 86
rows remains `unresolved` with an empty Proof ID list; this mapping assigns
first ownership only and resolves nothing.

## W4 packet sequence and worktree table

The charter graph (WS-STREAM depends on WS-CONTRACT+WS-FAMILIES; WS-BACKUP on
WS-CONTRACT+WS-STORAGE+WS-FAMILIES; WS-REPLICATION on
WS-CONTRACT+WS-STORAGE+WS-ENGINE+WS-STREAM; WS-PLATFORM on WS-PROTOCOL +
WS-REPLICATION + WS-BACKUP) constrains the schedule but does not fully order
it: W4-0 must come first, W4-STREAM and W4-BACKUP may run in parallel, and
W4-REPLICATION cannot start before W4-STREAM. The serialization of
W4-REPLICATION after W4-BACKUP below is a lane-scheduling choice, not a
charter edge — replication's non-lane work (types, algorithm implementation,
catch-up state machine) could start on the W4-STREAM handoff alone, with only
its exclusive-lane fault scenarios serialized against W4-BACKUP's. The choice
below orders replication after both packets so a single handoff carries the
complete W4 evidence bundle to W5; the trade-off is a longer critical path.
The parent may select the faster variant at dispatch:

```text
W4-0 declaration preparation (parent-assigned collision actions W4-CA-1..W4-CA-7)
          |
   +------+------+
   |             |
W4-STREAM     W4-BACKUP        (parallel; disjoint paths)
   |             |
   +------+------+
          |
   W4-REPLICATION              (needs STREAM; serialized after W4-BACKUP by
          |                     lane-scheduling choice — see above; shares the
          |                     exclusive recovery lane with W4-BACKUP fault
          |                     work by parent scheduling, never concurrently)
          |
   W5 Platform Adapter handoff (both W4 handoffs required)
```

| Packet | Worktree and base | Write scope | Collision owner |
| --- | --- | --- | --- |
| W4-0 preparation | `terrabasev2-w4-type-prep`, based on the integrated W4 design tip | `src/streaming/mod.rs`, `src/replication/mod.rs`, `src/backup/mod.rs`, `src/migration/mod.rs`, `tests/public_exports_w4.rs`, plus the parent-assigned `src/lib.rs` edit (W4-CA-1) | Owns the declaration-only tree through its preparation commit; after that, integrator |
| W4-STREAM | `terrabasev2-w4-streaming`, based on the recorded W4-0 commit | `src/streaming/**`, `tests/streaming/**`, explicitly named `docs/streaming/**` | Streaming only; no engine, storage, families, replication, backup, adapter, or shared-root edits |
| W4-BACKUP | `terrabasev2-w4-backup`, based on the recorded W4-0 commit | `src/backup/**`, `src/migration/**`, `tests/recovery/**`, explicitly named `docs/backup/**` and `docs/migration/**` | Backup, restore, and Logical Migration only; no replication, streaming, adapter, or shared-root edits |
| W4-REPLICATION | `terrabasev2-w4-replication`, based on both accepted W4-STREAM and W4-BACKUP commits (the serialized choice above; the parent may instead base non-lane work on the W4-STREAM handoff) | `src/replication/**`, `tests/replication/**`, explicitly named `docs/replication/**` and lane receipts under `evidence/replication/**` | Replication behavior and receipts only; no backup, migration, adapters, or shared roots |

Collision ownership during W4:

| Path family | Owner during W4 | Rule |
| --- | --- | --- |
| `src/lib.rs`, `src/types.rs`, `src/operations.rs`, `tests/public_exports.rs` | W4-0 through preparation (lib.rs only), then integrator | No behavior packet edits them; changes are named collision actions |
| `src/engine/**`, `src/storage/**`, `src/families/**` | integrator via W4-CA-2, W4-CA-3, W4-CA-4, W4-CA-5, W4-CA-7 | W4 packets request; the parent assigns and executes |
| `Cargo.toml`, `Cargo.lock`, toolchain, CI, root manifests | integrator | No dependency edits in W4 packets without a named action |
| `parity/inventory.json`, `parity/overlays/**`, `parity/contracts/**` | integrator and WS-PARITY | W4 adds no Proof IDs and changes no status |
| `src/streaming/**`, `tests/streaming/**`, `docs/streaming/**` | W4-STREAM | Streaming only |
| `src/replication/**`, `tests/replication/**`, `docs/replication/**`, `evidence/replication/**` | W4-REPLICATION | Replication and its receipts only |
| `src/backup/**`, `src/migration/**`, `tests/recovery/**`, `docs/backup/**`, `docs/migration/**`, `evidence/recovery/**` | W4-BACKUP | Backup, restore, migration, and recovery receipts only |
| `docs/design/w4-transfer-replication-contract.md` | this packet | The durable W4 design record; behavior builders do not rewrite it |

The exclusive recovery lane rule: W4-BACKUP's fault/restore scenarios and
W4-REPLICATION's fault lane scenarios both require the lane. The parent
schedules them serially; the two packets never run lane scenarios
concurrently.

## Parity obligations and non-claims

- All 86 mapped rows stay `unresolved` with empty Proof ID lists until the
  parity owner records dispositions. W4 packets may add focused tests and
  lane receipts as evidence for later Proof IDs; they may not change a row's
  status or add a Proof ID themselves.
- Explicit non-claims for W4:
  - No "V2 is multi-region capable" claim until the three-Region fault lane
    passes and its Replication Receipts exist.
  - No RPO, RTO, convergence-time, or lag numbers until measured in the lane;
    lane numbers are lane-scoped facts with stated definitions, not product
    claims.
  - No speed or performance claims of any kind; benchmark claims require the
    exclusive benchmark lane and TerraBase.Benchmark gates.
  - No claim that V2 opens V1 files directly; the only V1 path is the
    manifest-verified Logical Migration.
  - No Platform authorization, tenant-authorization, confirmation, audit, or
    secret-handling claim (W5 owns PB-3042 and PB-6036).
  - No claim that any recommendation in this document is an accepted
    decision.

## Checks and evidence

Each W4 packet reports its exact commit, worktree, changed paths, commands
with exit codes, assumptions, and risks. The shared checks, run by the parent
after integration and by this packet at its tip, are:

- `cargo fmt --all -- --check`;
- `cargo check --workspace --locked`;
- `cargo test --workspace --locked --release`;
- `python3 scripts/validate_parity.py --check`.

Receipt content requirements:

- A `ReplicationReceipt` names scenario identity, topology (Regions,
  Members, failure domains), fault classes, the acknowledgement facts per
  operation, the convergence observation and its condition, measured
  RPO/RTO/convergence/lag with their definitions, and explicit limits. It is
  secret-safe: no credentials, connection strings, document content,
  customer data, backup contents, or private host paths.
- A `RecoveryReceipt` (restore, migration, rejoin, region-loss recovery)
  names the exact source identity (commit and storage-format version),
  manifest identity, fault or interruption class, restored and missing
  values, duplicate handling, checksum verdicts, and limits — with the same
  redaction rules.
- Ordinary focused tests support Proof IDs for their observable behavior
  only. Formal receipts come only from the exclusive lanes.

## Open decisions and limits

Parent adjudication is required before the corresponding packets can claim
anything (each is recorded above with options, evidence, and a
recommendation):

- D-1 replication algorithm (blocks PB-5000, PB-5016..PB-5020 and the
  leader-loss red tests);
- D-2 consistency and acknowledgement defaults and the `AcknowledgementLevel`
  lattice (blocks PB-5026, PB-5042, PB-5043);
- D-3 conflict policy (blocks PB-5022, PB-5034, PB-5045 and the conflict red
  tests);
- D-4 production topology limits (blocks PB-5044 and any topology claim);
- D-5 transfer transform-script mechanism (blocks the script portions of
  PB-6020 and PB-6006);
- D-6 optional/legacy transfer dispositions (ETL, queue sink, relational
  migration — PB-6023..PB-6030);
- D-7 V2 sharding scope (PB-5036..PB-5041, PB-5048, PB-6034).

W5 owns Platform authorization, tenant isolation, confirmation, audit, secret
handling, and the MCP/SDK boundary (`src/adapters/platform/**`, PB-3042,
PB-6031/PB-6032/PB-6035/PB-6036). The Platform/Orchestrator owns provisioning,
routing, lifecycle, external authentication policy, and control-plane
replication. The pending distribution license remains a release gate.

Limits of this document: it is a design contract, not behavior evidence; its
decision records are proposals; its collision actions W4-CA-1..W4-CA-7 are
proposed
exact changes, executable only when the parent assigns them; its red-test
lists are the minimum sufficient set, and packets may add tests but not
remove a listed one.

## Acceptance checklist

This document is ready for the W4-0 dispatch when all of the following remain
true:

1. Scope names the base commit `46bccd5`, cites the W3 contract sections it
   builds on, and lists the W4 non-goals.
2. Every public W4 type has an ownership table row with visibility, owner
   module, and required facts; crate-private machinery is enumerated and never
   re-exported.
3. `CommitReceipt` is reaffirmed local-only, and no W4 type extends it with
   Region, Member, `AcknowledgementLevel`, `Convergence`, or
   `ReplicationReceipt` facts.
4. The W4-0 declaration tree, the verbatim `src/lib.rs` export block, and the
   four red export test names are recorded.
5. Collision actions W4-CA-1..W4-CA-7 name the exact file, the proposed
   signature, and why the requesting builder cannot make the change; W4-CA-3
   records the sink-registry absorption of the queued W3-2 catalog action and
   the non-blocking/bounded sink rule; W4-CA-4 records its integrator
   verification obligations.
6. The streaming contract fixes ordering per source, bounded memory,
   backpressure, cancellation liveness, the never-committed read rule, and
   checkpoint/acknowledgement/resend semantics, and lists the W4-STREAM red
   tests.
7. The replication contract presents decisions D-1..D-4 before any dependent
   rule, each marked proposal-only with alternatives and a recommendation,
   and defines the acknowledgement lattice, per-level visibility,
   anti-entropy/replay/rejoin, the engine/control-plane split, and the
   three-Region fault lane with honest RPO/RTO/convergence/lag definitions.
8. The backup/migration contract fixes manifest and chain verification,
   clean-restore verification, restartable and idempotent Logical Migration,
   the explicit loss matrix, secret-safe evidence, and the W4-BACKUP red
   tests.
9. All 86 rows PB-5000..PB-5048 and PB-6000..PB-6036 are mapped exactly once
   in the row mapping with counts cross-checked, and 31 rows carry explicit
   parent-decision markers (15 W4-owned, 16 later-wave).
10. The packet sequence W4-0 → {W4-STREAM ∥ W4-BACKUP} → W4-REPLICATION → W5
    is consistent with the charter graph, records the W4-REPLICATION-after-
    W4-BACKUP serialization as a lane-scheduling choice with its trade-off
    stated, and tabulates worktrees, write scopes, and collision owners, with
    the exclusive recovery lane rule stated.
11. Parity non-claims are explicit: no multi-region capability claim, no
    unmeasured numbers, no speed claims, no direct-V1-file claim, no
    authorization claim, and no decision treated as accepted.
12. Checks, receipt content requirements, open decisions, and W5/Platform
    ownership limits are recorded, mapped to the charter's AC-10
    (multi-region qualification) and AC-11 (recovery and durability
    evidence), which W4 prepares but does not close.
