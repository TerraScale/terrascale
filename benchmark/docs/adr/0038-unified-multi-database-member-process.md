---
status: accepted
date: 2026-08-30
authority: direct workspace-owner instruction
---

# Unified multi-database Member process

## Decision

One production TerraBase Member is one Rust process containing the clustered
engine, document and Data Family operations, query execution, native
replication and repair, reactive delivery, schema, tiers, usage, maintenance,
health, metrics, and graceful shutdown. Required TBEP operations cannot fall
through to a partial host that refuses another plane.

The Member may host many databases for DBaaS density. Each database opens a
separate engine location with its own WAL, immutable files, manifests,
publication journal, encryption keys, backup state, recovery frontier, quotas,
and lifecycle. A database operation resolves its admitted database identity to
exactly one location before engine dispatch. A tenant or database identifier
from the request never becomes an unchecked filesystem path.

All mutation, usage, replication, and reactive facts for one database derive
from that database's durable engine journal. The process may retain bounded
caches and delivery queues, but no in-memory subsystem becomes another source
of commit or checkpoint order.

## Isolation and placement

Shared Members use per-database admission, weighted fair queues, memory and
cache budgets, compaction and backup limits, I/O accounting, cancellation, and
bounded concurrency. Exhaustion in one database returns a typed refusal for
that database and must not consume another database's reserved capacity.

`ts-orchestrator` may place a database on dedicated Members when its measured
load, size, compliance needs, or isolation policy crosses an admitted limit.
Shared and dedicated placement use the same engine format, TBEP contract, and
recovery procedures. Moving placement is a durable resumable operation.

## Process lifecycle

Startup validates the complete database-location catalog before admitting
traffic. A location that cannot open remains unavailable and cannot be reported
healthy. Readiness requires the serving Member, routing catalog, required
database locations, replication, membership, storage, metrics, and control
plane to report an admitted state.

Shutdown stops new admission, drains bounded work, closes reactive sessions,
persists every required frontier, syncs each open location, and closes network
listeners within the configured deadline. A deadline expiry reports which
locations did not close cleanly and relies on their normal crash recovery at
restart.

## Required evidence

Release tests cover multiple databases in one process, tenant and filesystem
isolation, fair admission under noisy load, independent keys and backups,
partial location failure, concurrent compaction, process restart, crash
recovery, dedicated-placement movement, and every required TBEP plane through
one production host. The production daemon must become ready only with the
complete host, never with a standalone document-only engine.
