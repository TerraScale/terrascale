# ADR 0041: Replication on SQLite — WAL-frame follower and journal tailer

Status: accepted (owner decisions D-35, D-36, D-45, 2026-09-03).
Authority: the workspace goal (`../../../GOAL.md`) owns these decisions; this
ADR records them in-repo for durable local context. If they diverge, the
workspace goal wins.

Supersedes the former intra-region two-writer state machine and per-field
CRDT replay model. The per-field last-writer-wins replica model of
`terrabase-replication-core` (`ReplicaDoc`, `ReplicaFrontier`,
`ReplicaExchange`, the in-process `DurableMember`/`ComposedMember` stand-ins)
is deleted with this record. Current replication and reconciliation mechanics
are defined by ADR 0044.

## Decision

**Intra-region (D-35, D-45).** The RF=2 copy is a physical WAL-frame
follower of the writer's SQLite file: WAL-frame hooks (not LTX), one
frame batch per wave, ack on the follower's `fdatasync`. `RegionalDurable`
succeeds when the follower has fsynced the frame. Promotion opens the
file under a new replica epoch — no logical replay, no CRDT,
byte-identical. There is no second writer inside a region.

**Cross-region (D-35, D-36).** Leaderless active-active: every region
has its own writer per database. A journal tailer ships committed
journal rows (canonical body bytes + causal metadata) over pooled QUIC
lanes. A clean apply writes bytes; a true concurrent conflict parses
both bodies and merges per field with remove-wins map semantics and an
HLC tiebreak; losers go to history with a deterministic explanation.
Document-level causal metadata by default: one dotted version + HLC per
document. Collections may declare a merge schema enabling typed CRDT
fields (PN-counter, add-wins/remove-wins set, MV register) from ADR
0022; only those collections store per-field causal kernels.

**Mechanics (D-45).** One frame per wave per peer region; zstd with a
dictionary trained on canonical JSON; credit-window pipelining (never
wait for wave *k*'s ack before sending *k+1*); same-document writes
coalesced within a wave (last bytes + merged causal context).
Range-based set reconciliation exchanges fingerprints over `seq` ranges and
refines only mismatches. A joining region bootstraps from a peer's native
TerraBase backup snapshot (ADR 0046) in its own region's object storage, then tails. Peer lists
come from the orchestrator over control gRPC; NATS exists only inside
the orchestrator.

## Consequences

- Convergence inside a region is a file property, not a merge property;
  the old coordinator/replay state machine cannot arise again.
- Cross-region conflicts are surfaced per document in history (D-37):
  uniqueness and conditional writes are enforced within a region only.
- `terrabase-replication-core` keeps only the transport- and
  storage-independent kernel the repair path reuses: the range-summary
  comparison and reconciliation kernel (`anti_entropy`). The durable
  counter-stream `progress` kernels are deleted (ADR 0044); the follower
  and tailer own their progress directly.
- The frame format stays shippable to a browser/mobile SQLite-WASM
  client later (D-57): no member-only assumptions in the frame.
