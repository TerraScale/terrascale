# Shape storage foundation

Client databases persist a local apply order in `_tb_feed.seq`. The singleton
`_tb_feed_sequence` row starts at zero; each feed append increments it and stores
the resulting sequence in the same write transaction as the document state.
Each feed row also stores the transaction's existing `commit_offset`, so a sealer
can identify commit boundaries after a restart, including commits that joined
multiple origins. The database's single writer keeps one commit's rows contiguous.
A rollback also rolls back the counter. Retention never deletes the counter, so an empty feed
followed by a reopen continues above the last committed sequence. Exhaustion of
the signed 64-bit sequence refuses the write rather than reusing an offset.

`seq` is a unique column independent of `rowid`; neither SQLite rowids nor
`AUTOINCREMENT` define offsets. Turso 0.8.2 can restart an emptied table's
autoincrement sequence after a reopen (see [the spike](turso-spike.md)). The R8
vector cursor remains based on origin and stamp; it does not change with this
local sequence.

The derived shape tables exist only in client databases:

| Table | Key | Stored data |
| --- | --- | --- |
| `_tb_shape_chunk` | `(handle, offset)` | Exclusive start offset, inclusive `end_offset`, sealed `body` bytes, `sealed_at` Unix milliseconds |
| `_tb_shape_snapshot` | `(handle, offset, page)` | Snapshot offset, zero-based page, nullable `next_id` keyset continuation, sealed `body` bytes, `sealed_at` Unix milliseconds |

Shape writers must call `SuspendCaptureAsync` before writing these tables and
finish that transaction before writing captured application state. Capture-off
writes stay out of CDC and peer change groups; the sealer lane must preserve this
contract. Database copies and backup snapshots keep
the empty table definitions but omit their rows: the new lineage rebuilds shapes
under new handles. Feed rows and the sequence counter are copied and backed up
together. The counter updates remain captured for point-in-time replay.

This is a greenfield storage-format change: `StorageFormat.Version` is bumped
and the schema digest changes. Existing data directories of another format are
refused; no migration is provided. This foundation adds storage only; sealing,
retention of derived bytes, and the HTTP shape protocol are subsequent lanes.
