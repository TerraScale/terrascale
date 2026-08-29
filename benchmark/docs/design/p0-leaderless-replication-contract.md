# P0 contract: leaderless replication, acknowledgement, and repair

> Superseded implementation contract. ADR 0019 and the workspace `GOAL.md`
> now define native `CommitId`, Partition-scoped DVVs, RF=2/2
> `RegionalDurable`, all-selected-Region `GlobalDurable`, anti-entropy, and
> separate causal and materialized Merkle verification. Do not extend the
> Loro-era rules below.

Status: frozen by the P0 contract pass, 2026-08-28. Governs P2
implementation and AC-10 qualification. Parents: ADR 0018 (leaderless),
ADR 0014 D-1/D-5/D-6 (active-active, quorums, merge), charter
PD-2026-08-28-07. This contract supersedes the leader-per-range sections
of `docs/design/w4-transfer-replication-contract.md`; that document
remains the record of the superseded implementation.

## 1. Symmetric replica-set placement

- A shard's replica set is a versioned set of eligible voting Members per
  Region. Version = `ReplicaSetEpoch`, a monotonically increasing
  configuration number assigned by the accepted configuration epoch
  (§7), never by a Member acting alone.
- Every active Member of a replica set has identical write, read,
  replication, repair, and recovery authority. No Member, endpoint, or
  process holds shard ownership, a write lease, a term, or a primary
  role. The word "leader" names nothing in code, config, logs, protocol,
  or receipts for the active data path.
- Placement rule: each Region that hosts the shard contributes
  `replication_factor_region` voting Members (default 2); the cluster's
  total replica set is the union. Backup clusters are appended as
  non-voting replication targets (ADR 0017 D-5) and never enter the
  voting set.
- A replica set is *eligible* at a Member when the Member's locally
  verified configuration epoch names that Member in the current epoch's
  replica set for the shard.

## 2. Operation identity and idempotency

- Every mutation carries a stable `OperationId` (existing W3 identity:
  tenant, collection, operation discriminator, uniqueness material).
  Retry through any Member MUST reuse the identical `OperationId`.
- Each replica applies an operation at most once per durable operation
  ledger identity; duplicate delivery resolves to the recorded outcome
  (`Committed` receipt or the recorded rejection), never a second apply.
- The ledger entry binds `(tenant, OperationId) -> write-set digest`.
  Same id with a different digest is `OperationIdReuse` (existing W3
  rule) at any replica — the invariant that makes any-Member retry safe.

## 3. Leaderless write flow

1. The client sends a typed mutation to any active Member (the
   *request coordinator* for that operation only).
2. The coordinator validates the mutation (schema admission per the
   schema-merge contract), resolves the current epoch's replica set, and
   assigns/reuses the `OperationId`.
3. The coordinator fans the encoded operation to every eligible voting
   replica of the shard in parallel over authenticated engine links.
4. Each replica durably applies the operation through the local commit
   batch (WAL + LSM ordered batch per ADR 0016 D-2), records the ledger
   binding, and acknowledges with `(MemberId, StorageSequence,
   durability-mode)`.
5. The coordinator returns success only when the requested
   acknowledgement level is met:
   - `RegionalQuorum`: durable acceptance by a majority of eligible
     voting Members in the accepting Region. Default and minimum.
   - `GlobalQuorum`: `RegionalQuorum` in a majority of eligible active
     Regions, including the accepting Region.
   - `BackupAck` (additional, optional): after the requested regional or
     global level succeeds, durable apply by 2 of the configured backup
     cluster's 3 HDD Members (ADR 0017 D-6).
6. The receipt names every contributing Member and Region, the achieved
   level, the accepting Member, and the local durability facts. A local
   commit alone is never client success.

### Coordinator failure

- If the coordinator fails before step 5 completes, the operation is in
  `Unknown` outcome at the client: partially or fully durable at
  replicas, never rolled back by another authority.
- Recovery is retry: the client resubmits the same payload with the same
  `OperationId` to any other active Member. Idempotency (§2) makes the
  retry converge to one apply; the retrying coordinator's fan-out
  deduplicates against each replica's ledger.
- No election, lease transfer, or redirect occurs at any point. A
  coordinator that returns an error mid-flight MUST NOT emit a
  durability claim stronger than what step 5 observed.

### Failure classes and their observable results

| Fault | Required behavior |
| --- | --- |
| replica crashes before apply | no ack from it; quorum proceeds without it |
| replica acks then crashes | operation stays committed (ledger replay on restart) |
| coordinator crashes pre-quorum | client retry through another Member; single apply |
| coordinator crashes post-quorum, pre-response | client retry returns the recorded outcome |
| duplicate delivery | ledger dedup; identical second receipt |
| reordered delivery | causal metadata orders; apply is order-independent per Loro |
| partitioned minority Region | `RegionalQuorum` in accepting Region unaffected; `GlobalQuorum` may refuse |
| partitioned majority Region loss after ack | receipt already durable; rejoin catch-up via anti-entropy |

## 4. Leaderless read flow

- Any active Member accepts a read. The requested consistency level:
  - `Local`: the Member's own materialized state (fastest; may lag).
  - `RegionalQuorumRead` (name frozen in P4): read a majority of the
    Region's eligible voting Members, merge causally compatible
    results, prefer the merged Loro frontier.
  - `GlobalQuorumRead`: regional quorum reads in a majority of eligible
    active Regions.
- Divergence observed during a quorum read queues a bounded read repair
  (§6). Reads never block on repair completion.
- Backup-cluster reads are last-resort only (backup-last routing
  contract) and always return position and lag.

## 5. Anti-entropy

- Every replica tracks per-peer causal frontiers (per-document Loro
  version vectors packed into shard-level frontier summaries).
- Anti-entropy runs as a background exchange of bounded summaries over
  the same authenticated links; a difference schedules a delta or
  snapshot transfer for the affected documents. It is
  ownership-agnostic: any replica may initiate with any peer.
- Rejoin: a returning Member exchanges manifest + frontier, then catches
  up through deltas, falling back to snapshot transfer when its retained
  history floor is passed (existing `BehindRetainedFloor` shape).
- Bounded: exchange frequency, bytes in flight, and concurrent repair
  streams are budgeted per tenant; anti-entropy yields to foreground
  traffic.

## 6. Read repair

- When a quorum read observes a replica missing operations others have,
  the reader queues the missing delta to that replica (bounded queue,
  coalesced per document, deduplicated).
- Repair is never required for the read's own result: the merged result
  is returned immediately.
- Repair traffic counts against the same per-tenant budgets as
  anti-entropy. A repair that fails retries with backoff and otherwise
  waits for anti-entropy; it never surfaces as a client error.

## 7. Membership and replica-set epochs

- The cluster configuration (membership, Regions, replica sets, shard
  map) is a versioned object: `ConfigEpoch n` names the full voting
  topology. It becomes effective only after overlapping old and new
  regional quorums durably accept it (ADR 0014 D-4; any active Member
  may propose).
- A write acknowledged under epoch `n` is never lost by a change to
  epoch `n+1`: the overlap rule guarantees the new quorum intersects the
  old. Replicas not in `n+1` retain data and history for
  `retirement-grace` (default 7 days) before local reclamation.
- Quorum math is always evaluated against the epoch active at the
  coordinator when the operation was accepted; receipts record that
  epoch.
- NATS advertises accepted epochs (subject map in the NATS control
  contract) but is never the source of truth: a Member acts on an epoch
  only after durable local verification.

## 8. Minimum active-active convergence scenarios (AC-10 seed)

These are the required qualification scenarios, each producing a
secret-safe Replication Receipt:

1. One Region, one Member: `RegionalQuorum` write/read, restart receipt.
2. One Region, multiple Members: quorum under member delay; convergence.
3. Write enters through every active Member in rotation: identical
   validation, fan-out, acknowledgement; no redirect.
4. Coordinator fails mid-write (pre- and post-local-durability): retry
   through another Member completes without election or duplicate apply.
5. Replica-set epoch changes during traffic: overlapping quorums, no
   split replica set, no acknowledged write lost.
6. Concurrent same-document writes through different Members: Loro
   merge, deterministic scalar winner, identical materialized state on
   all replicas, losers in shadow history.
7. Two active Regions, healthy links: regional success; `GlobalQuorum`
   requires both; convergence and read visibility.
8. Three active Regions: `GlobalQuorum` = 2 of 3 including accepting.
9. Member loss before acknowledgement: explicit error or retry; no
   silent success.
10. Region loss after acknowledged write: receipt states durable
    visibility and recovery limit.
11. Delayed/reordered/duplicated replication: idempotent replay,
    eventual contract outcome.
12. Member rejoin: manifest/frontier check, catch-up, isolation intact.
13. Delete-wins and shadow-delete: concurrent delete + update converges
    deleted with loser in shadow history; restore creates an
    incarnation.
14. `BackupAck` requested: level ordering, 2-of-3 HDD durability,
    timeout partial outcome, idempotent retry.
15. Tenant-isolation probe: cross-tenant operation denied and audited.

## 9. Honest limits (recorded, not hidden)

- The current `src/replication/` implementation is leader-per-range and
  fails this contract; it is replaced by P2 (ADR 0018 D-8). Until then,
  every scenario above is `unresolved`.
- Default read consistency level, active replication factor per Region,
  sloppy-quorum/hinted-handoff policy, read-repair timing, and
  anti-entropy summary structure remain open decisions with named
  owners (charter unresolved-decisions list).
