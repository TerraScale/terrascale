---
status: accepted
---

# ADR 0018: Leaderless, masterless active database

Parent record of charter PD-2026-08-28-07, 2026-08-28, accepted on user
instruction.

Owner: the parent fleet orchestrator, on the workspace owner's direction
that TerraBaseV2 must use a leaderless and masterless architecture.

## Context

ADR 0004 D-1 accepted single-leader-per-range replication with ownership
and fencing, and the W4-REPLICATION implementation delivered exactly
that. The owner replaced the model: the active database is leaderless
and masterless, and the existing leader-based implementation must be
replaced with symmetric replica sets, request-scoped coordination, and
quorum/idempotency evidence — not renamed or hidden behind routing.

## Decisions

### D-1 — no permanent authority

TerraBaseV2 elects no cluster master, Region leader, shard leader,
primary replica, or permanent write coordinator. Every active Member in
a shard replica set has the same write, read, replication, repair, and
recovery role. There is no leader lease, primary endpoint, election
term, failover promotion, or `NotLeader` redirect in TBEP.

### D-2 — leaderless writes

A client may submit a mutation to any active Member. That Member
resolves the versioned replica set, validates the mutation, creates or
reuses its stable operation identity, fans the operation to eligible
replicas in parallel, and collects the requested acknowledgements.
`RegionalQuorum` remains a majority of eligible voting Members in the
accepting active Region. `GlobalQuorum` remains `RegionalQuorum` in a
majority of eligible active Regions. No acknowledgement depends on a
distinguished replica.

### D-3 — request-scoped coordination

The accepting Member coordinates only that request. It owns no shard or
lease and gains no authority over later requests. If it fails, the
client or router retries the same operation through any other active
Member. Durable idempotency, partial acknowledgement state, and CRDT
operation identity prevent duplicate apply. Recovery makes progress
without an election or primary handoff.

### D-4 — leaderless reads and repair

Any active Member may serve a read. The requested consistency level
decides whether it uses local materialized state or collects a replica
quorum and merges causally compatible results. Divergence queues bounded
read repair, while Loro frontier exchange, anti-entropy, and snapshot
catch-up repair data without assigning ownership to one replica. The
default read level remains a separate public-contract decision.

### D-5 — leaderless configuration

Any active Member may propose membership, replica-set, shard-map,
schema, or tier-manifest changes. A versioned change becomes effective
only after its required quorum accepts it. Membership changes use
overlapping old and new quorums. NATS advertises accepted control
metadata but cannot appoint a TerraBase leader, grant a vote, or decide
data ownership.

### D-6 — coordination without a leader

Multi-writer CRDT mutations remain leaderless. Cross-document
transactions, uniqueness, compare-exchange, and non-merge-safe
constraints may use a request-scoped coordinator and quorum-persisted
transaction state. Any active Member may recover an abandoned operation.
A request may refuse when its quorum is unavailable, but it may not
create a permanent coordinator or hidden primary.

### D-7 — backup boundary

Immutable backup clusters remain non-voting and never become leaders or
active replicas (ADR 0017 D-4..D-6). Their RF=3/quorum=2 `BackupAck` is
separate from active write quorums. Backup-last reads grant no routing
or ownership authority to the backup cluster.

### D-8 — supersession

Supersedes ADR 0004 D-1's single-leader-per-range behavior, the current
replication implementation's ownership and fencing machinery, and every
active plan or record requiring leader election, ownership fencing, a
primary replica, or a master endpoint. Dated ledger entries remain
factual history.

## Consequences

The current `src/replication/` implementation (ownership validation,
epoch fencing, deposed-leader conflict history) conflicts with this
decision and is replaced in P2 by symmetric replica sets and
request-scoped coordination; its reusable transport, idempotent replay,
and anti-entropy seams carry over. The qualification matrix (AC-10)
gains any-Member entry, coordinator-kill, and replica-epoch-change
scenarios that must pass without election or redirect.
