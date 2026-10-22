# 0013b: Operation ids and cross-region idempotency (revision 3 pending)

> **Split, 2026-10-13.**
> - The success barrier for one write attempt (B4) is now [0013a](0013a-remote-durable-acknowledgement.md). That
>   covers the commit marker, eligible acks, outcomes, placement, routes and bounds.
> - This record keeps the operation-id protocol: sections 4 (idempotency states), 6, 7 and 12 (keys and lookups).
> - It is **proposed, revision 3 pending**. Round 2's review (#171, 6138821716) found the majority reservation of
>   revision 2 unsafe across acceptor changes, without termination for split votes, and too loose in certifying records
>   and fences.
> - Nothing below is claimed or implemented until revision 3 passes independent review. Until then:
>   - `Idempotency-Key` stays refused;
>   - no cross-region exactly-once is promised;
>   - where the sections below overlap 0013a, 0013a governs.
>
> Revision 2 follows unchanged, for its context.

## Revision 2 text

- **Status:** proposed, revision 2 (2026-10-13).
  - The contract was approved by the user through COS. This record defines it before step 2 is coded.
  - Placement option A was decided by the user through COS (2026-10-13).
  - Revision 2 answers the design review of revision 1 (#171, at `4d360ece`).
  - Owner: R6-FIX.
- **Scope:**
  - `Features/Replication`: the receiver's ack, the sender's acks, operation reservations.
  - `Engine/Sqlite`: client-database sync, the commit marker.
  - `Features/Documents`: the client write path, the operations route.
  - `Features/Management`: placement.
  - Catalog writes need R10a's lattice catalog (section 15 of the design).
- **Amends:**
  - [0003](0003-sqlite-synchronous-normal.md): every write transaction syncs.
  - Design C ([multi-region-replication.md](../multi-region-replication.md) section 6, "ack after one node").
- **Replaces** [0010](0010-write-wait-and-synced-durable-ack.md), which is already superseded.

## 1. Contract and definitions

- **Contract.** A production mutating request answers success only after its effect is committed and durable on its
  accepting machine *and* on a machine of a second placed region. A request carrying an operation id executes at most
  once, whichever machines its retries reach.
- **Durable.** Recoverable after a process crash, an OS crash or a power loss of the machine, with its persistent volume
  intact and its sync acknowledgements honoured. A write's frames are durable once a sync of the write-ahead log that
  contains them has returned success. A checkpoint moves them into the database file. It may reset the log only after the
  database file is synced, so durability transfers and the log need not keep them. Whether the shipped Turso binary does
  this is tested (section 9), not assumed.
- **Local durable commit (B1).** The write's transaction committed with `synchronous = FULL` on the accepting machine,
  and `CommitAsync` returned without error.
- **Remote durable commit (B2).** The receiver's transactions that joined the change group committed with
  `synchronous = FULL`. That holds for every transaction when a batch splits into several, and for every repair
  transaction of a refill. Only after that does the receiver send a non-refused ack. Receipt, queueing, a refused ack, an
  unsynced log or a failed sync never count.
- **Two regional copies.** B1 on the accepting machine, plus B2 on a machine of another region that the database's
  placement includes. The guarantee survives the loss of either region while the other's volume survives. It does not
  cover both copies being destroyed.
- **Last writer wins stays.** A durable write can still be superseded by a later concurrent write under the lattice's
  rules (design sections 3 and 4). Durable means not lost; it does not mean final.

## 2. Four barriers, audited separately

Main at `e30ffdc2`, with the evidence on the step branches.

| # | Barrier | Code | Evidence |
| --- | --- | --- | --- |
| B1 | Origin commit and fsync | At `e30ffdc2` client stores run `synchronous = NORMAL`: `SyncsEveryCommit` is set only for `_system` (`Engine/Sqlite/SqliteStorageEngine.cs:776` against `:1416`, `SqliteSession.cs:96`). `83291845` makes every write transaction FULL (`SqliteDatabaseStore.BeginAsync`). | `ClientWritePowerLossTests` (H4 shim: SIGKILL, then every log cut back to its last sync). It **fails at NORMAL** ("write 1 was answered before the power loss") and **passes at `83291845`**. Not yet covered: checkpoint/reset, sync errors, connection reuse (section 9). |
| B2 | Remote apply commit and fsync | `StorageChangeGroupSink.cs:36`/`:66` → `BeginDurableTransactionAsync` (FULL). Ack after `ApplyAsync` (`PeerStreamReceiver`). | `ReplicatedApplyPowerLossTests` at `4adf8424`: a node acks three groups and loses power; all three survive the restart. The **ack-before-fsync mutant fails it** ("group 1 was acknowledged before the power loss"). Not yet covered: the same gaps as B1. |
| B3 | Backup shipping | `Features/Backup/ContinuousBackup.cs:57`/`:317`: 1 s after the commit, off the request path. | Gates no answer and is never counted as a regional copy. Acceptance includes a successful restore of a non-empty database carrying valid operation records (section 9). |
| B4 | Client-success release | `DocumentService.cs:363` → `DocumentRoute.cs:277` answers `local_durable` right after the local commit. | Step 2 moves the release after B1 and B2 of the request's own commit marker (section 5). The success-before-remote-ack mutant must fail. |

Configuration alone (a PRAGMA) is never cited as proof. Each claim cites its fault test at the SHA of the code it covers.

## 3. Scope: every production mutating route, no switches

- **Where the contract holds.** On every machine that belongs to a cell: one with a member directory. In production that is the admitted membership directory. In
  tests it is the multi-machine suites' directory.
  - The seam `IRemoteDurability` is registered by the same rule as the membership (`AddTerraBaseCluster`).
  - A DNS failure or a partition does not remove the directory, so the machine fails closed. It never falls back.
  - No app name, setting, flag or per-database durability level selects behaviour.
- **The one documented limitation: a cell of one.** A machine without a directory (development, and the
  single-machine Fast servers) has no second region. It answers after B1 with `durability: "local_durable"`. It never
  runs in a production cell or in acceptance.
- **Every production mutating route is in scope.** That covers:
  - documents (`PUT`, `PATCH` including `inc`/`append`/`add`/`remove`, `DELETE`, `POST`);
  - transactions;
  - collections and collection schemas;
  - indexes and unique schemas;
  - database create, update and delete, and placement;
  - backups and restore;
  - management commits;
  - API keys.
- **Catalog writes.** Catalog and management writes live in `_system`. Section 15 of the design makes `_system` a
  lattice database placed in every region and replicated by the same streams (R10a; it supersedes the catalog primary
  of 0009). Its writes then take the same reservation, commit marker and wait. FULL on `_system` alone (0012) proves
  only one copy.
- **No announcement before coverage.**
  - Until every route above waits, no answer says `remote_durable` for an uncovered route, and the contract is not
    announced.
  - Each delivery PR lists the routes still uncovered.
  - Covered routes answer `remote_durable`. Uncovered ones keep answering `local_durable`, which is true, and the
    release notes name them.
- **Single-machine production cells fail clearly.** A cell with only one region has no remote region, so its writes answer
  `503 remote_region_unavailable` (section 4). That includes the platform databases placed in gru, sjc and fra. Adding another test region is a tooling change outside this record.

## 4. Outcome state machine

An operation id is checked **before** any precheck that could claim "not applied".

| State | When | Answer | Applied? |
| --- | --- | --- | --- |
| `rejected_invalid` | Validation fails before reservation (malformed, unauthorized, too large) | `4xx` as today; nothing is recorded | No. The same key may be retried |
| `remote_region_unavailable` | Reservation lookup found no record, and no placed machine of another region is reachable: the reservation round could not start | `503 remote_region_unavailable`, `Retry-After: 1` | **No** (proven: nothing reserved, nothing executed) |
| `reservation_unavailable` | The reservation reached no majority within the bound (section 6) | `503 operation_quorum_unavailable` with `operation_id` | Not executed by this attempt. The key may hold a minority reservation, so a later retry decides |
| `in_progress` | Another attempt holds the reservation and has not finished | Waits, within the bound, for its outcome; then the outcome, or `503 write_outcome_unknown` | Unknown until it finishes |
| `rejected_recorded` | Executed and deterministically refused, for example a local unique violation (`409`) | Recorded, and replayed on every retry | No |
| `committed_unconfirmed` | B1 holds; no eligible remote ack within the bound, a sync error after `COMMIT` was issued, or a cancellation after commit | `503 write_outcome_unknown` with `operation_id`, `region`, `machine`, `expires_at` | **Unknown to the client.** It is applied locally and keeps replicating |
| `confirmed` | B1 and B2 of the request's commit marker | `2xx` with `durability: "remote_durable"` | Yes |
| `absent_here` | A lookup on a machine without the record, and no majority answer | `404 operation_unknown`, `authoritative: false` | Unknown |
| `not_executed` | A lookup whose majority round found no reservation; it then fences the key | `404 operation_not_executed`, `authoritative: true` | **No, and never will be under this key** |
| `expired` | The key's ULID time is outside the admission window, or its record expired | `410 operation_expired` | Unknown: use a new key only after reconciling by other means |

- **Cancellation.** A client that disconnects after the commit cancels only its wait. The write, its record and its
  replication go on.
- **Sync errors.** If an error happens at or after `COMMIT`, the outcome is `committed_unconfirmed`. The store's
  writer is faulted (`_faulted`) and reopened before the next write. That error is never reported as "not applied".

## 5. Commit marker and the ack rule

- **The marker.**
  - The local commit returns its marker: `(store incarnation, change id of the transaction's own last CDC row)`, read
    on the writer right after `COMMIT` and before the writer is released.
  - Every request committed in that group-commit batch shares it.
  - A newly read CDC head is never used. Bookkeeping and received applies move the head without shipping anything.
- **What confirms.** The marker is confirmed by an ack that is:
  - received on a live push link of this process;
  - for this database (tenant ids);
  - from a peer node whose region differs from this machine's and which the database's placement includes;
  - for a batch or refill sent from the same store incarnation as the marker;
  - not refused;
  - with `change_id ≥ marker`.

  Welcomes, applied HLCs, refused acks, other databases' acks, same-region acks and stored `_tb_peers.acked` values never
  confirm: retention raises the stored value over joined rows (#169). Acks from before a restore, a copy or a volume
  replacement never confirm, because the incarnation differs.
- **Any eligible remote.** The write waits on every link at once. The first eligible ack confirms it, and a link that
  drops does not end the wait while another can still confirm. The bound is **10 s**, set in code.
- **Refill.**
  - A refill's ack certifies every own group up to its `head`. `head` is read before the receiver's digest and bucket
    reads, joins are monotone, and the receiver acks only after every repair transaction committed with FULL.
  - #168's per-database order holds: no later batch of that database joins before the refill commits.
  - A failed, carried or reconnected refill confirms nothing.
  - Other databases keep flowing meanwhile.
- **Replay on another machine.** The operation record carries the executor's `region` and its marker. When the
  executor sees the confirming ack, it writes `confirmed_by: <region>` into the record, and that update replicates. A
  machine that holds the record answers a retry or lookup as follows:
  - `confirmed` if the record says so, or if this machine is itself in a placed region other than the executor's: its
    own FULL apply plus the executor's B1 make the second copy;
  - otherwise `committed_unconfirmed`.

  It never compares its own CDC ids with the executor's.

## 6. Operation identity and at-most-once execution

**Keys.**
- `Idempotency-Key: <ULID>`, which replaces today's 400 "not supported yet".
- SDKs mint the key before the first transmission and reuse it on every retry.
- A request without a key gets a server id `m-<ULID>`, returned in `TerraBase-Operation-Id`. It can be looked up, but
  a request may never present an `m-` id as its key. Only client keys can be retried.
- Keys are scoped to the database's immutable organization, project and database ids.
- A key must be a canonical ULID.
- **Admission window.** A key whose ULID time lies outside `[now − 24 h + 1 h, now + 5 min]` (server time) answers
  `410 operation_expired` before anything else. The ULID time is used only for this admission. Expiry itself uses the
  executor's commit time (section 7).

**Reservation before execution.** One write's key reservation goes through these steps:
1. **Local check, atomic.** In the database's write lane, a per-database key latch admits one attempt per key. A
   second attempt with the same key, in the same group-commit batch or not, waits for the first one's outcome.
2. **Majority round.** The machine asks the acceptor of every placed region for `Reserve(database, key, fingerprint,
   attempt)`. The acceptor of a region is the first machine of that region by name, the same rule the catalog used for
   its primary.
   - An acceptor durably records the first reservation of a key (FULL, first wins) and answers `granted`, or answers
     the existing reservation with its fingerprint and outcome.
   - Execution needs grants from a majority of the placed regions, this machine's region included.
   - Two attempts with the same key can never both reach a majority. That is the cross-region guarantee, with no
     availability promise beyond what is stated below.
3. **Atomic execution.** In one local transaction:
   - re-check the local reservation;
   - apply the write;
   - write the immutable operation record (section 7).

   A savepoint or batch rollback removes all three together.
4. **Wait** for B2 of the marker (section 5), then answer.

**If a reservation exists:**
- same fingerprint: replay the recorded outcome, or wait for it (`in_progress`);
- different fingerprint: `409 idempotency_key_reused`.

A retry never executes a key that any acceptor has reserved.

**Availability, stated plainly:**
- Writes need a majority of their placed regions reachable.
  - With three placed regions, one region can be lost and writes go on.
  - With exactly two, both are needed: losing one stops writes, with a clear 503 and no false success.
- This is the cost of at-most-once execution in a leaderless system.
- Placement option A keeps two regions as the minimum. Three is the recommended default wherever three regions serve
  (gru, sjc, fra).

**Acceptor changes and recovery.**
- **A new acceptor** (machine replaced, new volume) grants nothing until it has pulled the reservations and records of
  every reachable placed region, through a refill of `_operations`.
- **Losing reservations.** A reservation is lost for good only if every machine holding it loses its volume: two
  regions at once, outside the contract.
- **An owner that restarts** with a reservation but no record knows it never executed: execution and record commit
  together. It writes the outcome `not_executed` for that key, and it replicates.
- **Lookups.** `GET …/operations/{op}` runs the same majority round, read-only.
  - It answers `confirmed`, `committed_unconfirmed` or `rejected_recorded` from a record.
  - It answers `in_progress` from a reservation.
  - With no reservation at a majority, it records a fence (`not_executed`) at that majority. It then answers
    `404 operation_not_executed`, `authoritative: true`. A late attempt with that key can no longer reach a majority.
- **A missing record proves nothing.** A missing record on one machine, on a same-region machine, on a new volume or on
  a restored node never authorizes execution and never proves "not applied". Only a majority answer does.

## 7. The operation record

- **Storage.** A reserved, replicated collection, `_operations`, with one document per key.
  - Client reads, writes, DDL and queries cannot reach it (`CollectionName.IsReserved`).
  - The public changes feed excludes it: `AppendFeed` gets `_operations` next to `_collections`, `_indexes` and
    `_unique`, a change to R8's file that R8 makes.
  - Replication, refill, anti-entropy, backup and restore include it.
  - Reservations live in a local table, `_tb_operation_reservations`, which is not a lattice.
- **Immutable envelope.**
  - The record is written once, as one whole-document put by its executor. The only later changes are `confirmed_by`
    (set once) and expiry.
  - At most one executor exists per key (section 6), so no field-wise merge of competing envelopes can happen.
  - If two envelopes ever meet, the join is detected: a different fingerprint or executor. That is logged, counted
    (`terrabase.operations.conflicts`), and the lookup answers `409 operation_conflict`.
- **Fields:**
  - `v` (1);
  - `fingerprint`;
  - `executor` (machine, region, incarnation, marker);
  - `committed_at` (the executor's HLC physical time);
  - `status`;
  - `response`;
  - `confirmed_by`.
- **Fingerprint v1.** SHA-256 over a canonical, length-prefixed encoding of the following:
  - version 1;
  - the operation name (the OpenAPI operation id);
  - organization, project and database ids;
  - collection;
  - document id (`?id=`);
  - the normalized `Content-Type` media type, which tells merge PATCH from operation PATCH;
  - `If-Match` and `If-None-Match` if present, even though design C answers them `400 unsupported_precondition`;
  - every other query parameter the route reads;
  - the raw body.

  A key reused with any of these changed answers `409 idempotency_key_reused`.
- **Replay.**
  - The recorded response is replayed with `TerraBase-Replayed: true`: status, `Location`, `ETag`, generated ids,
    versions, `offset` and `durability`.
  - The materialized `document` is not stored. A replayed PATCH body has every other field and `"document": null`,
    and the SDK reads the document again if it needs it.
  - A committed conditional or constrained request replays its original outcome. It is never evaluated again against
    today's state.
  - Terminal `409`/`412` results after reservation are recorded and replayed.
- **Permissions.**
  - A retry is a full request: authenticated and authorized again, for the same scope as the original route.
  - A lookup needs the write scope of the database the record belongs to.
  - Records never cross databases.
- **Retention.** A record is kept at least **24 h + 1 h** after `committed_at`. The extra hour covers clock skew, since
  `committed_at` is HLC time.
  - Any machine of a placed region may expire a record once it is old enough. The expiry is a lattice delete, and its
    tombstone dominates the older put, so a stale replica's record cannot come back.
  - Tombstones stay until garbage collection (R7), whose horizon is at least the retention.
  - Cleanup runs in the background, at most 1,024 records and 2 MiB per transaction.
  - Records count toward the database's storage quota.
- **Limits.**
  - The record rides in the request's own transaction and change group. Group bounds become 1,024 documents plus one
    operation record, and the receiver's decode bound follows (versioned).
  - A 1,024-document transaction that is valid today stays valid.
  - The record's bytes count toward the 2 MiB group bound. A request that is already at the bound is refused before
    reservation, as today.

## 8. Placement (option A) and availability

- **Every database of a cell is placed in at least two distinct regions.**
  - A create without `replica_regions` places replicas in every other serving region from the membership.
  - With none listed, it answers `503 remote_region_unavailable`.
  - A placement with fewer than two regions answers `400 placement_needs_two_regions`, on create and on update.
- **Existing single-region databases** are never relocated, and their data is never dropped. Their writes fail clearly
  (`503 remote_region_unavailable`, naming the single-region placement) until an explicit placement update adds a
  region. Their writes then succeed once the added region has refilled and acknowledges them.
- **Placement changes keep surviving copies.**
  - Recovery, refill and placement changes never erase the only surviving acknowledged copy before a replacement is
    durable. R7's copy-refill must compare before wiping.
  - Streams do not forward remote-origin groups. After the loss of an origin, its writes that one other region holds
    reach the remaining regions through anti-entropy rounds (two differing rounds, 60 s apart).
- **Availability after a region loss:**
  - writes need a majority of placed regions (section 6) and at least two reachable placed regions;
  - a placement of exactly two cannot keep writing when one is lost;
  - three regions can.

## 9. Evidence required before the contract is claimed

**Fault model.**
- The H4 shim models only unsynced log bytes (`DropUnsynced`). It does not model unsynced writes to the database file,
  or log generations after a checkpoint reset.
- It is extended, still test-only: a snapshot of each database file and log at its last successful sync, restored on a
  simulated power loss.

**Durability barriers.** For both B1 and B2, on the shipped Turso binary:
- a remote sync held or failed (`EIO`, `ENOSPC`), then a crash before the ack: no success is answered;
- after an ack or a success: power loss, then restart. The data **and the operation record** survive;
- the same across a checkpoint and log reset, and across reuse of a pooled connection;
- mutants that must fail: receiver at NORMAL, ack before the sync, origin at NORMAL.

**Acks and ordering.**
- All six directions among gru, sjc and fra.
- Acks that must never confirm: same-region, unplaced, refused, wrong-database, stale-incarnation.
- One remote held while another confirms.
- Either remote of a three-region placement killed, with writes going on through the survivor.
- Full and asymmetric partitions: never a false success.
- Grouped writes, receiver batch splitting, bookkeeping-only CDC heads, and replay on a receiving machine.
- Held, failed and reconnected refills, with another database flowing.
- Mutants: success before the ack, missing refill order.

**Idempotency.**
- Lose the ack and the whole response, separately.
- Retry the same key concurrently: on the same machine, on another machine of the region, and in a different region
  before the record arrives.
- POST-generated ids, `inc`, `append` and multi-document transactions execute once.
- A changed body, `?id=`, `Content-Type` or conditional header answers `409`.
- A committed conditional request replays after state changes.
- First-attempt `409`/`412`, generated ids, `Location` and `ETag`.
- Savepoint and whole-batch rollback.
- Mutant: deduplication removed.

**Reconciliation and retention.**
- Restart either side in the middle of an ack.
- Lose the accepting region, rejoin, reconcile.
- Lookup and retry on a surviving replica, with no reliance on new writes.
- A missing record elsewhere is never authoritative.
- Expiry just before, at and after its time, with clock jumps and skew.
- The origin unavailable during cleanup; a stale replica refill; tombstone garbage collection.
- A non-empty restore that carries valid records.
- No operation metadata in the changes feed or through an unauthorized lookup, and no duplicate user event on a retry.

**Scope and resources.**
- Every in-scope route.
- Placement create, update and move.
- Existing single-region databases failing clearly.
- Default region discovery, and a membership directory failure with no local fallback.
- Maximum-size transactions with the record.
- Waiter and byte bounds; cleanup under a stalled remote; storage and memory pressure.
- Cancellation after commit, while unrelated databases progress.

**Where the tests run.**
- Fast: the critical request, retry and failure paths.
- Slow and E2E: power loss, the six-direction matrix and load.
- All integration tests, run in parallel.

## 10. Resources and admission

- Waiting requests hold no storage session or lock.
- They are bounded per machine and per database, with the numbers in code. The starting numbers are 4,096 and 256,
  measured and adjusted with evidence.
- A response body held for a replay is bounded by the response size limit and counted in the memory budget.
- Over a bound, a write is refused **before** reservation with `503 confirmation_busy`. It is not applied.
- A cancelled request frees its waiter, and replication and reconciliation go on.
- At 100 writes/s, 24 h of records is 8.64 million rows plus tombstones. Their bytes, disk and quota cost are measured
  before the contract is announced.

## 11. Write latency and availability budget: measured, not promised

A success answer is released only after every barrier below. No optimization may answer earlier. The ones that keep the
barriers, such as one reservation round shared by a group-commit batch, or the local and remote reservation syncs
running in parallel, are possible later work, not part of this design.

**Components of one keyed write, in order on the success path:**

| # | Component | What it costs | Measured today |
| --- | --- | --- | --- |
| 1 | Client to the accepting machine (regional ingress) and back, at the end | One client round trip | No |
| 2 | Validation, authorization, key latch, fingerprint | Local CPU | No |
| 3 | **Majority reservation round** (section 6): `Reserve` to every placed region's acceptor at once. Each acceptor syncs its reservation (FULL) before it answers. The round ends at the first majority. | One **cross-region round trip** to the nearest other placed region's acceptor, plus that acceptor's sync. Plus the own region's acceptor sync: local, or one in-region round trip if another machine is the acceptor. **This is an extra round trip that unkeyed writes and design C do not have.** | No |
| 4 | Local execution and **B1**: apply, record, `COMMIT` with FULL. The group-commit batch shares one log sync. | One local log sync, plus the apply | No (B1 correctness proven; no timing) |
| 5 | Shipping: the sender wakes on the commit signal, reads and encodes the batch, and sends it on the live stream | Local CPU, plus one-way network | No |
| 6 | **B2**: the receiver joins with FULL, syncs its log, then acks | One remote log sync, plus the apply, plus the ack's way back. Steps 5 and 6 together are one **cross-region round trip** to the nearest eligible placed region | No (B2 correctness proven; no timing) |
| 7 | Confirmation and answer | The waiter wakes; the response is written. Writing `confirmed_by` happens off the path. | No |

**Expected shape, not a promise:**
- latency ≈ client round trip + **2 × the round trip to the nearest other placed region** + 3 log syncs (own-region
  acceptor, local commit, remote apply) + 1 remote acceptor sync + processing;
- with three placed regions the first majority and the first eligible ack both come from the nearest other region, so
  the farthest region adds nothing while healthy;
- the 10 s bound covers steps 3 to 7, and expiry answers `write_outcome_unknown`.

**Availability budget:**
- **Step 3** needs a majority of placed regions.
- **Step 6** needs one placed region other than the accepting one.
- **Three placed regions** tolerate one region down or partitioned, at the cost of a slower round trip if the nearest
  one is down.
- **Two placed regions** tolerate none: writes answer `503`, never a false success.

**Measured today:** no latency, throughput or sync-time component of the new path. The B1 and B2 power-loss tests
prove correctness only. Timings from a development host or local processes are never presented as WAN figures.

**Measurement plan (before the contract or any latency is announced; results in `benchmark/bench/<date>-remote-durable-writes.md`):**
- **Where.**
  - On a planned isolated OCI acceptance target, verified serving gru, sjc and fra and placed in all three regions. One
    load client runs in each region, against its own region.
  - Degraded-link cases run separately on the local three-process topology
    (`RegionProcesses`), with its link proxy adding fixed delays. Those are labelled local, never WAN.
- **Before and after.**
  - *Before:* the current release path (local commit only, NORMAL), same app, same workload.
  - *After:* each delivery step separately (B1 FULL only; then the wait; then the reservation round), so each component's
    cost is attributed.
- **Workload.**
  - Document `PUT` of 1 KiB, 16 KiB and 256 KiB.
  - `PATCH inc`.
  - Transactions of 10 and 1,024 documents.
  - Concurrency 1, 16 and 64.
  - 10 minutes per cell, after a warm-up.
- **Scenarios.**
  - All regions healthy.
  - One remote region's machine stopped: writes go on through the majority.
  - The nearest remote stopped: latency moves to the next region.
  - Two regions stopped: every write fails clearly.
  - Local topology only: one remote link delayed by 50, 150 and 300 ms.
- **Recorded:**
  - p50, p95, p99 and p99.9 end to end;
  - each component's histogram: the reservation round, the local commit including its sync, the remote confirmation
    wait, and the receiver's apply and sync. These are instruments added with the implementation, on the
    `TerraScale.TerraBase` meter, labelled only by peer region;
  - throughput;
  - errors by code;
  - RSS and managed heap;
  - log and checkpoint stall time;
  - disk growth, including `_operations` bytes and rows.
- **Machine sizes and commit SHAs** are recorded with every table.

## 12. API contract for the SDKs (ts-dotnet first, separate repository)

- **Request:** an optional `Idempotency-Key: <ULID>` on every mutating request. SDKs always send one, minted before the
  first transmission and reused on retries.
- **Response headers:**
  - `TerraBase-Operation-Id` on every mutating answer;
  - `TerraBase-Replayed: true` on a replay.
- **Body:** `durability: "remote_durable"` on success. A cell of one, which is development only, answers `local_durable`.
- **Errors**, all with the standard error body:
  - `503 remote_region_unavailable`: not applied.
  - `503 operation_quorum_unavailable`: not executed by this attempt. Retry with the same key.
  - `503 confirmation_busy`: not applied.
  - `503 write_outcome_unknown` with `operation_id`, `region`, `machine`, `expires_at`: applied locally, not confirmed.
    Retry with the same key and request, or look it up.
  - `409 idempotency_key_reused`.
  - `409 operation_conflict`.
  - `410 operation_expired`.
  - `400 placement_needs_two_regions`.
- **Lookup:** `GET /v1/projects/{project}/databases/{database}/operations/{operation}`.
  - It waits within the bound, then answers `200` with `{operation_id, status, durability, response, executed_by
    {machine, region}, committed_at, expires_at}`.
  - Or `404` with `{code, authoritative}`, or `410`.
  - It is built with TerraScale.Endpoints, with source-generated OpenAPI and Scalar schemas.
- **SDK rules:**
  - `write_outcome_unknown`, `operation_quorum_unavailable` and `404 authoritative: false` are distinct typed outcomes.
    They are never success and never failure.
  - Only `operation_not_executed` and `remote_region_unavailable` mean "not applied".

## 13. Not guaranteed

- **Consistency models.** Neither global serializability nor cross-region linearizability is promised. Neither is
  read-your-writes on a machine other than the two that hold the copies.
- **Superseding.** A durable write can still be superseded by a concurrent one (last writer wins).
- **Two regions lost at once.** The loss of both durable copies: two regions' volumes at the same time.
- **Requests without a key.** Execution at most once for a request sent without `Idempotency-Key`. Its `m-` id serves
  lookups only.
- **Latency.** Any latency figure until section 11's measurements exist.

## 14. Delivery (separate PRs against main)

| # | PR | Status |
| --- | --- | --- |
| 1 | B2 proof: `ReplicatedApplyPowerLossTests` with its mutant | Evidence exists (`4adf8424`) |
| 2 | B1: every write transaction FULL, with its power-loss test (fails at NORMAL) | Evidence exists (`83291845`) |
| 3 | The H4 extension (database file, checkpoint/reset, `EIO`/`ENOSPC`); B1 and B2 re-proven under it | |
| 4 | Placement option A | |
| 5 | Commit marker, ack rule and wait: documents and transactions first, with `local_durable` kept on uncovered routes | |
| 6 | Reservations, records, replay, lookup and fences | |
| 7 | The remaining routes | Catalog routes after R10a's section 15 |
| 8 | Measurements (section 11), then the announcement | |

Each PR lists the routes still uncovered, its fail-first tests and its mutants. The `api.md` and design-section
updates land with the PR that changes behaviour.
