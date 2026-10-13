# Shape chunk sealer (FR-12)

`IShapeSealer` seals a collection's local apply order into immutable bodies in
`_tb_shape_chunk`. Its implementation and SQLite transaction operations live in
`Features/Shapes/ShapeSealer.cs` and
`Engine/Sqlite/SqliteStorageTransaction.Shapes.cs`. The storage format remains v7.

The caller supplies a `ShapeSealDefinition`: lineage-bound shape id and handle,
collection and generation, 16-byte resume scope, and an optional predicate over
materialized JSON.
The identity and predicate must remain fixed for that handle. The writer verifies
the collection generation before sealing, so a stale handle cannot seal a re-created
collection. An absent predicate includes every visible document. Registration and endpoint wiring belong to FR-10
and FR-20.

1. `SealUpToHeadAsync(store, shape, offset, cancellationToken)` returns the sealed
   feed sequence. It observes the committed head on entry and seals whole commits
   through that head. Each chunk uses a standalone write-lane transaction, avoiding
   capture suspension inside another request's group commit.
2. `ReadChunkAsync(store, handle, offset, cancellationToken)` returns only persisted
   bytes, or null when there is no sealed chunk. Dispose the returned
   `ShapeSealedChunk` after sending or retaining its bytes: it owns memory admission.
3. `RunDatabaseAsync(store, shapes, cancellationToken)` runs one database's
   background worker. The owner supplies current active shape requests and cancels
   the worker when the store or host closes. The worker seals on committed writes;
   live flights may call `SealUpToHeadAsync` directly to seal on wake. The worker
   retains each active request's sealed boundary so new commits do not replay the
   previous chunk chain.

A chunk covers `(offset, end_offset]` by `_tb_feed.seq`. Feed rows sharing a
`commit_offset` stay together, including joined writes. The normal limits are 4096
feed rows and 1 MiB of complete response body; a larger single commit travels alone
up to 8 MiB. Commits outside the selected collection advance the range without
adding a change. Each selected commit lists an id once, in ordinal order, using its
last touch in that commit. Values come from current visible documents in the same
transaction, and `values_at` records that transaction's committed feed head. Absent
documents, hidden uniqueness contenders and nonmatching documents produce delete ops. A deleted document re-created before sealing carries its current value.
Upserts preserve stored document bytes and use the Document API version token.

Bodies include the protocol-v1 resume envelope, frozen at seal time. A body is
inserted once, with `SuspendCaptureAsync`, and subsequent reads do not reconstruct
it. The derived tables remain excluded from replication, copies and backups.
Memory is reserved before CDC decoding, body materialization and result reads;
refusal, rollback and cancellation release transient reservations.

Retained CDC is validated with the sender's existing state/image decoder and row
shape checks before a new seal. Admission covers one captured transaction at a
time and is released before the next transaction.
A damaged log refuses that database; other database workers continue. Invalid or
missing feed boundaries raise `ShapeSealOffsetException` (`must_refetch`). A
materialized singleton body over 8 MiB raises `ShapeTooLargeException`
(`shape_too_large`), leaving that commit unsealed. Memory admission raises the
existing `QosMemoryException`. Endpoint status and cache headers belong to FR-20.

The frontier is the local committed head: every write already syncs its WAL. This
does not claim survival of host or volume loss. FR-25 must gate publication by both
the covered offset and `values_at` once the remote acknowledgement frontier is
available. A seal adds no durability barrier and serves no open tail.
