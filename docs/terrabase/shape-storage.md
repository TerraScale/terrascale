# Shape storage

Shapes read the database file's own change capture. Every write connection captures in `full` mode, so each
committed transaction appends its row changes (before and after images) and one commit record to the native capture table in
the same transaction. `change_id` is `AUTOINCREMENT`, so ids are never reused while the table keeps its newest
commit record; the native capture table retention (PIVOT F5) must keep it.

Derived shape tables exist only in client databases:

| Table | Key | Stored data |
| --- | --- | --- |
| `_tb_shape_chunk` | `(handle, offset)` | Exclusive start offset, inclusive `end_offset`, sealed `body`, `sealed_at` Unix milliseconds; also `~floor:`, `~lineage:` and `~table:` sentinels |
| `_tb_shape_snapshot` | `(handle, offset, page)` | Snapshot offset, zero-based page, `next_id` (null on the last page), sealed `body`, `sealed_at` |

Shape writers call `SuspendCaptureAsync` before writing these tables, so shape writes never reach the native capture table.
Database copies, backup snapshots and rewrites keep the table definitions but omit their rows; the new incarnation
rebuilds shapes under new handles.

A `~table:<name>` sentinel (offset 0) records a table's shape generation: `body` is the UTF-8 text
`<generation ULID>\n<sqlite_schema SQL>`, `end_offset` the head it was issued at, and `sealed_at` the database's
`PRAGMA schema_version` when it was last checked. Generations add no table, so the storage format is unchanged.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
