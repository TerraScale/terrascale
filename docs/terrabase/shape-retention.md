# Sealed shape retention

`IShapeRetention` is the internal maintenance API for the shape endpoint and
sealer wiring. FR-10 owns registration; FR-20 owns scheduling, handle rotation
notifications and the public endpoint's error response.

- `GetFloorAsync(store, handle, cancellationToken)` returns the minimum allowed
  offset: an offset equal to the floor is retained; an offset below it must
  refetch. `CheckOffsetAsync` returns the existing `Conflict` value with code
  `must_refetch`, which maps to HTTP 409 with `Cache-Control: no-store`.
- `RunPassAsync` commits one batch, at most 1024 rows and 8 MiB of sealed bodies
  across both tables. It uses the injected `TimeProvider` and the existing
  `FeedCursor.Retention` (7 days). Call again on a later maintenance turn when
  work remains; a partially completed pass is safe. A positive deletion count
  schedules another pass: a byte-limited batch can stop below 1024 rows.
- Chunks older than 7 days or starting below the floor are deleted. Superseded
  snapshots are deleted only when both older than 7 days and below the floor.
  Every page at the newest snapshot offset is kept, including an old snapshot.
  If that snapshot is below the floor, bootstrap wiring must seal a fresh one
  before directing clients to it.
- `DropHandleAsync` removes a rotated handle's chunks and snapshots in the same
  bounded batches, then removes its floor metadata. Invoke it repeatedly until
  the old handle has no rows. Rotation never raises another handle's floor.

The engine adapter runs through a write transaction with capture suspended.
Retention changes neither `_tb_feed` nor its hourly garbage collection. Floor
reads use indexed retained-prefix and head lookups and the persisted per-handle
floor. A maintenance pass also scans the sequence index for interior holes left
by per-origin feed trimming and persists the highest hole before deleting rows.
An empty feed uses the persisted sequence counter as its floor. Sealers must
still refuse a range containing missing feed rows: a floor is not a density
proof for feed GC that ran after the shape maintenance pass.

Before deleting a chunk, the transaction persists the greatest deleted end
offset for that handle. This bookkeeping uses a reserved `_tb_shape_chunk` key:
`handle = "~floor:" + <canonical handle>`, `offset = 0`, `end_offset = floor`,
empty `body`, and `sealed_at = 0`. The reserved key cannot be a v1 handle. It is
never a sealed object; readers and mirrors select the canonical handle. Count
or enumerate real objects with canonical handles, excluding reserved keys.
Copies and backup snapshots already omit every row of both shape tables, so
the floor metadata is omitted with the sealed objects.

There is no schema change or migration. Existing sealed-object keys, encodings
and bodies retain their meaning. The new reserved key is local derived
bookkeeping for a newly introduced feature. Floor updates and chunk deletion
commit atomically, including when deletion empties a handle's chunks.

A stored body exceeding the protocol's 8 MiB object limit fails maintenance
with `InvalidDataException`. It never reports a successful empty pass while
leaving a damaged oversized object, or exceeds the batch's byte bound to remove
it. Recovery for damaged data remains an engine responsibility.

Floor checks and object reads must share a storage view, or recheck a miss after
concurrent maintenance. A separate successful `CheckOffsetAsync` call is not a
lease on a sealed object. Offset grammar, commit-boundary checks, stale-handle
checks, the current handle response field and `TB-Shape-Handle` stay at the
endpoint boundary.
