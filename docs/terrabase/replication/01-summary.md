# 1. Summary

Every database is one Turso file on every machine of every region in its placement. Any machine accepts any write and
answers after its own local commit. A document is a map of fields. Each field merges on its own: the newest
`(HLC stamp, node)` wins. The document has a clear register for PUT and DELETE. Fields can also be PN-counters or
add-wins sets. All of this state is a join-semilattice, so a replica can apply deltas in any order, any number of
times, and every replica ends in the same state.

```text
 client ─HTTP─> machine (gru)                                           machine (sjc)
                 │ write lane (the only writer connection, Turso WAL + CDC)
                 │   op -> stamp S -> merge into DocumentState -> collection row + _tb_state row
                 │   commit -> 200
                 ▼
               turso_cdc ──tailer──> change groups (one per Turso txn, local-origin rows only)
                 │                     │                          ┌─> receiver: join, materialize, ack
                 │                     └──gRPC Push over NetBird──┘   (origin = gru, never re-shipped)
                 ├──tailer──> _tb_feed (per-origin index) ──> GET /databases/{id}/changes (SSE)
                 └──_tb_merkle (4096 buckets) <── anti-entropy Digest/ReadBucket ──> peers
```

- **Writes** are leaderless. There is no `If-Match`, no compare-and-set, no primary and no forwarding.
- **Replication** is a direct per-origin stream from each machine to every other replica of the database, in every
  region. A machine never forwards another machine's changes, so changes never echo back.
- **Anti-entropy** compares Merkle buckets and repairs whatever the streams missed. It also seeds a new or refilled
  replica.
- **The catalog** (`_system`) is a lattice database replicated to every machine. The catalog primary goes.
- **SQL** can write. A statement is parsed, its `WHERE` runs against local state, and the result becomes field
  deltas, so SQL writes merge like API writes.
- **The changes feed** comes from CDC. Its cursor is a vector of per-origin stamps, so it is valid on any machine.
