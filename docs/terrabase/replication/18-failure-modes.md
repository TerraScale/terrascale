# 18. Failure modes

| Failure | Effect | Handling |
| --- | --- | --- |
| Process crash mid-batch | uncommitted operations lost, not acknowledged | committed ones are in CDC and ship after restart |
| OS crash or power loss, tenant database (`NORMAL`, [0003](../decisions/0003-sqlite-synchronous-normal.md)) | the commits acknowledged since the database's WAL last synced (at the latest its next checkpoint) may be lost: documents, collections, indexes and schemas written there | ones already shipped come back through anti-entropy; unshipped ones are lost. The epoch rises, so no version is reused |
| OS crash or power loss, `_system` (`FULL`, [0012](../decisions/0012-system-database-synchronous-full.md)) | none: every acknowledged catalog write, API key and revocation was synced before its answer | `SystemDatabasePowerLossTests` |
| Volume lost | the machine's databases are gone | new member number, refill from peers. Unshipped writes (< ~100 ms) are lost: "ack after one node" |
| Region offline (partition or `machine stop`) | both sides keep accepting writes | streams resume from `acked`. Anti-entropy covers gaps. More than 24 h: evicted, refill on return |
| Duplicate, reordered or replayed groups | none | joins are idempotent and commutative |
| Concurrent writes, same field | one value everywhere | the larger `(stamp, node)` wins |
| Concurrent writes, different fields | both survive | per-field entries |
| Delete vs concurrent edit | the later version wins at document level | `clear` dominates older fields; newer fields bring the document back with only themselves |
| Clock ahead > 1 s | that writer wins concurrent LWW choices | the stamp is kept, the clock advances at most 1 s, an alert fires |
| A peer far behind | CDC grows | peer pinning ends at commit age 24 h or backlog 1 GiB, then `refill_required`; backup's anchor still applies |
| Tailer behind | replication lag, the feed lags | the lane signal and a 1 s timer; lag is a metric (`terrabase.replication.tail_lag`) |
| Corrupt or forged group | rejected | canonical decoding at the trust boundary; the stream errors and resumes from `acked` |
| Same name created in two regions | two databases briefly | the smallest ULID wins; the other operation fails `name_taken` |
| Turso aborts (single-writer bug, early-preview CDC) | the machine restarts | an unclean start bumps the epoch and rebuilds the Merkle tree; peers keep serving |
| A database deleted while a region was away | that region still has the file | the catalog delete replicates. The machine closes and deletes files whose database is deleted or not placed in its region |
