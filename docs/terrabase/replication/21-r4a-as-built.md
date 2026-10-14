# 21. Slice plan: R4a as built

**R4a as built** (2026-10-12):

- `PeerStreams` (sender) follows the membership every heartbeat and keeps one `Push` stream per other machine. For every
  database whose placement includes the peer's region, it keeps at most one batch (≤ 1024 groups, ≤ 2 MiB) in flight.
  It heartbeats every second when idle and requires a response within 15 s, even while a write is blocked. A batch
  not acked within 15 s also ends the stream; a refill has 10 minutes while heartbeat replies prove the link is live.
  The sender reconnects with a backoff of 1 s, doubling to 30 s.
- `PeerStreamReceiver` checks every batch (canonical ULIDs, growing change ids per database, at most 1024 groups and
  1024 deltas per group, valid collections and ids, canonical states). A bad batch applies nothing and is refused for
  its database alone (an error log and `terrabase.replication.undecodable_batches`): the sender holds that database and
  sends it again 5 s later, and the stream goes on with the others. A frame that names no valid database ends the stream
  with `INVALID_ARGUMENT`. A newer stream from the same origin ends the older one, so one origin's groups apply in order.
- `StorageChangeGroupSink` joins whole groups, at most 1024 documents per durable transaction (`synchronous = FULL`),
  through `IStorageTransaction.JoinAsync`, and records `_tb_peers.applied`. An ack follows the last commit.
- `JoinAsync` writes the document only when the join changed it, with `stamp = S` and `origin` = the delta's node,
  and hides unique-constraint losers instead of failing. A `_collections` document runs its collection's DDL (create,
  identity, drop) without writing metadata of its own. A delta of a collection this machine lacks creates the
  collection, unless that collection's dropped metadata document dominates the delta. The drop's clear joins into
  document deltas whether the table exists or not; joined drops remove covered entries and retain newer state, so
  receiving a newer write before or after the drop produces the same document states and collection identity.
- The outbound status (`GET /replication`, `/health/replication`) lists connected streams. `CatchupSeconds` is the age
  of the backlog since a read first returned groups, and `Diverged` is always false until anti-entropy (R6). The status
  reports `Initialized = false` while no change group source exists, so `/health/replication` stays `degraded` for
  every other region until R3.
- Metrics: `terrabase.replication.send_lag` and `ack_lag` (gauges, `peer_region`), and `merged_groups` and
  `apply_delay` (receiver, labelled with the origin's region from its `Hello`).
- Not in R4a:
  - persisting `applied` from heartbeats (R7 defines the heartbeat stamp as a frontier);
  - DDL from replicated `_indexes` and `_unique` documents: those documents replicate, but indexes and constraints are
    created only where they were defined;
