# W3 core contract: storage, engine, query, and protocol

Status: accepted implementation contract for W3, recorded 2026-08-23.

This document turns the W3 design review into rules that builders and tests can
use. It covers the Storage, Engine, Data Family, Query, and Protocol seams. It does not
claim that any TerraBaseV2 behavior has been implemented or qualified. The
first implementation packet is W3-1, one sequential builder for Storage plus
Engine because their first durable commit contract cannot be split safely.

The contract is based on the reviewed W2 matrix at commit `8771f13`, the four
seam design integrator packets, the cross-seam coherence review, and the
reliability corrections accepted by the parent. Those inputs are design
evidence. They are not behavior Proof IDs.

The governing records are the workspace-root execution charter identified as
`workspace-root:specs/2026-08-23-terrabase-v2-goal.md`, the
[W2 contract extraction](../parity/w2-contract-extraction.md), and the
[accepted storage-format ADR](../adr/0001-independent-rust-repository-and-storage-format.md).
The parent dispatch resolves that identifier to an absolute path in assignment
metadata and passes it into every worktree. A builder must not infer a path
from its worktree location.

## Decision summary

The selected design is a hybrid. Callers get small typed interfaces. The
implementation keeps the difficult ordering, retry, snapshot, index, and
transport rules behind those interfaces. The seams are deliberately different
for local database behavior and for remote adapters.

| Seam | Chosen interface | Main reason | What stays behind the seam |
| --- | --- | --- | --- |
| Storage | One commit/recovery port with immutable published roots and a private WAL adapter | A single publication rule can govern restart, durability, and commit resolution | File layout, codec, WAL records, manifest files, locks, checksums, and recovery replay |
| Engine | Opaque `Transaction`, immutable `ReadSnapshot`, and operation-identified commit | The common document/session case is short while conflict and retry behavior remains centralized | MVCC, write-set validation, storage calls, index notifications, and conflict details |
| Data Family | Closed typed family requests/results with transaction-bound writes | Family-specific rules stay explicit while document transactions can remain atomic across families | Attachment, counter, time-series, revision, lifecycle, validation, identity, and tombstone details |
| Query and Index | Typed `QuerySpec`, compiled `QueryPlan`, and snapshot execution | Plans can be reused without making callers know index generations or storage roots | Planner, index choice, stale handling, reduction, and index publication |
| Protocol | Typed operation envelope over a synchronous adapter port | Every operation has an explicit identity and error mapping; no generic escape hatch | Wire framing, headers, compression, cancellation, and transport-specific status |

No chosen interface is a promise about a replication algorithm, consistency
default, conflict policy, production topology, or final distribution license.
Those decisions remain separate gates in the charter.

## Problem frame and constraints

W3 has four callers with different failure costs. Storage must distinguish an
accepted local write from a write that survives the declared local durability
point. Engine must make retries safe when a response is lost after publication.
Query must read a coherent immutable view while indexes catch up. Protocol must
carry typed public operations without becoming an authority for Platform or a
private control plane.

The shared constraints are:

1. The core is synchronous and runtime-neutral. It may use blocking local I/O
   behind a bounded adapter, but it does not require Tokio, an executor, or a
   particular network runtime.
2. Every mutating operation has a caller-supplied `OperationId`. A transport
   timeout never authorizes a second operation with a new identity.
3. A commit is observable only after the storage publication rule completes.
   Index publication follows storage publication and never holds a storage lock
   while it waits for or performs index work.
4. `ReadSnapshot` is immutable. `IndexGeneration` is monotonic. A query must
   state whether it permits an older index or needs a generation at least as
   new as the requested snapshot.
5. `Buffered`, `Written`, and `Synced` describe local durability only. They do
   not describe `Acknowledgement Level`, replication `Convergence`, or member
   visibility.
6. `PlatformGrant` is accepted and checked at the Platform Adapter seam. It is
   not an Engine credential and is not a Protocol authority.
7. Raw bytes are private to the storage codec and WAL implementation. Public
   types carry structured documents, identifiers, errors, and receipts.
8. A durable Engine is opened from a configured storage location. Production
   callers cannot construct a durable Engine backed by an in-memory store.
   Ephemeral construction exists only in `cfg(test)` code or an explicitly
   non-durable test fixture that is unavailable to production callers.

The dependencies fall into the categories used by the codebase-design guide:

- In-process: document validation, operation identity, conflict checks, query
  planning, generation ordering, and response mapping.
- Local-substitutable: the file system, clock, checksum implementation, and
  private storage adapter. Tests may provide a controlled fixture, but that
  fixture is not a production constructor.
- Ports and adapters: approved protocol and Platform integration. The core
  owns behavior and callers inject a typed synchronous adapter.
- True external: none is required by the core. Authentication policy and
  Platform authorization are owned outside the Engine.

A rough shape, for orientation only, is:

```rust
let snapshot = engine.read_snapshot(&scope)?;
let transaction = engine.begin(&scope, snapshot, operation_id)?;
let outcome = transaction.commit(LocalDurability::Synced)?;
```

The actual public interface includes invariants, ordering, errors, and
durability semantics below. A code sketch without those facts is incomplete.

## Accepted reliability corrections

The reliability review made these conditions explicit. They are accepted
contract rules for W3, not suggestions for a later pass:

| Correction | Required contract |
| --- | --- |
| Ambiguous commit responses | Return `Unknown` with the original `OperationId`; resolve it from the durable operation ledger before retrying or reporting an outcome. |
| Publication visibility | Publish a new root only after a valid commit marker and the requested pre-publication writes. It may become visible to live reads at atomic pointer publication; `Synced` and durable recoverability require post-publication pointer and directory sync. A post-publication sync failure returns `Unknown` and is resolved through the operation ledger. Never expose an uncommitted root during recovery. |
| Index/storage coupling | Release storage locks before index work. Publish only immutable-root deltas with a monotonic `IndexGeneration`. |
| Snapshot mutation | `ReadSnapshot` is immutable and tenant-scoped. A later commit creates a new snapshot. |
| Durability vocabulary | Use only `Buffered`, `Written`, and `Synced` for local durability, and keep them separate from `Acknowledgement Level` and `Convergence`. |
| Test construction | Do not export a durable in-memory Engine constructor. Test-only ephemeral construction is `cfg(test)` or an explicitly non-durable fixture. |
| Authority placement | Keep `PlatformGrant` at Platform Adapter validation. Engine and Protocol receive no grant authority. |
| Data representation | Keep raw bytes inside private storage codec/WAL code. Public seams use typed values and receipts. |
| Runtime assumptions | Keep the core synchronous and runtime-neutral. Future async adapters need bounded blocking capacity, deadlines, cancellation, and overload errors. |

Builders must treat a violation as a failing test or a design blocker. They may
not defer one of these corrections while implementing a convenient tracer.

## Design-it-twice record

Each seam was considered under four different constraints. The alternatives
are recorded because the first attractive interface was not automatically the
deepest one.

### Storage alternatives

#### A. Minimal commit port

```rust
trait Storage {
    fn commit(&self, batch: WriteBatch, durability: LocalDurability)
        -> Result<StorageReceipt, StorageError>;
    fn snapshot(&self) -> Result<StorageSnapshot, StorageError>;
    fn resolve(&self, operation: OperationId)
        -> Result<DurableResolution, StorageError>;
}
```

This has high leverage. Engine callers learn three entry points, and all
publication ordering can live in one implementation. It is weak when backup,
replay, and recovery need an explicit phase boundary. A single `commit` also
tempts an implementation to hide an unresolved outcome as an ordinary error.

#### B. Maximum flexibility

```rust
trait Storage {
    fn prepare(&self, request: PrepareRequest) -> Result<Prepared, StorageError>;
    fn append(&self, prepared: &Prepared) -> Result<AppendReceipt, StorageError>;
    fn sync(&self, prepared: &Prepared) -> Result<SyncReceipt, StorageError>;
    fn publish(&self, prepared: Prepared) -> Result<StorageReceipt, StorageError>;
    fn recover(&self, source: RecoverySource) -> Result<RecoveryState, StorageError>;
    fn backup(&self, source: BackupSource) -> Result<BackupManifest, StorageError>;
    fn restore(&self, manifest: BackupManifest) -> Result<RestoreReceipt, StorageError>;
}
```

This exposes every useful phase and makes adapters easy to substitute. It is
too shallow for Engine callers. It leaks sequencing decisions, encourages
callers to publish an invalid prepared value, and multiplies tests across
every phase combination.

#### C. Common caller first

```rust
trait Storage {
    fn put(&self, operation: OperationId, changes: Changes)
        -> Result<StorageReceipt, StorageError>;
}
```

The ordinary document write is easy to read. The interface cannot express
snapshot reads, an explicit durability request, recovery, or durable
resolution without side channels. It is a shallow convenience wrapper, not a
storage seam.

#### D. Ports and adapters

```rust
trait StoragePort {
    type Snapshot;
    type Receipt;
    fn apply(&self, request: CommitRequest) -> Result<Self::Receipt, StorageError>;
    fn view(&self, request: SnapshotRequest) -> Result<Self::Snapshot, StorageError>;
}

struct FileStorage<F, C> { file_system: F, clock: C }
struct FixtureStorage { /* test-only */ }
```

This makes the file system and test fixture explicit. It is a good internal
seam, but a generic public storage port would expose codec and recovery types
too early. The caller still needs a durable-resolution operation.

#### Selected Storage hybrid

The external Engine-facing interface takes the small shape from A. Inside the
crate, a private Storage port separates commit validation, WAL append, sync,
manifest publication, recovery, and index notification. A file adapter and a
test-only controlled fixture satisfy that private port. The public seam stays
deep because callers cannot bypass publication phases. The internal seam is
real because production file I/O and deterministic tests are two adapters.

### Engine alternatives

#### A. Command dispatcher

```rust
fn execute(&self, command: EngineCommand) -> Result<EngineResponse, EngineError>;
```

One entry point offers strong locality and makes operation identity universal.
The command enum grows with every V1 capability and makes the common session
case noisy. It also risks turning the Engine into a transport protocol.

#### B. Maximum flexibility handles

```rust
fn open_session(&self, options: SessionOptions) -> Result<Session, EngineError>;
fn open_transaction(&self, options: TransactionOptions) -> Result<Transaction, EngineError>;
fn open_reader(&self, options: ReaderOptions) -> Result<Reader, EngineError>;
fn open_writer(&self, options: WriterOptions) -> Result<Writer, EngineError>;
```

This supports many callers, but every handle would need rules for snapshots,
operation identity, retries, and durability. It spreads the contract across a
shallow family of modules.

#### C. Common caller first

```rust
fn save(&self, document: Document) -> Result<CommitReceipt, EngineError>;
fn load(&self, id: DocumentId) -> Result<Option<Document>, EngineError>;
```

This makes a single save/load pair pleasant. It cannot represent atomic
batches, compare-and-change-vector writes, explicit snapshots, or an
ambiguous response that needs resolution.

#### D. Ports and adapters

```rust
trait EnginePort {
    fn execute(&self, request: EngineRequest) -> Result<EngineResponse, EngineError>;
}

struct LocalEngineAdapter<E> { engine: E }
struct PlatformEngineAdapter<P> { platform: P }
```

The port keeps transport concerns out of the Engine, but a generic request
type still needs a transaction and query model. It is useful at the adapter
seam, not as the only Engine interface.

#### Selected Engine hybrid

The Engine exposes an opaque `Transaction` for atomic writes, a snapshot method
for reads, and a durable-resolution method keyed by `OperationId`. Common
operations are methods on the transaction, while the internal command model
is crate-private. This combines the leverage of A with the common-case clarity
of C. The transaction owns write-set validation and commit outcome mapping, so
callers do not implement retry or WAL rules themselves.

### Query and Index alternatives

#### A. One-shot query

```rust
fn query(&self, spec: QuerySpec) -> Result<QueryResult, QueryError>;
```

It is small and easy to use, but it hides the snapshot and index generation.
Callers cannot safely reuse a plan or explain why a result is stale.

#### B. Maximum flexibility builder

```rust
fn query_builder(&self) -> QueryBuilder;
```

The builder can express every V1 clause, but it exposes planner vocabulary and
allows invalid combinations to survive until execution. It makes the public
seam as complex as the implementation.

#### C. Common caller first

```rust
fn find_by_id(&self, id: DocumentId) -> Result<Option<Document>, QueryError>;
```

The identity lookup is fast to understand but does not provide query parity,
ordering, projections, or index consistency.

#### D. Ports and adapters

```rust
trait QueryPort {
    fn plan(&self, spec: QuerySpec) -> Result<QueryPlan, QueryError>;
    fn execute(&self, plan: &QueryPlan, view: ReadSnapshot)
        -> Result<QueryResult, QueryError>;
}
```

This puts the planner behind a clean seam and permits a real index adapter. It
still needs a policy for stale indexes and generation monotonicity.

#### Selected Query hybrid

The public shape is a typed `QuerySpec` compiled into an immutable `QueryPlan`
and executed against an immutable `ReadSnapshot` with an explicit staleness
policy. The internal planner and index adapters remain private. The common
one-shot path is a convenience method implemented in terms of this shape, not
a second semantic path. Query depth comes from hiding index choice, reduction,
and stale fallback while exposing the result contract callers need.

### Protocol alternatives

#### A. Generic request map

```rust
fn dispatch(&self, method: String, body: GenericBody) -> Result<GenericBody, ProtocolError>;
```

This can carry any future operation, but it is raw bytes and a generic escape
hatch. It cannot prove that private routes and unknown methods are excluded.

#### B. One trait per operation

```rust
trait SaveDocument { fn save(&self, request: SaveRequest) -> SaveResponse; }
trait LoadDocument { fn load(&self, request: LoadRequest) -> LoadResponse; }
```

It gives strong types but duplicates transport error, identity, deadline, and
cancellation rules across a large interface. New public operations require
many adapter changes.

#### C. Common caller first

```rust
fn send(&self, request: TypedRequest) -> Result<TypedResponse, ProtocolError>;
```

This is concise, but without an explicit operation envelope it leaves retry
identity and authorization context implicit.

#### D. Ports and adapters

```rust
trait ProtocolPort {
    fn call(&self, operation: PublicOperation) -> Result<PublicResponse, ProtocolError>;
}

struct HttpProtocolAdapter<T> { transport: T }
struct InProcessProtocolAdapter { /* tests */ }
```

This keeps wire details in adapters and supports deterministic tests. The
operation set must remain typed and the Platform Adapter must remain the
authorization owner.

#### Selected Protocol hybrid

Use a typed `PublicOperation` envelope with a stable `OperationId`, deadline,
cancellation token, tenant routing metadata, and a closed set of approved
operations. A synchronous `ProtocolAdapter` maps the envelope to a typed
Platform or Engine call. HTTP, TCP, and future transports are adapters behind
the port. No raw request body, route string, private control-plane operation,
or generic HTTP forwarding method crosses this seam.

### Why the hybrids win

The hybrids put a small external interface over the most stateful rules and
leave variation at internal seams where there are actually two adapters. They
keep storage publication and Engine commit identity together, let Query reuse
immutable views without exposing indexes, and make Protocol transport-neutral
without weakening its operation allowlist. This is the best balance of depth,
locality, and seam placement found in the alternatives.

## Chosen contract

### Public type ownership

The following types are crate-public because callers and tests must use them
to state the contract. Their fields remain private where construction must be
validated.

The visibility rule is exact: the types in the table below are declared
`pub` (with private fields where validation matters). The types in the
crate-private table are declared `pub(crate)` only when another V2 module needs
them, and `pub(crate)` is not re-exported from `src/lib.rs`. Codec and file
layout types are plain private declarations.

| Type | Owner | Required facts |
| --- | --- | --- |
| `Engine` | `src/engine/` | Opens durable storage and coordinates transactions, snapshots, query, and commit resolution |
| `EngineConfig` | `src/engine/` | Requires an explicit durable storage location and format policy |
| `TenantId`, `DocumentId`, `CollectionName` | shared logical types, integrator-owned | Validated identifiers; no raw storage keys |
| `Document`, `DocumentValue`, `DocumentMetadata` | `src/engine/` shared logical types | Structured values and metadata; never storage bytes |
| `OperationId` | shared logical types, integrator-owned | Stable caller identity for a mutating operation and all retries |
| `TenantScope` | shared logical types, integrator-owned | Opaque validated scope; no public constructor from an untrusted string or `PlatformGrant` |
| `Transaction` | `src/engine/` | Opaque write set tied to one snapshot and one operation identity |
| `ReadSnapshot` | shared logical types, integrator-owned | Immutable storage sequence, tenant scope, and per-namespace index-generation view |
| `IndexGeneration` | shared logical types, integrator-owned | Namespace, persisted epoch, and monotonic non-wrapping sequence |
| `IndexGenerationSet` | shared logical types, integrator-owned | Immutable per-namespace generations carried by a read snapshot and query result |
| `LocalDurability` | shared logical types, integrator-owned | Exactly `Buffered`, `Written`, and `Synced` with the meanings below |
| `CommitOutcome`, `CommitReceipt` | `src/engine/` | Distinguishes committed, rejected, and unknown acceptance |
| `DurableResolution` | `src/engine/` | Resolves an unknown operation from the durable operation ledger |
| `QuerySpec`, `QueryPlan`, `QueryResult` | `src/query/` | Typed query clauses, immutable plan, result metadata and generation |
| `FamilyKind`, `FamilyReadRequest`, `FamilyWriteRequest` | `src/families/` | Closed typed family operations with no generic family escape hatch |
| `FamilyReadResult`, `FamilyReadContext`, `FamilyStore` | `src/families/` | Tenant-scoped family reads and transaction-bound family writes |
| `ProtocolAdapter`, `PublicOperation`, `PublicResponse` | `src/adapters/protocol/` | Typed approved operations and transport-neutral errors |
| `PlatformGrant`, `PlatformAdapter` | `src/adapters/platform/` | Platform-only authorization, tenant isolation, confirmation, audit, and secret handling |

The public `Engine` has no `new_in_memory`, `from_memory`, or equivalent
durable constructor. `Engine::open(config)` is the production opening path and
must reject a missing or non-durable storage location. A test-only constructor
may be declared `#[cfg(test)] pub(crate)` inside the owning crate, or a named
non-durable fixture may live under test support with no production feature that
exports it. Integration tests must use the durable temporary-directory path
when they test the real Engine contract.

`PlatformGrant` is visible only at the Platform Adapter seam. The Engine does
not accept it, inspect it, or decide authorization. The Protocol Adapter does
not mint it and cannot use a route or method name to bypass the Platform
Adapter. A validated `TenantScope` is the only tenant context passed to Engine
operations.

### Crate-private ownership

The following types and modules must remain `pub(crate)` or private:

| Type or module | Owner | Rule |
| --- | --- | --- |
| `WalRecord`, `WalFrame`, `WalOffset`, `CommitMarker` | `src/storage/` | Private codec and replay details; no raw bytes in public results |
| `StorageKey`, `EncodedDocument`, `EncodedValue` | `src/storage/` | Private physical representation, not a public identifier |
| `StoragePort`, `FileStorage`, `FixtureStorage` | `src/storage/` | Internal adapters; fixture construction is test-only |
| `Manifest`, `StorageRoot`, `RecoveryState` | `src/storage/` | Manifest verification and root publication details |
| `CommitIntent`, `PreparedCommit`, `StorageReceipt` | `src/storage/` | Engine receives typed receipts, not mutable preparation handles |
| `OperationLedgerRecord`, `ReadyToPublish`, `Published` | `src/storage/` and `src/engine/` | Durable retry-resolution metadata; never a public storage or byte representation |
| `MvccVersion`, write-set index, conflict detector | `src/engine/` | Internal transaction implementation |
| family physical records, retention workers, `FamilyProjectionInput` | `src/families/` | Family implementation details; projection inputs are typed and read-only at the Query adapter seam |
| `IndexSegment`, `IndexDelta`, `IndexBuilder`, index lock | `src/index/` | Internal index publication and generation machinery |
| planner AST, reducer state, scorer, wire codec | `src/query/` and adapters | Never expose implementation or raw transport bytes |
| `PlatformGrant` validation internals and audit sink | `src/adapters/platform/` | Authorization policy stays at Platform boundary |

No public method may return a byte slice, WAL frame, file offset, manifest
path, internal lock, or private route descriptor as a substitute for a typed
contract.

### Dependency arrows and collision rules

The intended dependency direction is:

```text
Protocol Adapter -> Platform Adapter -> Engine -> Families -> Query -> Index
                                      |          \\-> Storage -> private codec/WAL/filesystem
                                      \\-> Storage
```

The arrows mean calls and typed data flow, not ownership of authorization.
`Platform Adapter` can construct `TenantScope` only after validating a
`PlatformGrant`. `Protocol Adapter` can call Platform Adapter or a trusted
in-process Engine adapter, but it never grants permission. `Query` consumes a
`ReadSnapshot`; it does not open storage or mutate an index. `Families` owns
family value and lifecycle rules and exposes typed projection inputs to Query;
it does not implement query planning, index publication, replication, or
backup. `Index` consumes
published storage roots and emits a new `IndexGeneration`; it cannot publish a
storage root. Storage never depends on Protocol, Platform, Query, or Families.

Shared logical types, `src/lib.rs`, root Cargo files, `Cargo.lock`, generated
parity output, overlays, and root CI manifests are integrator-owned. A builder
must not edit them unless the parent names one exact file in the dispatch
packet. Each builder uses a worktree based on the preceding accepted commit.

### W3-0 type and export preparation packet

The documentation gate is followed by one small executable preparation packet
before behavior work. The parent dispatches `W3-0-TYPE-EXPORT-PREP` in
worktree `terrabasev2-w3-type-prep`, based on the integrated W3-0 docs tip. The
dispatch must record the exact current docs tip by running
`git rev-parse fleet/w3-core-design` after the docs commits are integrated. Its
purpose is to materialize the shared logical types and exports named in this
document, then prove that all later packets can compile against one vocabulary.
The docs history leading to that dispatch includes the initial contract commit
`ad3e52c` and the first reliability correction commit `74d5473`.

The packet owns exactly these shared files:

- `src/lib.rs`, for the public re-exports and crate module declarations;
- `src/types.rs`, if the integrator chooses a separate shared-type module.

`Cargo.toml`, `Cargo.lock`, root CI, generated output, parity overlays, and all
other root/shared files remain integrator-owned collision paths. If shared
types require a dependency, the parent integrator makes that exact manifest
change as a separate named action; W3-0 does not take ownership of the Cargo
files.

W3-0 owns the shared declarations in `src/lib.rs`; after the preparation
commit, W3-1 has a narrow, named exception to that file only for declaring and
re-exporting the Storage and Engine modules it implements. This exception does
not transfer ownership of shared logical types or the rest of `src/lib.rs`.

The W3-0 integrator owns those shared source files exclusively for this
preparation commit.
It materializes only the exact shared logical types and their exports. It must
not implement Storage, Engine, Query, Index, Protocol, or Platform behavior.
It may add compile-only type/export checks, but it must not edit generated
parity output, overlays, or any W3 behavior path. The packet reports a clean
locked check, format, clippy, and docs result, and then commits the preparation.
The parent records that preparation commit hash and W3-1 must base on that
hash, never directly on `8771f13` or on an unrecorded docs tip.

| Packet | Worktree and base | Write scope | Collision owner |
| --- | --- | --- | --- |
| W3-1 Storage plus Engine | `terrabasev2-w3-storage-engine`, based on the recorded W3-0 type/export preparation commit | `src/storage/**`, `src/engine/**`, `tests/storage/**`, `tests/engine/**`, and explicitly named storage/engine docs or receipts, plus the narrow `src/lib.rs` module declaration/re-export edit described below | W3-1 owns those paths only; integrator retains shared type declarations, Cargo files, generated outputs, overlays, and all other `src/lib.rs` content |
| W3-F Data Families | `terrabasev2-w3-families`, based on the accepted W3-1 commit | `src/families/**`, `tests/families/**`, and explicitly named `docs/families/**` | W3-F owns family paths only; it does not edit Storage, Engine, Query, Index, replication, backup, migration, adapter, shared, or generated paths |
| W3-2 Query | `terrabasev2-w3-query`, based on the accepted W3-F Families commit | `src/query/**`, `src/index/**`, `tests/query/**`, `tests/index/**`, and explicitly named query/index docs | W3-2 owns query/index paths; it does not edit W3-1, W3-F, or shared files |
| W3-3 Protocol | `terrabasev2-w3-protocol`, based on the accepted W3-2 commit | `src/adapters/protocol/**`, named protocol tests, and named protocol docs | W3-3 owns protocol paths; Platform implementation remains a later adapter packet |

The parent integrates each packet in sequence. Generated inventory and parity
overlays are never generated from a builder worktree. They are regenerated by
the integrator after behavior evidence exists.

Collision ownership is explicit:

| Path family | Owner during W3 | Rule |
| --- | --- | --- |
| `src/lib.rs` and shared logical type declarations | later integrator, with a W3-1 narrow exception | W3-1 may edit only the exact lines needed to declare and re-export its newly implemented Storage and Engine modules. It may not change shared logical types or any other export. |
| `Cargo.toml`, `Cargo.lock`, toolchain, format, and root CI files | later integrator | No dependency or manifest edits in W3 seam packets unless the dispatch names one exact file |
| `parity/inventory.json` and `parity/overlays/**` | later integrator | Builders provide test/evidence handoffs; they do not regenerate or change status |
| `docs/design/w3-core-contract.md` | W3-0 contract writer | This document is the durable W3 design record; behavior builders do not rewrite it |
| `docs/storage/**`, `docs/engine/**`, `docs/families/**`, `docs/query/**`, `docs/index/**`, and `docs/adapters/protocol/**` | the packet named in the dispatch | A packet may create only its named documentation path; there is no residual `docs/**` ownership |

If a builder discovers a required change outside its row, it records the exact
path and asks the parent to assign that collision. It does not opportunistically
edit a shared file.

## Storage and Engine semantics

### Operation identity

Every mutating request supplies an `OperationId`. It is opaque to storage,
stable across retries, unique within the tenant's operation namespace, and
bound to the complete logical write set, collection, and transaction boundary.
Reusing an identity with a different request is a typed `OperationIdReuse`
error. Retrying the exact request with the same identity is idempotent.

The operation ledger is durable enough to answer a commit resolution after a
lost response. Compaction may remove payload details only after retaining the
operation outcome and the fields required by the configured resolution
retention policy. A missing payload is never evidence that a commit did not
happen.

### Commit outcome and durable resolution

`Transaction::commit` returns one of three outcomes:

```rust
pub enum CommitOutcome {
    Committed(CommitReceipt),
    Rejected(CommitError),
    Unknown(UnknownCommit),
}
```

`Committed` means the storage publication rule completed and the receipt names
the operation, storage sequence, achieved `LocalDurability`, and the snapshot
from which the result became visible. It does not imply any remote
`Acknowledgement Level` or `Convergence`.

`Rejected` means validation, conflict detection, or an explicit storage error
proved that this operation was not published. The error is typed and includes
whether the operation may be retried with a new identity.

`Unknown` is returned when the caller cannot know whether publication finished,
including a deadline or transport interruption after WAL publication, or a
post-publication pointer/directory sync failure. It always carries the original
`OperationId`. It is not a successful commit and not a rejection. The caller
must use the same identity and call the durable resolution method before
deciding whether to retry:

```rust
pub enum DurableResolution {
    Committed(CommitReceipt),
    Rejected(CommitError),
    Pending,
}

impl Engine {
    pub fn resolve_commit(
        &self,
        scope: &TenantScope,
        operation: OperationId,
    ) -> Result<DurableResolution, EngineError>;
}
```

`Pending` means the durable operation ledger contains a prepared or in-flight
operation whose final publication is not yet known. Repeated resolution must
eventually return `Committed` or `Rejected`, or retain `Pending` with an
explicit operational limit. An unknown response never permits a new identity
for the same logical write.

### Stable operation-ledger ordering

The operation ledger is a private append-only resolution record, not a second
document store. A `ReadyToPublish` record contains the `OperationId`, request
digest, storage sequence, commit-marker identity, new-root identity, and
requested local durability. The record is written and durably synced before
the atomic root-pointer replacement. A later `Published` record is appended
after pointer publication and its required syncs. Neither record exposes WAL
frames or raw storage bytes.

The ledger record is the durable resolution anchor for every local durability
level. `Buffered` and `Written` may lose data bytes after a fault, but the
resolution metadata is still persisted so reopen can distinguish a committed
root, an unpublished intent, and a rejected operation. This metadata guarantee
does not upgrade the operation's local data durability.

After reopen, resolution follows the same deterministic rules:

1. A matching published root and `ReadyToPublish` or `Published` record resolve
   to `Committed` with the strongest durability actually proven. If the
   post-publication pointer or directory sync was not confirmed, the result
   cannot claim `Synced`.
2. A durable `ReadyToPublish` record without its matching root remains
   `Pending` while recovery checks whether publication can be completed. It is
   never silently reported as `Rejected`.
3. A durable rejection record resolves to `Rejected`. A request with the same
   `OperationId` and a different digest is always `OperationIdReuse`.

The operation ledger, WAL, pointer replacement, pointer sync, and directory
sync therefore have one order. The ledger records intent before publication,
the pointer makes a generation visible, post-publication sync establishes the
`Synced` claim, and the ledger records the final published state afterward.

### Storage publication ordering

The storage implementation must use this order for every accepted write:

1. Validate the request, tenant scope, snapshot, operation identity, and
   write-set conflicts without changing the published root.
2. Encode the logical write set into private WAL frames with checksums. Raw
   bytes exist only in this private codec path.
3. Append a prepare record and the write frames to the WAL. The prepare record
   names the operation and expected write-set digest.
4. Apply the write set to a new private root and append a commit marker. The
   old published root remains readable while this work is in progress.
5. Apply the requested local durability action. For `Synced`, write and sync
   the WAL frames, commit marker, referenced data, and temporary manifest. The
   temporary manifest names the complete new root but is not visible yet.
6. Append the `ReadyToPublish` operation-ledger record and durably sync the
   ledger record. This sync is required even when the requested data durability
   is `Buffered` or `Written`; it persists resolution metadata, not data bytes.
7. Atomically rename or otherwise publish the manifest/root pointer. The new
   root may now be visible to live reads. For `Synced`, sync the published
   pointer and the containing directory after that publication. Only after this
   post-publish sync may the Engine return a `Committed` receipt claiming
   `Synced` or claim that the root is durably recoverable.
8. Append and durably sync the `Published` operation-ledger record. If pointer
   or directory sync, or this final ledger sync, fails after publication, the
   caller receives `Unknown`; reopen resolves the operation by its identity and
   root identity without claiming `Synced`.
9. For `Buffered` and `Written`, return only the guarantees defined below,
   after pointer publication and the resolution record complete. No level may
   report visibility before the commit marker and pointer publication sequence
   completes.
10. Release storage locks. Emit a private immutable commit-publication event
   carrying the committed root and storage sequence. Index publication is a
   separate W3-2 concern and may lag.

Recovery ignores prepare frames without a valid commit marker and ignores
manifests whose checksums or root identity do not verify. After atomic pointer
publication, the new root may be visible to live reads. If the post-publish
pointer or containing-directory sync, or the final `Published` ledger sync,
fails, the operation returns `Unknown`,
the operation ledger remains authoritative, and resolution must determine
whether the publication is committed. The result must not claim `Synced` or
durable recoverability until those syncs complete. Lower levels may lose bytes
after a crash, but they still follow the same logical marker, pointer,
lock-release, and event order. The implementation may use a different file
arrangement, but it may not change this order.

### Local durability

`LocalDurability` is exactly:

| Level | Meaning when returned | Loss window |
| --- | --- | --- |
| `Buffered` | The operation and its commit marker are accepted into the process-local WAL buffer and the new root is visible to this live process. No OS write or flush guarantee exists. | Process crash, machine loss, or power loss may remove the operation. |
| `Written` | The WAL and commit marker bytes have been handed to the OS through successful writes, and the new root is published. No fsync or power-loss guarantee exists. | OS cache loss, power loss, or device failure may remove the operation. |
| `Synced` | The WAL, commit marker, referenced data, and temporary manifest are written and synced; the root pointer is atomically published; then the published pointer and containing directory are synced after publication, before the receipt is returned. | The receipt covers the declared adapter/storage fault model only. It does not cover a lost region or remote member. |

These names describe local storage only. `Acknowledgement Level` describes
which replication members confirmed an operation. `Convergence` describes the
replicated state observation. Neither term may be substituted for a local
durability level, and a local `Synced` receipt does not imply remote durability.

### Read snapshots and index generations

`ReadSnapshot` is an immutable value containing a storage sequence, a tenant
scope, and an `IndexGenerationSet` observed when the snapshot was created. It
can be copied and used by concurrent readers. No method mutates it, advances
it, or silently changes its tenant scope. A later query obtains a new snapshot.

Each `IndexGeneration` contains an index namespace, a persisted epoch, and a
monotonic non-wrapping sequence. The namespace identifies the tenant and
logical index definition. The epoch is created with that namespace and is
persisted in the index manifest, so a restart cannot reset it. A new epoch is
allowed only when the namespace is explicitly recreated under its migration
contract. The sequence increases within one namespace and epoch. Generations
from different namespaces, or from different epochs, are never ordered or
compared as if they belonged to one sequence. Builders use compare-and-publish
or an equivalent serialized rule. A stale, replayed, or reordered index build
can never lower the visible generation for its own namespace.

Index publication follows storage publication, not the reverse. Once the
storage lock is released, an index builder reads the immutable committed root,
builds privately, and publishes its segment with a monotonic generation. It
does not wait while holding a storage lock and it never asks Storage to publish
the data root.

For a query that uses multiple indexes, the `QueryPlan` names each participating
namespace. Execution checks each selected index against the matching generation
in the snapshot's `IndexGenerationSet`, using the namespace and persisted epoch
as part of the match. A strict policy requires every selected index to meet the
snapshot's generation for its namespace. An allow-stale policy records the
older generation used for each namespace and returns that map in the result.
If a generation is missing or too old, the query uses the approved stale
fallback, waits through a bounded synchronous operation, or returns a typed
`IndexNotReady` error. It never silently returns a result with an older
generation for a strict namespace request.

### Engine interface and invariants

The public Engine interface is intentionally small. The exact Rust naming may
follow repository conventions, but the following facts are mandatory:

```rust
impl Engine {
    pub fn open(config: EngineConfig) -> Result<Self, EngineOpenError>;

    pub fn read_snapshot(
        &self,
        scope: &TenantScope,
    ) -> Result<ReadSnapshot, EngineError>;

    pub fn begin(
        &self,
        scope: &TenantScope,
        snapshot: ReadSnapshot,
        operation: OperationId,
    ) -> Result<Transaction, EngineError>;

    pub fn resolve_commit(
        &self,
        scope: &TenantScope,
        operation: OperationId,
    ) -> Result<DurableResolution, EngineError>;
}
```

`Transaction` is opaque. It exposes typed document operations and one commit
operation. It may not expose a WAL frame, mutable storage root, lock, or raw
bytes. A transaction is tied to one tenant scope, one immutable read snapshot,
and one operation identity. A successful commit publishes all writes in its
write set atomically. Conflicts return `Rejected` with a conflict reason.

Engine methods are synchronous. A future async adapter may call them from a
bounded blocking pool, with a deadline, cancellation propagation, and an
explicit overload error when capacity is exhausted. An async wrapper must not
create an unbounded thread or queue, hold a core lock across an await, or turn
cancellation into an unknown success without preserving the operation identity.

## Query contract

`QuerySpec` is a typed description of source, predicates, ordering, projection,
paging, aggregation, and the requested staleness policy. It cannot contain a
raw query string that bypasses validation or an untyped route. `QueryPlan` is
immutable after compilation and can be reused with compatible snapshots and
parameters. A plan does not own a mutable index or storage lock.

The query path is:

1. Validate the `QuerySpec` and tenant scope.
2. Compile or retrieve an immutable `QueryPlan`.
3. Select an index whose generation satisfies the plan's policy, or use the
   documented stale fallback.
4. Execute against the requested immutable `ReadSnapshot`.
5. Return typed rows/documents, ordering metadata, staleness information, and
   the `IndexGeneration` used.

The plan must preserve V1-observable result ordering, null/missing behavior,
error classes, paging boundaries, and cancellation semantics for each parity
row that later receives a Proof ID. Query builders may add convenience methods,
but they compile to this one execution contract. Query code cannot mutate
documents, publish indexes, or select a private admin operation.

The first Query tracer tests must cover a known result and ordering, missing
versus null predicates, a strict generation request, a stale fallback, plan
reuse with an immutable snapshot, cancellation at a bounded point, and typed
authorization rejection. These are contract tests, not an assertion that V1
parity already exists.

## Data Family contract

A Data Family is a V1-named group of related capabilities whose public rules
remain explicit even when storage, query, replication, and transfer later
reuse its values. W3 gives families a typed logical seam. It does not turn
family handling into a generic command channel or move another seam's
ownership into `src/families/**`.

### Public logical interface

The public interface is a closed registry of typed requests and typed results.
The exact variant fields follow the corresponding contract fragment, but the
shape and ownership are fixed here:

```rust
pub enum FamilyKind {
    Attachments,
    Counters,
    TimeSeries,
    Revisions,
    CompareExchange,
    Identities,
    ExpirationRefresh,
    Archival,
    SchemaValidation,
    ConflictTombstones,
}

pub struct FamilyReadContext {
    pub tenant: TenantScope,
    pub snapshot: ReadSnapshot,
}

pub enum FamilyReadRequest {
    AttachmentMetadata(AttachmentRead),
    CounterGroup(CounterRead),
    TimeSeriesRange(TimeSeriesRead),
    RevisionPage(RevisionRead),
    CompareExchangeValue(CompareExchangeRead),
    IdentityRange(IdentityRead),
    Lifecycle(LifecycleRead),
    Validation(ValidationRead),
    Tombstones(TombstoneRead),
}

pub enum FamilyWriteRequest {
    AttachmentMutation(AttachmentWrite),
    CounterBatch(CounterBatchWrite),
    TimeSeriesBatch(TimeSeriesBatchWrite),
    RevisionCommand(RevisionWrite),
    CompareExchangeCondition(CompareExchangeWrite),
    IdentityAllocation(IdentityWrite),
    LifecycleUpdate(LifecycleWrite),
    ArchivalUpdate(ArchivalWrite),
    SchemaValidationUpdate(ValidationWrite),
    TombstoneOperation(TombstoneWrite),
}

pub struct FamilyStore { /* Engine-owned implementation */ }

impl FamilyStore {
    pub fn read(
        &self,
        context: FamilyReadContext,
        request: FamilyReadRequest,
    ) -> Result<FamilyReadResult, FamilyError>;

    pub fn stage(
        &self,
        transaction: &mut Transaction,
        request: FamilyWriteRequest,
    ) -> Result<(), FamilyError>;
}
```

`FamilyReadResult` is a closed typed result set for attachment metadata and
bounded readers, counter groups, time-series pages, revision pages,
compare-exchange values, identity ranges, lifecycle state, validation state,
and tombstone views. Attachment content uses an opaque bounded reader and
typed chunks. It never exposes a storage page, WAL frame, codec buffer, or
generic byte escape hatch. `FamilyStore` is obtained from the durable Engine;
it has no independent durable or in-memory production constructor.

Family writes can only be staged into an Engine `Transaction`. The transaction
supplies the single `OperationId`, snapshot, conflict check, and commit
outcome. A family operation cannot commit independently or return a remote
`Acknowledgement Level`. Local `Buffered`, `Written`, and `Synced` outcomes
come from Engine storage. Replication `Convergence`, backup/import receipts,
and Protocol authorization belong to their later seams.

### Family invariants and internal seams

- Every read and staged write carries one validated `TenantScope`; family
  values, attachment metadata, cursors, and errors cannot cross tenants.
- A transaction that changes documents and one or more families publishes one
  transaction marker. Validation, conflict, abort, retry, and commit never
  expose a partial family result.
- Family-specific rules remain typed. Counter overflow, invalid time-series
  arity or ranges, invalid revision references, malformed validation policy,
  duplicate attachment names, and invalid lifecycle values are distinct
  `FamilyError` cases.
- Family reads use the immutable `ReadSnapshot`. A later Engine commit creates
  a later snapshot; no family reader mutates or silently advances its snapshot.
- Large attachment and time-series reads use bounded readers and cursors with
  explicit range, cancellation, disposal, and backpressure behavior. W3-F
  proves the logical limits; W4 owns the full streaming qualification.
- Families emit typed, read-only projection inputs for Query and Index through
  a crate-private adapter. Family code does not plan queries, choose indexes,
  publish `IndexGeneration`, or edit `src/query/**` or `src/index/**`.
- Storage owns WAL, manifest, pointer publication, recovery, and local
  durability. Families do not inspect raw storage bytes or implement recovery.
- Replication, sharding, backup/import, logical migration, Protocol, and
  Platform authorization consume the family seam through later adapters. W3-F
  leaves those concerns as explicit unresolved handoffs.

### W3-F data-family packet

`W3-F` is the W3 family implementation packet. It starts from the accepted W3-1
Storage plus Engine commit in worktree `terrabasev2-w3-families` and owns only:

- `src/families/**`;
- `tests/families/**`;
- explicitly named family documentation under `docs/families/**`.

It depends on the W3-1 public Engine transaction and immutable snapshot
contract. It must not edit `src/storage/**`, `src/engine/**`, `src/query/**`,
`src/index/**`, `src/replication/**`, `src/backup/**`, `src/migration/**`,
`src/adapters/**`, or their tests. It does not edit `src/lib.rs`; the W3-1
narrow declaration/re-export exception remains the only behavior-module
export exception. Cargo files, root CI, generated parity output, overlays,
and shared manifests remain integrator-owned.

W3-F must finish before W3-2 Query and Index starts. Query's family-aware
projection and index-source tests depend on the typed family result contract.
This is a safe sequential order, not accidental serialization. W3-F has no
query or index implementation authority, and W3-2 has no family mutation
authority. W3-3 Protocol starts only after both W3-F and W3-2 are accepted.

The public-seam red tests begin red at `FamilyStore::read` and
`FamilyStore::stage`:

- `attachment_metadata_round_trip_is_tenant_scoped`;
- `counter_crud_preserves_signed_values_and_missing_errors`;
- `counter_batch_is_atomic_across_increment_delete_and_recreate`;
- `counter_overflow_rejects_without_partial_state`;
- `time_series_append_and_delete_preserve_timestamp_order`;
- `revision_page_and_revert_keep_snapshot_identity`;
- `expiration_and_refresh_updates_do_not_resurrect_deleted_documents`;
- `schema_validation_rejects_invalid_writes_without_mutation`;
- `family_tombstone_blocks_stale_local_resurrection`;
- `family_transaction_abort_hides_all_staged_family_changes`;
- `family_read_snapshot_remains_immutable_after_engine_commit`;
- `family_scope_rejects_cross_tenant_reads_and_writes`.

These tests observe typed results, errors, atomicity, isolation, and snapshot
behavior through the family seam. They do not assert V1 parity or create
Recovery, Replication, Benchmark, or migration receipts. Family streaming,
query/index, replication, backup/import, and remote-adapter tests remain in
their named packets.

### Family parity-row mapping and handoffs

All 46 family rows remain `unresolved` with empty Proof ID lists. The mapping
below assigns the first owner and prevents a family worker from swallowing a
neighboring seam:

| Owner packet | Family rows | Scope |
| --- | --- | --- |
| W3-F Families | PB-3000, PB-3001, PB-3003, PB-3005, PB-3009, PB-3015, PB-3016, PB-3019, PB-3024, PB-3025, PB-3026, PB-3029, PB-3030, PB-3031, PB-3033, PB-3036, PB-3038, PB-3039, PB-3042 | Initial typed family CRUD, lifecycle, validation, identity, transaction, and tenant-isolation behavior |
| W3-2 Query/Index | PB-3010, PB-3020, PB-3040 | Family query sources, projections, index maintenance, staleness, and ordering. Query owns the query/index code. |
| W4 Streaming | PB-3002, PB-3014, PB-3017, PB-3023, PB-3041, PB-3043 | Bounded family readers, changes, subscriptions, cancellation, acknowledgement, and backpressure |
| W4 Replication/qualification | PB-3004, PB-3006, PB-3011, PB-3012, PB-3018, PB-3021, PB-3027, PB-3028, PB-3032, PB-3034, PB-3035, PB-3037, PB-3044 | Family replication, conflicts, tombstones, sharding, archival interactions, and Region/Member behavior |
| W4 Backup/migration | PB-3007, PB-3013, PB-3022, PB-3045 | Family backup, import/export, restore, Logical Migration, manifests, and checksums |
| W5 adapters | PB-3008 | Remote attachment destination and typed credential/authorization behavior |

The W3-F handoff to W3-2 contains the accepted W3-F commit, public type
exports, family projection-input contract, redacted focused-test output,
changed paths, and unresolved row list. The W3-2 handoff returns query/index
evidence without changing family ownership. Later packets add Proof IDs only
after their tests and required receipts exist.

## Protocol and Platform contract

`PublicOperation` is a closed typed set of approved public operations. Every
mutation includes `OperationId`; every request includes a deadline and a
cancellation handle or equivalent bounded cancellation token. The adapter
returns typed `PublicResponse` values and typed errors. It does not expose raw
bytes, arbitrary method/path dispatch, private control-plane routes, or a
generic HTTP forwarding method.

Every public Protocol operation must pass `PlatformAdapter` authorization
before it reaches the Engine. `PlatformGrant` is validated only by
`PlatformAdapter`. Validation covers the tenant, principal, operation
permission, confirmation requirement, expiry, and secret-handling policy. The
adapter produces an opaque `TenantScope` and an audited typed Engine request.
The Engine receives no grant and cannot make a Platform authorization
decision. Protocol receives no grant authority and cannot mint a scope. A
direct Engine adapter is allowed only for `cfg(test)` or an internal trusted
path; it cannot be mounted behind a public Protocol route.

Protocol tracer tests must begin red for:

- an approved typed operation with a stable operation identity;
- malformed or unknown operation rejection without generic forwarding;
- private control-plane operation rejection;
- missing, expired, wrong-tenant, and insufficient `PlatformGrant` rejection;
- confirmation and audit requirements on mutating operations;
- secret redaction in typed errors and audit records;
- bounded cancellation and deadline errors that preserve operation identity;
- adapter behavior with an in-process test transport and a production-shaped
  transport adapter.

The core remains sync. A future async Protocol Adapter must use bounded
blocking capacity, deadlines, cancellation, and a typed overload error. It may
not alter commit outcome semantics or claim that cancellation proves rejection.

## W3 tracer packets and red tests

Builders use vertical TDD slices. Each named test starts red at its public
seam, receives the smallest implementation that makes it green, and then
refactors only after the behavior is observed. Private helper tests do not
replace these seam tests.

### W3 dependency graph and implementation sequence

The W3 behavior sequence is explicit:

```text
W3-0 type/export preparation
          |
W3-1 Storage + Engine, exclusive recovery lane
          |
W3-F Data Families
          |
W3-2 Query + Index
          |
W3-3 Protocol Adapter
```

W3-F follows W3-1 because it needs the durable Engine transaction and
immutable `ReadSnapshot`, but it does not need Query or Index implementation.
W3-2 follows W3-F because its family-query and family-index tracer slices
consume the typed family result and projection-input contract. The disjoint
path ownership is still enforced at every step. W3-1 remains the only packet
allowed to modify or qualify Storage/WAL/recovery behavior, and W3-F cannot
pull Query, Index, replication, Protocol, or backup code into its scope.

The sequence handoffs are:

1. W3-0 returns the shared logical types and exports, with an exact prep
   commit recorded by the parent.
2. W3-1 returns the Storage plus Engine commit, operation-ledger and
   publication-event contract, redacted recovery/storage evidence, and its
   unresolved PB obligations.
3. W3-F returns the family public types, transaction-bound implementation,
   family projection-input contract, redacted family tests, and the complete
   PB-3000 through PB-3045 ownership map.
4. W3-2 returns Query/Index behavior and family-aware query evidence without
   taking ownership of `src/families/**`.
5. W3-3 returns typed Protocol evidence. Every public call still passes the
   Platform Adapter later, in its own adapter packet.

### W3-1, Storage plus Engine

W3-1 is one sequential packet. The Storage and Engine contracts are inseparable
for the first tracer because operation identity, WAL publication, commit
outcome, and durable resolution must be proved together.

The initial red test names are:

- `engine_opens_only_configured_durable_storage`;
- `synced_commit_reopens_with_the_published_document`;
- `publication_does_not_expose_a_root_before_the_commit_marker`;
- `recovery_discards_prepare_frames_without_a_valid_commit_marker`;
- `recovery_rejects_bad_checksum_and_truncated_frame`;
- `retrying_the_same_operation_id_returns_one_commit`;
- `different_payload_with_same_operation_id_is_rejected`;
- `unknown_commit_resolves_from_the_durable_operation_ledger`;
- `pending_resolution_is_not_reported_as_rejection`;
- `ready_to_publish_ledger_record_is_durable_before_pointer_replacement`;
- `reopen_resolves_a_ready_to_publish_record_by_operation_id`;
- `transaction_conflict_returns_rejected_without_partial_writes`;
- `one_transaction_publishes_all_writes_atomically`;
- `buffered_written_and_synced_have_distinct_loss_windows`;
- `read_snapshot_remains_immutable_after_a_later_commit`;
- `commit_publication_event_is_emitted_after_storage_locks_release`;
- `commit_publication_event_carries_an_immutable_root_and_storage_sequence`;
- `commit_publication_event_is_not_emitted_before_pointer_publication`;
- `post_publication_pointer_sync_failure_returns_unknown_and_reopens_deterministically`;
- `post_publication_directory_sync_failure_returns_unknown_and_reopens_deterministically`;
- `tenant_scope_cannot_change_during_a_transaction`;
- `production_api_has_no_ephemeral_durable_engine_constructor`.

W3-1 is the exclusive recovery and storage lane. No other packet may modify or
qualify storage/WAL/recovery behavior concurrently. W3-1 may define and test
only a crate-private immutable `CommitPublicationEvent` sink emitted after
storage locks release. It must not edit `src/index/**` or run index
implementation tests. Builder evidence is redacted and includes the exact
source/storage-format identity, test result, fault setup, and limits without
credentials, customer data, raw backup contents, or private host paths.

The first tracer should be a synced single-document write, reopen, and read.
The next tracer should interrupt the response after publication and resolve the
same `OperationId`. Recovery Receipts do not come from these ordinary unit or
integration tests. Formal Recovery Receipts come only from the exclusive
recovery qualification lane after crash, restart, restore, migration, and
fault scenarios have run with the required redaction and source identity.

The two post-publication fault tests inject failures through a private,
test-only faulting storage adapter at the public Engine commit seam. One fails
the pointer-file sync after atomic replacement; the other fails the containing
directory sync after pointer publication. Each test asserts `Unknown` with the
original `OperationId`, permits the controlled fixture's live-read visibility,
asserts that no `Synced` claim is returned, reopens the Engine, and resolves
the same operation deterministically from the persisted ledger and root
identity. Retrying the same logical request with that `OperationId` returns the
same resolution, while a different digest is rejected as `OperationIdReuse`.
The tests do not inspect raw pages, WAL frames, codec bytes, or private lock
state. They prove the public outcome and durable-resolution rule.

### W3-2, Query and Index

W3-2 starts only from an accepted W3-F Families commit. Its red tests are:

- `query_plan_preserves_result_ordering_on_an_immutable_snapshot`;
- `null_missing_and_exists_have_distinct_results`;
- `strict_generation_rejects_an_index_that_is_too_old`;
- `allowed_stale_query_reports_the_generation_it_used`;
- `index_delta_is_derived_from_the_immutable_commit_publication_event`;
- `index_publication_does_not_hold_the_storage_lock`;
- `index_generation_never_moves_backward_within_its_namespace_and_epoch`;
- `index_rebuild_publishes_a_monotonic_generation`;
- `replayed_index_build_cannot_overwrite_a_newer_generation`;
- `plan_reuse_does_not_mutate_the_snapshot`;
- `projection_and_paging_have_typed_errors_and_boundaries`;
- `query_cancellation_is_bounded_and_does_not_leak_a_lock`;
- `query_authorization_rejects_a_wrong_tenant_scope`.

W3-2 does not claim the complete V1 query/index matrix. It proves the chosen
seam and leaves each row unresolved until the parity test and Proof ID exist.

### W3-3, Protocol

W3-3 starts only from accepted W3-2 behavior and owns only the Protocol Adapter
paths. Its red tests are the typed-operation, grant, private-operation,
confirmation, audit, secret-redaction, deadline, cancellation, and overload
cases listed in the Protocol section above. It must use an in-process adapter
for deterministic tests and keep transport-specific details out of Engine.

## W3 parity obligations and non-claims

The W2 inventory has 258 rows, 251 V1-backed rows, 267 pinned source
citations, 231 exact test citations, and 42 structured replication/storage
test gaps. All rows remain `unresolved` with empty Proof ID lists. W3 may add
tests and implementation evidence, but it must not change a row to
`implemented` or `qualified` merely because a tracer is green.

The 42 W2 gaps are explicit W3 test obligations and continue to block strict
qualification. They are PB-5000 through PB-5041:

| IDs | Obligations |
| --- | --- |
| PB-5000 to PB-5004 | replication; storage/WAL/recovery; environment lifecycle; file headers/versioning; WAL ordering and sync |
| PB-5005 to PB-5009 | storage transactions/MVCC/concurrency; trees/tables/paging; scratch buffers; compaction; restart/recovery |
| PB-5010 to PB-5015 | corruption/disk-full; encryption; schema upgrade; full backup/snapshot; incremental backup chain; recovery-point manifest |
| PB-5016 to PB-5020 | consensus commit; elections; log divergence/snapshots; membership/topology; failover/member loss |
| PB-5021 to PB-5027 | change vectors; conflict resolution; tombstones; anti-entropy/catch-up; external destinations; write acknowledgement; pull hub/sink |
| PB-5028 to PB-5034 | attachments; counters/time-series; revisions; local progress; health/convergence observations; restart/rejoin; active-active |
| PB-5035 to PB-5041 | commands/snapshots; sharding topology; buckets; resharding/bucket migration; orchestrator coordination; replication/backup/rejoin; failover/cluster transactions |

W3-1 owns the first storage and Engine tracer coverage for these obligations.
W3-1 must not claim that the 42 rows are complete. Consensus, replication,
sharding, backup, and recovery behavior that exceeds the tracer contract
belongs to later exclusive lanes. PB-5042 through PB-5048 remain charter-backed
V2 additions with an explicit `No claim that V1` disposition in their contract
facts; they are not silently counted as V1 parity.

The broader packet mapping is:

| Packet | Rows it may exercise | Claim allowed in the packet |
| --- | --- | --- |
| W3-1 | document/transaction rows needed for the tracer, plus PB-5000 through PB-5041 | Contract and focused-test evidence only; no parity qualification |
| W3-F | family rows PB-3000 through PB-3045 assigned by the family mapping | Contract and focused-test evidence only; no parity qualification |
| W3-2 | query and index rows PB-4000 through PB-4061 that its tests cover, including family query rows only after W3-F | Contract and focused-test evidence only; no parity qualification |
| W3-3 | approved protocol rows PB-1000 through PB-1033 that its tests cover | Typed adapter evidence only; no Platform authorization claim until the Platform packet |

Every later overlay update is integrator-owned and must name the exact test,
source identity, scenario, and Proof ID. A green W3 test never erases an
unresolved V1 test gap.

## Checks and evidence

Before integration, each W3 packet reports its exact commit, worktree, changed
paths, tests, assumptions, and risks. The parent runs the shared checks after
sequential integration.

Required checks for this contract document and W2 synchronization are:

- Markdown syntax and every in-repository link resolves from the repository
  root. The workspace charter uses the portable identifier
  `workspace-root:specs/2026-08-23-terrabase-v2-goal.md`; the parent resolves it
  to an absolute path in assignment metadata for every worktree. No arbitrary
  worktree-relative path is assumed. The design's in-repository ADR and W2
  links resolve locally.
- JSON syntax, parity generation, parity `--check`, deterministic inventory,
  parity tooling tests, and the pinned V1 citation audit.
- Secret scan over declared existing roots.
- `cargo fmt --check`, locked debug and release tests, `cargo check --locked`,
  clippy with warnings denied, and documentation build.
- `git diff --check`, clean status after commit, and named-path staged diff
  inspection.

Optional Draft 2020-12 validation through the Python `jsonschema` package is
`UNRUN` when that package is unavailable. `cargo-deny` is likewise recorded as
`UNRUN` when the executable is unavailable. Neither result may be reported as
a pass.

The evidence hierarchy remains strict. Ordinary tests can support a Proof ID
for their observable behavior. Formal Recovery Receipts require the exclusive
recovery qualification lane. Replication Receipts require the multi-region
qualification lane. Benchmark Receipts require the exclusive benchmark lane
and TerraBase.Benchmark statistical and correctness gates. This document is a
design contract, not any of those receipts.

## Open decisions and limits

The following decisions remain outside this W3 contract and require a named
parent decision with evidence before qualification:

- replication algorithm, consistency defaults, `Acknowledgement Level` values,
  conflict policy, and active-active behavior;
- production topology limits, region placement, failure-domain assumptions,
  and cross-region timing limits;
- optional, legacy, admin, and protocol-version parity dispositions;
- V1 logical migration input format and the final distribution license;
- benchmark workload set and the final V1/V2 environment allocation.

W3 may define types and tests that make these decisions observable. It may not
silently choose them. The Engine/Platform/control-plane separation, no runtime
license enforcement, new V2 storage format, manifest-verified logical
migration, typed Protocol Adapter, and fixed DevHost Authentication and
Dashboard HTTPS ports remain accepted workspace rules.

## Acceptance checklist

This document is ready for the W3 builders when all of the following remain
true:

- Storage and Engine share one publication and operation-resolution contract.
- Query consumes immutable snapshots and publishes monotonic index generations
  without waiting under a storage lock.
- `Buffered`, `Written`, and `Synced` have separate local meanings and are not
  confused with `Acknowledgement Level` or `Convergence`.
- `PlatformGrant` stays at the Platform Adapter seam, and Protocol has no
  generic or private operation escape hatch.
- Data Families have a closed typed logical seam, a W3-F packet, explicit
  family-row ownership, and no authority over Query, Storage, replication,
  backup, Protocol, or Platform behavior.
- Production has no public durable in-memory Engine constructor, and raw bytes
  stay in private storage codec/WAL code.
- The W3-1, W3-F, W3-2, and W3-3 worktrees and collision ownership are explicit.
- The 42 PB-5000 through PB-5041 obligations remain visible and unresolved
  until their required tests and receipts exist.
- W2 is reviewed GO at integrated and pushed commit `8771f13`; strict
  qualification remains blocked by the same 42 gaps.
