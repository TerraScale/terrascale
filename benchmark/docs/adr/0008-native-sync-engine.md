---
status: accepted
---

# ADR 0008: The native sync engine

Parent record of charter PD-2026-08-26-05, 2026-08-26, accepted on user
instruction.

Owner: the parent fleet orchestrator, on the workspace owner's direction
that TerraBaseV2 have a native sync engine — really fast and native to
the database, to make everything faster.

## Context

This sharpens the existing architecture (ADR 0005 D-3 and the SE wave)
rather than replacing it: the question is placement and integration, not
a new component. The W4 replication plane already runs in-process inside
the engine; this decision extends that placement to the whole sync
engine and binds its speed to the evidence rules of PD-2026-08-26-03.

## Decisions

### D-1 — engine-native placement

The sync engine is a first-class engine subsystem, alongside storage,
engine, replication, and streaming. Its state machines (sessions,
checkpoints, delta computation, delivery) run inside the engine core
process. The hot path from commit publication to sync delivery is
in-process: no external service, broker, or process sits between an
engine commit and a subscribed consumer inside the engine server.
External surfaces (TBEP, the C# API host, NATS, sockets) are transports
and clients of the native engine, never the engine itself.

### D-2 — base-engine candidates must qualify as native

Loro, or any adopted base per the SE-02 spike, qualifies only if it
meets the native-integration criteria: usable in-process inside the
engine core, bounded and low-copy on the hot path, no runtime dependency
on external services, and performance within the written budgets. If the
spike cannot satisfy these, the native extraction path (generalizing the
W4 member and streaming state machines into the sync core) applies. The
spike's GO criteria are amended accordingly.

### D-3 — transports serve the engine

The acknowledged-write hot path prefers native engine-owned transports:
in-process lanes where the members share a process boundary, engine
sockets (`quinn`) for networked links. NATS is used only where its
semantics genuinely fit — lossy observation, replayable side-lanes,
cross-topology integration — per SE-06's per-link-class evidence rule.
No broker sits in the acknowledged-write hot path by default.

### D-4 — speed claims stay evidence-bound

"Really fast" and "makes everything faster" are objectives under
PD-2026-08-26-03: benchmark-mandatory, exact verdict vocabulary, the
PostgreSQL-parity-or-better objective, plus sync-path budgets
(commit-to-delivery latency, delivery throughput, allocations per frame)
in SE-07 and sync-path workloads alongside the PostgreSQL and V1
baselines in SE-15. Nothing in this record is a performance claim.
