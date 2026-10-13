# 0013a: A write attempt answers success only after two independently durable regional copies

- **Status: proposed** (2026-10-13; revision 3 approved in round 5 at `40464691`; revision 4 applies its should-fixes).
  - This record is split from 0013 for a narrow review. It holds the success barrier (B4) for **one write attempt**.
  - Cross-region idempotency, operation ids, lookups and replay are
    [0013b](0013b-operation-idempotency.md), revision 3 pending. This record claims none of them.
  - The split is the author's technical recommendation. It is not a recorded COS approval.
  - Owner: R6-FIX.
- **Contract (user-approved).** A production write answers success only after a durable copy exists on the accepting
  machine **and** on a machine of another region that holds the database (placement option A: two regions or more).
- **Scope:**
  - `Engine/Sqlite`: the commit marker.
  - `Features/Replication`: the acks and the wait.
  - `Features/Documents`: the document and transaction answers.
  - `Features/Management`: placement.

## 1. What one write attempt means

- **A write attempt** is one mutating HTTP request on a covered route (section 6).
  - Today these are the document routes (`PUT`, `PATCH`, `DELETE`, `POST`) and `POST …/transactions`.
  - A transaction is one attempt, and all of its operations commit in one local transaction.
- **Its commit marker** is `(store incarnation, the change id of its own transaction's last CDC row)`.
  - It is read on the writer right after `COMMIT` returns successfully, before the writer is released.
  - Requests that group commit puts in one storage transaction share one marker, because they are one transaction. A
    request rolled back by its savepoint, or a whole batch rolled back, never inherits a sibling's marker or outcome.
- **After COMMIT returns successfully**, B1 holds.
  - If reading the marker then fails, the attempt's barrier cannot be checked, so the answer is outcome unknown, even if
    an ack later arrives.
  - If resetting the writer session fails after the marker was read, that is not a barrier. The session is discarded
    and never reused, and the attempt is decided by the wait like any other. **Pending:** this needs #181's
    unpublished revision. At its published head `83291845`, a failed reset still rethrows after a commit.
- **A transaction that writes nothing** (only `assert` / `assert_count` operations) has no own change to ship and no
  marker. It is answered without a wait, with `durability: "unchanged"`. No mutation is added to it, and it does not
  wait for some unrelated head.
- **Its local durable commit (B1).** Its transaction committed with `synchronous = FULL`, and `CommitAsync` returned
  without error. Implementation: #181 (published `83291845`). Its validation is pending, so it is not evidence yet.
- **An eligible acknowledgement (B2).** An ack confirms the marker only when it is:
  - received on a live push link of this process;
  - for the same database;
  - from a peer node of a region other than this machine's, which the database's placement includes;
  - for groups sent from the **same store incarnation** as the marker;
  - not refused;
  - with `change_id ≥ marker`.

  The receiver acks only after every group it joined is durable: the ack follows a successful FULL commit made after
  the last join, and that commit's sync must cover every part of a batch split into several transactions (the
  sync-coverage invariant). #180 implements a stricter contract, every receiver transaction FULL, which this record
  does not rely on, and tests it separately (section 8). Implementation: #180; validation pending.
- **Refill certification.** A refill's ack certifies its `head` only when every one of these holds:
  1. **Complete coverage, validated.** Every bucket that differs from the source was read and repaired. A source that
     no longer holds the database, any refused or undecodable page, or any change of the source's or the receiver's store
     incarnation during the refill aborts it, and it is acked refused. Finishing the control flow is not coverage.
     Equal digests mean equal records only once a record's digest covers its key as well as its state. Today
     `WriteState` hashes the state alone and buckets XOR those hashes, so two different keys with the same encoded
     state on opposite replicas compare equal. That fix (#114 should-fix 3, `SqliteStorageTransaction.States.cs`) is a
     prerequisite owned by R8; until it lands, no refill certifies.
  2. **A successful receiver durability barrier after the last join.** Every repair transaction is FULL. Then, also
     when nothing differed, the receiver commits the barrier: one FULL write transaction that upserts the refill's
     certificate row (origin, source incarnation, `head`) on that database. It is a real write, so its commit appends
     to the log and syncs it; an empty `COMMIT` is never the barrier. The ack waits for its `COMMIT` to return
     without error.
  3. **No uncertain receiver commit on that file.** An error at or after a receiver `COMMIT` on a database (a failed
     sync included) faults the receiver's copy of that database for its store incarnation: from then on it acks
     nothing for that database, on any link, live batch or refill. Reopening the writer or the file is not enough,
     because after a failed sync the OS can keep serving pages it never wrote, so equal digests prove nothing about the
     disk. The copy certifies again only as a new incarnation rebuilt from peers (R7's copy-refill, under section 5's
     surviving-copy rule). Delivery 3's `EIO`/`ENOSPC` evidence on the shipped binary may justify a narrower rule
     later; until then this one holds.

     The fault belongs to the database incarnation on that node, not to a link or a process. Every link of that
     database refuses, and the fault survives a process restart on the same volume. So it is recorded outside the
     suspect file (the node's system database), with FULL, before the faulted writer is released.

     **Open gap, blocking runtime qualification.** Suppose the process dies after the failed sync but before that
     record is durable, then restarts on the same volume. It could reopen the incarnation with the unsynced pages still
     cached and certify them. Writing this down does not close the gap. Durable acknowledgement is not qualified until
     a fail-closed recovery mechanism covers that window, and its controlled crash-and-restart test passes (section 8).
     For example, every receiver copy could be treated as faulted after an unclean exit until it is rebuilt or proven.
- **Incarnation scoping.** On the sender, a link's acked, in-flight and confirmed positions belong to one source store
  incarnation. When the file is replaced, copied or restored (a new incarnation):
  - they are discarded;
  - the link resumes from the new file's own stored ack for that same incarnation (a copy starts at 0);
  - it re-sends or refills.

  **A stored ack names its incarnation, and is written only into it.** `_tb_peers` keeps, with each peer's `acked`,
  the source incarnation it was acked for. Persisting an ack is conditional: inside the writer transaction that
  records it, the source reads the file's own incarnation and writes only when it equals the incarnation of the batch
  the ack covers. On a mismatch the ack is dropped: neither the replacement's peer position nor its retention floor
  changes. Reading a stored ack whose incarnation is not the file's (a copied or restored file) gives 0. So a delayed
  ack from file A never becomes file B's resume position, and B's later acks never cover a write B never sent.

  No change id of one incarnation is ever compared with another's. A refill captures its `head` together with the
  source incarnation. The receiver's `Digest` and every `ReadBucket` page name that incarnation, and the source refuses
  a mismatch. The receiver aborts a refill or a batch if its own store is replaced while it runs.
- **What never confirms:**
  - a welcome;
  - an applied HLC;
  - a refused ack;
  - another database's ack;
  - a same-region ack;
  - a stored or retention-raised `_tb_peers.acked`, or one stored for another incarnation;
  - an ack from before a restore, copy or volume replacement (another incarnation);
  - an intermediate anti-entropy repair page;
  - a refill that finished without complete validated coverage and a successful receiver barrier.
- **Acks are prefixes.** An ack of `N` covers every own group up to `N`, so a whole transaction is confirmed together,
  never in part. A later attempt's confirmation also covers earlier attempts on that machine.

## 2. The answer to a client

| Outcome | When | Answer | What the client may conclude |
| --- | --- | --- | --- |
| **Confirmed** | B1 for the marker, then an eligible ack within **10 s** | `2xx` as today, with `durability: "remote_durable"` | The attempt's whole effect is durable in two regions. It survives the loss of either region while the other's volume survives |
| **Refused before execution** | Validation fails, or no machine of another placed region has a connected stream for the database (precheck), or the waiter bounds are full | `4xx` as today, or `503 remote_region_unavailable` / `503 confirmation_busy`, all with `Retry-After` | **This attempt** executed nothing. It says nothing about any other attempt |
| **Outcome unknown** | `COMMIT` returned an error or did not return; or B1 held but no eligible ack came within 10 s; or the link dropped and none other confirmed | `503 write_outcome_unknown` with `region` and `machine`, and no `remote_durable` | **Nothing.** The attempt may be applied (even durably, later) or lost. It is not failed, not absent, and not safe to retry blindly |
| **Disconnected** | The client lost the connection before the answer | No answer | The same as outcome unknown |

- **The cancellation and partition rules:**
  - A cancelled request cancels only its own wait. Its commit, if any, stays and keeps replicating.
  - A partition never produces success. A write already committed answers outcome unknown, and new writes are refused
    by the precheck while no other placed region is connected.

## 3. Rules for SDKs and callers

- **No automatic replay of an ambiguous mutation.** An SDK never resends a mutating request on its own after:
  - `write_outcome_unknown`;
  - a timeout;
  - a reset connection;
  - any transport error after the request may have reached the server.
- **What an SDK may retry.** It may retry by itself only answers that state the attempt executed nothing:
  - `503 remote_region_unavailable` and `503 confirmation_busy`;
  - a connection that failed before any request byte was sent.
- **Outcome unknown is its own typed outcome.** It is neither success nor failure, and it carries `region` and
  `machine`. It stays unknown.
  - **A read does not resolve it.** Absence or an unchanged value proves nothing. The commit may be on its way, or on
    a machine the read did not reach: gru's `inc +1` may arrive after sjc's read showed the old counter, so a resend
    increments twice. Concurrent updates can also make a value match without identifying this attempt.
  - Without proof specific to the application, or the separately approved 0013b protocol, the caller cannot turn
    unknown into failed or confirmed. Any resend is a new attempt that may apply in addition.
  - A whole-document `PUT` resent converges to the resent value, but that may overwrite writes made in between. It is
    not exactly-once.
  - `PATCH` with `inc`/`append`/`add`/`remove`, a `POST` without a client-chosen id, and a transaction can apply twice
    if resent.
- **No `Idempotency-Key` under 0013a.** It stays refused (`400`, "not supported yet"), so no client can believe it is
  protected. Operation ids and exactly-once retries come only with 0013b.

## 4. Lost acks, reconnects and refills

- **Any eligible ack, on any link.**
  - The wait listens on every live link of the database's placed other-region peers, and the first eligible ack
    confirms. One link dropping does not end the wait while another can still confirm.
  - A link that reconnects starts again from its stored ack for the current incarnation. Its later acks count if they
    are for the same incarnation.
- **A lost ack is re-sent, not re-applied.** If the ack itself is lost, the sender resends from its last ack. The
  receiver joins idempotently, and the next ack covers the marker.
- **Refills.**
  - A refill's ack certifies its `head` only as certified above: `head` and its incarnation are read before the
    receiver's digest and bucket reads, coverage is complete and validated, joins are monotone, and a successful FULL
    barrier follows the last join.
  - #168's order holds: a database's later batches join only after its refill commits.
  - A failed, aborted, carried or reconnected refill confirms nothing.
- **The bound.** If none of these confirms the marker within 10 s, the answer is outcome unknown. The write keeps
  replicating.

## 5. Placement and availability

- **Option A.** Every database of a cell is placed in at least two regions.
  - A create without `replica_regions` places replicas in every other serving region of the membership. With none,
    it answers `503 remote_region_unavailable`.
  - Creating one with fewer than two regions answers `400 placement_needs_two_regions`.
  - An update may still remove every replica region; that is the step before a delete, which needs none. The database
    is then a single-region one, as below.
  - Existing single-region databases are never moved, and their data is never dropped. Their writes fail clearly
    (`503 remote_region_unavailable`) until an explicit placement update adds a region.
- **Availability.**
  - A write needs its own region plus one other placed region reachable.
  - With three placed regions, writes go on with one region lost.
  - With exactly two, losing either stops writes clearly, and never with a false success.
- **The surviving copy is kept.** After a region is lost, an acknowledged write may live in one remaining region only.
  - Automatic recovery, refill and placement changes preserve the surviving acknowledged state until a replacement
    that contains or dominates it is durable. Today's refill merges and deletes nothing. A copy-refill, which deletes a
    file before copying (garbage-collection work, R7), must compare first, and never wipe the only copy of an
    acknowledged write.
  - Streams do not forward remote-origin groups. After the origin's loss, anti-entropy rounds between the remaining
    regions copy the write on: two differing rounds, 60 s apart.
- **A cell of one.** A machine without a member directory has no second region: off Fly, development and the
  single-machine Fast servers. It answers after B1 with `local_durable`, and never on Fly. On Fly the directory is the
  app's DNS, and a DNS failure or partition keeps it strict.

## 6. Routes and rollout

- **Delivery 5 covers** the document routes and transactions.
- **Every other mutating route keeps answering `local_durable`** until it is covered: collections, schemas, indexes,
  unique schemas, database create/update/delete, backups and restore, management commits, API keys. Catalog routes wait
  for R10a's lattice catalog (design section 15).
- **No announcement.** The contract is not announced, and no SDK documents `remote_durable` as the guarantee, until
  every route is covered, delivery 3's durability evidence (database file, checkpoint/reset, `EIO`/`ENOSPC`, connection
  reuse) passes, and real three-region acceptance passes.

## 7. Resources and the bound

- **No lock is held.** A waiting request holds no storage session and no lock. The commit has ended before the wait.
- **Bounded waiters.** At most 4,096 per machine and 256 per database, both in code. Beyond that, a write is refused
  before execution with `503 confirmation_busy`.
- **The 10 s bound is a ceiling, not a latency promise.** Latency is measured before it is stated: the plan in
  [0013b](0013b-operation-idempotency.md) section 11, without the reservation round.

## 8. Required tests (delivery 5)

- **Confirmed only after both barriers:**
  - A remote held before its ack: no success. Answer `write_outcome_unknown` at the bound.
  - Mutant: success before the ack. It must fail.
- **Eligible acks only:**
  - Same-region, unplaced, refused, other-database and stale-incarnation acks never confirm.
  - Mutant: any ack confirms. It must fail.
- **Any remote:** one remote held while another confirms; either remote of three killed, with writes going on.
- **No false success under partitions:**
  - Full and asymmetric partitions: committed attempts answer unknown, and new attempts are refused before execution.
- **Whole transaction:**
  - A transaction confirmed by one ack.
  - Grouped writes sharing a marker.
  - Receiver batch splitting: the ack comes only after every part.
  - Sync coverage: the power is cut at the ack (at the first sync after it), and every acked group survives. The
    mutant "only the last part FULL" is expected to pass this test, because the last FULL sync covers the earlier
    parts. It is not forced to fail.
  - #180's stricter per-transaction contract, tested separately: the power is cut between two parts, and the parts
    committed before the cut survive. Mutant: only the last part FULL. It must fail this test.
- **Refill certification:**
  - Held, failed and reconnected refills confirm nothing until a refill completes, while another database flows.
  - Reconnect with equal digests: the ack comes only after the barrier's certificate row committed. Mutant: no
    barrier when nothing differed. It must fail.
  - A failed receiver sync (the H4 shim), then a reconnect with equal digests: no ack for that database from that file,
    and a power loss after the last ack keeps every acked record. Mutant: reopen the writer and certify. It must fail.
  - Two keys with the same encoded state on opposite replicas: the digests differ and the refill repairs both (after
    R8's key fix).
- **Receiver copy fault (blocks runtime qualification):**
  - Multi-link refusal: a failed sync on origin A's link, then origin B's live link sends a group the failed commit
    already made readable. Neither link acks. Mutant: a per-link fault. It must fail.
  - Controlled restart: the process restarts on the same volume after the fault was recorded. That incarnation still
    acks nothing. Mutant: a fault kept in memory only. It must fail.
  - Crash before the fault record (the open gap): a crash injected between the failed sync and the record's commit,
    then a restart on the same volume. That incarnation certifies nothing. This test must pass before durable
    acknowledgement is qualified.
  - Rebuild: R7's copy-refill rebuilds the copy from a peer into a new incarnation that meets section 5 and refill
    certification. Acks resume, for the new incarnation only.
  - A source that no longer holds the database, and a refused repair page: the refill is refused, never acked.
  - The source's file replaced between its head, its digest and its pages: aborted.
- **Incarnations:**
  - A file replaced while a sender link keeps its cursor: the cursor resets, and no ack of the new file confirms a
    marker the peer never received.
  - The receiver's file replaced during a batch, and during a refill: no ack.
  - A delayed ack from file A arriving after the source file was replaced by B: B's stored `acked`, retention floor and
    resume position are unchanged, and no later ack of B confirms a write of B the peer never received. Mutant: the ack
    persisted without its incarnation check. It must fail.
- **Origin errors:**
  - `COMMIT` error, or a failed marker read after `COMMIT`: outcome unknown, even if an ack arrives later.
  - A failed session reset after the marker (#181): decided by the wait.
  - Savepoint and whole-batch rollback: no inherited outcome.
- **Waiter races:** an ack that arrives before the waiter registers still confirms (the waiter checks the current
  position first). Bookkeeping and received-apply CDC rows never replace an attempt's marker.
- **A transaction of assertions only**, on an idle database: answered `unchanged`, at once.
- **Recovery:**
  - An acknowledged write survives the loss of its origin region and that region's rejoin.
  - A refill never wipes the only surviving copy.
- **Lost ack, reconnect, restart:** a dropped ack confirmed by the next one; the sender restarts in the middle of a wait.
- **Cancellation:** after commit, the write still replicates.
- **Bounds:** waiter bounds refuse before execution.
- **The SDK contract:** the error codes and headers, and `Idempotency-Key` still refused. Plus runtime tests in the
  SDK repositories:
  - no automatic replay on unknown, timeout, reset or a transport error after send;
  - the stale-read / delayed-`inc` case left unknown, with no deduplication promised.
- **Topology:**
  - All six directions of gru, sjc and fra.
  - A Fly DNS failure with no fallback to a cell of one.
- **Prerequisite, not reproduced here:** delivery 3's shipped-binary evidence (database file, checkpoint/reset,
  `EIO`/`ENOSPC`, connection reuse) is required before the guarantee is claimed.
- **Placement:** delivery 4 (#pending), with fail-first and mutants.

## 9. Code boundary (delivery 5)

| Area | Change | Owner |
| --- | --- | --- |
| `Engine` (`IDatabaseStore.WriteAsync` result, `SqliteStorageTransaction.CommitAsync`) | The commit returns its marker: the store incarnation, and `max(change_id)` of `turso_cdc` read on the writer right after a successful `COMMIT`. The writer is exclusive, so that id is this transaction's own last row. A `COMMIT` error, or a failure to read the marker, returns no marker, and the request answers outcome unknown. A marker already read survives a later session-reset failure: the session is discarded and the wait decides (**pending**: #181's unpublished revision keeps that failure out of `DisposeAsync` once committed. At the published `83291845`, `DisposeCoreAsync` still rethrows it and `CommitBatchAsync` treats it as a commit error: a conservative failure, never a false success). | R6-FIX (sync settings and commit, as assigned) |
| `Features/Replication/RemoteDurability.cs` | `IRemoteDurability` gains `CanConfirm(database, placement)` (the precheck) and `WaitAsync(database, marker, cancellationToken)`, which answers confirmed or not within the 10 s bound. | R6-FIX |
| `Features/Replication/PeerStreams.cs` | It keeps, per link and database, the highest eligible ack with the incarnation of the batch it covers. Its cursors reset when the source incarnation changes. A waiter registry is signalled on every eligible ack and on link loss, and a waiter checks the current position when it registers. `LocalDurability` (a cell of one) confirms at once. | R6-FIX |
| `Features/Replication/ChangeGroups.cs` / `TursoChangeGroupSource` | **Dependency:** every source read used for a prefix must report the store incarnation it read: `ReadAsync`, `HeadAsync`, `AckedAsync`, `RetainsAsync`, `DigestAsync` and `ReadBucketsAsync`. The digest and bucket reads must also refuse an expected incarnation that no longer matches. `AckAsync` takes the acked incarnation and writes only when it matches the file's own, checked in the same writer transaction; `_tb_peers` stores the incarnation with `acked`, and a stored ack of another incarnation reads as 0. | **R3** (CDC source). To be agreed before coding |
| `Features/Replication` (`AntiEntropy`, `PeerStreamReceiver`, `StorageChangeGroupSink`, `Protos/.../replication.proto`) | Refill certification: abort on a source that no longer holds the database, on a refused or undecodable page, and on an incarnation change. A FULL barrier commit after the last join, also with nothing to repair. `Refill`, `DigestRequest`/`DigestReply` and `BucketRequest` carry the source incarnation (a versioned contract change). The receiver aborts if its own store is replaced. | R6-FIX |
| `Features/Replication` (`PeerStreamReceiver`, `StorageChangeGroupSink`) | The receiver copy fault: per database incarnation on the node, recorded with FULL outside the suspect file before the writer is released, refused on every link, kept across restarts; the fail-closed recovery for a crash before the record (open gap); cleared only by a rebuilt new incarnation. | R6-FIX. Where the record is stored in the node's system database: agreed with the catalog owner (R10a). The rebuild: **R7** (copy-refill) |
| `Features/Documents` (`DocumentService`, `DocumentRoute`, `TransactionEndpoint`) | Precheck before the write; await the wait after the commit; answer `remote_durable` or `503 write_outcome_unknown`; precheck refusals. | R6-FIX (client write path, as assigned) |
| `Errors`, OpenAPI | `write_outcome_unknown`, `remote_region_unavailable`, `confirmation_busy` typed in the source-generated schemas. | R6-FIX |
| `Engine/Sqlite/SqliteStorageTransaction.States.cs` | **Dependency:** a record's digest covers its key as well as its state (#114 should-fix 3). | **R8**. Before any refill certifies |
| Tests | Fast cluster tests (`PeerStreamTests`), the E2E three-region topology (`RegionProcesses`, R3's harness: test registrations only, agreed with R3), and the mutants of section 8. | R6-FIX |

Nothing here changes `_operations`, keys or lookups (0013b).
