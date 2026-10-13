# 0013a: A write attempt answers success only after two independently durable regional copies

- **Status: proposed** (2026-10-13).
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
  - Requests that group commit puts in one storage transaction share one marker, because they are one transaction.
- **Its local durable commit (B1).** Its transaction committed with `synchronous = FULL`, and `CommitAsync` returned
  without error. Evidence: #181, `83291845`.
- **An eligible acknowledgement (B2).** An ack confirms the marker only when it is:
  - received on a live push link of this process;
  - for the same database;
  - from a peer node of a region other than this machine's, which the database's placement includes;
  - for groups sent from the **same store incarnation** as the marker;
  - not refused;
  - with `change_id ≥ marker`.

  The receiver acks only after every transaction that joined those groups committed with FULL. That includes every part
  of a batch split into several transactions, and every repair of a completed refill. Evidence: #180, `4adf8424`.
- **What never confirms:**
  - a welcome;
  - an applied HLC;
  - a refused ack;
  - another database's ack;
  - a same-region ack;
  - a stored or retention-raised `_tb_peers.acked`;
  - an ack from before a restore, copy or volume replacement (another incarnation);
  - an intermediate anti-entropy repair page.
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
  `machine`. A caller reconciles by reading the state it wrote before deciding anything.
  - Rewriting a whole document with `PUT` converges by value.
  - `PATCH` with `inc`/`append`/`add`/`remove`, a `POST` without a client-chosen id, and a transaction must **not** be
    resent without that check, because a second attempt can apply twice.
- **No `Idempotency-Key` under 0013a.** It stays refused (`400`, "not supported yet"), so no client can believe it is
  protected. Operation ids and exactly-once retries come only with 0013b.

## 4. Lost acks, reconnects and refills

- **Any eligible ack, on any link.**
  - The wait listens on every live link of the database's placed other-region peers, and the first eligible ack
    confirms. One link dropping does not end the wait while another can still confirm.
  - A link that reconnects starts again from its stored ack. Its later acks count if they are for the same incarnation.
- **A lost ack is re-sent, not re-applied.** If the ack itself is lost, the sender resends from its last ack. The
  receiver joins idempotently, and the next ack covers the marker.
- **Refills.**
  - A refill's ack certifies its `head` only after the whole refill committed. `head` is read before the receiver's
    digest and bucket reads, joins are monotone, and every repair transaction is FULL.
  - #168's order holds: a database's later batches join only after its refill commits.
  - A failed, carried or reconnected refill confirms nothing.
- **The bound.** If none of these confirms the marker within 10 s, the answer is outcome unknown. The write keeps
  replicating.

## 5. Placement and availability

- **Option A.** Every database of a cell is placed in at least two regions.
  - A create without `replica_regions` places replicas in every other serving region of the membership. With none,
    it answers `503 remote_region_unavailable`.
  - Fewer than two regions answers `400 placement_needs_two_regions`, on create and on update.
  - Existing single-region databases are never moved, and their data is never dropped. Their writes fail clearly
    (`503 remote_region_unavailable`) until an explicit placement update adds a region.
- **Availability.**
  - A write needs its own region plus one other placed region reachable.
  - With three placed regions, writes go on with one region lost.
  - With exactly two, losing either stops writes clearly, and never with a false success.
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
- **Refill:** held, failed and reconnected refills confirm nothing until a refill completes, while another database
  flows.
- **Lost ack, reconnect, restart:** a dropped ack confirmed by the next one; the sender restarts in the middle of a wait.
- **Cancellation:** after commit, the write still replicates.
- **Bounds:** waiter bounds refuse before execution.
- **The SDK contract:** the error codes and headers, and `Idempotency-Key` still refused.
- **Placement:** delivery 4 (#pending), with fail-first and mutants.
