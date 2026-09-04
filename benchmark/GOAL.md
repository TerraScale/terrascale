# terrabase goal

## Mission

Build the Rust multi-Region document database, replication engine, sync engine,
and public API in one member process. Optimize for correctness, bounded resource
use, and measured cost per write.

## Authority and work rules

The [workspace goal](../GOAL.md) defines product scope and shared acceptance.
This repository goal restates its applicable responsibilities. Operational
integration follows the GRU master, regional authority, same-host management,
and qualification requirements reconciled from
[`ts-orchestrator/GOAL.md`](../ts-orchestrator/GOAL.md). Goals contain decisions
and acceptance criteria; implementation findings and feature proposals belong
in review documents, not progress claims here.

Work on `develop` in one checkout. Use `fj` for Forgejo. Never edit human-owned
`AGENTS.md` or `CLAUDE.md`, add co-author trailers, or include unrelated work in a
commit. The owner's explicit commit request means commit and push before
running tests, checks, or builds; verify afterward. Release qualification still
requires the appropriate green gates. Run Rust commands at lowered priority.

## Shared product contract

- This is the first public release. Use TerraBase and the coordinated v1 names;
  the public API is `/api/v1`, sync is `terrascale.sync.v1`, and MessagePack is
  1.0. Retired pre-launch designs need no compatibility layer. Published public
  contracts follow the workspace compatibility commitment.
- TerraBase's Rust member owns the engine and public database/reactive API.
  SQLite is the only storage engine; SQL is read-only, while writes are named
  document operations. A separate gateway, TQL, vector/spatial support,
  PostgreSQL compatibility, and a shipped local engine are outside launch.
- Only writes and logical storage are billed, including their declared Region
  multiplier. Reads, egress, subscribers, delivery, replay, TTL expiry, and
  webhook delivery stay unbilled within published guards. TerraBase owns usage
  truth; no other service calculates a competing usage ledger.
- Launch uses GRU/SFO/FRA on OCI plus Cloudflare. Every member host has local
  orchestrator and `ts-node` services. The permanent global control R3 stays in
  GRU/SFO/FRA when the estate grows to 10 or 20 Regions. Customer data and
  replication never pass through NATS or the orchestrator.
- Placement and residency are immutable per database. Estate size does not
  determine a database's replication set. Honor the same lock for routing,
  replication, backup, restore, export, branches, and repair.

## Engine and data ownership

One mainline `rusqlite` SQLite file per database uses WAL, one regional writer,
a read pool, and group commit. A write parses and canonicalizes document JSON
once, then stores body bytes, extracted columns, index maintenance, history,
causal metadata, publication journal, and usage facts in the required atomic
boundary. Reads preserve session frontiers and never select a stale index.
Keep body bytes opaque after ingestion except conflict merge and backfill.

SQL SELECT/WITH/EXPLAIN are allow-listed, parameterized, authorized, bounded,
and read-only. Collection tables expose schema fields, `body`, and metadata.
Auto-indexing follows ADR 0045: invisible budgeted build, durable watermark,
shadow validation, no stale reads, measured activation and regression rollback.
Implement key-range splits with journal catch-up and atomic cutover; no
intra-Region multi-master or alternate storage backend.

RF=2 uses a physical WAL follower in each placed Region. A regional durable
acknowledgement requires the actual follower durability frontier. Cross-Region
QUIC tails causal journal entries, coalesces waves, applies deterministic
CRDT merge, and performs range-based set reconciliation. Define and enforce
RegionalDurable/GlobalDurable exactly at the acknowledged frontiers; unknown
frontiers cannot become readiness. Join from a permitted regional snapshot,
catch up, and prove equality before serving. Follow ADR 0044.

Shared authorized result and collection views consume the durable publication
journal incrementally. Canonical query, parameters, authorization scope, and
entitlement generation determine sharing. Coalesce writes within 100 ms, encode
each immutable data-bearing log response once, and freeze bytes per offset.
Snapshot cuts and log offsets stay cache-key inputs. Empty polling responses
are no-store. Bound view count and history independently of attached readers.
Permission changes, resets, checkpoint expiry, and member movement fail safely.

## Public API, trust, and contracts

Axum serves REST, read-only SQL, view attachment/capability issuance, immutable
HTTP responses, optional WebSocket, and OpenAPI on the member. There is no
separate public gateway. Wrong-region requests return `421` plus placement;
regional writer forwarding preserves scope, sessions, and idempotency.

API keys and BYO-JWT must be composed into production authentication and live
authorization. Publish the named atomic persistence operations needed by the
Better Auth adapter; the dashboard receives no bypass. Provide scope-safe CRUD,
bulk, tenants, schema migration, FTS, TTL/history, backup/PITR/branch, usage,
webhook, and consistent resumable export/import contracts required at launch.

Publish OpenAPI, MessagePack manifest/vectors, private TBEP protobufs, and the
shared HTTP/reactive conformance bundle with exact digests. SDKs consume the
public contracts. `terrabase-go` is the private TBEP client for the orchestrator;
it is not the Go customer SDK.

## Lifecycle, durability, and cost

The host-local orchestrator sends membership intent and lifecycle operations
over mTLS TBEP with deadlines, idempotency, and durable operation status.
TerraBase owns membership acceptance, writer/follower transitions, drain,
catch-up evidence, and permission to serve. Host health never substitutes for
replication readiness. Survive loss of control services after admission.

Litestream backs each file to its own Region's OCI Object Storage with the
workspace retention and 10-second backup interval. Restore creates a new
database and proves content against the journal. Encrypt files, WAL, backup,
export, and branch data under the database DEK. Enforce residency and safe
crypto-shred. Rehearse restoration and never silently lose acknowledged writes.

Export authoritative write units and logical byte-seconds. Bill neither
indexes/WAL/backups nor reads/delivery/replay. Enforce plan guards and spend
caps without converting rejected free work into charges. Bound per-database
resources and expose usage facts separately from low-cardinality host metrics.
Fence writes above the 500 ms clock-offset limit. Maintain the Free pause and
reap lifecycle under product notices and orchestrator authority.

## Completion gates

Production startup requires concrete durable membership, RF=2, replication,
backup, identity, and control adapters. An absent adapter is an actionable
startup failure, never successful readiness. The production binary and release
artifact contract must agree with `ts-deploy` and `ts-node`.

Run `nice -n 19 make check` and applicable contract, fault, simulation,
backup/restore, SQL, security, and benchmark lanes. Record benchmark tables in
`bench/` with hardware/resource ceilings. Qualify the real member inside all
six Firecracker guests with same-host management and the fourth-Region test.
Then pass the full product journey, partition/kill/clock-skew matrix, residency
proof, real OCI/Cloudflare gates, million-reader proof, and launch soak. No
passing unit suite replaces these gates.
