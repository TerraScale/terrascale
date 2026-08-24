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
  use `TenantScope::from_parts`, which is `pub(crate)`), plus the three
  repair-loop proofs; the 2
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
document resume rule inherits the storage seam's documented caveats for an
identifier reused across collections: duplicates only, never losses — and,
because the seam's cursor is identifier-only, a *cycle*: with a small batch
bound (the extreme is `limit = 1`) and the same identifier stored in an
earlier collection and the streamed one, each resume resolves to the
identifier's first canonical key and re-serves the later occurrence, so
the stream can redeliver that document indefinitely and never report
`EndOfStream`. Collection-scoped selectors do not avoid this, because the
streaming cursor is identifier-only even when the selector is scoped; the
structural fix is the collection-aware cursor already proposed as
W4-SCA-5. A tenant-stream caller that reuses identifiers across
collections should not use bound 1.

## Delivery design and the overflow policy

Subscriptions are pull-based and journal-driven, with the registry sink as
a bounded notification ingress. The split is the honest answer to the
W4-CA-3 sink rule (the sink runs synchronously on the committing thread,
must never block, never take a storage lock, and never grow an unbounded
queue):

1. **Ingress.** One streaming-owned sink per subscribed Engine registers
   through `Engine::subscribe_publications` on the first `subscribe` and
   detaches when the engine's last subscription closes. The sink clones
   two small identifiers and then takes every lock with `try_lock`
   **only** — it never waits. **Overflow policy: DROP with a counted
   drop.** Two drop kinds exist, both observable as the batch's cumulative
   `dropped_notifications` count:
   - *queue-full* — the consumer has not drained
     `MAX_SUBSCRIPTION_NOTIFICATIONS` (64) identifier pairs, so the oldest
     is dropped (drop-oldest) and counted in the guarded state;
   - *lock contention* — the subscription's lock is busy because a
     consumer is mid-poll (a poll holds its lock across the journal
     read), or the engine's core map is busy in a concurrent
     open/close/lookup, so the notification is dropped and counted in a
     lock-free counter that the committing thread can increment without
     waiting. An engine-level drop is attributed to every subscription of
     that engine, which is exactly what it means: no subscription
     received the notification.

   What a subscriber sees when it falls behind is therefore a growing
   cumulative drop count on its batches, never a blocked commit and never
   a lost event. The repair-loop red test
   (`publication_sink_never_blocks_a_commit_behind_a_wedged_poll`) wedges
   a consumer inside a poll — holding the subscription lock — and asserts
   a concurrent commit returns in milliseconds with the contention drop
   counted and the event still delivered from the journal; against the
   previous blocking-lock sink the same commit waited out the wedge
   (~5 s) and the test failed.
2. **Delivery.** `next_subscription_batch` resolves events on the
   consumer's thread from the durable publication journal after the
   checkpoint. Because the journal is the source of truth, a dropped
   notification — either drop kind — costs latency, never correctness:
   unacknowledged and never-polled events remain servable, which is also
   the resend source after reconnect.
3. **Lock order.** The consumer takes the subscription lock, then the
   storage lock (inside `tenant_commits`). The commit path takes the
   storage lock, releases it, and only then emits to the sink, which
   takes no lock it could wait on and no storage lock at all. Because the
   W3 publication order emits after lock release and the sink never
   waits, the two paths cannot deadlock and a poll can never stall a
   commit. `stream_backpressure_never_blocks_an_engine_commit` runs 72
   publications past a never-draining consumer and asserts every commit
   completed, the batch stayed bounded, and the overflow was observable.
4. **Collective ingress cost.** The sink fans each publication of an
   engine into each of its live subscriptions, so the per-commit ingress
   cost grows with the subscription count. The count is bounded at
   `MAX_SUBSCRIPTIONS_PER_ENGINE` (64): a subscribe beyond it is the
   typed `StreamError::InvalidSource` refusal, mirroring the engine's own
   `MAX_PUBLICATION_SINKS` rule, and closing a subscription frees its
   slot (`engine_bound_subscriptions_are_bounded_and_refused`).

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
The scan pages the journal after the checkpoint within the scan budget
(`MAX_SUBSCRIPTION_SCAN_EVENTS`, 4096 examined writes per poll) until the
batch is full, the journal is drained, or the budget is exhausted, so an
accepted event beyond a run of filtered events is reachable in one poll
(`subscription_reaches_events_beyond_a_long_filtered_run`; against the
previous single-window scan the same fixture delivered nothing
permanently). The recorded boundary is the position of the last event the
batch **delivered or reported as a filtered drop**. The boundary is
recorded before the call returns; the durable checkpoint advances only in
`acknowledge`, which is idempotent for the current checkpoint and returns
`StreamError::InvalidCursor` for a stale one. An unacknowledged batch is
redelivered verbatim on the next poll and after a
disconnect/reconnect/subscribe cycle — at-least-once, with stable
(`OperationId`, `StorageSequence`) identity so duplicates are
identifiable.

Filtered events are progress facts (`filtered_dropped`), and the recorded
boundary covers exactly the drops a batch reports: the advance is
*reported*, never silent, and the durable checkpoint still moves only at
the acknowledgement seam — the reading of the contract's "Filtered or
skipped items do not silently advance a checkpoint" that preserves
liveness. Without it, a filtered run longer than the fetch window stalled
delivery permanently, because the boundary moved only on delivered events
(the repair-loop S2 defect). Consequences: a compliant consumer that
acknowledges its batches never re-examines acknowledged drops (the
amended `stream_filters_report_drops_without_advancing_the_checkpoint`
asserts the reported advance, the verbatim redelivery before the ack, and
the no-re-scan after it), and a filtered run longer than the scan budget
is delivered as successive partial batches — each reporting its drops —
rather than one batch.

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

## Deviations and additive surface (S5)

The frozen W4-0 names and shapes are unchanged. The implementation adds
surface the declarations did not enumerate, listed here for the parent's
adjudication:

- **Types and variants:** `StreamSourceKind` (public, re-exported from
  `streaming`) is the public observation of a cursor's source kind that
  `StreamPosition` carries.
- **Module constants:** `MAX_STREAM_BATCH_ITEMS`,
  `MAX_SUBSCRIPTION_BATCH_ITEMS`, `MAX_EXCLUDED_IDS` (in `streaming`),
  and `MAX_SUBSCRIPTION_NOTIFICATIONS`, `MAX_SUBSCRIPTIONS_PER_ENGINE`,
  `MAX_SUBSCRIPTION_SCAN_EVENTS` (in the crate-private `subscription`
  module). Each is the configured bound its section names.
- **Constructors and methods:** `SubscriptionSpec::from_beginning`;
  `DocumentStreamSelector::{try_new, with_id, with_prefix, with_match,
  excluding}` and its accessors; `TimeSeriesStreamRange::try_new` and its
  accessors; `FamilyStreamSelector::try_new` and `request`;
  `ChangeFilter::try_new/kinds/collections/accepts`;
  `SubscriptionAcknowledgement::confirming`; `StreamBatch`'s accessors
  (including `index_generation`); `StreamPosition::{kind, sequence}`;
  `ChangeEvent`'s accessors; and the `StreamStore` methods
  `open/next_batch/position/subscribe/next_subscription_batch/acknowledge/
  subscription_status/disconnect_subscription/close_subscription`.
- **Signature adjustment:** `next_batch` takes a per-call
  `&Cancellation` in addition to the contract's sketched
  `(cursor, bound)` shape — the shared `Cancellation` severs on clone, so
  only a per-call reference observes a live cancel; this is how the
  cancellation-liveness red test is honestly satisfiable.
- **Derived-trait adjustments:** `TimeSeriesStreamRange` and
  `FamilyStreamSelector` keep their declared `Eq`/`PartialEq` via manual
  structural implementations (the declared derives are unsound over the
  wrapped family payloads' `f64` fields); semantics are documented at
  each impl.
- **Dead state:** `SubscriptionStatus::Opening` is named by the public
  vocabulary but never observed: the synchronous W4-STREAM open completes
  the negotiation inside `subscribe` (which returns `Active` directly).
  The private state enum carries the variant with a documented
  dead-code allowance; a transport-async open would occupy it.

## Limits

- **Checkpoint durability is engine-lifetime.** Subscription state lives
  in a crate-private registry keyed by engine identity; it survives
  consumer reconnect but not engine shutdown or process restart. The
  journal can rebuild any checkpoint, so restart durability is a storage
  seam away, not a redesign.
- **Notification-registry retention.** The registry entry of an engine
  dropped with open subscriptions persists until process end (bounded by
  abandoned subscriptions; removed on normal close).
- **Per-poll materialization is bounded in commits, not writes (S3).**
  `tenant_commits` returns whole commits, so a poll transiently clones
  full write sets — complete `Document` values — for one journal page at
  a time: at most `max_events + 1` commits of the tenant, plus the ≤
  `max_events` delivered events the batch retains. The paging loop bounds
  the *peak* to one page (pages are processed and dropped, not
  accumulated), and a single commit's write set is itself bounded by the
  engine's per-transaction document limits; but a poll over commits with
  large write sets transiently materializes their documents without a
  writes-level bound. A write-windowed journal read
  (`commits_after`-style, paged by writes) is proposed as W4-SCA-7.
- **No per-document storage sequence.** Document cursors position by
  document identity within the snapshot, so `StreamPosition` reports the
  snapshot's publication sequence, not a per-item sequence. The windowed
  read's id-only cursor also means a collection-scoped stream may
  re-scan non-matching documents between batches (bounded,
  at-least-once), and — per the cursor-resume disclosure above — can
  cycle indefinitely at small batch bounds when an identifier is reused
  across collections (W4-SCA-5).
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
- **Scan-budget chunking.** A filtered run longer than
  `MAX_SUBSCRIPTION_SCAN_EVENTS` (4096 examined writes) is delivered as
  successive partial batches, each reporting its own drops, instead of
  one batch; delivery never stalls, but one poll cannot cross the budget.
- **`StreamError` has no storage-failure variant** (frozen set): an
  unreachable or poisoned storage lock reports `EngineClosed`, and family
  or query rejections report `InvalidSource`, each documented at the
  mapping site.
- **Close/cancel conflation (S6).** `close_subscription` reports
  `Closed(StreamCompletion::Cancelled)` because the frozen
  `StreamCompletion` set has no dedicated close distinction, while
  PB-6005 names an explicit close reason distinct from cancellation.
  Recorded as tracked parent decision **W4-SD-1** (below); a later
  `StreamCompletion` widening or a close-reason parameter resolves it.

## Tracked parent decisions

- **W4-SD-1 (from S6):** distinguish an explicit subscription close from
  cancellation at the terminal status. Today both observations report
  `Closed(Cancelled)`. Options: widen `StreamCompletion` with a close
  distinction (a frozen-contract change), or add a close-reason input to
  `close_subscription`. No builder may pick silently; W4-STREAM records
  the conflation and its test (`subscription_close_reports_an_explicit_
  terminal_status`) asserts today's honest shape.

## Proposed collision actions

| ID | File | Proposed change | Why this packet cannot |
| --- | --- | --- | --- |
| W4-SCA-1 | `src/engine/mod.rs` | Engine-owned streaming subscription state: hold the subscription registry on the Engine (or expose a shutdown hook), making checkpoints restart-rebuildable and cleaning registry entries at engine drop. | `src/engine/**` is frozen to this packet. |
| W4-SCA-2 | `src/engine/mod.rs`, `src/storage/mod.rs` | A per-id snapshot read (`read_document_at` surfaced on the Engine) so exact-id selectors probe existence in one call instead of paging canonical order. | Same freeze. |
| W4-SCA-3 | `src/streaming/mod.rs` after parent adjudication | Add a storage-failure (or internal) variant to `StreamError` so poisoned locks and family/query rejections stop mapping onto `EngineClosed`/`InvalidSource`. | The variant set is the frozen W4-0 contract; widening it is a parent decision. |
| W4-SCA-4 | `src/query/mod.rs` or `src/engine/mod.rs` | Bind `QueryStore` execution to the Engine's live index catalog (the W4-CA-3 catalog) so query streams can report a used generation other than `None`. | `src/query/**` and `src/engine/**` are frozen. |
| W4-SCA-5 | `src/storage/root.rs`, `src/engine/mod.rs` | A collection-aware window cursor (resume at (collection, id) instead of id-only) so collection-scoped document streams stop re-scanning foreign collections and cannot cycle on a cross-collection reused identifier. | `src/storage/**` and `src/engine/**` are frozen. |
| W4-SCA-6 | `src/families/mod.rs` | Expose `RESERVED_COLLECTION` crate-wide (one `pub(crate) use`) so streaming and query stop mirroring the literal. | `src/families/**` is frozen. |
| W4-SCA-7 (from S3) | `src/storage/mod.rs`, `src/storage/ledger.rs`, `src/engine/mod.rs` | A write-windowed publication-history read: page `tenant_commits` by reconstructed writes (or return write stubs resolvable per write) so a subscription poll's transient materialization is bounded in events, not in commits × write-set size. | `src/storage/**` and `src/engine/**` are frozen. |
| — | `src/engine/mod.rs:604` (doc-only) | The integrator's W4-CA-2 doc link `[`StreamStore`](crate::streaming::StreamStore)` triggers a `redundant explicit link target` rustdoc warning; simplify to `[`StreamStore`]`. Pre-existing at the base commit; recorded, not fixed (read-only file). | Same freeze. |

## Repair history

- **a1** (commit `9791d49`): initial W4-STREAM implementation. Inspector
  GO (finding 1 strongly recommended); warden NO-GO on the same defect.
- **a2** (tip of `fleet/w4-stream` after `9791d49`; the parent's mission
  report records the exact hash, which a commit cannot contain of
  itself): parent adjudication of both gates. Fixes applied:
  - **S1 (blocking, HIGH):** the publication sink now takes every lock
    with `try_lock`, dropping and counting the notification on
    contention (engine-level counter for the core map, per-subscription
    counter for a consumer mid-poll, both lock-free). Red test
    `publication_sink_never_blocks_a_commit_behind_a_wedged_poll`:
    against the old blocking sink the concurrent commit measured
    ~5.0 s (wedged) and failed; with the fix it passes with the drop
    counted and delivery intact.
  - **S2 (medium):** the scan pages the journal within the 4096-event
    budget, and the recorded boundary covers exactly the drops a batch
    reports, so a filtered run longer than the fetch window no longer
    stalls delivery. Durable checkpoints still advance only at
    acknowledgement. Red test
    `subscription_reaches_events_beyond_a_long_filtered_run`: against
    the old single-window scan the accepted event was never delivered
    (0 events, assertion failed); with the fix it is delivered in one
    poll with the run reported as drops. The amended contract test
    `stream_filters_report_drops_without_advancing_the_checkpoint`
    asserts the reported (non-silent) advance, verbatim redelivery
    before the ack, and no re-scan after it.
  - **S3:** disclosed precisely under Limits (peak = one journal page of
    full write sets; per-commit write sets bounded by engine limits);
    proposed W4-SCA-7.
  - **S4:** the cursor-resume disclosure now includes the seam's cycling
    caveat (structural fix W4-SCA-5).
  - **S5:** additive module-public surface and the `Opening` dead state
    enumerated under "Deviations and additive surface".
  - **S6:** close/cancel conflation recorded as tracked parent decision
    W4-SD-1.
  - **S7:** per-commit collective ingress cost bounded by
    `MAX_SUBSCRIPTIONS_PER_ENGINE` (64), refused beyond, slot freed on
    close; test
    `engine_bound_subscriptions_are_bounded_and_refused`.

## Checks

Run at the a1 tip and re-run at the a2 tip (exit codes in the mission
report): `cargo fmt --all -- --check`, `cargo check --workspace
--locked`, `cargo test --workspace --locked --release` (a2: 226 passing
= 198 baseline + 24 contract tests + 4 focused extras), `cargo clippy
--workspace --all-targets --all-features --locked -- -D warnings`,
`cargo doc --workspace --no-deps --locked` (one pre-existing warning in
`src/engine/mod.rs`, see the collision table), `python3
scripts/validate_parity.py --check`.
