# 6. Write path

1. The endpoint validates as today (JSON, size, collection schema, unique constraints on the local state). The
   request enters the database's write lane (`SqliteWriteLane` group commit). Unconditional `POST /transactions` stays
   atomic.
2. In the lane's transaction (`synchronous = FULL`, [0003](../decisions/0003-sqlite-synchronous-normal.md) as amended by
   #181), for each
   operation:
   1. Mint `S`.
   2. Point-read `_tb_state` and the collection row.
   3. Decode, apply the operation (section 4), normalize and materialize.
   4. Write the collection row (INSERT, UPDATE or DELETE), then the `_tb_state` row with `stamp = S`,
      `origin = self` and the new digest. Both rows are always written, even when the doc bytes did not change, and
      always in this order: the tailer pairs them.
   5. Fold `old ⊕ new` digests into the lane's in-memory bucket deltas.
3. Commit, then answer. The body carries the materialized document and `version` (from `S`). ETags stay
   `tbv1-<hash of state>` for caching only. **No write-wait:** acknowledgement means one machine committed (plan:
   "ack after one node"). `IReplicationWait` goes.
4. After the commit, the lane signals the tailer and invalidates the document cache by key. This replaces
   `StampingStorageEngine`.

`If-Match`, `If-None-Match`, `assert` and `assert_count` are deleted from every route (400 `unsupported_precondition`
until the SDKs drop them). Create-only `If-None-Match: *` goes too. Client ULIDs make creates collision-free.
