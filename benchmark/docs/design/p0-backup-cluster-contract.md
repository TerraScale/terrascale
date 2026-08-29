# P0 contract: immutable backup clusters and backup-last routing

> Historical P0 implementation contract. Backup remains immutable, read-only,
> non-voting, and optional. Current placement, acknowledgement, retention, and
> qualification rules live in the workspace `GOAL.md`; the old phase and
> fixed-shape language below does not override it.

Status: frozen by the P0 contract pass, 2026-08-28. Governs P2/P5
implementation and AC-10/AC-11 backup evidence. Parents: ADR 0017
(D-4..D-8), ADR 0018 D-7.

## 1. Immutable cluster identity

- A cluster records `mode: active | backup` in its immutable cluster
  identity at creation. The identity document is content-hashed,
  quorum-pinned, and verified at every Member start and join.
- `backup` is terminal. There is no promote, convert, re-role, or
  mode-change operation anywhere: no TBEP operation, no administration
  command, no recovery artifact, no operator path. Requests fail with a
  typed `BackupClusterImmutable` error (distinct from
  `BackupClusterReadOnly` so tooling can distinguish mode errors from
  write refusals).
- Restoring service from backup data creates a NEW active cluster
  identity that ingests the backup's verified export; the backup
  cluster remains a backup.
- A backup Member never mints a local Writer identity, never originates
  a CRDT operation, and never enters a voting set or quorum computation
  (active quorums are computed over active-cluster Members only).

## 2. Ingress and storage

- A backup accepts authenticated replication, snapshot, catch-up, and
  tier data from configured source clusters (mTLS member links;
  sources are named in the backup's configuration, not discovered
  ad hoc).
- Storage: three bulk-HDD Members, RF=3, quorum=2. No NVMe requirement.
  The write path favors large sequential batches, immutable segments,
  and bounded background compaction. Random-read latency is explicitly
  not a target.
- Retention: complete verified current copy; the same configurable
  recent CRDT history as the active database; shadow history 30 days by
  default (per-collection configurable). Older causally stable
  operations compact into checkpoints preserving current state, causal
  frontiers, retention pins, and restore data.
- A backup never demotes its sole fallback copy into the active
  cluster's S3 tier.

## 3. Write refusal

Every client database mutation — documents, transactions,
compare-exchange, schema changes, collections, indexes, and any other
write — returns the typed error `BackupClusterReadOnly` with the
cluster's replication position attached. Locally originated CRDT
operations are refused before durable apply. Administration that would
mutate database state is refused identically; monitoring,
receipt-reading, and replication-source administration remain allowed.

## 4. `BackupAck` partial-outcome rules

- Ordering: the requested regional or global level MUST succeed first;
  only then does the coordinator wait for backup durability (durable
  apply by 2 of the backup's 3 Members).
- Timeout: the active write is already committed; the receipt reports a
  partial outcome — `committed: true, backup_applied: {members},
  backup_level: partial` — and the same `OperationId` makes retry safe
  and idempotent.
- A `BackupAck` timeout NEVER erases, rolls back, or downgrades the
  committed write.
- Receipts name the backup cluster, contributing Members, and the last
  applied causal frontier.

## 5. Backup-last routing

Read routing order for cold and fallback reads is frozen:

1. Local active NVMe (resident or cache).
2. Home S3 Region.
3. Eligible active Regions over QUIC.
4. Backup cluster — last resort only.

- The backup read returns the latest applied value plus exact
  replication position and lag; a client can never mistake a stale
  result for a current one.
- Minimum-frontier reads: the caller may require `frontier >= F`; the
  backup waits within the deadline to reach `F` or fails with
  `FrontierUnreachable` — it never returns older state silently.
- Normal routing never touches the backup while steps 1-3 can serve the
  data (enforced by routing tests, per-tenant read budgets, and
  receipts naming the chosen source).

## 6. Qualification scenarios (AC-10/AC-11 seed)

1. Three-HDD replication ingress with two-Member durable apply.
2. `BackupAck` success, one-Member loss, timeout-after-commit partial
   outcome, idempotent retry, backup restart.
3. Complete fallback retention and latest-applied reads with position
   and lag.
4. Minimum-frontier wait and fail behavior.
5. Non-voting proof: backup Members present in no quorum computation.
6. Immutable mode across restart and restore: no path promotes it.
7. History compaction preserves current state, frontier, 30-day shadow,
   and pins.
8. Refusal of every client mutation class with `BackupClusterReadOnly`.
9. Restore from backup produces a new active cluster identity; the
   backup remains backup.

## 7. Honest limits

- The current tree has no backup-cluster mode, `BackupAck`,
  `BackupClusterReadOnly`, or bulk-HDD Member type; all scenarios are
  `unresolved` until P2/P5 deliver them.
- Existing `src/backup/` bundle/restore machinery (BK waves) remains the
  snapshot/import substrate the backup cluster reuses; it is not itself
  a backup cluster.
