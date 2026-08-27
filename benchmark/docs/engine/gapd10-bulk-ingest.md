# GAP-D10: the engine-side bulk-insert ingestion stream

Status: decision record for Forgejo ticket TerraBaseV2#61 (GAP-D10), recorded
2026-08-27 against the `fleet/gapd10-2` worktree of `develop` @ `d7d8563`.

Parity row: PB-2015 `bulk-insert` (core), currently `not-implemented` in
`docs/parity/ps-01-documents-sweep.md:78` and `:382-395`. This packet
implements the **engine half** of the row: the streaming multi-document
writer with bounded memory, per-document typed refusals, exact abort
accounting, and a reconciling batch receipt. The **public path** — a TBEP
frame family and the C# host's bulk-insert surface over it — is proposed in
D7 below and belongs to a later packet; no host/CLI surface is built here.

V1 evidence identity: `fa99b9257776dbe625e6a2388c74c9ce916637ee` under
`.worktrees/terrabase-v1-fa99b925-readonly` (read-only). Every V1 file
reference below is inside that pinned tree, cited by exact file and line.

Test evidence: `src/engine/ingest_tests.rs` (the in-crate contract proofs:
bounded memory, refusal-without-abort, duplicate-key last-write-wins,
conflict abort accounting, unknown-outcome abort accounting, cancellation
accounting, bounded refusal disclosure) and
`tests/engine/ingest_public_contract.rs` (public construction vocabulary).
`TenantScope` has no public constructor by design, so the commit-path proofs
run in-crate, following the engine, patch, and session convention.

Disposition vocabulary: **closed-here**, **pinned-compatible**, **defer**,
**accepted-difference** — used exactly as the neighboring records use them.

## D1 — V1's bulk insert, mapped from the pinned tree

V1's `BulkInsert` is one long-lived client stream feeding server-side batch
transactions. The observable contract, from source:

- **One operation id per stream.** The client asks the server for the next
  operation id before the first store (`WaitForId`,
  `src/TerraBase.Client/Documents/BulkInsert/BulkInsertOperation.cs:311-329`)
  and every document rides one chunked HTTP POST to
  `/databases/{name}/bulk_insert?id={id}` (`BulkInsertCommand.CreateRequest`,
  `BulkInsertOperation.cs:67-83`).
- **Documents stream one at a time.** Each `StoreAsync` writes one JSON
  command object (`{"Id":...,"Type":"PUT","Document":...}`) to the request
  body and flushes by need — never a materialized whole batch
  (`WriteToStreamAsync`, `BulkInsertOperation.cs:412-446`). Heartbeat objects
  keep the connection alive between stores (`SendHeartBeatAsync`,
  `BulkInsertOperation.cs:243-280`).
- **The server batches with explicit bounds.** The handler reads commands one
  at a time into a growable array (initial capacity 8,
  `AbstractBulkInsertHandlerProcessor.cs:103`) and flushes the accumulated
  batch when the network read would block, when the batch exceeds 16 MB, or
  when it holds 8192 operations (`AbstractBulkInsertHandlerProcessor.cs:
  119-121`, growth at `:160-162`). Server memory is bounded by the batch, not
  the stream.
- **One transaction per batch — per-batch atomicity, not whole-stream.** Each
  flush enqueues one `MergedInsertBulkCommand` on the transaction merger
  (`BulkInsertHandlerProcessor.cs:34-48`), which applies the commands
  sequentially in one transaction (`MergedInsertBulkCommand.ExecuteCmd`,
  `src/TerraBase.Server/Documents/Handlers/Processors/BulkInsert/
  MergedInsertBulkCommand.cs:29-80`).
- **Where V1 continues on a per-document problem (client side).** The
  client-side protocol checks — empty id, id ending with `|`
  (`VerifyValidId`, `BulkInsertOperation.cs:528-539`; pinned test
  `test/TerraBase.EndToEndTests/Client/BulkInsert/BulkInserts.cs:134-146`) —
  throw to the caller *before anything reaches the stream*: the operation
  object stays usable, so a caller that catches can keep storing. Inside a
  batch, `SkipOverwriteIfUnchanged` skips unchanged documents and continues
  (`MergedInsertBulkCommand.cs:42-59`), and a Voron write conflict rewrites
  HiLo-style ids and replays the whole batch as a new transaction
  (`MergedInsertBulkCommand.cs:63-80`).
- **Where V1 stops (server side).** Any failure that reaches the server —
  a stream parse error or a batch transaction failure — aborts the whole
  operation: the handler closes the connection and rethrows with the
  progress snapshot, and **no result object is produced**
  (`AbstractBulkInsertHandlerProcessor.cs:216-223`; the success-only
  `BulkOperationResult` construction sits at `:205-214`). Batches already
  committed stay committed — there is no cross-batch rollback — but the
  operation's terminal state is failure, and the client observes it as
  `BulkInsertAbortedException` on every later store or dispose
  (`HandleErrors`/`GetExceptionFromOperation`,
  `BulkInsertOperation.cs:442-468` and `:541-549`).
- **What abort promises.** `AbortAsync` is a no-op when nothing was stored
  (`OperationId == -1`, `BulkInsertOperation.cs:600-604`), otherwise it kills
  the server operation (`KillOperationCommand`, `:605-613`); post-abort
  stores fail (pinned test `KilledTooEarly`, `BulkInserts.cs:111-134`). An
  empty stream disposes cleanly (`BulkInsertOperation.cs:210-214`).
- **The batch shape.** V1's session `BatchOperation` orders deletes, then
  puts, then deferred commands (`Session/Operations/BatchOperation.cs:37-39`,
  cited in `docs/sessions/gapd01-ownership-decision.md` row 2). Bulk insert
  carries only puts, applied in stream order within each batch transaction
  (`MergedInsertBulkCommand.cs:31-61`).
- **The result shape.** Success reports counts only — `Total`,
  `DocumentsProcessed`, `AttachmentsProcessed`, `CountersProcessed`,
  `TimeSeriesProcessed` (`BulkOperationResult`,
  `AbstractBulkInsertHandlerProcessor.cs:207-214`) — never a per-document
  ledger.

## D2 — the V2 engine-side stream

`IngestStream` (`src/engine/ingest.rs`) is the engine-owned half: a caller
supplies any `IntoIterator<Item = Document>`; the stream consumes it **one
document at a time**, staging into a forming batch bounded at
`MAX_INGEST_BATCH_DOCUMENTS` (8192, V1's server operations bound at
`AbstractBulkInsertHandlerProcessor.cs:121`), and publishes each full batch
as **one engine transaction** — one `OperationId`, one atomic commit, all or
nothing — exactly V1's per-batch transaction shape mapped onto the engine's
commit seam.

Decisions:

- **D2.1 Bounded memory (closed-here).** Nothing in the stream is O(stream
  length): the forming batch holds at most `batch_documents` documents, the
  receipt holds counters plus O(1) detail (the last committed batch, the
  terminal failure, a bounded refusal window), and the source iterator is
  pulled incrementally. The focused test
  `bulk_ingest_never_materializes_the_whole_batch` proves it behaviorally:
  while the source is being consumed, the published sequence must already
  have advanced for every batch behind the cursor — an implementation that
  collects the source fails the per-pull invariant. It was observed RED
  against a collect-everything scaffold and GREEN against the streaming
  implementation (receipts in the packet report).
- **D2.2 Per-document refusal without abort (closed-here, V1-continued
  class).** A document that fails the engine's structural limits
  (`document_within_limits`) or its collection's schema is **refused typed**
  (`IngestRefusal`, reason `InvalidDocument`), stages nothing, and **the
  stream continues** — V1's continue class (client-side protocol checks and
  skip-unchanged) mapped to the engine's per-document validation position.
  Refusals are counted exactly; their details are disclosed through a
  bounded drop-oldest window (`MAX_INGEST_REFUSAL_DETAILS` = 64) with the
  overflow counted, the same bounded-disclosure idiom the streaming
  subscription seam uses.
- **D2.3 Commit failure aborts the stream (closed-here, V1-stop class).** A
  batch commit that returns `Rejected` or `Unknown` stops the stream, mapping
  V1's server-side stop (`AbstractBulkInsertHandlerProcessor.cs:216-223`):
  earlier committed batches stay committed (V1 has no cross-batch rollback),
  the failed batch's documents are accounted **aborted**, and the receipt
  makes no success claim for them. An `Unknown` outcome keeps the batch's
  `OperationId` in the receipt (`IngestTermination::Unknown`) so the caller
  resolves it by `Engine::resolve_commit` — the receipt itself claims
  nothing either way.
- **D2.4 Duplicate keys: last write wins within a batch (closed-here,
  V1 parity).** V1 applies a batch's puts sequentially, so a repeated id
  overwrites in-transaction (`MergedInsertBulkCommand.cs:31-61`). The engine
  commit seam refuses duplicate targets in one write set
  (`has_duplicate_targets`, `src/storage/file.rs:332,548`), so the forming
  batch keys by `(collection, id)` and a re-put replaces — V1's observable
  outcome on the engine's atomicity contract. Both pulls count as committed
  when their batch commits; the reconciling identity is the pull count.
- **D2.5 Abort and cancellation (closed-here).** The caller's
  `Cancellation` gates between pulls and before the final partial batch
  publishes — the position of V1's per-store token check — observed, the
  stream stops, the forming batch's documents are accounted aborted, and
  committed-so-far stays honestly counted: an abort never reports a
  document it did not commit. The signal is passed **by reference to
  `run`**, mirroring `StreamStore::next_batch`'s cancellation parameter,
  because a cloned `Cancellation` is an independent snapshot
  (`src/types.rs:229-238`) — a by-value signal on the request could never be
  cancelled mid-run, which the scaffold's RED run demonstrated concretely.
  `IngestStream::abort` is V1's nothing-was-done `AbortAsync`
  (`BulkInsertOperation.cs:600-604`): a receipt with zero counts and no
  claim. There is deliberately no post-abort store to refuse: `run`/`abort`
  consume the stream, so use after abort is a compile error, not a runtime
  refusal.
- **D2.6 The receipt reconciles (closed-here).**
  `attempted == committed + refused + aborted` holds on every path; every
  pulled document lands in exactly one bucket. `is_complete()` is true only
  for `IngestTermination::Completed` (source exhausted). This is V1's
  counts-only result shape (`AbstractBulkInsertHandlerProcessor.cs:207-214`)
  plus the failure-side accounting V1 could only put in an exception
  message.

## D3 — operation identity: one `OperationId` per batch

The engine's `OperationId` discipline binds identity to the atomic unit: one
transaction reserves one operation identity, every staged write adopts it,
and the durable ledger resolves by it (`Engine::begin` reservation,
`Engine::resolve_commit`). Bulk insert therefore mints one identity per
**batch** — `tbv2-ingest-{stream}-{ordinal}` — never per document: a
per-document identity would imply per-document transactions, which is
neither V1's shape (one `MergedInsertBulkCommand` per flush) nor atomic.
Per-document accounting rides the receipt's counts; per-batch identity
rides `IngestBatchReceipt` (the last committed batch) and the termination's
`operation` field, which are the resolution handles a caller needs after an
abort. The stream identity is minted from a process-wide monotonic counter,
like the session's (`src/session/mod.rs:511-514`), so identities are never
reused within or across streams.

## D4 — the destructive variant is an explicit typed selection

Bulk insert's V1 semantics overwrite existing documents (`Put` with a null
change vector — `MergedInsertBulkCommand.cs:61`). The typed request makes
that destructive mode an **explicit constructor argument**, never a default:
`IngestConflictMode::Overwrite` stages every put under
`ExpectedVersion::Any` (V1 behavior), while `IngestConflictMode::CreateOnly`
stages under `ExpectedVersion::New` and a batch containing an existing
document is rejected as a typed conflict, aborting the stream (D2.3). A
caller cannot reach the overwrite path without naming it. Per charter
`PD-2026-08-26-07`, agent-facing confirmation gates (an MCP/host confirm
step before a destructive bulk ingest) belong to the typed-operation
**surface**, not the engine seam; the engine's contribution is that the
destructive variant is structurally explicit. The `EngineRequest`/
`PublicRequest` registry deliberately gains no `BulkIngest` variant in this
packet: `src/adapters/platform/engine_port.rs` matches that enum
exhaustively and adapters are outside this packet's write scope — D7
proposes the wiring for the host-path packet instead.

## D5 — V1 surface elements and their dispositions

| V1 observable | Disposition | Where |
|---|---|---|
| One operation id per stream (client) | **accepted-difference** | V2 identity is per batch (D3); the durable ledger resolves per batch, which is the unit V1's kill-operation could not actually make precise mid-batch |
| Document-at-a-time streaming, heartbeats | **closed-here** (streaming) / **defer** (heartbeats) | The Rust iterator has no idle connection to keep alive; heartbeat frames are a transport concern for the TBEP packet |
| Server batch bounds 16 MB / 8192 ops | **closed-here** (8192 documents) / **accepted-difference** (bytes) | `MAX_INGEST_BATCH_DOCUMENTS` mirrors the ops bound; the engine's per-document limits already bound bytes (`document_within_limits`), so a second byte bound would be redundant |
| Client id checks: empty id, trailing `\|` | **pinned-compatible** | Structurally unreachable in V2: the seam takes `Document`, whose `DocumentId` is validated at construction (`types.rs:51-66`) — no suffix rule exists to re-implement |
| `SkipOverwriteIfUnchanged` | **defer** | A read-compare per document is a policy V2 can add on the typed seam later; the record notes it, the engine implements overwrite and create-only (D4) |
| Voron conflict id-rewrite replay | **accepted-difference** | V2 has no HiLo id-rewrite path; a conflicting create-only batch aborts typed, and the caller retries with new identities (`CommitError::retry_disposition`) |
| Counters/attachments/time-series bulk sub-streams | **out of scope** | Family writes stage through the family seam (`src/families`); a bulk family stream is a family-wave ticket, not the documents row |
| `BulkOperationResult` counts | **closed-here** | `IngestReceipt` counts, plus the failure-side exact accounting V1 packed into an exception (D2.6) |
| Progress events (`OnProgress`, `BulkInsertProgress`) | **defer** | Observability surface; the receipt carries the terminal facts. Progress callbacks are natural on the host path (D7) |

## D6 — what this packet does not claim

No performance claim of any kind: bounded memory is a structural claim
proved by the focused test, not a throughput or latency claim, and nothing
here ran the benchmark lane (PD-2026-08-26-03). No durability or recovery
claim beyond what the engine commit seam already proves: the stream adds no
new storage behavior. No whole-stream atomicity: V1 does not have it and
neither does V2 — atomicity is per batch, and the receipt says exactly where
the boundary fell.

## D7 — proposed public path (not built here)

The host/TBEP bulk-insert packet should wrap `IngestStream` as: (1) a TBEP
frame family carrying documents in bounded frames over the UDS transport to
an engine-side stream — the engine consumes frame-by-frame through the same
forming-batch logic, so the bounded-memory proof transfers; (2) a
`BulkIngest` entry in the typed operation registry
(`src/operations.rs`) wrapping `BulkIngestRequest` with the caller's
`TenantScope`, matched in `src/adapters/platform/engine_port.rs`; (3) a
destructive-mode confirmation at the surface (explicit
`IngestConflictMode::Overwrite` acknowledgement, per PD-2026-08-26-07's
confirmable-mutation rule); (4) progress disclosure mapping
`IngestReceipt`-style counters per frame window. SE-08's host owns the
caller-facing retry story: the receipt's termination and the engine's
`retry_disposition` mapping are the inputs.
