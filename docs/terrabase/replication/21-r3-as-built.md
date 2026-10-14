# 21. Slice plan: R3 as built

**R3 as built** (2026-10-12, on T1's Turso engine; no storage format change):

- `TursoChangeGroupSource` (`Engine/Sqlite`, registered with the engine) replaces `NoChangeGroupSource`, which is deleted.
  There is no tailer pass and no `_tb_tail`: `turso_cdc` is the outbox, and `ReadAsync` rebuilds groups from it on a
  pooled read connection (never the writer), in pages of 4096 rows, up to the last COMMIT row. After-images are decoded
  from SQLite's record format in C# (T1's `SqliteRecord`), so no `bin_record_json_object` and no `CASE` guard are needed.
  A file this machine never wrote has no `turso_cdc` and yields nothing.
- **Groups.** One group per Turso transaction holding at least one `_tb_state` row of this machine (same region and
  member, any epoch: a write committed before an unclean restart is still unsent), `change_id` = the transaction's COMMIT
  row. Each local state row pairs with the last insert or update of that document's collection row before it in the
  transaction; a state with `live = 0` needs no image. The delta is `DecodeStored(state, doc).DeltaAt(stamp)`. Joined
  rows (another node's origin) and repairs (origin 0) are skipped, so nothing echoes. Rows of other tables are ignored.
- **Bounds at write time**, so a group stays one atomic transaction (instead of splitting at read time): a client write
  transaction holds at most 1024 local state rows and 2 MiB of collections, ids, stored states and documents
  (`IStorageTransaction.MaxChangeGroupChanges`/`MaxChangeGroupBytes`). Past that, a transaction alone fails with
  `ChangeGroupCapacityException` (the existing 503 `capacity` answer); a group commit write that would push a
  shared transaction past it moves to the next batch (`LimitsReached`). One document of up to 1 MiB always fits.
- **Cursors.** `_tb_peers.acked` names a position in `_tb_peers.incarnation`, written with capture suspended (the
  tailer's bookkeeping). A peer's first `AckedAsync` returns the file incarnation and gives it a row at 0, so it holds
  retention from first contact. Reads, heads and retention checks require that incarnation. ACK persistence checks the
  expected incarnation under the writer and refuses another file's ACK, including one whose numeric position fits the
  current head. Copies and restores reset stored ACKs; mismatched peer rows return 0 and require refill before their
  persisted scope advances. The internal v1 wire and
  digest/bucket reads pin the source incarnation as described in [the incarnation contract](../replication-incarnations.md).
- **Retention** (`SqliteStorageTransaction.Retention`, run after every ack and by the backup tap's trim): delete below the
  newest COMMIT row that is either at or below every active peer's `acked` or more than 24 h old, always at or below the
  backup tap's anchor, and, once the database has a `_tb_peers` row, at least 60 s old by the host clock (Turso's `change_time`
  is Unix seconds). The hold covers a peer whose stream has not reached the database yet; a database without peers keeps
  nothing back. A peer that never reached a new database before its first trim relies on refill (R6). Deleting below a
  COMMIT row keeps whole transactions and the newest row.
- **Clocks.** Stamps (`MemberClock`) and retention read the host clock (`SqliteStorageEngine.HostTime`), not the query
  deadline's clock, which tests may freeze: until R3, the e2e harness's frozen deadline clock also froze every stamp, so its
  clock-skew scenarios never skewed anything.
- **Wake-up.** `ChangedAsync` completes at the engine's next committed write. A per-file cache of change id ranges
  without local groups keeps a replica that mostly joins remote writes from rescanning them on every pass.
- **PeerStreams** now reconnects after any stream failure, not only transport errors: a storage exception from the source
  (for example `StorageBusyException`) used to end that peer's loop until the process restarted.
- Metrics: `terrabase.replication.cdc_backlog` (CDC rows left after each database's last ack, summed) and
  `terrabase.replication.tail_lag`: the age of the oldest local commit that a peer heard from in the last 24 h has not
  acknowledged, max over databases. It is looked at after acks (at most once a second per database) and every 5 s for
  each database a peer stream reached, so it keeps growing while the only peer is away. `send_lag` and `ack_lag` are
  separate per-peer-region series of the connected streams: they go when a peer's stream goes, which `tail_lag` does not. There is no tailer pass: every
  commit is readable as a group at once.
- **Peer leaves the machine list.** `PeerStreams` drops a link whose machine is no longer listed at its next membership
  refresh (5 s), ending the open stream (`PeerStreamTests`); the peer resumes from its ack once it is listed again.
- **Replicated DDL** (the receive side R4a left out). A joined `_indexes` document builds its index where it is missing
  (on a collection made without local metadata when absent) or drops it when deleted; a definition this build cannot
  read changes nothing rather than stalling the stream. It also replaces an index of that name with other fields or on
  another collection: a redefinition that reaches a replica only as its final state (refill, repair), or a concurrent
  creation elsewhere that won the document's join. Every replica then builds the winning definition. A joined `_unique` document re-projects its whole collection:
  every live document and retained loser gets its keys again, then the smallest id wins, as everywhere. Before this, a
  replica that took documents in before their constraints kept serving the larger id. Schemas already replicate inside
  the `_collections` document.
- Not in R3: `_tb_feed` (R8), Merkle folding (R6), the 1 GiB retention cut and per-database readiness (R6).
