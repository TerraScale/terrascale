# 9. Receive and merge path

1. The receiver checks the batch (a known database, origin = the stream's node, `last_change_id` advancing) and
   decodes groups. Decoding is a trust boundary with the `DeltaGroup` bounds: ≤ 1024 deltas and ≤ 2 MiB per group.
2. Groups apply in stream order on the write lane, in transactions of at most 1024 deltas, splitting only between whole
   groups. Every transaction uses `synchronous = FULL`; the ack waits for all their commits. For each delta:
   1. `Observe(S)`.
   2. Read the row, join, normalize and materialize.
   3. Write only if the state changed, with `stamp = S` and `origin = the delta's origin`.
   4. Run local DDL if the row is metadata.
   5. Set `applied[origin] = max(applied, last S)`.
3. After the commit, the receiver acks `(change_id, applied, stable)`.

- **Idempotent and commutative.** Applies are joins. A duplicate, a replay after reconnect or an anti-entropy page
  that overlaps a stream changes nothing. Cross-origin order does not matter. No dependency vectors are needed:
  removes carry the dots they remove, and clears carry their versions.
- **No echo loops.** Three things prevent them:
  - the shipper sends only CDC rows whose after-image has `origin = self`;
  - an applied row carries the remote origin;
  - repairs carry origin 0.

  A machine never re-sends what it received, and every origin sends to every replica directly. With 1 machine per
  region that is 1 stream per database per pair. `// anna-full`: in-region relays once regions run 3 to 12 machines.
- **Unique constraints** stay deterministic on materialized rows, unchanged in semantics (smallest id wins; losers go
  to `_tb_unique_conflicts` and stay out of the collection table). A merge never fails a constraint.
