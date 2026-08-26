---
status: accepted
---

# ADR 0012: Sync-engine bandwidth, the NATS role, and trigger events

Parent record of charter PD-2026-08-26-13, 2026-08-26, accepted on user
instruction.

Owner: the parent fleet orchestrator, on the workspace owner's direction
that the sync engine must be efficient on the bandwidth as well; that
NATS on the TerraBase side is a KV, config-sharing, and discoverability
point, not a data store; and that the sync engine must generate events
that trigger clients to get new data fast and efficiently.

## Context

This sharpens ADR 0005 D-4, ADR 0008 D-3, and the SE wave's transport
plan rather than replacing them. The native-sync-engine placement
(ADR 0008) is unchanged; what changes is the NATS placement rule, the
addition of bandwidth to the measured objectives, and the elevation of
the in-process notification design into a public trigger-event
requirement. The workspace charter's amendment record
(PD-2026-08-26-13) supersedes the member-lane NATS clauses of
PD-2026-08-26-01 D3, ADR 0005 D-4, and ADR 0008 D-3; those records
stand as history.

## Decisions

### D-1 — bandwidth is a first-class objective

The sync engine must be bandwidth-efficient on every link it feeds —
client channels (`terrascale.sync.v2` through the C# host) and member
lanes alike. Delta delivery instead of full-document re-sends, frame
batching, and checkpoint-bounded retransmission are requirements, each
carrying a written, measured budget: bytes-on-wire per delivered
change, per-frame envelope overhead, and trigger-notification size.
The budgets join ADR 0008 D-4's sync-path budgets in SE-07, with
bandwidth workloads in SE-15. The hard-data rule (ADR 0003,
PD-2026-08-26-03) applies to bandwidth claims exactly as to latency and
throughput claims: no budget is met until its receipt exists.

### D-2 — NATS is KV, config sharing, and discoverability only

Within TerraBaseV2, NATS serves exactly three roles: a key-value store
(JetStream KV) for operational key-value state, configuration sharing,
and service/member discoverability. No database data — documents,
change events, journal or replication frames, sync checkpoints, or any
other client or member payload — is stored in or transported through
NATS. All data movement rides engine-owned transports: in-process lanes
and engine sockets for member links, TBEP for C# consumers. The
Orchestrator NATS spec's separation rules (own accounts, trust roots,
credentials, subject namespaces; never the control plane's) govern the
KV/config/discovery usage. SE-06 implements this: engine-owned member
transport plus the NATS KV/config/discovery integration, with evidence
that no database data rides NATS.

### D-3 — the engine generates trigger events

The sync engine emits small, typed, content-free trigger events on
commit publication so clients and consumers fetch new data immediately
instead of polling — push the hint, pull the delta. The existing
in-process notification design (two-identifier hints, bounded queues,
drop-and-count, never block a commit) extends to the public surfaces:
the change-event backbone (SE-03), the Sync Protocol's
trigger-notification frame (SE-04), TBEP subscription frames (SE-14),
and the host's SSE/WebSocket endpoints (SE-08). Clients respond by
pulling delta batches from their checkpoint. Trigger events never
carry document data (D-1) and never block a commit; their latency
joins the commit-to-delivery budgets of ADR 0008 D-4.

### D-4 — claims stay evidence-bound

Nothing in this record is a performance or bandwidth claim. Verdict
vocabulary, receipt gating, and the exclusive benchmark lane are
unchanged (ADR 0003, PD-2026-08-26-03); SE-07 carries the budgets and
SE-15 the workloads.
