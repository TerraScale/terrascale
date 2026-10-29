# Bounded replica writer transactions

Batch 20 on gru recorded repair holds up to 4.2 s, stream-apply holds up to
2.3 s, and writer queues up to 10.4 s. SYSTEM previously packed up to 16,384
deltas into a transaction; tenant transactions allowed 1,024.

`ReplicationWriteBatch` caps a receiving transaction at 256 deltas/states or
250 ms of work, whichever comes first. The item cap reduces SYSTEM's previous
maximum by 64 times. The time cap leaves headroom below the 1 s hold target for
deferred admission, closure observation, commit synchronization and disposal.
These are code constants, with no operator settings or switches.

Before the fix, test-only commit `19966fb81` applied 4,096 SYSTEM states in one
commit on both paths. Both tests verified every value and the applied cut before
failing the requirement for at least sixteen commits. Maximum holds in that run
were 0.764661 s (apply) and 0.794319 s (repair); an earlier, more loaded run of
the same pages measured 1.999413 s and 1.778552 s respectively. At 256 items,
the latter measurements imply roughly 111–125 ms of proportional join work,
leaving room for the 250 ms time budget and transaction finalization. The
post-fix measurements must still be checked, rather than assuming linear scaling.

The elapsed clock starts after transaction acquisition, excluding writer queue
time. Apply checks between complete origin groups. An oversized group runs
alone, preserving its atomicity. A group, one state join, deferred admission,
closure observation, native BEGIN/COMMIT and scheduling can exceed the budget;
this is a cooperative work bound, not a hard deadline or a guarantee that every
production hold is below 1 s.

Every committed apply chunk still checks the expected applied cut, joins ordinary
states, admits deferred webhooks and closure jobs, observes closure cancellation,
records its applied cut, then commits durably. The expected cut advances only
after commit. A reconnect can resume from the last persisted cut. Webhook
admission remains after all ordinary joins of that transaction, so authority in
the same group/transaction is visible; earlier committed authority also remains
visible. Repair keeps the same ordering, checks the incarnation under each
writer, and uses origin 0 without advancing any stream's applied cut.
The shared `RepairPageAsync` also serves atomic snapshot replay: that caller
still processes every state of its page in the import's single transaction,
preserving rollback and the terminal applied/absence proof. Ordinary repair
supplies a bounded iterator to the same join/admission path.

`RebuildLedger.RepairRebuildAsync` had the same count-only chunking hazard and
uses the same bounds. Each chunk retains its under-writer rebuild/incarnation
checks and per-state salvage check. It neither certifies an incomplete rebuild
nor discards its side copies.

## Frontier investigation

`RecordFrontierAsync` performs point reads of the incarnation and origin's
applied cut, updates that one origin, and commits with `synchronous = FULL`.
It has no multi-state loop to split. `AcquireWriterAsync` starts the hold clock
only after acquiring the writer, and `ReleaseWriter` records it at release.
Therefore batch 20's three frontier holds (maximum 1.7 s) cannot be writer-queue
time. Its separately recorded waits of 5.5/5.8 s are queue time.

The saved hold/wait aggregates do not time the frontier's individual storage
calls. They cannot distinguish native sync/BEGIN latency from write-lane or
continuation scheduling delays during a hold. A sync attribution would be an
inference, not a measurement. The durable commit is required for a truthful
frontier acknowledgement; no speculative change weakens it here.

## Compatibility

The writer-bound change uses the PR label `storage-format-unchanged`.
`RebuildLedger.RepairRebuildAsync` changes when existing salvage transactions
commit; it retains the same stored rows, schema, encodings and incarnation checks.
No storage-format bump or migration is needed. This classification applies to
the replication writer bound; the folded recovery PR separately documents its
SQL, socket protocol and authentication behavior changes.
