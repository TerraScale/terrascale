---
status: superseded
superseded_by: ADR-0019
---

# ADR 0014: Active-active Loro convergence, CRDT-aware schemas, NATS cluster control

> Superseded on 2026-08-29 by the workspace `GOAL.md` and ADR 0019. Loro is
> removed, causal and CRDT semantics are TerraBase-native, NATS is optional,
> and membership correctness lives in durable engine state. This record
> remains unchanged below as historical evidence.

Parent record of charter PD-2026-08-28-03, 2026-08-28, accepted on user
instruction.

Owner: the parent fleet orchestrator, on the workspace owner's direction
that multi-writer conflict-free behavior, schema validation, easy cluster
creation, and NATS KV plus request/reply are required.

## Context

ADR 0004 D-3's per-key LWW document path and the hybrid single-server
writer model in ADR 0005 D-3 assumed one authoritative writer. The owner
required true server-side multi-writer convergence: concurrent writes at
multiple Members across Regions merge without manual repair. Loro 1.13.9
is already the sync-core CRDT base (`crates/terrabase-sync-core`); this
decision makes it the required base for server-to-server document
convergence and defines the merge semantics, schema interaction, and the
NATS control-plane boundary around it.

## Decisions

### D-1 — server-side active-active

Multiple Rust Members across Regions accept writes concurrently. Loro is
the required CRDT base for server-to-server document convergence. C#
clients never author, persist, or merge local Loro operations; they send
typed mutations to a server. The server creates, validates, persists,
replicates, and merges the CRDT operations (ADR 0013 D-2).

### D-2 — delete-wins and shadow delete

A concurrent delete defeats concurrent updates. Shadow delete is enabled
by default: the visible document becomes deleted while the losing
concurrent state remains recoverable in auditable shadow history for 30
days by default, configurable per collection. Garbage collection waits
for retention expiry, causal stability, and every snapshot, backup,
replication, sync, and recovery pin. Storage pressure triggers alerts or
backpressure, never unsafe early collection. Restore creates a new
incarnation and never silently removes the original tombstone.

### D-3 — schema modes and validation

Every collection selects `schema-free` (default), `validated`, or
`strict`. `schema-free` accepts undeclared application paths and records
their stable CRDT kinds. `validated` enforces a versioned schema on
governed paths while permitting undeclared paths. `strict` requires every
application path to be declared and rejects unknown paths. A declared
path fixes its value domain and CRDT kind. The server validates every
mutation before acknowledgement and validates the materialized document
after merge. Rules closed under merge may remain available during a
partition; cross-field, uniqueness, compare-exchange, and other
coordinated rules may refuse without quorum. Invalid merged state is
quarantined from reads and indexes and emits an auditable failure
receipt.

### D-4 — NATS cluster control, never data

NATS KV stores versioned membership leases, topology, cluster mode,
configuration, capabilities, and route advertisements. Typed, bounded
NATS request/reply handles join, health, topology snapshot,
configuration refresh, and route lookup. Compare-and-swap revisions
fence stale Members. NATS never carries documents, CRDT operations, WAL,
replication frames, snapshots, sync payloads, or checkpoints —
authenticated engine-owned QUIC links carry all database data. If NATS
is unavailable, admitted Members keep serving through the last verified
topology; joins and topology/config changes pause. Voting-membership
changes require a versioned engine configuration epoch and overlapping
old and new regional quorums. NATS advertises that decision but cannot
grant a vote or change a write quorum by itself. Lease expiry alone
never authorizes eviction or ownership transfer.

### D-5 — write acknowledgement

`RegionalQuorum` is the minimum successful write acknowledgement: durable
acceptance by a majority of eligible voting Members in the Region that
accepts the write. Local durable commit is internal partial progress and
never client success. `GlobalQuorum` is an optional stronger level whose
receipt includes the Members and Regions that contributed; it requires
`RegionalQuorum` in a majority of eligible active Regions, including the
accepting Region.

### D-6 — parallel merge semantics

Concurrent changes to distinct document paths merge independently. Maps
merge by key, lists and text use Loro's ordered CRDT semantics, and
counters merge additively. Concurrent assignments to the same scalar
select one deterministic visible winner (Loro's causal and operation
ordering, never wall-clock arrival) and retain losing values in
retention-governed shadow history; the public document never exposes an
unresolved multi-value register. Delete-wins (D-2) takes precedence.

### D-7 — hybrid CRDT storage and compaction

Each TerraBase document owns one logical Loro state and causal identity.
Shared shard-level WAL and LSM segments physically pack document states,
operations, and checkpoints — neither a file per document nor one
shard-wide Loro causal state. The engine retains recent operations under
a configurable time and byte budget, then compacts causally stable
operations into per-document checkpoints and causal frontiers,
preserving every configured history window, tombstone, shadow record,
and pinned version. Compaction runs incrementally under explicit I/O,
CPU, memory, and object-request budgets.

## Consequences

ADR 0004 D-3's document LWW path, ADR 0005 D-3's single-server writer
model, and the PD-2026-08-26-12/14 records' single-writer assumptions
are superseded where they conflict; the ratified counter PN-counter
encoding (PD-2026-08-26-15) remains the counter merge evidence.
`terrabase-sync-core`'s current single-peer, flat-scalar-subset shape is
convergent delivery, not the required multi-server CRDT database — P2
owns that rebuild. Exact general CRDT-history budgets and coordinated
transaction limits remain named open decisions.
