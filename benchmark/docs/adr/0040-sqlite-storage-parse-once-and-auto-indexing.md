# ADR 0040: SQLite storage, parse-once byte pipeline, and auto-indexing

Status: accepted (owner decisions D-27, D-28, D-29, D-30, D-31, 2026-08-30).
Authority: the workspace goal (`../../../GOAL.md`) owns these decisions; this
ADR records them in-repo for durable local context. If they diverge, the
workspace goal wins.

Supersedes the storage-engine portions of earlier ADRs and design docs that
describe the retired segment/manifest storage machinery. That code and those
documents are deleted
**immediately** (workspace task TB-21, in lockstep with TB-20 — owner
order of 2026-08-30: no transition period, SQLite only from now on). The
CRDT/causality, sync, replication, TBEP, encryption-seam, and observability
decisions in earlier ADRs remain in force.

## Context

The 2026-08-30 decision-grade benchmarks measured the prior storage
implementation 20–70x
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

One Litestream stream per database file to OCI Object Storage **in the
same region** (free traffic), syncing every **10 s** (durability comes
from the RF=2 follower; the backup tier's RPO is 10 s). Disaster
recovery and PITR only — never a replication mechanism and never in the
hot path. Restore always lands as a NEW database. Backup streams are
encrypted under the same per-database keys, so crypto-shredding a database
covers its backups. Bucket lifecycle is orchestrator-owned, per region:
segments older than 24 h move to Infrequent Access, older than 7 days to
Archive.

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

> Superseded in detail by ADR 0045 (lifecycle, selection, verification,
> revert). The paragraphs below remain correct as the outline.

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
- Implementation is split across the SQLite storage, Litestream backup,
  auto-indexing, and shard-routing modules. Public gateway and SDK behavior
  follows the current contracts; verification runs through the repository
  checks and benchmark tables without a separate evidence ledger.
