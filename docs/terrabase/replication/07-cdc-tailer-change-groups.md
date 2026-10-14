# 7. CDC tailer → change groups

- **Mode `after`.** Shipping, the feed and digests need no `before` image (section 2). Slice R3 checks that `after`
  records updates and deletes on 0.8.2. If it does not, the mode is `full`, which only costs bytes.
- **One tailer per open database**, driven by the lane's commit signal plus a 1 s timer:
  1. On a read connection, read CDC rows with `change_id > _tb_tail.change_id`, up to the last commit row
     (`change_type = 2`), in pages of at most 4096 rows. Decode them in the same query with
     `bin_record_json_object(table_columns_json_array(table_name), after)`. Our tables are never altered, so the
     `ALTER TABLE` caveat does not apply.
  2. Group the rows by `change_txn_id`. Within a transaction, pair each collection-row change with the `_tb_state`
     change that follows it.
  3. For each transaction, take the `_tb_state` rows whose after-image has `origin = self`. These form one **change
     group** `{origin, firstChangeId, lastChangeId, deltas[]}`. Each delta is `(collection, id, S, entries with
     changed = S, plain values from the paired doc image)`. A transaction with no local rows is not shipped.
  4. For every `_tb_state` row, local or remote, append `_tb_feed(origin, stamp, collection, id, deleted = 1 - live)`.
     Repairs (origin 0) write their own feed rows when they apply (section 14).
  5. On the write lane, in one transaction with capture turned off for that transaction (`PRAGMA … ('off')` and back),
     do three things:
     - write the feed rows and the bucket deltas;
     - advance `_tb_tail`;
     - delete CDC rows up to `min(_tb_tail, min over live peers of acked)`, always keeping the newest row.

     If toggling the pragma per transaction fails on 0.8.2, the tailer's own rows are captured too, recognized by
     table name and dropped at the next pass.
- **`turso_cdc` is the outbox.** Groups are rebuilt from it on demand for each peer (`acked < change_id`), so there is
  no second log. A peer cannot hold a commit for more than 24 h, even if its own writes or stable reports keep it
  active, or it acknowledges a little at a time. The existing 1 GiB backlog eviction may release it sooner. Retention
  still respects continuous backup's anchor and keeps whole transactions and the newest commit. A peer behind a cut
  refills from current states before resuming (section 10); the cut does not acknowledge the missing groups.
- **Acks over joined rows.** Joined remote writes and repairs are never shipped, so a peer never acknowledges them. Each
  5 s retention pass therefore raises a peer's `acked` to the last commit before the next local group past it (the read's
  quiet point), but only while every row past that ack is still here: after a cut the peer may lack deleted local
  groups and refills instead. Without this a replica that mostly receives keeps every joined row until the 1 GiB cut.
  The CDC source remembers each raise, and each read over all of its rows, as a quiet range of the file (merged where
  ranges touch, pruned once retention cuts past them), and `RetainsAsync` follows those ranges, so a live stream whose
  in-memory ack is below a raised ack is not refilled.
- **Copies and restores.** `_tb_peers.acked` names rows of one file's `turso_cdc`; a copy's ids restart at 1, so a copy
  (database copy, restore, backup snapshot) resets every peer's `acked` to 0. What the peers applied stays.
- **Atomicity.** One Turso transaction → one group → one apply transaction at the receiver. Readers never see part of
  an operation, nor part of a lane batch.

**Damaged change log rows.** The tailer refuses, never skips: a row of a change type Turso does not write (only 1, 0,
-1 and 2), a row whose shape breaks its type (Turso's commit rows carry no table and no after-image, its delete rows no
after-image, so a write whose type was damaged into 2 or -1 is caught), an insert or update without an after-image, a state
that is not base64url or not a `DocumentState`, or a live state without its document row fails the read with
`InvalidDataException`. Nothing past the peer's ack ships or is
acknowledged: that database is held, logged as an error, counted (`terrabase.replication.unreadable_reads`) and visible in
its `send_lag`, and read again from the same ack every 30 s; the other databases on the stream go on. Acks and first
contacts never fail on it: the `tail_lag` gauge they refresh keeps its last value for a held database. Once the damage is
gone the next read ships the held writes. If it stays, the peer's own writes can refresh `seen`, but cannot extend the
24 h age bound on the held CDC commits. That bound or the 65,536-row / 1 GiB eviction cuts the prefix and leads to
a refill. Anti-entropy also compares current states (60 s rounds; a bucket is repaired after two differing rounds). CDC rows
carry no checksum of their own: they live in the same file as the states they come from, so a checksum would only cover
the copy that ships. Torn writes are caught by the WAL's frame checksums, damage that breaks decoding is refused as above,
and replicas that diverge silently show in anti-entropy's bucket digests (section 10), which cannot tell which replica is
right. Revisit when Turso exposes page checksums.
