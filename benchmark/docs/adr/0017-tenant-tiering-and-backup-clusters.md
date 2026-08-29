---
status: accepted
---

# Tenant-aware tiering and immutable backup clusters

## Cold-tier placement and fairness

One verified S3 Region satisfies cold-object placement. Before every active
NVMe copy of a cold range is reclaimed, its manifest names the home object,
checksum, encryption identity, publication epoch, and at least one verified
active-Region or backup-cluster fallback. The fallback need not be a second S3
copy. S3 remains outside every write acknowledgment and is not a backup.

The Storage Tier Mover inherits policy from deployment to tenant to database to
collection. It estimates working set, growth, time to exhaust the Tier 1 share,
decayed heat, scan locality, refetch cost, and the reserve needed for WAL,
foreground writes, manifests, and recovery. It starts before exhaustion.

A tenant above its fair share supplies its own cold candidates first and cannot
evict another tenant's reserved hot working set. Weighted fair queues,
partitioned cache budgets, admission control, and tenant-scoped backpressure
protect other tenants. Every move has an explainable plan, durable progress
under ADR 0028, and a receipt; no opaque model silently relocates data.

## Cold-read fallback

After the local cache and home S3 fail, the Member tries every eligible active
Region over authenticated QUIC and then the configured backup cluster. NATS may
locate a source but never transports the range. The reader authenticates the
source and verifies manifest identity, encryption, and checksum before
publishing an NVMe cache entry. A typed availability error appears only after
all eligible sources and bounded retries fail.

## Immutable backup mode

Each cluster has a `ClusterUlid` and an immutable `active` or `backup` mode
recorded at creation. A backup cluster can never change to active. Restore
mints a different active `ClusterUlid` and leaves the backup unchanged.

Each backup cluster has three bulk-HDD Members, RF=3, and a two-Member durable
quorum. It accepts authenticated replication, snapshots, catch-up, and tier
data from configured source clusters, applies incoming native commits without
originating commits, and rejects client database writes with
`BackupClusterReadOnly`.

The storage path favors sequential batches, large immutable segments, capacity,
and bounded background compaction. It retains a complete verified copy and
does not demote its only fallback copy into the active cluster's S3 tier.
Normal reads do not use backup while the local active path, home S3, or an
active Region can serve the data.

`BackupDurable` first satisfies the requested `RegionalDurable` or
`GlobalDurable` level and then waits for durable apply by any two backup
Members. Timeout returns a typed partial outcome: the active commit remains
valid and retry with the same client-request and commit ULIDs discovers or
finishes the existing result.

A backup read returns its exact applied causal frontier and lag. A caller may
require a minimum frontier; the backup waits within the deadline or fails
rather than returning an older value.

## Retention and stable frontiers

Enabling backup does not make every backup Member an indefinite participant in
the active stable causal frontier. The active database retains history through
explicit, durable backup send cursors, snapshot pins, PITR policy, and
`BackupDurable` work in progress. A backup that exceeds retained incremental
history restarts from a verified snapshot and current deltas instead of
blocking active compaction forever.

The backup maintains its own applied frontier, retains a complete current copy,
and keeps shadow and CRDT history under the configured retention policy. It may
compact causally stable operations into verified checkpoints while preserving
current state, restore data, PITR, audit, and legal-hold pins.

Backup, restore, snapshot bootstrap, fallback reconstruction, and retention
rebuild are durable long-running operations under ADR 0028. Their progress
reports verified bytes, chunks, causal intervals, frontiers, lag, and exact
partial or terminal outcomes.

## Qualification

Qualification covers S3 outage, corrupt and stale fallbacks, active Region
loss, backup quorum loss, `BackupDurable` timeout and retry, read-only denial,
snapshot restart after cursor expiry, compaction and PITR pins, restore to a new
identity, tenant fairness, slow backup isolation, and resource exhaustion.
Performance claims follow ADR 0026 and separate hot, cold, backup ingest,
restore, and fallback-read workloads.
