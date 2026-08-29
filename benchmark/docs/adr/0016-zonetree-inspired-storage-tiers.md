---
status: accepted
---

# ZoneTree-inspired native storage and NVMe/object tiers

## Decision

TerraBaseV2 implements an independent Rust ordered-segment storage engine. It
adopts the useful organization of a mutable memory segment, immutable frozen
segments, immutable disk segments, sparse indexes, and background merge. It
does not embed or port ZoneTree or another database storage format.

New writes enter an ordered mutable segment protected by the WAL. Rotation
freezes it. Background maintenance merges frozen segments into immutable disk
segments and may retain unchanged key ranges. Deletes write tombstones and
reclaim older state only after every snapshot, backup, PITR, replication,
causal-stability, audit, and recovery pin permits it.

Segment limits use both bytes and record counts. The versioned format includes
checksums, crash-safe manifests, MVCC snapshots, forward and reverse seekable
iteration, encryption, compression capabilities, and compaction receipts.
Primary documents, secondary indexes, publication history, CRDT metadata,
tombstones, and operation records use distinct ordered keyspaces and bounded
maintenance budgets.

One atomic commit batch covers the primary value, CRDT and causal metadata,
index deltas, publication facts, idempotency outcome, and transaction markers.
Recovery observes all of it or none. TerraBase-owned keys store ADR 0027 ULIDs
as fixed 16-byte values without a duplicated textual form.

## Internal durability achievements

`Buffered` and `Synced` are internal storage achievements, not public write
levels. `Buffered` means accepted into the WAL buffer but not yet proven on
stable storage. `Synced` means the required bytes survived the configured
stable-storage boundary. Bounded group commit may produce `Synced`.

Public `RegionalDurable`, `GlobalDurable`, and `BackupDurable` receipts in ADR
0023 compose these achievements across the required Members and Regions. An
internal `Buffered` or one-Member `Synced` result never becomes a successful
production acknowledgment.

No-WAL storage is permitted only for rebuildable temporary state such as index
build scratch. Acknowledged database writes never use an unsafe asynchronous
tail. Every mode has crash, torn-tail, checksum, replay-idempotency, and
manifest-publication tests.

## NVMe and object storage

Tier 1 NVMe retains the WAL, mutable and frozen segments, hot immutable
segments, manifests, indexes, active CRDT state, and bounded caches. Tier 2
S3-compatible storage contains encrypted, immutable, checksum-addressed cold
segments and checkpoints.

The tenant-aware Storage Tier Mover inherits versioned policy from deployment
to database to collection. It transfers immutable segments or key ranges,
never one object per document. A mixed-temperature segment is split only
through bounded compaction after the predicted NVMe saving exceeds rewrite and
refetch cost.

Upload, authentication, read-after-write verification, and checksum validation
complete before both active local Members publish a cold-tier manifest under
the current replica-set epoch. Object storage never counts toward a write
acknowledgment and is not a backup by itself.

Tier movement and compaction are durable long-running operations under ADR
0028. They report planned and verified bytes, segments, key ranges, source,
target, cache impact, amplification, tenant resource use, and exact terminal
results.

## Qualification

Qualification covers atomic recovery, torn WAL and manifest tails, checksum
failure, idempotent replay, MVCC pinning, tombstone safety, merge starvation,
tenant fairness, tier outage and fallback, encryption and compression, ULID key
ordering, and bounded cancellation. Performance and regression claims follow
ADR 0026 and include durable bytes per write, fsync behavior, read and write
latency, compaction amplification, recovery, memory, and cold-tier cost.
