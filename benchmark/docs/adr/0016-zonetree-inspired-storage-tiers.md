---
status: accepted
---

# ADR 0016: ZoneTree-inspired Rust storage engine and the NVMe/S3 tiers

Parent record of charter PD-2026-08-28-05, 2026-08-28, accepted on user
instruction.

Owner: the parent fleet orchestrator, on the workspace owner's direction
to use a design similar to ZoneTree for the storage layer.

## Context

The W3-1 storage engine rewrites a full tenant root per commit. The V2
commissioning run measured 154,246,326 durable bytes appended by a
512-write window of roughly 300-byte documents — per-commit write
amplification of ~300 KB per write. Compaction bounds accumulation but
does not fix per-commit amplification. The owner selected ZoneTree's
organizational ideas as the design input for the replacement.

Reference: `https://github.com/koculu/ZoneTree` at upstream commit
`910f9dba2998599efe6312163012c65c33dfa6e8`, reviewed 2026-08-28, MIT
licensed. ZoneTree is a C#/.NET library; TerraBaseV2 neither embeds nor
ports it. The implementation is independent Rust with its own tests,
format identity, and recovery evidence.

## Decisions

### D-1 — adopted organization

New writes enter an ordered mutable memory segment protected by the WAL.
Rotation freezes it into an immutable memory segment. Background
maintenance merges frozen segments into immutable disk segments. Sparse
indexes position reads without loading every key. Multipart disk segments
may retain unchanged key ranges across a merge. Deletes write tombstones
and reclaim old values only after every snapshot, backup, replication,
causal-stability, and shadow-delete retention gate permits it.

### D-2 — TerraBase requirements

Segment bounds use bytes and record counts (document sizes vary). The
format includes checksums, crash-safe manifests, MVCC snapshots,
forward and reverse seekable iteration, encryption, compression
negotiation, live backup, and explicit compaction receipts. Primary
documents, secondary indexes, publication history, CRDT metadata, and
tombstones use ordered keyspaces with separate maintenance budgets so
one backlog cannot starve the others. One ordered commit batch covers
the primary record, CRDT operation and causal metadata, index deltas,
publication facts, and transaction markers; recovery observes all of it
or none.

### D-3 — durability contract

`Synced` acknowledgements may use bounded group commit but return only
after the required stable-storage boundary. `Buffered` reports its
weaker achieved durability honestly. No-WAL storage is allowed only for
rebuildable temporary state such as disposable index build scratch.
Acknowledged database writes never use an unsafe asynchronous tail. Every
mode has crash, torn-tail, checksum, replay-idempotency, and
manifest-publication tests.

### D-4 — NVMe and S3-compatible tiers

Tier 1 (NVMe SSD) keeps the WAL, mutable and frozen segments, hot
immutable segments, manifests, indexes, active CRDT state, and a bounded
cache. Tier 2 (S3-compatible object storage) stores encrypted, immutable,
checksum-addressed cold segments and checkpoints. A configurable Storage
Tier Mover inherits versioned policies from cluster to database to
collection. The mover transfers immutable segments or key ranges, never
one object per document; mixed-temperature segments may be split through
bounded compaction only when the predicted NVMe saving exceeds the
rewrite cost. Upload and read-after-write verification complete before a
`RegionalQuorum` publishes the cold-tier manifest. S3 never counts
toward write acknowledgement and is not a backup by itself. Hot
PostgreSQL parity and cold-tier performance have separate evidence
gates.

### D-5 — acceptance

The new storage format must reduce durable bytes per write by orders of
magnitude from the full-root rewrite, retain crash and recovery
guarantees, and pass TerraBase.Benchmark against PostgreSQL. This record
selects an architecture, not a performance verdict.

## Consequences

The current full-root engine remains the working store until P1 lands
the replacement behind the same engine-facing seams (read snapshots,
commit intent pipeline, operation ledger) so engine, families, query, and
replication consumers migrate without protocol change. Tenant-aware
tiering policy, cold-read fallback ordering, and backup-cluster storage
are governed by ADR 0017.
