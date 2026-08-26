# SE-03 change-event backbone design and evidence record

Status: implementation record for the SE-03 packet (issue #5), 2026-08-26.
Authority: `specs/GOAL.md` PD-2026-08-26-13 (D3 above all),
`specs/2026-08-26-terrabase-v2-sync-engine-event-system-tickets.md`
(SE-03), and the accepted W4-STREAM record
(`docs/streaming/w4-stream.md`). Worktree `terrabasev2-se03`, branch
`fleet/se03`, based on `develop` @ `431841a`. Salvage:
`fleet/wip-agent-partial-2026-08-26` @ `dc43f29` contributed the
backbone, replay, and conflict-ingress work in `src/streaming/` and
`tests/streaming/`; this packet re-verified every salvaged line, proved
the tenant-isolation guards red-then-green, and added the public
trigger-notification surface. Nothing here was reviewed before this
packet owned it.

This record describes what was built, the V1 Changes API selector
mapping, the replay contract, the trigger-notification surface, and the
explicit non-claims, limits, and proposed parity dispositions. It
resolves no parity row and mutates no overlay: every proposal below is
WS-PARITY's to disposition.

## What was built

`src/streaming/` gains the change-event backbone over the durable
publication-history journal (W4-CA-4), extending the accepted W4-STREAM
subscription seam without changing its delivery discipline:

- `backbone.rs` — the public replay surface
  (`ChangeCheckpoint`, `ChangeReplayRequest`, `ChangeReplayBatch`,
  `StreamStore::replay_changes`), the bounded conflict-observation
  ingress (`notify_conflict`, `fan_out_conflict`), and the public
  trigger-notification surface (`ChangeNotification`,
  `ChangeNotificationTap`, `MAX_CHANGE_NOTIFICATION_CAPACITY`,
  `StreamStore::change_notifications`).
- `mod.rs` — the `ChangeFilter` extensions to the V1 Changes API
  selector surface (`with_document`, `with_prefix`, `for_operations`,
  the `ChangeKind::Conflict` kind, `accepts_event`), the
  `ChangeEvent` subject generalization (key or content-free
  `ConflictRecord`), `SubscriptionBatch::conflicts`, and
  `SubscriptionCheckpoint::journal_position`.
- `subscription.rs` — the shared cursor/coverage rule
  (`position_covered`), the shared commit expansion
  (`expand_commit_for`) now parameterized by the validated scope, the
  bounded conflict queues per subscription, and the shared scope
  validation (`validated_identity`) both the live path and replay bind
  to.
- `tests.rs` — the SE-03 in-crate tests (selectors, replay parity,
  conflict restrictions and isolation, trigger notifications); the
  public-construction extras live in `tests/streaming/
  streaming_change_backbone.rs`.

No file outside `src/streaming/` and `tests/streaming/` changed; the
engine publication registry is consumed exactly as W4-CA-3 published it
(`Engine::subscribe_publications`), so `src/engine/mod.rs` is untouched.

## V1 Changes API selector mapping (PB-6002, PB-6006)

`ChangeFilter` is extended — not replaced — so every W4 filter remains
valid. The mapping, recorded on the type and verified by
`change_selector_extends_the_filter_to_the_v1_parity_surface`:

| V1 Changes API selector | V2 form | Recorded limit |
| --- | --- | --- |
| `forDocument(id)` | `ChangeFilter::with_document` | exact identity match |
| `forDocumentsStartingWith(prefix)` | `ChangeFilter::with_prefix` | validated selector text (1..=255 bytes, no control characters) |
| `forAllDocuments` | kinds `Some([Document])`, no refinement | — |
| collection-scoped document watch | the `collections` include list | — |
| `forIndex` / `forAllIndexes` | kinds `Some([Index])` | no journal path reports `Index` yet — the kind is selectable, and the index-change producer lands with the IX wave (proposal below) |
| counter watch | kinds `Some([Counter])` | reported for `terrabase.families` counter writes |
| time-series watch | kinds `Some([TimeSeries])` | reported for family time-series writes |
| `forOperationId(id)` | `ChangeFilter::for_operations` | matches the event's operation identity; for a conflict, any conflicting operation of the record |
| conflict watch (V1 `DocumentChangeTypes.Conflict`) | kinds `Some([Conflict])` | conflict events carry no collection or document identity, so a collection- or document-refined filter does not observe them |
| V1 criteria scripts | typed filters only | unchanged W4 decision D-5: no untrusted script executes in this seam; the script surface stays an unresolved PB-6006 question |

## Replay contract (PB-6002, PB-6004)

`StreamStore::replay_changes(scope, request, cancellation)` is
journal-driven and stateless: it pages
`Engine::tenant_commits(scope.tenant(), ...)` — the same durable
publication-history journal the live subscriptions deliver from — in
storage-sequence order, expands every reconstructable write with the
same `expand_commit_for` the live path uses, and stops at the request's
bound, the shared scan budget (`MAX_SUBSCRIPTION_SCAN_EVENTS`), or the
journal's end. Consequences, each proved by a focused test:

- **Any consumer reproduces the live sequence.**
  `replay_from_beginning_reproduces_the_live_subscription_sequence_exactly`
  drains a live `from_beginning` subscription and a `None`-anchored
  replay over the same interleaved single- and multi-write publications
  and asserts the sequences are equal, event for event.
- **An arbitrary checkpoint yields the exact suffix.**
  `replay_from_an_arbitrary_checkpoint_reproduces_the_exact_suffix`
  replays from a live subscription's acknowledged boundary
  (`SubscriptionCheckpoint::journal_position`) and from a hand-forged
  checkpoint splitting one multi-write commit; both serve exactly the
  remaining writes. The shared `position_covered` rule fixed a
  salvage-verified off-by-one in the W4 coverage check (a boundary that
  split a commit skipped the write at its own index; the rule is now
  "a cursor consuming `write_index` writes covers the indices strictly
  below it", shared by both paths).
- **Bounded, drop-reporting batches.**
  `replay_is_bounded_and_reports_filtered_drops` asserts the request
  bound, the counted filtered drops, and the typed bound validation.
- **Tenant isolation.**
  `replay_never_crosses_a_tenant_scope` interleaves two tenants'
  publications on one engine and asserts each scope serves only its own
  journal, including under a checkpoint forged from the other tenant's
  position — the journal read is keyed by the scope's validated tenant,
  never by the checkpoint's origin — and refuses a scope naming another
  engine before any read.

Delivery stays at-least-once over repeated calls with the same
checkpoint, and conflict observations are live notifications, never
journal events: they are not replayed. That is the backbone's recorded
accepted-difference from replaying conflicts (proposal below).

## Conflict events (PB-5022 observability, PB-6002 Conflict shape)

A conflict is not a commit: the journal has no conflict record, so
conflict events enter through the crate-internal
`streaming::backbone::notify_conflict` ingress and ride subscription
batches as advisory `SubscriptionBatch::conflicts` entries — bounded
queues (`MAX_CONFLICT_NOTIFICATIONS`), `try_lock` only, counted drops,
never a wait, drained into batches under the spec's filter where a
filtered conflict is a reported drop. The content restriction is
structural: a conflict `ChangeEvent` carries exactly one subject, the
replication seam's `ConflictRecord` (identities, members, clocks, the
recorded outcome), and there is no parameter through which a document
body could cross — `conflict_events_carry_only_conflict_record_metadata`
proves the carried payload and the absent key dimensions, and
`conflict_notifications_reach_only_the_owning_tenant` proves the
tenant guard red-then-green (the guard was disabled, the test failed,
the guard was restored, the test passed; same procedure for the replay
tenant keying).

## Trigger notifications (PD-2026-08-26-13 D3)

`StreamStore::change_notifications(capacity)` generalizes the W4
in-process notification hints into the public engine seam. The surface,
proved by the four `trigger_notification_*` tests and the public
integration test:

- **Content-free.** A `ChangeNotification` carries exactly two
  identifiers — the storage sequence and the operation identity of one
  published commit. There is no field for document data; the
  content-free test asserts the hint's entire observable payload names
  neither the committed document's identity nor its body.
- **Bounded, drop-counted.** The tap's queue is bounded by its
  requested capacity (`1..=MAX_CHANGE_NOTIFICATION_CAPACITY`); a
  publication beyond it drops the oldest hint and counts the drop,
  reported through `dropped()` alongside lock-contention drops.
- **Never blocks a commit.** The ingress runs synchronously on the
  committing thread under `try_lock` only: the non-blocking test holds
  the queue lock across a concurrent commit and observes the commit
  return promptly with the drop counted.
- **Engine-scoped by design.** The publication event carries no tenant
  (`CommitPublicationEvent` is operation, root, sequence), so hints
  carry no tenant dimension. A tap is therefore a trusted in-engine
  consumer surface — the TBEP subscription layer (SE-14/SE-08) and the
  studio live-view host own per-tenant fan-out and authorization; a tap
  itself is not a tenant-facing authorization boundary. Per-tenant hint
  fan-out at the engine level would need the tenant on the publication
  event — outside this packet's scope (see collision actions).

## Parity-row PROPOSALS (no overlay mutated; WS-PARITY dispositions)

- **PB-6002 (changes-api)** — propose: the typed selector and event
  surface above is implemented evidence for the V1 Changes API
  observables (document, index, counter, time-series, operation,
  database, conflict kinds; per-document, prefix, collection, operation,
  all-documents refinements; journal replay from any checkpoint).
  Remaining for the row: the adapter/protocol carriage (SE-08/SE-14)
  and the index-change producer.
- **PB-6006 (subscription-criteria-scripts-and-revisions)** — propose:
  typed filter parity is implemented (include lists, refinements,
  reported drops); V1 criteria scripts and revision subscriptions
  remain unresolved dimensions of the row, not covered by this packet.
- **PB-6004 (subscription-checkpoints-acknowledgements-and-resend)** —
  propose: `SubscriptionCheckpoint::journal_position` plus
  `ChangeReplayRequest` is implemented evidence that any acknowledged
  boundary is a resumable, replay-verified position; the W4
  checkpoint/acknowledge/resend evidence stands unchanged.
- **PB-6005 (subscription-reconnect-failover-and-backpressure)** —
  propose: the bounded conflict ingress and trigger-notification tap
  extend the counted-drop backpressure record; no new reconnect
  behavior is claimed.
- **PB-5022 (conflict observability)** — propose: conflict events
  carrying only `ConflictRecord` content-free metadata, with the
  tenant-guard proof, are supporting evidence for the row's
  observability semantics; the replication seam's own evidence governs.
- **PB-6007/PB-6008/PB-6009** — unchanged W4-STREAM coverage; the
  backbone adds no new document/query/time-series stream source.

## Non-claims and limits

- No performance, bandwidth, or trigger-notification-size claim is made
  anywhere in this packet; PD-2026-08-26-03 and PD-2026-08-26-13 D1 put
  those behind SE-07/SE-15 benchmark receipts.
- Conflicts are not journaled: conflict observations are advisory live
  notifications only, lost as notifications when dropped, and never
  replayed. The replication seam's durable conflict history is the
  record of truth.
- The conflict ingress is crate-internal and not yet wired to the
  replication seam (out of this packet's write scope); it is exercised
  in-crate and its wiring is a proposed collision action.
- Replay reads the journal under the caller's scope; a checkpoint from
  another engine or tenant is data the caller supplied, never access —
  the journal read itself is keyed by the scope's validated tenant.
- The trigger-notification tap's hints are engine-scoped; per-tenant
  fan-out and authorization live at the adapter seams.

## Proposed collision actions

1. **Replication wiring of the conflict ingress** (owner: replication
   seam / integrator): call
   `streaming::backbone::notify_conflict(scope, sequence, record)` from
   the replication conflict-recording path so real conflicts reach the
   backbone. The ingress is crate-internal and verified; only the call
   site is missing, and `src/replication/**` is outside this packet.
2. **Optional root re-exports** (owner: integrator, `src/lib.rs`): the
   SE-03 types are reachable at `terrabase_v2::streaming::…`
   (`ChangeCheckpoint`, `ChangeNotification`, `ChangeNotificationTap`,
   `ChangeReplayBatch`, `ChangeReplayRequest`,
   `MAX_CHANGE_NOTIFICATION_CAPACITY`); add them to the root
   `pub use streaming::{…}` list only if the root surface should mirror
   the W4 set.
3. **Tenant-bearing publication events** (owner: engine/storage):
   adding the tenant to `CommitPublicationEvent`
   (`src/storage/mod.rs`, the `Engine::commit` emit site) would let the
   trigger-notification ingress fan out per tenant inside the engine;
   this packet deliberately did not touch those paths.
