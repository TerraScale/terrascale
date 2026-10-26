# Row-derived repair digests

Anti-entropy recomputes each bucket's XOR from the current `_tb_state` rows on
every digest comparison. A state row deleted outside the engine therefore
differs from a healthy peer even when `_tb_merkle` still contains its old hash.
The existing two-round comparison and bucket repair restore the missing state.

When the row-derived and maintained digests differ, a writer transaction checks
again and replaces `_tb_merkle` from the current rows without capturing a client
change. Subsequent engine writes and repairs continue maintaining that summary.
The same transaction recounts client collection document and byte totals from
their remaining bodies, so restoring a lost body counts it once.
The comparison scans all state hash rows, with a fixed 4096-bucket buffer; it
does not decode all document bodies or retain all keys in memory.

This detects missing state rows and differences in their stored bucket hashes.
It is not a complete physical integrity scan: malformed state or body encodings
remain subject to the existing validation, copy-fault and peer rebuild paths.

No schema, storage version or peer message changes. Mixed deployments retain the
same digest layout and repair protocol. A node running the new code detects its
own missing state rows; older nodes need the new code to gain that detection.
Reconciliation also keeps the maintained summaries usable after a rollback.

Coverage: `KeyAwareDigestTests` exercises missing rows through peer repair;
`PipelinedBatchTests` covers readable and unreadable chunks between coalesced
inline batches. The E11 E2E scenario uses `/sql/execute` with a relational table
and an `INTEGER NOT NULL` counter, and the client/catalog power-loss scenarios
declare a valid replica placement before testing recovery.
