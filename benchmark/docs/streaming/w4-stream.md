# W4-STREAM design and evidence record

Status: implementation record for the W4-STREAM packet, 2026-08-24.
Authority: `docs/design/w4-transfer-replication-contract.md` ("Streaming
contract", "W4-STREAM red tests", "Public type ownership"). Worktree
`terrabasev2-w4-stream`, branch `fleet/w4-stream`, based on `ecdca26`
(W4-0 declarations plus the executed integrator actions W4-CA-2, W4-CA-3,
W4-CA-7).

This record describes what was built, the delivery and overflow policy, the
per-row coverage of the parity rows this packet owns, and the explicit
non-claims, limits, and proposed collision actions. It resolves no parity
row: every row below stays `unresolved` with an empty Proof ID list until
the parity owner records dispositions.

## What was built

`src/streaming/` implements the frozen W4-0 declaration tree:

- `mod.rs` — the public types (frozen names, private fields, validated
  constructors) and the `StreamStore<'engine>` seam: `open`, `next_batch`,
  `position`, `subscribe`, `next_subscription_batch`, `acknowledge`,
  `subscription_status`, `disconnect_subscription`, `close_subscription`.
  The only acquisition path stays `Engine::streams()` (W4-CA-2).
- `sources.rs` — snapshot-bound sources: cursor minting and binding,
  document selectors (id, prefix, match, exclusion), the W4-CA-7 windowed
  read batching, query-plan slicing, time-series and family reads through
  the family seam, and the typed error mappings.
- `subscription.rs` — the publication-registry sink, the bounded
  notification ingress, journal-driven delivery, checkpoint/acknowledge/
  resend state, and the reconnect lifecycle.
- `tests.rs` — the 22 scope-bound red tests of the contract's list (they
  use `TenantScope::from_parts`, which is `pub(crate)`); the 2
  public-construction red tests live in `tests/streaming/`
  (`engine_streams_returns_store_bound_to_durable_engine`,
  `stream_store_borrow_prevents_engine_shutdown_while_live`) with one
  focused extra validating typed request construction. All 24 contract
  names are present and green, plus focused extras.

### The batch contract

`open(scope, snapshot, source)` validates the scope against the Engine
(a `read_snapshot` probe), the snapshot against the scope, and the source
(selectors, query-plan page bound, deadlines, signals, exact-id
existence), then mints one opaque `StreamCursor` binding the engine
identity, the source's structural token, and the snapshot's storage
sequence. `next_batch(scope, snapshot, source, cursor, limit,
cancellation)` serves at most `limit` items, returns the cursor that
resumes exactly after them, and reports `StreamCompletion::EndOfStream`
exactly once; re-polling a finished stream is an idempotent empty terminal
batch.

Ordering per source, as the contract fixes it:

- **Documents** (PB-6007): canonical key order within the selected
  collection, served by the W4-CA-7 windowed read
  (`Engine::snapshot_documents_from`), never `snapshot_documents`. The
  stream never materializes the tenant: one call reads at most
  `MAX_WINDOW_READS_PER_BATCH` (64) windows of at most `limit + 1`
  documents and keeps only the matches.
- **Queries** (PB-6008): each batch executes the immutable plan through
  the public `QueryStore` path and slices the plan's bounded result window
  at the cursor offset, so query-stream memory is bounded by the plan's
  page limit. A limit-free plan is refused at `open` with
  `StreamError::InvalidSource` (an unbounded page would make stream memory
  unbounded). The batch reports the index generation that execution used —
  faithfully `None` while the W3-2 execution path is index-less.
- **Time-series** (PB-6009): one family-seam read per batch, sliced at the
  cursor offset; timestamp, tag, and value shape pass through unchanged.
- **Family** (PB-3002, PB-3014): one `FamilyStore::read` per stream; the
  stream wraps the family seam's readers and never bypasses them (the
  test compares the streamed value with a direct family read).

### The never-committed read rule

Streaming code never begins an Engine transaction. Snapshot sources read
through `snapshot_documents_from`, the public query path, or the family
read seam; subscription batches read the durable publication journal
(`tenant_commits`), which contains only published commits; the
notification sink receives `CommitPublicationEvent` facts only after
storage locks release. `changes_never_expose_an_uncommitted_or_rejected_
transaction` proves a staged-but-uncommitted write, a dropped transaction,
and a rejected commit are all invisible.

### Cursor resumability

The cursor binds (engine identity, structural source token, snapshot
sequence, private position). The token covers every structural field of
the source and deliberately excludes deadlines and cancellation signals —
`Cancellation::clone` severs identity, so a token that included signals
would reject every legitimate continuation. A cursor from another source,
engine, or snapshot is `StreamError::InvalidCursor`
(`stream_cursors_resume_exactly_where_the_previous_batch_ended`). The
document resume rule inherits the storage seam's documented
at-least-once caveat for an identifier reused across collections:
duplicates only, never losses.

## Delivery design and the overflow policy

Subscriptions are pull-based and journal-driven, with the registry sink as
a bounded notification ingress. The split is the honest answer to the
W4-CA-3 sink rule (the sink runs synchronously on the committing thread,
must never block, never take a storage lock, and never grow an unbounded
queue):

1. **Ingress.** One streaming-owned sink per subscribed Engine registers
   through `Engine::subscribe_publications` on the first `subscribe` and
   detaches when the engine's last subscription closes. The sink clones
   two small identifiers, takes the subscription's own mutex, pushes into
   a bounded drop-oldest queue, and returns. **Overflow policy:
   DROP-OLDEST with a counted drop.** The bound is
   `MAX_SUBSCRIPTION_NOTIFICATIONS` (64 pairs); what a subscriber sees
   when it falls behind is a growing cumulative `dropped_notifications`
   count on its batches, never a blocked commit and never a lost event.
   A lock the sink cannot take is skipped the same way.
2. **Delivery.** `next_subscription_batch` resolves events on the
   consumer's thread from the durable publication journal after the
   checkpoint. Because the journal is the source of truth, a dropped
   notification costs latency, never correctness: unacknowledged and
   never-polled events remain servable, which is also the resend source
   after reconnect.
3. **Lock order.** The consumer takes the subscription lock, then the
   storage lock (inside `tenant_commits`). The commit path takes the
   storage lock, releases it, and only then emits to the sink, which
   takes the subscription lock. Because the W3 publication order emits
   after lock release, the two orders never hold both locks at once and
   cannot deadlock. `stream_backpressure_never_blocks_an_engine_commit`
   runs 72 publications past a never-draining consumer and asserts every
   commit completed, the batch stayed bounded, and the overflow was
   observable.

`StreamCompletion::SlowConsumer` remains the terminal observation a
future push-based transport reports when its own bounded buffer forces a
disconnect; W4-STREAM's pull path cannot observe it (batches are
caller-paced and bounded by request), which is documented rather than
papered over. The `StreamError::SlowConsumer` variant is likewise
reserved for a blocking-call seam this packet does not add.

### Checkpoint, acknowledgement, resend

The checkpoint is `SubscriptionCheckpoint { sequence, write_index,
epoch }` (opaque): `sequence` is the global storage sequence of the last
acknowledged publication, `write_index` the write within that commit (so
one batch boundary may split a commit without acknowledging writes the
consumer never saw), and `epoch` the incarnation counter so a
closed-and-reopened subscription never accepts a stale checkpoint.
Batches are built commit-by-commit and stop before a commit whose
deliverable events would exceed the spec bound; the recorded boundary is
the position of the last delivered event. The boundary is recorded before
the call returns; the durable checkpoint advances only in `acknowledge`,
which is idempotent for the current checkpoint and returns
`StreamError::InvalidCursor` for a stale one. An unacknowledged batch is
redelivered verbatim on the next poll and after a
disconnect/reconnect/subscribe cycle — at-least-once, with stable
(`OperationId`, `StorageSequence`) identity so duplicates are
identifiable.

Filtered events are progress facts (`filtered_dropped`) and never move
the boundary by themselves: an all-filtered batch reports its drops and
returns the checkpoint unchanged, which is what
`stream_filters_report_drops_without_advancing_the_checkpoint` asserts.
The honest cost: a filtered event before the next delivered event is
re-examined on every poll until a delivery passes over it, and a prefix
of filtered events longer than the scan budget
(`MAX_SUBSCRIPTION_SCAN_EVENTS`, 4096 examined writes per poll) is
reported as drops and re-examined without delivering — a bounded-work
liveness limit, not a correctness gap. The contract's own rule forces
this choice ("Filtered or skipped items do not silently advance a
checkpoint").

### Change-kind mapping

`ChangeEvent`s are expanded from journal `PublishedCommit` writes. Writes
to the reserved family collection map `family:counters/*` to
`ChangeKind::Counter` and `family:time-series/*` to
`ChangeKind::TimeSeries`; every other write — user documents, the
remaining family records, and deletes — maps to `ChangeKind::Document`
with the changed key's collection. A no-op delete (an identifier that
existed nowhere) produces no event. Index and database change kinds are
not produced in W4-STREAM: index publication is not an engine event
source yet (see non-claims).

### Subscription state and ownership

`subscribe` is refused while a subscription is open — exactly one active
consumer at a time — and reattaches to the preserved checkpoint and
pending batch when the subscription is `Reconnecting` (set by
`disconnect_subscription`). `close_subscription` is terminal and releases
the core and its queue. Cancellation is a per-call live signal (see
limits): a cancelled poll returns `StreamError::Cancelled` and releases
the subscription's state.

## Per-row coverage notes (no resolution claims)

Every row remains `unresolved`; these notes name what the packet's tests
observe at the public seam, as candidate evidence for future Proof IDs
the parity owner may record.

| Row | Coverage note |
| --- | --- |
| PB-6000 (streaming) | Bounded batches, canonical ordering, typed terminal observations, cancellation liveness, and backpressure-free commits are proven at the seam (`stream_batches_honor_…`, `stream_backpressure_…`, `stream_cancellation_…`). |
| PB-6002 (changes-api) | Committed-only observation, event identity (operation + sequence) in order, uncommitted/rejected invisibility, and tenant isolation are proven; the V1 wire shape, connection state machine, and throttling are not implemented or claimed. |
| PB-6003 (subscription wire/batches) | Named specs, bounded batches, one-batch-then-ack negotiation, and explicit status transitions are proven at the engine seam; no wire format, heartbeat, compression, or buffer-size negotiation exists (see non-claims). |
| PB-6004 (checkpoints/ack/resend) | Boundary recording before the wait, checkpoint advance only at the acknowledgement seam, stale-ack refusal, resend after reconnect, and duplicate identity are proven; checkpoint durability is engine-lifetime, not restart-durable (see limits). |
| PB-6005 (reconnect/failover/backpressure) | In-process disconnect/reconnect preserving the checkpoint and the bounded non-blocking ingress are proven; cross-member failover, worker takeover, and retry timing belong to W4-REPLICATION's lane and are not claimed. |
| PB-6006 (subscription criteria; typed portion only, D-5) | Typed collection/kind filters with reported drops and the no-advance rule are proven. No script runs (D-5); projection and revision criteria are not implemented. |
| PB-6007 (document streaming) | Id/prefix/match/exclusion selectors, canonical order, result shape, missing-document and invalid-selector typed errors, and snapshot-bound continuation are proven. The `match` selector is substring containment, not V1's regex (D-5-adjacent; see limits). |
| PB-6008 (query streaming) | Plan-order streaming, per-batch index-generation reporting (honestly `None` on the index-less tracer), staleness-policy refusal, and the bounded-page requirement are proven; result-format parity, statistics, and sharded merging are not. |
| PB-6009 (time-series streaming) | Timestamp/tag/value order and range boundaries are proven through the family seam; rollups, offsets, and incremental series views are the family seam's current behavior only. |
| PB-3014 (counters changes/subscriptions) | Counter family writes surface as `ChangeKind::Counter` events with ordering and acknowledgement (not separately asserted); counter-bearing document delivery semantics are not separately claimed. |
| PB-3017 (time-series range stream) | Range boundaries, ordering, bounded batches, and cancellation are proven through the family reader; totals, includes, and offsets are not implemented. |
| PB-3023 (time-series changes/subscriptions) | Time-series family writes surface as `ChangeKind::TimeSeries` events under the same checkpoint rules; include-ranges in document batches are not implemented. |
| PB-3041 (family changes/subscriptions) | Family-record writes participate in the change stream under the same bounded/acknowledged/checkpointed rules; per-family payload inclusion is not implemented. |
| PB-3043 (family size stream range) | Bounded reads and cancellation through the family stream source reuse the family seam's readers; size limits and partial-consumption positioning are the family seam's, not extended here. |
| PB-6020 (partial; typed portion only, D-5) | No smuggler/transform work belongs to this packet; nothing is claimed. |

## Non-claims

- No parity row is resolved and no Proof ID is added; the tests are
  candidate evidence only.
- No V1 wire-format, framing, heartbeat, compression, or
  `MaxDocsPerBatch`-negotiation claim for subscriptions; the engine seam
  is typed and synchronous.
- No at-least-once claim beyond what the tests prove: redelivery of an
  unacknowledged recorded batch on poll and after in-process reconnect,
  with stable event identity. No cross-member failover, takeover, or
  retry-timing claim (PB-6033, PB-6005 lane work).
- No durability or replication claim of any kind: `Buffered`, `Written`,
  and `Synced` stay local; no acknowledgement-level, convergence, member,
  or region fact appears in any streaming type.
- No performance or speed claim; no numbers are recorded.
- No script execution (D-5): filters are typed only; the document `match`
  selector is substring containment, not regex.
- No claim that query streams serve index data: the batch reports the
  generation the (index-less, W3-2) execution path used.

## Assumptions

- The integrator actions W4-CA-2/3/7 as executed at `ecdca26` are the
  frozen read-only seams this packet builds on; their contracts (sink
  non-blocking rule, windowed read resume rule) are taken as given.
- `TenantScope` construction is Platform's production role; the in-crate
  tests mirror it with `from_parts` exactly as the W3/W4 engine and
  family tests do.
- `Engine::identity()` is test-only, so production streaming code derives
  the engine identity from a scope the Engine itself validated.
- The reserved family collection name is mirrored as a local constant the
  same way `src/query/spec.rs` mirrors it, because the constant lives in
  the family-private state module.
- A fixed notification bound (64) is the W4-STREAM configuration;
  per-deployment configurability is a later disposition.

## Limits

- **Checkpoint durability is engine-lifetime.** Subscription state lives
  in a crate-private registry keyed by engine identity; it survives
  consumer reconnect but not engine shutdown or process restart. The
  journal can rebuild any checkpoint, so restart durability is a storage
  seam away, not a redesign.
- **Notification-registry retention.** The registry entry of an engine
  dropped with open subscriptions persists until process end (bounded by
  abandoned subscriptions; removed on normal close).
- **No per-document storage sequence.** Document cursors position by
  document identity within the snapshot, so `StreamPosition` reports the
  snapshot's publication sequence, not a per-item sequence. The windowed
  read's id-only cursor also means a collection-scoped stream may
  re-scan non-matching documents between batches (bounded, at-least-once).
- **Exact-id existence probes** page in canonical order up to
  `MAX_WINDOW_READS_PER_BATCH` windows per probe at `open` time; a tenant
  with more than that many documents before the target in canonical
  order reports a present document as missing (the probe bound is
  documented and would be lifted by a per-id snapshot read seam).
- **Query streams re-execute the plan per batch** (the W3-2 path is a
  full scan) and require a bounded page; `index_generation` is `None`
  until query execution binds the engine's live index catalog.
- **Family and time-series streams read whole records** per call
  (series are retention-bounded); page-by-page family streaming awaits a
  family-seam capability.
- **Filter-drop liveness.** As described above, an all-filtered prefix is
  re-examined per poll within the scan budget.
- **`StreamError` has no storage-failure variant** (frozen set): an
  unreachable or poisoned storage lock reports `EngineClosed`, and family
  or query rejections report `InvalidSource`, each documented at the
  mapping site.

## Proposed collision actions

| ID | File | Proposed change | Why this packet cannot |
| --- | --- | --- | --- |
| W4-SCA-1 | `src/engine/mod.rs` | Engine-owned streaming subscription state: hold the subscription registry on the Engine (or expose a shutdown hook), making checkpoints restart-rebuildable and cleaning registry entries at engine drop. | `src/engine/**` is frozen to this packet. |
| W4-SCA-2 | `src/engine/mod.rs`, `src/storage/mod.rs` | A per-id snapshot read (`read_document_at` surfaced on the Engine) so exact-id selectors probe existence in one call instead of paging canonical order. | Same freeze. |
| W4-SCA-3 | `src/streaming/mod.rs` after parent adjudication | Add a storage-failure (or internal) variant to `StreamError` so poisoned locks and family/query rejections stop mapping onto `EngineClosed`/`InvalidSource`. | The variant set is the frozen W4-0 contract; widening it is a parent decision. |
| W4-SCA-4 | `src/query/mod.rs` or `src/engine/mod.rs` | Bind `QueryStore` execution to the Engine's live index catalog (the W4-CA-3 catalog) so query streams can report a used generation other than `None`. | `src/query/**` and `src/engine/**` are frozen. |
| W4-SCA-5 | `src/storage/root.rs`, `src/engine/mod.rs` | A collection-aware window cursor (resume at (collection, id) instead of id-only) so collection-scoped document streams stop re-scanning foreign collections. | `src/storage/**` and `src/engine/**` are frozen. |
| W4-SCA-6 | `src/families/mod.rs` | Expose `RESERVED_COLLECTION` crate-wide (one `pub(crate) use`) so streaming and query stop mirroring the literal. | `src/families/**` is frozen. |
| — | `src/engine/mod.rs:604` (doc-only) | The integrator's W4-CA-2 doc link `[`StreamStore`](crate::streaming::StreamStore)` triggers a `redundant explicit link target` rustdoc warning; simplify to `[`StreamStore`]`. Pre-existing at the base commit; recorded, not fixed (read-only file). | Same freeze. |

## Checks

Run at the packet tip (exit codes in the mission report): `cargo fmt
--all -- --check`, `cargo check --workspace --locked`, `cargo test
--workspace --locked --release` (223 passing: 198 baseline + 24 contract
tests + 1 focused extra), `cargo clippy --workspace --all-targets
--all-features --locked -- -D warnings`, `cargo doc --workspace
--no-deps --locked` (one pre-existing warning in `src/engine/mod.rs`,
see the collision table), `python3 scripts/validate_parity.py --check`.
