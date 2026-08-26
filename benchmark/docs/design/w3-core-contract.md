# W3 core contract: storage, engine, data families, query/index, and typed adapters

Status: accepted implementation contract for W3, recorded 2026-08-23.

This document turns the W3 design review into rules that builders and tests can
use. It covers the Storage, Engine, Data Family, Query/Index, and typed
Protocol/Platform Adapter seams. It does not claim that any TerraBaseV2
behavior has been implemented or qualified. The
first implementation packet is W3-1, one sequential builder for Storage plus
Engine because their first durable commit contract cannot be split safely.

The contract is based on the reviewed W2 matrix at commit `8771f13`, the five
W3 behavior-seam design packets, the cross-seam coherence review, and the
reliability corrections accepted by the parent. Those inputs are design
evidence. They are not behavior Proof IDs.

The governing records are the workspace-root execution charter identified as
`workspace-root:specs/GOAL.md`, the
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

W3 has five behavior seams with different failure costs. Storage must
distinguish an accepted local write from a write that survives the declared
local durability point. Engine must make retries safe when a response is lost
after publication. Data Families must preserve typed tenant-scoped rules while
sharing Engine transactions. Query/Index must read a coherent immutable view
while indexes catch up. Typed adapters must carry public operations without
becoming a private control plane or bypassing Platform authorization.

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
6. The Platform Adapter validates caller data and creates any short-lived
   private `PlatformGrant`. The grant is not an Engine credential or a
   Protocol authority and never crosses the trusted Engine port.
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
| Authority placement | Keep the crate-private `PlatformGrant` inside Platform Adapter validation. Engine and Protocol receive no grant or grant authority. |
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
Platform Adapter call; only that Platform path may reach the trusted Engine
port. HTTP, TCP, and future transports are adapters behind the port. No raw
request body, route string, private control-plane operation, or generic HTTP
forwarding method crosses this seam.

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

| Type | Visibility and ownership | Required facts |
| --- | --- | --- |
| `TenantId`, `TenantScope`, `DocumentId`, `CollectionName`, `OperationId`, `StorageSequence`, `ExpectedVersion` | `pub`; W3-0 `src/types.rs`, then later integrator | Validated logical identifiers and versions. No raw storage keys. `OperationId` remains stable across retries. |
| `DocumentValue`, `Document`, `DocumentMetadata` | `pub`; W3-0 `src/types.rs`, then later integrator | Structured values and metadata. They never contain storage bytes, WAL frames, or customer paths. |
| `Deadline`, `Cancellation` | `pub`; W3-0 `src/types.rs`, then later integrator | Bounded request deadline and cancellation state used by typed operations. |
| `ReadSnapshot`, `IndexGeneration`, `IndexGenerationSet`, `LocalDurability` | `pub`; W3-0 `src/types.rs`, then later integrator | Immutable engine identity, tenant scope, storage sequence, index generations, and exactly `Buffered`, `Written`, `Synced`. |
| `EngineConfig`, `Engine` | `pub`; W3-0 declarations, then W3-1 `src/engine/` | `EngineConfig::durable` requires a durable location. `Engine` owns storage identity and borrowed stores. |
| `Transaction` | `pub`; W3-0 declaration, then W3-1 `src/engine/` | Opaque engine-borrowing write set tied to one snapshot, scope, and `OperationId`. |
| `EngineConfigError`, `EngineOpenError`, `EngineError`, `EngineClosed`, `EngineShutdownError` | `pub`; W3-0 declarations, then W3-1 `src/engine/` | Typed configuration, open, operational, closed, and shutdown errors. They expose no path, credential, lock, WAL, page, or codec. |
| `CommitReceipt`, `CommitOutcome`, `CommitError`, `UnknownCommit`, `UnknownReason`, `DurableResolution` | `pub`; W3-0 declarations, then W3-1 `src/engine/` | Local commit and durable-resolution outcomes. `CommitReceipt` contains only `OperationId`, `StorageSequence`, and `LocalDurability`. |
| `FamilyKind`, `FamilyReadContext`, `FamilyReadRequest`, `FamilyWriteRequest`, `FamilyReadResult`, `FamilyError`, `FamilyStore<'engine>` | `pub`; W3-0 declarations, then W3-F `src/families/` | Closed typed family seam. `FamilyStore<'engine>` borrows `Engine`; context validation prevents cross-tenant or cross-engine reads. |
| `AttachmentRead`, `AttachmentWrite`, `AttachmentMetadataResult`, `CounterRead`, `CounterBatchWrite`, `CounterGroupResult`, `TimeSeriesRead`, `TimeSeriesBatchWrite`, `TimeSeriesPage`, `RevisionRead`, `RevisionWrite`, `RevisionPage`, `CompareExchangeRead`, `CompareExchangeWrite`, `CompareExchangeValueResult`, `IdentityRead`, `IdentityWrite`, `IdentityRangeResult`, `LifecycleRead`, `LifecycleWrite`, `LifecycleResult`, `ArchivalReadRequest`, `ArchivalWrite`, `ArchivalReadResult`, `ValidationRead`, `ValidationWrite`, `ValidationResult`, `TombstoneRead`, `TombstoneWrite`, `TombstoneResult` | `pub`; W3-0 declarations, then W3-F `src/families/` | Nested family inputs and results remain typed, bounded, tenant-scoped, and free of generic bytes or storage handles. |
| `QueryStore<'engine>`, `QuerySpec`, `QuerySource`, `QueryPredicate`, `QueryOrdering`, `QueryProjection`, `QueryPaging`, `StalenessPolicy`, `QueryPlan`, `QueryResult`, `QueryError` | `pub`; W3-0 declarations, then W3-2 `src/query/` and `src/index/` | Typed compilation and snapshot execution. `QueryStore<'engine>` borrows `Engine`; plans are immutable and generation-aware. |
| `PrincipalId`, `CallerEvidence`, `CredentialKind`, `CredentialInput`, `CallerContext`, `CallerContextError`, `ProtocolAdapter`, `ProtocolError`, `PublicOperation`, `PublicResponse` | `pub`; W3-0 declarations, then W3-3 `src/adapters/protocol/` | Structurally validated but untrusted caller evidence and closed typed forwarding. Credentials are redacted and non-`Clone`; no grant, permission claim, raw route/body, generic HTTP, arbitrary SQL, or private operation. |
| `PlatformAdapter<'engine>`, `PlatformError` | `pub`; W3-0 declarations, then W5 `src/adapters/platform/` | Platform-only validation, tenant isolation, confirmation, audit, expiry, and secret handling. The adapter borrows the trusted `EnginePort`; its constructor is W5-owned. |
| `DocumentReadRequest`, `DocumentWriteRequest`, `FamilyOperation`, `QueryOperation`, `QueryInput`, `V1QueryText`, `RequestValidationError`, `QueryInputError` | `pub`; W3-0 `src/operations.rs`, then later integrator | Closed nested inputs used only by `PublicOperation`; named constructors validate fields and carry required identity/deadline/cancellation data. V1-compatible query text is typed input, not a generic query escape hatch. |
| `RetryDisposition` | `pub`; W3-0 `src/types.rs`, then later integrator | Closed retry policy for rejected commits. `Unknown` is resolved with the same `OperationId` and is never reduced to a guessed retry disposition. |

The public `Engine` has no `new_in_memory`, `from_memory`, or equivalent
durable constructor. `Engine::open(config)` is the production opening path and
must reject a missing or non-durable storage location. A test-only constructor
may be declared `#[cfg(test)] pub(crate)` inside the owning crate, or a named
non-durable fixture may live under test support with no production feature that
exports it. Integration tests must use the durable temporary-directory path
when they test the real Engine contract. `EngineConfig::durable` selects the
V2 Storage Format. It does not open V1 files directly; V1 input reaches V2 only
through the later manifest-verified Logical Migration contract.

`PlatformGrant` is crate-private to the Platform Adapter implementation. The
Engine never accepts, stores, inspects, or creates it. The Protocol Adapter
never accepts, stores, creates, returns, or mints it. Platform validates a
borrowed `CallerContext`, creates a short-lived private grant, converts the
approved operation to a closed `EngineRequest`, and discards the grant before
calling the trusted Engine port. A validated `TenantScope` is the only tenant
context passed to Engine operations.

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
| `EngineIdentity`, `EnginePort`, `EngineRequest`, `EngineResponse` | `src/engine/` and `src/operations.rs` | Trusted in-process Platform-to-Engine seam. `EnginePort` is `pub(crate)` and accepts no `PlatformGrant`. |
| family physical records, retention workers, `FamilyProjectionInput` | `src/families/` | Family implementation details; projection inputs are typed and read-only at the Query adapter seam |
| `IndexSegment`, `IndexDelta`, `IndexBuilder`, index lock | `src/index/` | Internal index publication and generation machinery |
| planner AST, reducer state, scorer, wire codec | `src/query/` and adapters | Never expose implementation or raw transport bytes |
| `PlatformGrant`, `LocalPlatformGrant`, `TrustedAuthorizer`, authorization policy, `PlatformPort`, `map_engine_error`, `map_platform_error`, audit sink | `src/adapters/platform/` | Crate-private authorization state and pure error mapping. The trusted authorizer is supplied only by Platform wiring; the grant is short-lived, never re-exported, never returned, and never accepted by Protocol or Engine. |

No public method may return a byte slice, WAL frame, file offset, manifest
path, internal lock, or private route descriptor as a substitute for a typed
contract.

### W3 public signatures and declaration-only registry

The following Rust-like declarations are the compile surface that W3-0
prepares. They are declarations, not implementations. Every type named in a
public signature is listed in the ownership table above or the crate-private
table below.

```rust
impl EngineConfig {
    pub fn durable(location: impl Into<std::path::PathBuf>)
        -> Result<Self, EngineConfigError>;
}

impl Deadline {
    pub fn after(duration: std::time::Duration)
        -> Result<Self, RequestValidationError>;
}

impl Cancellation {
    pub fn new() -> Self;
    pub fn cancel(&self);
    pub fn is_cancelled(&self) -> bool;
}

impl Engine {
    pub fn open(config: EngineConfig) -> Result<Self, EngineOpenError>;

    pub fn read_snapshot(
        &self,
        scope: &TenantScope,
    ) -> Result<ReadSnapshot, EngineError>;

    pub fn begin<'engine>(
        &'engine self,
        scope: &TenantScope,
        snapshot: ReadSnapshot,
        operation: OperationId,
    ) -> Result<Transaction<'engine>, EngineError>;

    pub fn queries<'engine>(&'engine self) -> QueryStore<'engine>;

    pub fn families<'engine>(&'engine self) -> FamilyStore<'engine>;

    pub fn resolve_commit(
        &self,
        scope: &TenantScope,
        operation: OperationId,
    ) -> Result<DurableResolution, EngineError>;

    pub fn shutdown(self) -> Result<(), EngineShutdownError>;
}

impl<'engine> Transaction<'engine> {
    pub fn read(&self, id: &DocumentId)
        -> Result<Option<Document>, EngineError>;

    pub fn put(
        &mut self,
        document: Document,
        expected: ExpectedVersion,
    ) -> Result<(), EngineError>;

    pub fn delete(
        &mut self,
        id: DocumentId,
        expected: ExpectedVersion,
    ) -> Result<(), EngineError>;

    pub fn commit(self, durability: LocalDurability) -> CommitOutcome;

    pub fn operation_id(&self) -> &OperationId;
}

impl FamilyReadContext {
    pub fn new(tenant: TenantScope, snapshot: ReadSnapshot) -> Self;
    pub fn try_new(
        tenant: TenantScope,
        snapshot: ReadSnapshot,
    ) -> Result<Self, FamilyError>;
}

impl<'engine> FamilyStore<'engine> {
    pub fn read(
        &self,
        context: FamilyReadContext,
        request: FamilyReadRequest,
    ) -> Result<FamilyReadResult, FamilyError>;

    pub fn stage(
        &self,
        transaction: &mut Transaction<'engine>,
        request: FamilyWriteRequest,
    ) -> Result<(), FamilyError>;
}

impl<'engine> QueryStore<'engine> {
    pub fn compile(&self, input: QueryInput)
        -> Result<QueryPlan, QueryError>;

    pub fn execute(
        &self,
        plan: &QueryPlan,
        snapshot: &ReadSnapshot,
    ) -> Result<QueryResult, QueryError>;
}

pub struct ProtocolAdapter<'platform> { /* borrows a private PlatformPort */ }

impl<'platform> ProtocolAdapter<'platform> {
    pub(crate) fn new(platform: &'platform dyn PlatformPort) -> Self;

    pub fn from_platform(platform: &'platform PlatformAdapter<'_>) -> Self;

    #[cfg(test)]
    pub(crate) fn new_with_platform_port(
        platform: &'platform dyn PlatformPort,
    ) -> Self;

    pub fn call(
        &self,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, ProtocolError>;
}

impl<'engine> PlatformAdapter<'engine> {
    pub(crate) fn new(
        authorizer: TrustedAuthorizer,
        engine: &'engine dyn EnginePort,
    ) -> Self;

    pub fn dispatch(
        &self,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, PlatformError>;
}

pub(crate) trait EnginePort {
    fn execute(
        &self,
        request: EngineRequest,
    ) -> Result<EngineResponse, EngineError>;
}
```

`Engine::queries`, `Engine::families`, and `Engine::begin` borrow the Engine.
The returned `QueryStore<'engine>`, `FamilyStore<'engine>`, and
`Transaction<'engine>` cannot outlive it. Therefore Rust rejects
`engine.shutdown()` while one of those values is live. Operational methods
also return a typed `EngineError::Closed(EngineClosed)` when a controlled
closure path races with a call. The public `Engine` has no production
in-memory constructor.

`Engine::begin` validates the Engine identity embedded in `ReadSnapshot`, the
requested `TenantScope`, the snapshot tenant, and the operation identity before
creating a transaction. A snapshot from another Engine, a snapshot for another
tenant, or a scope that differs from the snapshot returns a typed
`EngineError` without touching storage. `FamilyStore::stage` applies the same
Engine and transaction identity rule.

`PublicOperation` and `PublicResponse` are the only public operation registry:

```rust
pub enum PublicOperation {
    DocumentRead(DocumentReadRequest),
    DocumentWrite(DocumentWriteRequest),
    Query(QueryOperation),
    Family(FamilyOperation),
}

pub enum PublicResponse {
    Document(Option<Document>),
    Query(QueryResult),
    Family(FamilyReadResult),
    Commit(CommitOutcome),
}

pub(crate) enum EngineRequest {
    DocumentRead { scope: TenantScope, request: DocumentReadRequest },
    DocumentWrite { scope: TenantScope, request: DocumentWriteRequest },
    Query { scope: TenantScope, request: QueryOperation },
    Family { scope: TenantScope, request: FamilyOperation },
}

pub(crate) enum EngineResponse {
    Document(Option<Document>),
    Query(QueryResult),
    Family(FamilyReadResult),
    Commit(CommitOutcome),
}
```

The nested operation fields are private. Every mutating request carries its
`OperationId`; every operation carries its `Deadline` and `Cancellation`. No
variant accepts a string method name, route, raw body, generic HTTP or TCP
payload, arbitrary SQL, storage or session handle, private control-plane
operation, or generic bytes. `EngineRequest` and `EngineResponse` are
crate-private and are not re-exported.

`ProtocolAdapter::new` is crate-private because `PlatformPort` is private. It
borrows that port and cannot outlive it. `ProtocolAdapter::from_platform` is
the public production acquisition path and borrows a `PlatformAdapter`, which
internally satisfies the private port. Only the `cfg(test)`
`new_with_platform_port` constructor accepts a private `PlatformPort` double;
W3-3 therefore tests forwarding without exposing an injectable production
authority. `PlatformAdapter::new` is crate-private W5 wiring. It takes the
trusted private `TrustedAuthorizer` and a borrowed `EnginePort`; neither is a
caller-supplied public credential or route.

`ProtocolAdapter::call` accepts only the borrowed opaque `CallerContext` and a
closed `PublicOperation`. Platform dispatch alone validates caller identity,
tenant, permission, expiry, confirmation, audit, and secret handling. It
creates a private short-lived `PlatformGrant`, converts the approved operation
to `EngineRequest`, calls `EnginePort`, and never returns the grant.
`EnginePort` receives no grant. W3-3 may prove typed forwarding and rejection
of generic/private operations, but it owns no authorization or PB-3042 claim.
W5 Platform owns those claims.

The forwarding seam is crate-private as well:

```rust
pub(crate) trait PlatformPort {
    fn dispatch(
        &self,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, PlatformError>;
}
```

`CommitReceipt` is strictly local:

```rust
pub struct CommitReceipt {
    operation_id: OperationId,
    storage_sequence: StorageSequence,
    durability: LocalDurability,
}
```

It has no `AcknowledgementLevel`, `Convergence`, `MemberId`, `RegionId`, or
`ReplicationReceipt`. Those are replication-owned future types under the later
replication seam and cannot be used to imply remote acknowledgement or
convergence from a local commit. `UnknownCommit` retains the original
`OperationId` and a typed `UnknownReason`; `DurableResolution` resolves it
through the operation ledger without changing the local receipt contract.

The public error and value declarations are closed and typed:

```rust
pub struct EngineClosed;

pub enum EngineConfigError {
    EmptyLocation,
    NonDurableLocation,
}

pub enum EngineOpenError {
    InvalidConfig,
    FormatMismatch,
    StorageUnavailable,
}

pub enum EngineError {
    Closed(EngineClosed),
    SnapshotEngineMismatch,
    SnapshotTenantMismatch,
    ScopeMismatch,
    OperationIdReuse { operation_id: OperationId },
    Conflict,
    InvalidDocument,
    Deadline,
    Cancelled,
    StorageFailure,
}

pub enum EngineShutdownError {
    StorageFailure,
    AlreadyClosed,
}

pub enum FamilyError {
    EngineClosed,
    TenantMismatch,
    SnapshotEngineMismatch,
    TransactionEngineMismatch,
    TransactionIdentityMismatch,
    InvalidRequest,
    Conflict,
    Cancelled,
}

pub enum QueryError {
    EngineClosed,
    SnapshotEngineMismatch,
    TenantMismatch,
    InvalidSpec,
    IndexNotReady,
    Deadline,
    Cancelled,
}

pub enum ProtocolError {
    InvalidOperation,
    PrivateOperation,
    GenericAccessRejected,
    PlatformDenied,
    Deadline { operation_id: Option<OperationId> },
    Cancelled { operation_id: Option<OperationId> },
    EngineFailure { operation_id: Option<OperationId> },
}

pub enum PlatformError {
    CallerDenied,
    TenantDenied,
    PermissionDenied,
    Expired,
    ConfirmationRequired,
    AuditFailure,
    SecretPolicyViolation,
    InvalidRequest,
    Conflict { operation_id: Option<OperationId> },
    Deadline { operation_id: Option<OperationId> },
    Cancelled { operation_id: Option<OperationId> },
    EngineFailure { operation_id: Option<OperationId> },
}

pub enum CallerContextError {
    EmptyPrincipal,
    EmptyTenant,
    EmptyCredential,
    UnsupportedCredential,
}

pub enum RequestValidationError {
    EmptyField,
    InvalidIdentifier,
    InvalidOperationIdentity,
    InvalidDeadline,
    InvalidCancellation,
    InvalidRange,
    InvalidFamilyPayload,
    InvalidQuery,
}

pub enum QueryInputError {
    EmptyText,
    UnsupportedGrammar,
    InvalidIdentifier,
}

pub enum ExpectedVersion {
    Any,
    New,
    Exact(u64),
}

pub enum RetryDisposition {
    Never,
    SameOperation,
    NewOperation,
    AfterRecovery,
}

pub struct Deadline { /* validated monotonic deadline */ }
pub struct Cancellation { /* bounded cancellation state */ }
pub struct PrincipalId { /* validated, non-secret principal identity */ }
pub enum CredentialKind { Bearer, ApiKey }
pub struct CredentialInput { /* private secret, non-Clone, redacted */ }
pub struct CallerEvidence { /* private principal, tenant, and credential */ }
pub struct CallerContext { /* untrusted, private credential, non-Clone, redacted */ }
pub struct DocumentReadRequest { /* private validated fields */ }
pub struct DocumentWriteRequest { /* private validated fields */ }
pub struct QueryOperation { /* private QueryInput and request metadata */ }
pub struct QuerySpec { /* private validated typed query fields */ }
pub struct QuerySource { /* private typed source */ }
pub struct QueryPredicate { /* private typed predicate */ }
pub struct QueryOrdering { /* private typed ordering */ }
pub struct QueryProjection { /* private typed projection */ }
pub struct QueryPaging { /* private typed paging */ }
pub struct StalenessPolicy { /* private typed staleness policy */ }
pub struct V1QueryText { /* private validated V1-compatible grammar */ }
pub enum QueryInput { Typed(QuerySpec), V1Compatible(V1QueryText) }

pub enum FamilyOperation {
    Read(FamilyReadRequest),
    Write(FamilyWriteRequest),
}
```

### Public construction, validation, and observation

The declaration-only tree must not leave callers guessing how to create a
valid value. Every public request has private fields and one named validated
construction path. Public enum variants are closed; their associated
constructors below are the supported spelling and prevent a caller from
smuggling a route, permission, storage handle, or generic payload into the
operation registry.

Caller evidence is untrusted input. It can state only a principal, a tenant
claim, and one closed credential kind. It cannot state permissions, a
`TenantScope`, a `PlatformGrant`, or an authorization decision:

```rust
impl PrincipalId {
    pub fn try_new(value: &str) -> Result<Self, CallerContextError>;
}

impl CredentialInput {
    pub fn try_new(
        kind: CredentialKind,
        secret: &str,
    ) -> Result<Self, CallerContextError>;

    pub fn kind(&self) -> CredentialKind;
}

impl CallerEvidence {
    pub fn try_new(
        principal: PrincipalId,
        tenant: TenantId,
        credential: CredentialInput,
    ) -> Result<Self, CallerContextError>;

    pub fn principal(&self) -> &PrincipalId;
    pub fn tenant(&self) -> &TenantId;
    pub fn credential_kind(&self) -> CredentialKind;
}

impl CallerContext {
    pub fn try_new(evidence: CallerEvidence)
        -> Result<Self, CallerContextError>;

    pub fn principal(&self) -> &PrincipalId;
    pub fn tenant_claim(&self) -> &TenantId;
    pub fn credential_kind(&self) -> CredentialKind;
}
```

`CallerEvidence`, `CallerContext`, and `CredentialInput` do not implement
`Clone`. Their
`Debug`, `Display`, and serialization forms contain only the principal,
tenant claim, credential kind, and a non-secret redacted fingerprint. They
never expose the credential, a secret, a grant, permissions, or a source
error. Platform authenticates and authorizes the context, checks that its
tenant claim is allowed, creates the private `TenantScope` and short-lived
`PlatformGrant`, and discards the grant before Engine dispatch. Any tenant
claim carried in a request is untrusted input that Platform must compare with
the authorized scope; it cannot select a tenant or permission by itself.

The operation constructors and read-only accessors are fixed as follows:

| Type | Construction path and inputs | Validation error | Read-only observation |
| --- | --- | --- | --- |
| `DocumentReadRequest` | `try_new(tenant_claim: TenantId, collection, document_id, deadline, cancellation)` | `RequestValidationError::{InvalidIdentifier, InvalidDeadline, InvalidCancellation}` | `tenant_claim`, `collection`, `document_id`, `deadline`, `cancellation` |
| `DocumentWriteRequest` | `try_new(tenant_claim: TenantId, document, expected_version, operation_id, deadline, cancellation)` | `RequestValidationError::{InvalidOperationIdentity, InvalidDeadline, InvalidCancellation}` | `tenant_claim`, `document`, `expected_version`, `operation_id`, `deadline`, `cancellation` |
| `QuerySpec` | `try_new(tenant_claim: TenantId, source, predicate, ordering, projection, paging, staleness, deadline, cancellation)` | `RequestValidationError::{InvalidIdentifier, InvalidQuery, InvalidDeadline, InvalidCancellation}` | Every typed clause, `tenant_claim`, `staleness`, `deadline`, `cancellation` |
| `V1QueryText` | `try_new(text: &str)` | `QueryInputError::{EmptyText, UnsupportedGrammar, InvalidIdentifier}` | `as_str()` returns validated text only; no raw transport or SQL escape |
| `QueryInput` | `typed(spec)` or `v1_compatible(text: V1QueryText)` | `QueryInputError` for the V1 form | Exhaustive matching on `Typed` or `V1Compatible` |
| `QueryOperation` | `try_new(input, operation_id: Option<OperationId>, deadline, cancellation)` | `RequestValidationError::{InvalidOperationIdentity, InvalidDeadline, InvalidCancellation}` | `input`, optional `operation_id`, `deadline`, `cancellation` |
| `FamilyOperation` | `read(request)` or `write(request)` | `RequestValidationError::InvalidFamilyPayload` | Exhaustive matching on `Read` or `Write` |
| `PublicOperation` | `document_read(request)`, `document_write(request)`, `query(request)`, or `family(request)` | The nested constructor's typed error | Exhaustive matching on the four closed variants |

`QuerySource`, `QueryPredicate`, `QueryOrdering`, `QueryProjection`,
`QueryPaging`, and `StalenessPolicy` are also closed typed values. Each has a
`try_new` or named builder that accepts only its documented typed clauses,
rejects `RequestValidationError::InvalidQuery`, and exposes typed read-only
accessors. None accepts a route, arbitrary SQL, a generic map, or generic
bytes. `QuerySpec::try_new` is the only composition path, so typed and
V1-compatible inputs reach the same `QueryStore::compile(QueryInput)` seam.

The principal document and query constructors have this exact shape:

```rust
impl DocumentReadRequest {
    pub fn try_new(
        tenant_claim: TenantId,
        collection: CollectionName,
        document_id: DocumentId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError>;
}

impl DocumentWriteRequest {
    pub fn try_new(
        tenant_claim: TenantId,
        document: Document,
        expected: ExpectedVersion,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError>;
}

impl QuerySpec {
    pub fn try_new(
        tenant_claim: TenantId,
        source: QuerySource,
        predicate: QueryPredicate,
        ordering: QueryOrdering,
        projection: QueryProjection,
        paging: QueryPaging,
        staleness: StalenessPolicy,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError>;
}

impl V1QueryText {
    pub fn try_new(text: &str) -> Result<Self, QueryInputError>;
    pub fn as_str(&self) -> &str;
}

impl QueryInput {
    pub fn typed(spec: QuerySpec) -> Self;
    pub fn v1_compatible(text: V1QueryText) -> Self;
}

impl QueryOperation {
    pub fn try_new(
        input: QueryInput,
        operation_id: Option<OperationId>,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError>;
}
```

Every concrete family payload follows the same explicit rule. The phrase
"typed family fields" below means the closed field set named by that family's
parity contract, not an untyped map or generic bytes. W3-0 records the
constructor and observation rule without inventing domain fields that belong
to W3-F:

| Type | Construction inputs | Validation error | Read-only observation |
| --- | --- | --- | --- |
| `AttachmentRead` | `try_new(typed attachment-read fields, deadline, cancellation)` | `InvalidFamilyPayload` | Typed attachment-read fields, `deadline`, `cancellation` |
| `CounterRead` | `try_new(typed counter-read fields, deadline, cancellation)` | `InvalidFamilyPayload` | Typed counter-read fields, `deadline`, `cancellation` |
| `TimeSeriesRead` | `try_new(typed time-series-read fields, deadline, cancellation)` | `InvalidFamilyPayload` | Typed time-series-read fields, `deadline`, `cancellation` |
| `RevisionRead` | `try_new(typed revision-read fields, deadline, cancellation)` | `InvalidFamilyPayload` | Typed revision-read fields, `deadline`, `cancellation` |
| `CompareExchangeRead` | `try_new(typed compare-exchange-read fields, deadline, cancellation)` | `InvalidFamilyPayload` | Typed compare-exchange-read fields, `deadline`, `cancellation` |
| `IdentityRead` | `try_new(typed identity-read fields, deadline, cancellation)` | `InvalidFamilyPayload` | Typed identity-read fields, `deadline`, `cancellation` |
| `LifecycleRead` | `try_new(typed lifecycle-read fields, deadline, cancellation)` | `InvalidFamilyPayload` | Typed lifecycle-read fields, `deadline`, `cancellation` |
| `ArchivalReadRequest` | `try_new(typed archival-read fields, deadline, cancellation)` | `InvalidFamilyPayload` | Typed archival-read fields, `deadline`, `cancellation` |
| `ValidationRead` | `try_new(typed validation-read fields, deadline, cancellation)` | `InvalidFamilyPayload` | Typed validation-read fields, `deadline`, `cancellation` |
| `TombstoneRead` | `try_new(typed tombstone-read fields, deadline, cancellation)` | `InvalidFamilyPayload` | Typed tombstone-read fields, `deadline`, `cancellation` |
| `AttachmentWrite` | `try_new(typed attachment-write fields, operation_id, deadline, cancellation)` | `InvalidFamilyPayload` | Typed fields, `operation_id`, `deadline`, `cancellation` |
| `CounterBatchWrite` | `try_new(typed counter-write fields, operation_id, deadline, cancellation)` | `InvalidFamilyPayload` | Typed fields, `operation_id`, `deadline`, `cancellation` |
| `TimeSeriesBatchWrite` | `try_new(typed time-series-write fields, operation_id, deadline, cancellation)` | `InvalidFamilyPayload` | Typed fields, `operation_id`, `deadline`, `cancellation` |
| `RevisionWrite` | `try_new(typed revision-write fields, operation_id, deadline, cancellation)` | `InvalidFamilyPayload` | Typed fields, `operation_id`, `deadline`, `cancellation` |
| `CompareExchangeWrite` | `try_new(typed compare-exchange-write fields, operation_id, deadline, cancellation)` | `InvalidFamilyPayload` | Typed fields, `operation_id`, `deadline`, `cancellation` |
| `IdentityWrite` | `try_new(typed identity-write fields, operation_id, deadline, cancellation)` | `InvalidFamilyPayload` | Typed fields, `operation_id`, `deadline`, `cancellation` |
| `LifecycleWrite` | `try_new(typed lifecycle-write fields, operation_id, deadline, cancellation)` | `InvalidFamilyPayload` | Typed fields, `operation_id`, `deadline`, `cancellation` |
| `ArchivalWrite` | `try_new(typed archival-write fields, operation_id, deadline, cancellation)` | `InvalidFamilyPayload` | Typed fields, `operation_id`, `deadline`, `cancellation` |
| `ValidationWrite` | `try_new(typed validation-write fields, operation_id, deadline, cancellation)` | `InvalidFamilyPayload` | Typed fields, `operation_id`, `deadline`, `cancellation` |
| `TombstoneWrite` | `try_new(typed tombstone-write fields, operation_id, deadline, cancellation)` | `InvalidFamilyPayload` | Typed fields, `operation_id`, `deadline`, `cancellation` |

The closed outer request enums have named constructors for every variant:

```rust
impl FamilyReadRequest {
    pub fn attachment_metadata(value: AttachmentRead) -> Self;
    pub fn counter_group(value: CounterRead) -> Self;
    pub fn time_series_range(value: TimeSeriesRead) -> Self;
    pub fn revision_page(value: RevisionRead) -> Self;
    pub fn compare_exchange_value(value: CompareExchangeRead) -> Self;
    pub fn identity_range(value: IdentityRead) -> Self;
    pub fn lifecycle(value: LifecycleRead) -> Self;
    pub fn archival(value: ArchivalReadRequest) -> Self;
    pub fn validation(value: ValidationRead) -> Self;
    pub fn tombstones(value: TombstoneRead) -> Self;
}

impl FamilyWriteRequest {
    pub fn attachment_mutation(value: AttachmentWrite) -> Self;
    pub fn counter_batch(value: CounterBatchWrite) -> Self;
    pub fn time_series_batch(value: TimeSeriesBatchWrite) -> Self;
    pub fn revision_command(value: RevisionWrite) -> Self;
    pub fn compare_exchange_condition(value: CompareExchangeWrite) -> Self;
    pub fn identity_allocation(value: IdentityWrite) -> Self;
    pub fn lifecycle_update(value: LifecycleWrite) -> Self;
    pub fn archival_update(value: ArchivalWrite) -> Self;
    pub fn schema_validation_update(value: ValidationWrite) -> Self;
    pub fn tombstone_operation(value: TombstoneWrite) -> Self;
}

impl FamilyOperation {
    pub fn read(value: FamilyReadRequest) -> Self;
    pub fn write(value: FamilyWriteRequest) -> Self;
}

impl PublicOperation {
    pub fn document_read(value: DocumentReadRequest) -> Self;
    pub fn document_write(value: DocumentWriteRequest) -> Self;
    pub fn query(value: QueryOperation) -> Self;
    pub fn family(value: FamilyOperation) -> Self;
}
```

No family payload has a public unchecked-field constructor. `FamilyReadContext`
has `new(tenant, snapshot)` for structural construction and `try_new` for
callers that want immediate tenant/snapshot validation. `FamilyStore::read`
performs the same check before family or index access. `FamilyStore::stage`
checks the write's `operation_id()` against the borrowed transaction identity.

The value and outcome types expose observation without exposing implementation
details:

```rust
impl CommitReceipt {
    pub fn operation_id(&self) -> &OperationId;
    pub fn sequence(&self) -> StorageSequence;
    pub fn durability(&self) -> LocalDurability;
}

```

`CommitOutcome` and `DurableResolution` have no lossy `is_success` or
`is_retryable` shortcut. Callers inspect every closed variant with exhaustive
matching. `Committed` exposes `CommitReceipt`, `Rejected` exposes
`CommitError`, `Unknown` exposes `UnknownCommit`, and `Pending` remains an
explicit `DurableResolution` state.

`OperationId`, `TenantId`, `TenantScope`, `DocumentId`, `CollectionName`,
`StorageSequence`, `ExpectedVersion`, and structured document values implement
safe equality and serialization for their validated logical representation.
Their `Display` forms are canonical identifiers or redacted typed summaries.
They never serialize storage keys, WAL frames, file paths, credentials,
customer secrets, or raw byte buffers. `OperationId` has stable `Eq`, `Ord`,
`Hash`, `Display`, and serialization behavior so retry and resolution tests can
compare it without opening storage.

Validated logical identifiers have named constructors. `TenantScope` is the
exception: Platform creates it only after authenticating and authorizing a
`CallerContext`, so no public string constructor can manufacture Engine
authority:

```rust
impl OperationId {
    pub fn try_new(value: &str) -> Result<Self, RequestValidationError>;
}

impl TenantId {
    pub fn try_new(value: &str) -> Result<Self, RequestValidationError>;
}

impl DocumentId {
    pub fn try_new(value: &str) -> Result<Self, RequestValidationError>;
}

impl CollectionName {
    pub fn try_new(value: &str) -> Result<Self, RequestValidationError>;
}
```

These errors expose only typed categories. They do not carry filesystem paths,
credentials, routes, WAL frames, pages, codec buffers, customer data, or
private implementation types. `EngineClosed` is the common typed closure
signal for operational paths; the borrow checker remains the primary guard
against shutting down while a borrowed store or transaction is live.

### Dependency arrows and collision rules

The intended dependency direction is:

```text
Protocol Adapter -> Platform Adapter -> EnginePort -> Engine -> Storage
                                      |              |-> Data Families
                                      |              |-> Query -> Index
                                      \\-> private authorization and audit
```

The arrows mean calls and typed data flow, not ownership of authorization.
`Protocol Adapter` accepts only an opaque `CallerContext` and a closed
`PublicOperation`. It forwards to `Platform Adapter`, which alone validates the
caller and creates an internal `EngineRequest` for `EnginePort`. `Platform
Adapter` never hands a `PlatformGrant` to Engine. `Engine` owns document
transactions and supplies borrowed Family and Query stores. `Data Families`
owns family value and lifecycle rules and exposes typed projection inputs to
Query; it does not implement query planning, index publication, replication, or
backup. `Query` consumes a `ReadSnapshot`; `Index` consumes published storage
roots and emits a new `IndexGeneration`; neither can publish a storage root.
Storage never depends on Protocol, Platform, Query, or Families.

The W3-0 preparation packet exclusively owns `src/lib.rs` through its
preparation commit. It declares and re-exports every W3 module root: Storage,
Engine, Families, Query, Index, and the typed Protocol and Platform Adapters.
Later packets
own only the implementation contents under `src/storage/**`, `src/engine/**`,
`src/families/**`, `src/query/**`, `src/index/**`, and
`src/adapters/protocol/**` and `src/adapters/platform/**`; they do not edit
`src/lib.rs` to add a root or change an export. After preparation, shared
logical types, operation registries, root Cargo files, `Cargo.lock`, generated
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
The docs-only tip is not a W3-0 GO. The declaration tree and
`tests/public_exports.rs` must land in their separate preparation commit, pass
the independent W3-0 gate, and be recorded before W3-1 can start.
The docs history leading to that dispatch includes the initial contract commit
`ad3e52c` and the first reliability correction commit `74d5473`.

W3-0 owns exactly this declaration-only tree for the preparation commit:

```text
src/
├── lib.rs
├── types.rs
├── operations.rs
├── storage/mod.rs
├── engine/mod.rs
├── families/mod.rs
├── query/mod.rs
├── index/mod.rs
└── adapters/
    ├── mod.rs
    ├── protocol/mod.rs
    └── platform/mod.rs
tests/public_exports.rs
```

`src/lib.rs` declares and re-exports the public roots. `src/types.rs` contains
the shared identifiers, values, snapshots, generations, durability types, and
public error-independent value declarations. `src/operations.rs` contains
the one closed operation/response registry and the crate-private Engine
registry. Every `mod.rs` contains only the module declaration, documentation,
and the exact public or crate-private declarations required for compilation.
`tests/public_exports.rs` checks the public export and borrow surface without
implementing database behavior.

The root declaration has one stable shape:

```rust
pub mod types;
pub mod operations;
pub mod storage;
pub mod engine;
pub mod families;
pub mod query;
pub mod index;
pub mod adapters;

pub use adapters::platform::{PlatformAdapter, PlatformError};
pub use adapters::protocol::{ProtocolAdapter, ProtocolError};
pub use engine::{
    CommitError, CommitOutcome, CommitReceipt, DurableResolution, Engine,
    EngineClosed, EngineConfig, EngineError, EngineOpenError,
    EngineShutdownError, Transaction, UnknownCommit, UnknownReason,
};
pub use families::{
    FamilyError, FamilyKind, FamilyReadContext, FamilyReadRequest,
    FamilyReadResult, FamilyStore, FamilyWriteRequest,
};
pub use operations::{
    DocumentReadRequest, DocumentWriteRequest, FamilyOperation, PublicOperation,
    PublicResponse, QueryInput, QueryOperation, QueryInputError,
    RequestValidationError, V1QueryText,
};
pub use query::{
    QueryError, QueryOrdering, QueryPaging, QueryPlan, QueryPredicate,
    QueryProjection, QueryResult, QuerySource, QuerySpec, QueryStore,
    StalenessPolicy,
};
pub use adapters::protocol::{
    CallerContext, CallerContextError, CallerEvidence, CredentialInput,
    CredentialKind, PrincipalId,
};
pub use types::{
    Cancellation, Deadline, Document, DocumentId, DocumentMetadata, DocumentValue,
    ExpectedVersion, IndexGeneration, IndexGenerationSet, LocalDurability,
    OperationId, ReadSnapshot, RetryDisposition, StorageSequence, TenantId,
    TenantScope,
};
```

`adapters/mod.rs` declares `pub mod protocol;` and `pub mod platform;`. The
root never re-exports `PlatformGrant`, `EnginePort`, `EngineRequest`, or any
private storage/query/family type. The export test uses these names only to
prove that the declaration tree compiles and that the borrowed store lifetimes
are visible to the compiler.

The W3-0 builder owns all paths in this tree through its preparation commit.
After that commit, the later integrator owns `src/lib.rs`, `src/types.rs`,
`src/operations.rs`, `src/adapters/mod.rs`, and `tests/public_exports.rs` as
shared vocabulary, root declarations, and export files. W3-1 takes the
contents under `src/storage/**` and
`src/engine/**`; W3-F takes `src/families/**`; W3-2 takes `src/query/**` and
`src/index/**`; W3-3 takes `src/adapters/protocol/**`; W4 takes the
replication, backup, and migration paths; and W5 takes
`src/adapters/platform/**` only after both W4 handoffs. Those packets may
replace declarations inside their assigned module directories, but none may
edit `src/lib.rs` or add a second root. Cargo, lockfile, CI, generated parity,
overlays, and shared manifests remain integrator-owned collision paths. If a
shared declaration needs a dependency or a semantic change, the parent
assigns that exact file as a separate collision action.

W3-0 must compile this tree with no speculative Storage, Engine, Families,
Query, Index, Protocol, or Platform behavior. It may add compile-only
type/export checks, but it must not edit generated parity output or overlays.
The packet reports a clean locked check, format, clippy, and docs result, and
then commits the preparation. No later packet has an exception to edit
`src/lib.rs`.

The declaration-only packet starts red with these public-surface tests:

- `public_exports_compile_with_declaration_only_tree`;
- `borrowed_store_lifetimes_prevent_engine_shutdown_while_borrowed`;
- `platform_grant_is_not_reexported_or_reachable_from_public_exports`;
- `closed_operation_registry_rejects_generic_routes_and_private_operations`.

These tests inspect only the compile/export and lifetime surface. They do not
claim that any database behavior exists.

The parent records that preparation commit hash and W3-1 must base on that
hash, never directly on `8771f13` or on an unrecorded docs tip.

| Packet | Worktree and base | Write scope | Collision owner |
| --- | --- | --- | --- |
| W3-1 Storage plus Engine | `terrabasev2-w3-storage-engine`, based on the recorded W3-0 type/export preparation commit | `src/storage/**`, `src/engine/**`, `tests/storage/**`, `tests/engine/**`, and explicitly named storage/engine docs or receipts | W3-1 owns those paths only; W3-0 retains `src/lib.rs` through preparation and the later integrator retains it afterward, along with shared type declarations, Cargo files, generated outputs, and overlays |
| W3-F Data Families | `terrabasev2-w3-families`, based on the accepted W3-1 commit | `src/families/**`, `tests/families/**`, and explicitly named `docs/families/**` | W3-F owns family paths only; it does not edit Storage, Engine, Query, Index, replication, backup, migration, adapter, shared, or generated paths |
| W3-2 Query | `terrabasev2-w3-query`, based on the accepted W3-F Families commit | `src/query/**`, `src/index/**`, `tests/query/**`, `tests/index/**`, and explicitly named query/index docs | W3-2 owns query/index paths; it does not edit W3-1, W3-F, or shared files |
| W3-3 Protocol | `terrabasev2-w3-protocol`, based on the accepted W3-2 commit | `src/adapters/protocol/**`, `tests/adapters/protocol/**`, `docs/adapters/protocol/**` | W3-3 owns typed forwarding only; Platform implementation remains a later adapter packet |
| W4 Replication | `terrabasev2-w4-replication`, based on the accepted W3-3 commit | `src/replication/**`, `tests/replication/**`, `docs/replication/**` | W4 owns replication behavior and receipts; it does not edit adapters, backup, migration, or shared roots |
| W4 Backup/Migration | `terrabasev2-w4-backup`, based on the accepted W3-3 commit | `src/backup/**`, `src/migration/**`, `tests/recovery/**`, `docs/backup/**`, `docs/migration/**` | W4 owns backup, restore, and Logical Migration behavior and receipts; it does not edit adapters, replication, or shared roots |
| W5 Platform Adapter | `terrabasev2-w5-platform`, based on the accepted W4 Replication and W4 Backup/Migration commits | `src/adapters/platform/**`, `tests/adapters/platform/**`, `docs/adapters/platform/**` | W5 owns authorization and PB-3042 proof; it starts only after both W4 dependencies and does not edit Protocol, Engine, Families, or shared roots |

The parent integrates each packet in sequence. Generated inventory and parity
overlays are never generated from a builder worktree. They are regenerated by
the integrator after behavior evidence exists.

The W5 Platform Adapter dispatch is blocked until both W4 Replication and W4
Backup/Migration have accepted handoffs. In charter terms,
`WS-PLATFORM` depends on both `WS-REPLICATION` and `WS-BACKUP`; a W3-3 commit or
one completed W4 branch is not a valid W5 base.

Collision ownership is explicit:

| Path family | Owner during W3 | Rule |
| --- | --- | --- |
| `src/lib.rs`, `src/types.rs`, `src/operations.rs`, `src/adapters/mod.rs`, and `tests/public_exports.rs` | W3-0 through preparation, then later integrator | W3-0 creates the declaration-only vocabulary, root declarations, and export test. No behavior packet may edit these shared paths or `src/lib.rs`; later integrations update them only as separately named collision actions. |
| `Cargo.toml`, `Cargo.lock`, toolchain, format, and root CI files | later integrator | No dependency or manifest edits in W3 seam packets unless the dispatch names one exact file |
| `parity/inventory.json` and `parity/overlays/**` | later integrator | Builders provide test/evidence handoffs; they do not regenerate or change status |
| `docs/design/w3-core-contract.md` | W3-0 contract writer | This document is the durable W3 design record; behavior builders do not rewrite it |
| `src/replication/**`, `tests/replication/**`, and `docs/replication/**` | W4 Replication | W4 owns replication behavior and receipts only; it does not edit backup, migration, adapters, or shared roots |
| `src/backup/**`, `src/migration/**`, `tests/recovery/**`, `docs/backup/**`, and `docs/migration/**` | W4 Backup/Migration | W4 owns backup, restore, and Logical Migration behavior and receipts only; it does not edit replication, adapters, or shared roots |
| `src/adapters/protocol/**`, `tests/adapters/protocol/**`, and `docs/adapters/protocol/**` | W3-3 Protocol | W3-3 owns typed forwarding only; it does not edit Platform authorization or shared roots |
| `src/adapters/platform/**`, `tests/adapters/platform/**`, and `docs/adapters/platform/**` | W5 Platform Adapter | W5 owns authorization, tenant isolation, confirmation, audit, secret handling, and PB-3042 only |
| `docs/storage/**`, `docs/engine/**`, `docs/families/**`, `docs/query/**`, and `docs/index/**` | the packet named in the dispatch | A packet may create only its named documentation path; there is no residual `docs/**` ownership |

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
only the operation, storage sequence, and achieved `LocalDurability`. It does
not contain a snapshot handle and does not imply any remote `Acknowledgement
Level` or `Convergence`.

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
pub enum CommitError {
    Validation,
    Conflict,
    OperationIdReuse,
    EngineClosed,
    StorageFailure,
}

impl CommitError {
    pub fn retry_disposition(&self) -> RetryDisposition;
}

impl UnknownCommit {
    pub fn operation_id(&self) -> &OperationId;
    pub fn reason(&self) -> UnknownReason;
}

pub struct UnknownCommit {
    operation_id: OperationId,
    reason: UnknownReason,
}

pub enum UnknownReason {
    Deadline,
    Cancellation,
    ResponseInterrupted,
    PostPublicationPointerSync,
    PostPublicationDirectorySync,
    PublishedLedgerSync,
}
```

`CommitError::retry_disposition` is exhaustive and stable:

| `CommitError` | `RetryDisposition` | Caller action |
| --- | --- | --- |
| `Validation` | `NewOperation` | Correct the request and use a new identity. |
| `Conflict` | `NewOperation` | Re-read and construct a new logical write with a new identity. |
| `OperationIdReuse` | `Never` | Do not retry this identity or reinterpret the request. |
| `EngineClosed` | `SameOperation` | Reopen or wait for controlled closure, then retry the exact request with the same identity. |
| `StorageFailure` | `AfterRecovery` | Resolve or reopen first; do not issue a new identity until durable state is known. |
| `UnknownCommit` | not a `CommitError` | Call `resolve_commit` with the original `OperationId`; never guess success or failure. |

The adapter error mappings are pure, closed, and exhaustive. They preserve an
optional `OperationId` for mutating deadline, cancellation, conflict, storage,
and retry-resolution paths. They deliberately collapse tenant and existence
details before an error crosses a less trusted seam:

```rust
pub(crate) fn map_engine_error(
    error: EngineError,
    operation_id: Option<OperationId>,
) -> PlatformError;

pub(crate) fn map_platform_error(error: PlatformError) -> ProtocolError;
```

Engine to Platform mapping has one row for every `EngineError` variant:

| Source `EngineError` | Result `PlatformError` | Redaction and identity rule |
| --- | --- | --- |
| `Closed(_)` | `EngineFailure { operation_id }` | No closure internals or storage details. |
| `SnapshotEngineMismatch` | `TenantDenied` | Do not reveal Engine identity or snapshot provenance. |
| `SnapshotTenantMismatch` | `TenantDenied` | Do not reveal the other tenant or existence. |
| `ScopeMismatch` | `TenantDenied` | Do not reveal scope contents or authorization policy. |
| `OperationIdReuse { operation_id }` | `Conflict { operation_id: Some(operation_id) }` | Preserve the safe operation identity; never expose the request digest. |
| `Conflict` | `Conflict { operation_id }` | Preserve the supplied identity when present. |
| `InvalidDocument` | `InvalidRequest` | Return a typed category without customer data or source details. |
| `Deadline` | `Deadline { operation_id }` | Preserve identity for exact retry or resolution. |
| `Cancelled` | `Cancelled { operation_id }` | Preserve identity; cancellation is not rejection proof. |
| `StorageFailure` | `EngineFailure { operation_id }` | Do not expose paths, WAL, pages, codec errors, or source chains. |

Platform to Protocol mapping has one row for every `PlatformError` variant:

| Source `PlatformError` | Result `ProtocolError` | Redaction rule |
| --- | --- | --- |
| `CallerDenied` | `PlatformDenied` | Hide caller authentication detail. |
| `TenantDenied` | `PlatformDenied` | Hide tenant existence and policy detail. |
| `PermissionDenied` | `PlatformDenied` | Hide permission names and control-plane policy. |
| `Expired` | `PlatformDenied` | Hide credential expiry detail. |
| `ConfirmationRequired` | `PlatformDenied` | Hide confirmation policy detail. |
| `AuditFailure` | `PlatformDenied` | Hide audit sink and operational detail. |
| `SecretPolicyViolation` | `PlatformDenied` | Never echo credentials or secret policy values. |
| `InvalidRequest` | `InvalidOperation` | Return only the stable typed category. |
| `Conflict { operation_id }` | `EngineFailure { operation_id }` | Preserve the safe identity for caller resolution. |
| `Deadline { operation_id }` | `Deadline { operation_id }` | Preserve identity and deadline semantics. |
| `Cancelled { operation_id }` | `Cancelled { operation_id }` | Preserve identity; do not claim rejection. |
| `EngineFailure { operation_id }` | `EngineFailure { operation_id }` | Preserve identity without source chains or storage detail. |

The W5 implementation owns these functions and their table-driven red tests:
`engine_error_mapping_is_exhaustive_and_redacted`,
`platform_error_mapping_is_exhaustive_and_redacted`,
`mapped_error_preserves_operation_identity`, and
`platform_denial_precedes_engine_dispatch`. The tests enumerate every source
variant, assert that no mapping leaks paths, credentials, routes, WAL/pages,
customer data, or private payloads, and verify that a denial never calls
`EnginePort`.

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

The public Engine interface is intentionally small. Its exact Rust-like
declarations, including `EngineConfig::durable`, borrowed stores, and
`Engine::shutdown`, are in the declaration-only block above. Those declarations
are mandatory; later packets implement their assigned module contents without
widening them through a second semantic path.

`Transaction<'engine>` is opaque. It exposes typed document operations and one
commit operation. It may not expose a WAL frame, mutable storage root, lock, or
raw bytes. A transaction is tied to one tenant scope, one immutable read
snapshot, one Engine identity, and one operation identity. A successful commit
publishes all writes in its write set atomically. Conflicts return `Rejected`
with a typed conflict error.

Engine methods are synchronous. A future async adapter may call them from a
bounded blocking pool, with a deadline, cancellation propagation, and an
explicit overload error when capacity is exhausted. An async wrapper must not
create an unbounded thread or queue, hold a core lock across an await, or turn
cancellation into an unknown success without preserving the operation identity.

## Query contract

`QuerySpec` is a typed description of source, predicates, ordering, projection,
paging, aggregation, tenant claim, and the requested staleness policy. It
cannot contain a raw query string that bypasses validation or an untyped route.
`V1QueryText` is the raw-query input wrapper for the supported V1-compatible
query grammar. It is validated before use and is not arbitrary SQL or a
route/body escape hatch. `QueryInput`
closes the two accepted forms:

```rust
pub enum QueryInput {
    Typed(QuerySpec),
    V1Compatible(V1QueryText),
}
```

`QueryPlan` is immutable after compilation and can be reused with compatible
snapshots and parameters. A plan does not own a mutable index or storage lock.

The query path is:

1. Validate the `QuerySpec` and tenant claim; Platform later binds the
   authorized `TenantScope` before Engine execution.
2. Normalize `QueryInput::Typed` and `QueryInput::V1Compatible` into the same
   validated internal query model.
3. Compile or retrieve an immutable `QueryPlan`.
4. Select an index whose generation satisfies the plan's policy, or use the
   documented stale fallback.
5. Execute against the requested immutable `ReadSnapshot`.
6. Return typed rows/documents, ordering metadata, staleness information, and
   the `IndexGeneration` used.

Callers obtain the query module from the already-open durable Engine and use
one compile/execute path:

```rust
impl<'engine> QueryStore<'engine> {
    pub fn compile(&self, input: QueryInput) -> Result<QueryPlan, QueryError>;

    pub fn execute(
        &self,
        plan: &QueryPlan,
        snapshot: &ReadSnapshot,
    ) -> Result<QueryResult, QueryError>;
}
```

`Engine::queries` returns a borrowed `QueryStore<'engine>` tied to that
Engine's durable state. The compiler validates typed query inputs, and
execution consumes only the immutable snapshot supplied by the caller. A
closed Engine returns typed `QueryError::EngineClosed`. Both typed `QuerySpec`
and validated `V1QueryText` pass through this one compile seam. Neither method
accepts a raw route, arbitrary SQL, storage access, or a mutable snapshot.

The plan must preserve V1-observable result ordering, null/missing behavior,
error classes, paging boundaries, and cancellation semantics for each parity
row that later receives a Proof ID. Query builders may add convenience methods,
but they compile to this one execution contract. Query code cannot mutate
documents, publish indexes, or select a private admin operation.

The first Query tracer tests must cover a known result and ordering, missing
versus null predicates, a strict generation request, a stale fallback, plan
reuse with an immutable snapshot, cancellation at a bounded point, and typed
tenant-scope rejection. These are contract tests, not an assertion that V1
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
    tenant: TenantScope,
    snapshot: ReadSnapshot,
}

pub enum FamilyReadRequest {
    AttachmentMetadata(AttachmentRead),
    CounterGroup(CounterRead),
    TimeSeriesRange(TimeSeriesRead),
    RevisionPage(RevisionRead),
    CompareExchangeValue(CompareExchangeRead),
    IdentityRange(IdentityRead),
    Lifecycle(LifecycleRead),
    Archival(ArchivalReadRequest),
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

pub enum FamilyReadResult {
    AttachmentMetadata(AttachmentMetadataResult),
    CounterGroup(CounterGroupResult),
    TimeSeriesPage(TimeSeriesPage),
    RevisionPage(RevisionPage),
    CompareExchangeValue(CompareExchangeValueResult),
    IdentityRange(IdentityRangeResult),
    Lifecycle(LifecycleResult),
    Archival(ArchivalReadResult),
    Validation(ValidationResult),
    Tombstones(TombstoneResult),
}

pub struct FamilyStore<'engine> { /* Engine-borrowing implementation */ }

impl<'engine> FamilyStore<'engine> {
    pub fn read(
        &self,
        context: FamilyReadContext,
        request: FamilyReadRequest,
    ) -> Result<FamilyReadResult, FamilyError>;

    pub fn stage(
        &self,
        transaction: &mut Transaction<'engine>,
        request: FamilyWriteRequest,
    ) -> Result<(), FamilyError>;
}
```

`FamilyReadResult` is a closed typed result set for attachment metadata and
bounded readers, counter groups, time-series pages, revision pages,
compare-exchange values, identity ranges, lifecycle state, validation state,
and tombstone views. Archival reads use `ArchivalReadRequest` and a typed
`ArchivalReadResult`. Attachment content uses an opaque bounded reader and
typed chunks. It never exposes a storage page, WAL frame, codec buffer, or
generic byte escape hatch. `FamilyStore` is obtained from the durable Engine;
it has no independent durable or in-memory production constructor.

`Engine::families` returns the borrowed `FamilyStore<'engine>` declared above.
It is tied to that Engine's lifetime and durable open state. It cannot outlive
the Engine, open a second storage location, or switch to an in-memory
implementation. The handle is the only production acquisition path for
`FamilyStore`; test fixtures may provide a non-durable store only through the
test-only construction rule above. A closed Engine returns typed
`FamilyError::EngineClosed` from family operations.

Family writes can only be staged into an Engine `Transaction`. The transaction
supplies the single `OperationId`, snapshot, conflict check, and commit
outcome. A family operation cannot commit independently or return a remote
`Acknowledgement Level`. Local `Buffered`, `Written`, and `Synced` outcomes
come from Engine storage. Replication `Convergence`, backup/import receipts,
and Protocol authorization belong to their later seams.

### Family invariants and internal seams

- Every read and staged write carries one validated `TenantScope`; family
  values, attachment metadata, cursors, and errors cannot cross tenants.
- `FamilyReadContext.tenant` must equal the tenant bound to its
  `ReadSnapshot`. A mismatch returns a typed `FamilyError` before any family
  data access, including index lookup or cursor creation.
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
`src/adapters/**`, or their tests. It does not edit `src/lib.rs`; W3-0 already
declared and re-exported the module roots, and no behavior packet has an export
exception. Cargo files, root CI, generated parity output, overlays, and shared
manifests remain integrator-owned.

W3-F must finish before W3-2 Query and Index starts. Query's family-aware
projection and index-source tests depend on the typed family result contract.
This is a safe sequential order, not accidental serialization. W3-F has no
query or index implementation authority, and W3-2 has no family mutation
authority. W3-3 Protocol starts only after both W3-F and W3-2 are accepted.

The public-seam red tests begin red at `FamilyStore::read` and
`FamilyStore::stage`:

- `engine_families_returns_store_bound_to_durable_engine`;
- `family_store_borrow_prevents_engine_shutdown_while_live`;
- `family_stage_rejects_transaction_from_another_engine`;
- `family_stage_rejects_transaction_with_a_different_operation_identity`;
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
- `family_read_context_rejects_tenant_mismatch_without_data_access`;
- `family_scope_rejects_cross_tenant_reads_and_writes`;
- `family_payload_constructors_validate_typed_fields_before_access`.

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
| W3-F Families | PB-3000, PB-3001, PB-3003, PB-3005, PB-3009, PB-3015, PB-3016, PB-3019, PB-3024, PB-3025, PB-3026, PB-3029, PB-3030, PB-3031, PB-3033, PB-3036, PB-3038, PB-3039 | Initial typed family CRUD, lifecycle, validation, identity, transaction, and supporting logical tenant-isolation behavior |
| W3-2 Query/Index | PB-3010, PB-3020, PB-3040 | Family query sources, projections, index maintenance, staleness, and ordering. Query owns the query/index code. |
| W3-3 Protocol | none | Closed typed forwarding only. W3-3 makes no authorization, tenant-isolation, confirmation, audit, secret-handling, or PB-3042 claim. |
| W4 Streaming | PB-3002, PB-3014, PB-3017, PB-3023, PB-3041, PB-3043 | Bounded family readers, changes, subscriptions, cancellation, acknowledgement, and backpressure |
| W4 Replication/qualification | PB-3004, PB-3006, PB-3011, PB-3012, PB-3018, PB-3021, PB-3027, PB-3028, PB-3032, PB-3034, PB-3035, PB-3037, PB-3044 | Family replication, conflicts, tombstones, sharding, archival interactions, and Region/Member behavior |
| W4 Backup/migration | PB-3007, PB-3013, PB-3022, PB-3045 | Family backup, import/export, restore, Logical Migration, manifests, and checksums |
| W5 adapters | PB-3008 | Remote attachment destination and typed credential/authorization behavior |
| W5 Platform Adapter (`adapter/platform-family-access`) | PB-3042 | Platform authorization, tenant isolation, confirmation, audit, secret handling, and the public family authorization proof. |

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

The public call path is concrete and typed:

```rust
impl ProtocolAdapter {
    pub fn call(
        &self,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, ProtocolError>;
}

impl PlatformAdapter {
    pub fn dispatch(
        &self,
        caller: &CallerContext,
        operation: PublicOperation,
    ) -> Result<PublicResponse, PlatformError>;
}
```

`ProtocolAdapter::call` routes every public operation through
`PlatformAdapter::dispatch`; the grant is validated before any Engine or
family data access. A direct Engine adapter is internal or `cfg(test)` only and
cannot back a public route. The closed `PublicOperation` type is the
typed-operation input; callers cannot replace it with a raw route, arbitrary
SQL, or storage handle.

Every public Protocol operation must pass `PlatformAdapter` authorization
before it reaches the Engine. `PlatformAdapter::dispatch` alone validates the
caller, tenant, operation permission, confirmation requirement, expiry, audit,
and secret-handling policy. It creates a private local `PlatformGrant`,
converts the approved operation to a closed `EngineRequest`, calls
`EnginePort`, and returns only a typed `PublicResponse`. The Engine receives no
grant and cannot make a Platform authorization decision. Protocol receives no
grant authority and cannot mint a scope. A direct Engine adapter is allowed
only for `cfg(test)` or an internal trusted path; it cannot be mounted behind a
public Protocol route.

Protocol tracer tests must begin red for:

- an approved typed operation with a stable operation identity;
- `caller_context_try_new_structurally_validates_and_redacts_evidence`;
- `public_operation_constructors_reject_generic_payloads_and_permissions`;
- `protocol_call_accepts_only_opaque_caller_context`;
- `protocol_forwarding_calls_platform_port_before_engine`;
- malformed or unknown operation rejection without generic forwarding;
- private control-plane operation rejection;
- `protocol_rejects_generic_route_body_and_storage_handle`;
- bounded cancellation and deadline errors that preserve operation identity;
- adapter behavior with an in-process test transport and a production-shaped
  transport adapter.

W3-3 constructs `ProtocolAdapter` with the `cfg(test)`
`new_with_platform_port` double only. It may observe call order and typed
forwarding through that private `PlatformPort`; it does not construct a real
authorizer, mint a grant, or claim PB-3042. Production construction uses the
W5-owned `PlatformAdapter` through the public
`ProtocolAdapter::from_platform` path.

The core remains sync. A future async Protocol Adapter must use bounded
blocking capacity, deadlines, cancellation, and a typed overload error. It may
not alter commit outcome semantics or claim that cancellation proves rejection.

W5 Platform Adapter owns the authorization tests. They begin red for denial,
confirmation, and grant-opacity behavior:

- `platform_denies_missing_expired_wrong_tenant_and_insufficient_callers`;
- `platform_requires_confirmation_for_mutations`;
- `platform_grant_is_private_and_never_reaches_protocol_or_engine`;
- `platform_redacts_secrets_from_errors_and_audit_records`;
- `platform_dispatch_creates_engine_request_only_after_authorization`;
- `engine_error_mapping_is_exhaustive_and_redacted`;
- `platform_error_mapping_is_exhaustive_and_redacted`;
- `mapped_error_preserves_operation_identity`;
- `platform_denial_precedes_engine_dispatch`;
- `platform_owns_pb_3042_authorization_proof`.

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
       /       \
W4 Replication  W4 Backup/Migration
       \       /
   W5 Platform Adapter
```

W3-F follows W3-1 because it needs the durable Engine transaction and
immutable `ReadSnapshot`, but it does not need Query or Index implementation.
W3-2 follows W3-F because its family-query and family-index tracer slices
consume the typed family result and projection-input contract. The disjoint
path ownership is still enforced at every step. W3-1 remains the only packet
allowed to modify or qualify Storage/WAL/recovery behavior, and W3-F cannot
pull Query, Index, replication, Protocol, or backup code into its scope.
W4 Replication and W4 Backup/Migration both start from the accepted W3-3
Protocol commit. W5 Platform Adapter starts only after both W4 commits are
accepted; it never bases directly on W3-3 and cannot begin on one W4 branch
alone.

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
5. W3-3 returns typed Protocol forwarding evidence without an authorization
   claim.
6. W4 Replication and W4 Backup/Migration return their separate behavior,
   recovery, migration, and receipt evidence from the accepted W3-3 commit.
7. W5 returns Platform authorization, tenant-isolation, confirmation, audit,
   secret-handling, and PB-3042 evidence only after both W4 handoffs.

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
- `begin_rejects_snapshot_from_another_engine`;
- `begin_rejects_snapshot_for_another_tenant`;
- `begin_rejects_scope_different_from_snapshot`;
- `begin_rejects_invalid_operation_id`;
- `unknown_operation_id_resolution_preserves_original_identity`;
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
- `query_store_compiles_and_executes_against_supplied_snapshot`;
- `query_input_normalizes_typed_and_v1_text_through_one_compile_seam`;
- `query_input_rejects_unsupported_or_generic_query_text`;
- `query_store_borrow_prevents_engine_shutdown_while_live`;
- `query_rejects_snapshot_from_another_engine`;
- `query_rejects_snapshot_for_another_tenant`;
- `query_rejects_generation_from_another_namespace_or_epoch`;
- `plan_reuse_does_not_mutate_the_snapshot`;
- `projection_and_paging_have_typed_errors_and_boundaries`;
- `query_cancellation_is_bounded_and_does_not_leak_a_lock`;
- `query_rejects_a_wrong_tenant_scope`.

W3-2 does not claim the complete V1 query/index matrix. It proves the chosen
seam and leaves each row unresolved until the parity test and Proof ID exist.

### W3-3, Protocol

W3-3 starts only from accepted W3-2 behavior and owns only the Protocol Adapter
paths. Its red tests are the typed-operation, private-operation, generic-access
rejection, deadline, cancellation, and overload cases listed in the Protocol
section above. Grant opacity, authorization, confirmation, audit, and
secret-redaction tests belong to the later W5 Platform Adapter packet. W3-3
must use an in-process adapter for deterministic tests and keep
transport-specific details out of Engine.

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
| W3-F | family rows PB-3000 through PB-3045 assigned by the family mapping, excluding PB-3042 | Contract and focused-test evidence only; no parity qualification |
| W3-2 | query and index rows PB-4000 through PB-4061 that its tests cover, including family query rows only after W3-F | Contract and focused-test evidence only; no parity qualification |
| W3-3 | approved protocol rows PB-1000 through PB-1033 that its tests cover | Typed forwarding evidence only; no Platform authorization claim |
| W5 Platform Adapter | PB-3042 | Platform authorization evidence only; no claim until the Platform tests and Proof ID exist |

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
  `workspace-root:specs/GOAL.md`; the parent resolves it
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
- `PlatformGrant` stays crate-private inside the Platform Adapter, and Protocol
  has no grant authority or generic/private operation escape hatch.
- Data Families have a closed typed logical seam, a W3-F packet, explicit
  family-row ownership, and no authority over Query, Storage, replication,
  backup, Protocol, or Platform behavior.
- Production has no public durable in-memory Engine constructor, and raw bytes
  stay in private storage codec/WAL code.
- W3-0's declaration-only module tree and public-export/lifetime tests are
  explicit; the integrator owns shared roots after preparation, and W5 owns
  the separate Platform Adapter implementation and PB-3042 proof.
- Every public operation, query input, and family payload has a named
  validated construction path and read-only observation rule.
- `CallerContext` is untrusted, structurally validated, redacted, and has no
  grant or permission authority. Protocol uses only the `cfg(test)`
  `PlatformPort` double for W3-3 forwarding tests.
- Commit outcomes expose safe accessors, `RetryDisposition` is exhaustive, and
  Engine-to-Platform-to-Protocol error mappings are exhaustive and redaction-safe.
- The W3-1, W3-F, W3-2, W3-3, W4 Replication, W4 Backup/Migration, and W5
  worktrees and collision ownership are explicit. W5 depends on both W4
  handoffs.
- The 42 PB-5000 through PB-5041 obligations remain visible and unresolved
  until their required tests and receipts exist.
- W2 is reviewed GO at integrated and pushed commit `8771f13`; strict
  qualification remains blocked by the same 42 gaps.
