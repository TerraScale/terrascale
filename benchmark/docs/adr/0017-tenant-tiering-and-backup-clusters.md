---
status: accepted
---

# ADR 0017: Tenant-aware tiering, single-Region S3, cold-read fallback, and immutable backup clusters

Parent record of charter PD-2026-08-28-06, 2026-08-28, accepted on user
instruction.

Owner: the parent fleet orchestrator, on the workspace owner's direction
that one S3 Region is sufficient, tiering must protect a multi-tenant
NVMe pool before it fills, cold reads must use remote fallback, and
TerraBaseV2 needs a read-only backup cluster mode.

## Context

Extends ADR 0016's tier model with placement, fairness, failure, and
backup semantics. Supersedes any fixed node-wide fullness default, any
requirement for two S3 Region copies, any backup quorum vote, and any
backup promotion path.

## Decisions

### D-1 — one S3 Region

One verified S3 Region satisfies cold-object placement. TerraBaseV2 does
not require a second S3 Region copy before evicting an NVMe range. The
cold-tier manifest records the home Region, endpoint identity, object
key, checksum, encryption identity, publication epoch, and any verified
remote fallback sources. Before all active NVMe copies are reclaimed,
the manifest names at least one verified active-Region or backup-cluster
fallback; that fallback need not be a second S3 copy. S3 remains outside
every write acknowledgement and is not a backup by itself.

### D-2 — adaptive multi-tenant mover

The Storage Tier Mover inherits policy from cluster to tenant to
database to collection. It estimates each tenant's working set, growth,
time to exhaust its Tier 1 allocation, decayed read and write heat, scan
locality, refetch cost, and the node reserve needed for WAL, foreground
writes, manifests, and recovery. It starts before the node fills. A
tenant above its fair share supplies its own cold candidates first and
cannot evict another tenant's reserved hot working set. Weighted fair
queues, partitioned cache budgets, admission control, and tenant-scoped
backpressure prevent noisy neighbors. Every choice is explainable and
receipt-backed; no opaque model silently moves data.

### D-3 — cold-read fallback

When the home S3 Region cannot serve a range, the Member uses verified
topology to try every eligible active Region over authenticated QUIC
before it contacts a backup cluster — and only after the local cache,
home S3, and all active sources fail. The reader verifies manifest
identity and checksum before publishing the NVMe cache entry. NATS may
locate a source but never transports the range. A typed availability
error appears only after all sources and bounded retries fail.

### D-4 — immutable `backup` cluster mode

The cluster identity records `active` or `backup` at creation. A
`backup` identity can never become `active`; there is no promotion,
conversion, or mode-change operation. Restoring from its data creates a
different active cluster identity and leaves the backup unchanged. A
backup accepts authenticated replication, snapshots, catch-up, and tier
data from configured source clusters, applies incoming CRDT operations
without originating operations, and rejects all client database writes
with `BackupClusterReadOnly`.

### D-5 — bulk-HDD fallback storage

Each backup cluster has three bulk-HDD Members, RF=3, quorum=2. It does
not require NVMe. Its storage path favors sequential replication
batches, large immutable segments, capacity, and bounded background
compaction over random-read latency. It retains a complete verified copy
and does not demote its sole fallback copy into the active cluster's S3
tier. Normal routing never reads from a backup while the local active
path, home S3, or any active Region can serve the data.

### D-6 — non-voting and `BackupAck`

Backup clusters never contribute to `RegionalQuorum` or `GlobalQuorum`.
Optional `BackupAck` succeeds only after the requested regional or
global level and durable apply by two of the configured backup cluster's
three HDD Members. Timeout reports that the active write may already be
committed and supports idempotent retry with the same operation
identity.

### D-7 — last-resort backup reads

A backup returns the latest applied value with its exact replication
position and lag. A caller may require a minimum causal frontier; the
backup waits within the deadline or fails without returning a value
older than that frontier.

### D-8 — backup history and compaction

The backup retains a complete verified current copy, shadow history for
30 days by default with per-collection configuration, and the same
configurable recent CRDT history as the active database. It compacts
older causally stable operations into checkpoints while preserving
current state, causal frontiers, retention pins, and restore data.

## Consequences

P1's mover design carries the D-2 fairness and forecasting contract; the
backup-cluster storage path lands with P2/P5 replication work. The
current tree has no tier manifest, mover, cold-read fallback, backup
mode, or `BackupAck` — all are recorded gaps until their phases deliver.
