# 0010: Write-wait, and a DurableAck synced to disk

- Status: proposed (TS-575, 2026-10-06); write-wait chosen by COS under the user's grant of autonomy. Also decides the
  TS-561 follow-up "DurableAck after a WAL fsync". Needs human acceptance.
- Scope: `Features/Replication` (the sender's wait, the receiver's apply), `Features/Documents` (client writes),
  `Engine` (`IDatabaseStore.BeginDurableTransactionAsync`), `Configuration` (`write-wait-ms`)

## Context

Since TS-574, gru-1 serves db-1 and db-2 active-active behind one round-robin backend set. ts-auth's readiness probe
writes, reads and conditionally deletes one document back to back. When the read reaches the other member before
replication has delivered the write, it answers 404 and ts-auth reports not ready. Between 13:20Z and 13:37Z that
happened 4 times on ctl-2, and once it reached gru.auth's `/readyz`.

The options in TS-575:

- **LB cookie stickiness**: ts-auth's TerraBase client sets `UseCookies = false`.
- **IP-hash stickiness**: ts-auth reaches TerraBase through Cloudflare, so the LB sees Cloudflare's edge addresses.
- **Version-wait read**: the client sends the version it wrote and the reader waits for it. This needs ts-dotnet and
  ts-auth changes, and every other client has to adopt it.
- **Write-wait**: a member answers a write only after its peers durably acknowledged it. This is TerraBase only.

Write-wait relies on `DurableAck`. Under [0007](0007-lz4-application-envelope-replication.md), a receiver sends it after
its SQLite commit. Under [0003](0003-sqlite-synchronous-normal.md) (`synchronous = NORMAL`), that commit survives a
process crash but not a power loss or an OS crash. TS-561 left "DurableAck after a WAL fsync" open, pending a decision
against 0003. Both questions decide what a peer's acknowledgment is worth, so this record covers both.

## Decision

### Write-wait

- **What waits.** These are client writes on a client database: document PUT, PATCH, DELETE and POST, transactions, and
  collection create and drop. Once the write transaction has ended, the member answers only after every *active* peer has
  durably acknowledged that database's change groups up to the journal head the member had right after the commit. The
  wait is bounded by `write-wait-ms`.
- **Bound.** `write-wait-ms` defaults to `2000`, from `0` to `60000`; `0` answers right after the commit, as before.
- **Outside the transaction.** The wait starts only after the write transaction has ended. Holding the database's writer
  lock across a network round trip would serialize every write to the database behind it. It would also block this
  member's own applier, which takes that lock to apply a peer's groups. Two members waiting for each other would then
  stall until the bound.
- **Active peers.** A peer whose `Sync` stream for the database is failing is not waited for: its stream is in backoff
  after an error (unreachable, restarting, refusing the database). Waiting resumes once a stream to it succeeds again.
  A connected peer that is slow is waited for, up to the bound. So a member down for a rolling restart costs each
  database one wait, until that database's first failed attempt, not a wait on every write.
- **Timeout: degrade, not an error.** When the bound expires, the member answers the write's normal success. The write is
  committed locally and keeps replicating. An error would report a committed write as failed: a client that retried a
  create with `If-None-Match: *` would then get 412, and a retried transaction would apply twice. Timeouts are counted
  in `terrabase_replication_write_wait_seconds_count{result="timeout"}`, and
  `terrabase_replication_sender_durable_lag` shows the remaining lag. A response's `durability` stays `local_durable`,
  because that is the only guarantee every answer carries.
- **What does not wait:**
  - Management and catalog writes: one primary writes them ([0009](0009-catalog-replication.md)).
  - Index create and drop: they change query plans, not results.
  - Database creation.
  - Members without `peer-addresses`.
  - Reads.

### A synced DurableAck

- The receiver applies every group in a write transaction with `PRAGMA synchronous = FULL`. The pragma is set before
  `BEGIN`, because SQLite refuses to change it inside a transaction. It is reset to `NORMAL` before the session returns
  to the pool, and a session whose reset fails is closed instead.
- With `FULL`, the commit syncs the WAL before `CommitAsync` returns, and `DurableAck` and `AppliedAck` are sent only
  after that. This is the terrabase-rust contract (a WAL sync before `DurableAck`) that 0007 recorded C# as falling short of.
- Local client writes keep 0003's `NORMAL`. Their commit-path cost does not change, except for the wait above.

## Guarantees

A write answered within the bound, while every peer is active:

- **Read-your-writes on every member.** Any read that starts after the answer, on any member, sees the write or a later
  state. This also covers caches: a replicated apply moves the same write stamps as a local write.
- **Durable beyond the accepting member.** The write is on disk on every peer, so it survives a power loss or an OS
  crash of the member that accepted it while one peer survives.

## Consequences

- Not covered, and observable:
  - **A peer in backoff, or a write that timed out.** A read on that peer can miss the write until replication delivers it.
  - **A restarted member catching up.** Readiness does not wait for client replication (0009). A member back from a
    restart serves while it catches up, so it can miss writes accepted elsewhere during its downtime. Writes made while
    its first stream is still failing or catching up do not wait for it either.
  - **Concurrent writes.** Writes from different members to one document still merge last-writer-wins (0007). Write-wait
    orders a client's own write before its next read, not two clients' writes.
- **The accepting member's own file.** It stays under `NORMAL`, so a power loss can roll it back to its last synced
  commit while its peers hold the lost groups. Its journal is then behind what peers applied. The sender refuses to
  reuse those sequence numbers, so that database stops replicating from it until an operator recovers it (a
  fresh-incarnation copy). Automatic recovery from peers is follow-up work.
- **Latency.** Every client write adds one `Sync` stream to each active peer: `Hello` and `Welcome`, the group, then a
  `DurableAck` after the peer's synced commit. On one network that is a few milliseconds. A cross-region peer would add
  its round trip to every write. Choosing which peers a write waits for (for example, only same-region ones) is follow-up
  work. The `terrabase_replication_write_wait_seconds` histogram measures the added time per write. TS-575 records the
  value measured on gru-1.
- **Queuing.**
  - A peer's sender serves one database at a time, so under writes to many databases a write can queue behind other
    databases' streams to the same peer, up to the bound.
  - Groups committed while a stream is open go in that database's next stream.
- **Apply throughput.** Each applied group costs the receiver one fsync, including catch-up and baseline groups.
- **Resources.** The wait holds an HTTP request, not a storage session or lock. Concurrent waiting requests are bounded
  by the listener's connection and request limits.

## Alternatives considered

- **LB cookie or IP-hash stickiness.** Neither works for ts-auth (see Context), and stickiness does not survive a member
  leaving the set.
- **Version-wait read.** Correct, but it needs every client to carry versions. Write-wait needs no client change. A
  later client-side session token can still narrow the wait.
- **Fail the write on timeout (503).** This reports committed writes as failed and invites duplicate retries.
- **Wait inside the write transaction.** This serializes writes behind the network and stalls the peer's applier.
- **Keep the NORMAL DurableAck.** Read-your-writes needs only the peer's commit to be visible. But then a peer's
  acknowledgment would claim more than it delivers, and an answered write could still be lost to a power loss of the
  accepting member.
- **`synchronous = FULL` for every write.** Decision 0003 rejected that for local write throughput. Here only
  replicated applies pay the fsync.
