# 2. Turso 0.8.2 constraints this design obeys

From the spike (PR #12 and its raw output). Each one is a hard rule for every slice.

| Fact (0.8.2) | Rule here |
| --- | --- |
| Two writer connections on one file abort the process (`wal.rs:3590`) | One writer connection per file: the write lane. Local writes, replicated applies, the tailer's checkpoint and CDC deletes, GC and DDL all run on it. Readers use the read pool. |
| `WITHOUT ROWID` cannot UPDATE, DELETE or be indexed, and CDC ignores it | Rowid tables only. Keys are `UNIQUE` columns. |
| CDC `id` holds the rowid, not the key | Consumers read keys from the after-image. |
| `bin_record_json_object` cannot format BLOB values | Captured tables have no BLOB columns. Encoded state is base64url TEXT. |
| For `ON CONFLICT DO UPDATE`, CDC `before` holds the new row. `INSERT OR REPLACE` is captured as a delete plus an insert | Captured tables use plain `INSERT`, `UPDATE` and `DELETE`. Merges happen in C#. Nothing here needs `before` images. |
| CDC is per connection. Rows land in the same transaction and are visible to that connection before commit. Rolled-back work leaves no rows. `change_txn_id` groups a transaction and type 2 marks its commit | Groups follow Turso transactions. |
| An empty `turso_cdc` restarts `change_id` at 1 after reopen | Truncation always keeps the newest row. |
| `capture_data_changes_conn` takes the write lock and ignores `busy_timeout` | Set it once when the lane opens, and retry on "database is locked". |
| Full mode writes each updated row three times | Mode `after` (section 7). |
| No statement cancellation and no progress handler | SQL keeps a bounded grammar (section 12). |
| The planner prefers an overlapping partial index over an expression index | Per-collection tables remove the overlapping `documents_live` partial index. Slice R2b measures this. |
| MVCC + CDC works on a database already in `mvcc` mode, but the poller can skip interleaved groups and CDC is a conflict hotspot | Stay on WAL. `BEGIN CONCURRENT` is section 22. |

The spike's "CDC costs about 2 %" figure is invalid: those runs never enabled CDC.
The design keeps captured rows per operation to two (the collection row and the
`_tb_state` row). Measure CDC overhead on the qualified runtime before using its
throughput for later slices; retired platform measurements are not OCI evidence.
