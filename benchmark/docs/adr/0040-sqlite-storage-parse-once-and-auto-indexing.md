# ADR 0040: SQLite storage, parse-once byte pipeline, and auto-indexing

Status: accepted (owner decisions D-27, D-28, D-29, D-30, D-31, 2026-08-30).
Authority: the workspace goal (`../../../GOAL.md`) owns these decisions; this
ADR records them in-repo for durable local context. If they diverge, the
workspace goal wins.

Supersedes the storage-engine portions of earlier ADRs and design docs that
describe the custom LSM, RootFiles, custom WAL, compaction, and
segment/manifest machinery. Those documents are deleted together with the
code they describe (workspace task TB-21). The CRDT/causality, sync,
replication, TBEP, encryption-seam, and observability decisions in earlier
ADRs remain in force.

## Context

The 2026-08-30 decision-grade benchmarks measured the custom LSM 20–70x
behind PostgreSQL 18 on read workloads. Closing that gap was an open-ended
engine-performance campaign with uncertain convergence, carried by a
one-owner team, in the single subsystem where a latent bug loses customer
data permanently.

## Decisions

### D-27 — Embedded SQLite storage

Mainline SQLite via `rusqlite`, exact version pinned, built from the
amalgamation. One SQLite file per customer database, WAL mode, one writer
connection plus a read pool per file, group-commit batching. The
publication journal is a table committed in the SAME transaction as the
document write — sync, replication, and metering all tail one atomic
source. libSQL is not adopted (watch list only). Everything above the
storage seam — CRDT merge/causality, sync engine, replication engine,
TBEP — is unchanged.

### D-28 — One writer per file; shard by key range

File-per-database removes cross-tenant write contention. A hot database
splits by key range into more files — never intra-region multi-master
(which would break local read-your-writes). Split: create shard file,
backfill by range, tail the journal to catch up, atomically cut routing
over, verify, drop the moved range. Auto-trigger: sustained > 10k writes/s
over 5 minutes, or > 10 GB, or sustained write-p99 breach. CRDT merge is
reserved for cross-region replication; intra-region RF=2 durability copies
are journal-followers with promotion, not second writers.

### D-29 — Litestream backup

One Litestream stream per database file to OCI Object Storage. Disaster
recovery and PITR only — never a replication mechanism and never in the
hot path. Restore always lands as a NEW database. Backup streams are
encrypted under the same per-database keys, so crypto-shredding a database
covers its backups.

### D-30 — Parse once, bytes everywhere

A document is parsed exactly once in its life: at write, in Rust
(`simd-json`), for validation + canonicalization + index-field extraction.
Afterward the body is canonical JSON bytes, opaque to every layer:

- stored as a BLOB (never SQLite JSONB; nothing in SQLite parses bodies);
- carried in TBEP as a protobuf `bytes` field (no structured document
  messages);
- written by the gateway directly into HTTP responses (the gateway never
  unmarshals a body — CI-enforced);
- shipped as-is in replication frames (clean applies apply bytes; only
  true concurrent conflicts parse to merge);
- delivered in reactive frames as raw JSON bytes inside the MessagePack
  envelope; patch frames are encoded once per affected query and fanned
  out as identical bytes.

ETags derive from revisions, metering from blob lengths, TTL from columns.
Public consequence, documented: the stored byte form is
TerraScale-canonical, so reads return semantically identical — not
byte-identical — JSON to what was written.

Permitted parses beyond the write: auto-index backfill (once per document
per new index, background, budget-governed) and CRDT conflict merge (rare
path).

### D-31 — Auto-indexing (non-negotiable; RavenDB-class or better)

The query optimizer derives indexes from live query shapes. Covered
queries run on extracted columns. Uncovered queries register an auto-index
descriptor — future writes extract the field inside the single D-30 write
parse (extraction lives in Rust; SQLite expression indexes are forbidden
because they re-parse bodies per write) — and backfill runs in the
background under per-tenant budgets. First-query policy: small collections
get an inline cost-capped scan; large ones get the capped scan or a typed
`IndexBuilding { retry_after }`.

Once active, index maintenance commits in the same transaction as the
write: index-backed queries are always transactionally current. Zero stale
reads is a correctness invariant and a public claim (RavenDB's async
indexes cannot make it).

Lifecycle: usage tracking, auto-merge into covering composites,
auto-retirement of idle auto-indexes, per-collection active-index cap with
a typed error; every optimizer action is audit-trailed and
customer-visible. Access rules may only reference extracted fields; saving
a rule auto-creates its descriptors.

## Consequences

- Hot reads perform zero document parses and zero document serializations
  end to end; a benchmark lane (TB-12 serde-honesty) enforces this
  permanently.
- Writes pay one parse plus per-active-index extraction; index lifecycle
  (retirement, caps) bounds write amplification.
- Auto-indexing is benchmarked head-to-head against RavenDB (time-to-index,
  indexed latency, write-amplification curve, staleness behavior) and
  auto-published.
- Workspace tasks: TB-20 (storage core), TB-21 (delete the old engine),
  TB-22 (Litestream), TB-23 (auto-indexing), TB-7 (shard split), TB-5
  (per-file encryption); gateway/SDK enforcement in INT-2/INT-3/INT-9/SDK-2.
