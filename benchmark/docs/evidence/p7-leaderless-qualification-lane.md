# P7 leaderless qualification lane (in-process receipt)

Status: recorded 2026-08-29 at develop post-`cd710db`. The lane is
`crates/terrabase-cluster/tests/qualification.rs` — 4 rows, all green
(`cargo test -p terrabase-cluster --test qualification`).

## What is proven (in-process, against real durable members)

- **AC-2 (leaderless operation)**: a write accepted at one member and
  the SAME operation identity retried at another member — the
  coordinator-failure recovery path — completes idempotently (the
  receipt answers, the value applies once, every member converges),
  and a later write moves the document on. No leader, lease, or
  redirect exists anywhere in the coordinator path (by construction:
  the API takes the accepting member, nothing appoints one).
- **AC-3 (quorum durability)**: `RegionalQuorum`, `GlobalQuorum`, and
  `BackupAck` answer distinct receipts with contributors; the backup
  level's fault variant names PARTIAL while the active write stands;
  backup members are structurally absent from every voter list (§6
  scenario 5); a disjoint membership step refuses typed — quorum
  intersection is enforced, not assumed.
- **AC-4 (CRDT convergence)**: concurrent writes from two Regions to
  one document converge to a single value across every member;
  duplicated delivery replays; the whole cluster restarts onto its
  converged state; a member that missed the writes rejoins through the
  epoch step and eager anti-entropy catch-up and serves the converged
  state.
- **Backup contract §6 scenario 9 (restore)**: a converged backup set
  restores into a NEW active cluster identity at a fresh root (a
  non-empty target refuses); the restored cluster serves the backup's
  converged values; the restored identity's content hash differs from
  the backup's; and the SOURCE backup remains mode `backup`, terminal,
  unchanged — no promotion path exists, and restore is not one.

## Honest limits

- This is the in-process lane: coordinator "failure" is modeled by the
  idempotent-retry path (the process model has no crash injection at
  the cluster seam; the composed plane's crash/reorder/partition rows
  live in `terrabase-replication-core`'s fault tests).
- The charter's isolated-machine benchmark lanes (TerraBase.Benchmark
  vs PostgreSQL, cold and backup gates) are NOT run here: this host is
  a shared development machine, and the standing verdict remains
  `PostgreSQL-parity-or-better: inconclusive` under the SE-15
  discipline.
- Live NATS and live object-store rows remain unrun (no broker, no S3
  endpoint on this host).
