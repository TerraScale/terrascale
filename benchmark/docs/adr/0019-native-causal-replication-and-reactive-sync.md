---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# ADR 0019: Native causal replication and reactive sync

## Context

TerraBaseV2 still implements parts of several incompatible designs. They
include single range ownership, Loro persistence and sync semantics, UDS TBEP,
C# hosts inside the Rust repository, NATS-backed topology assumptions, and a
client stream that does not maintain authorized query results.

The workspace owner replaced those designs with the consolidated contract in
`../../../GOAL.md`. This ADR records the TerraBaseV2 repository
consequences. The workspace goal remains authoritative if this summary drifts.

## Decision

### Native mutation and causal model

TerraBase owns its CRDT library, commit identity, dotted version vectors, HLC
rules, snapshots, compaction, and wire formats. Causal state is Partition
scoped and bounded by server replica actors. Loro is removed from persistence,
replication, sync, SDK conformance, and supported protocol formats.

The initial required CRDT types are `LWWRegister`, `MVRegister`, `GCounter`,
`PNCounter`, explicitly policy-bound add-wins and remove-wins sets, `ORMap`,
`Flag`, and delete-wins document tombstones with incarnation identity.
Advanced types follow only after the native core passes deterministic merge,
collection, snapshot, and recovery properties.

ADR 0022 fixes the exact identity, causal, datatype, codec, and collection
contract. ADR 0023 fixes RF=2 partial-write recovery, replication, repair,
snapshot, membership, and stable-frontier behavior. ADR 0024 fixes the
reactive query-view and checkpoint state machine. They are normative under the
workspace goal and cannot be replaced by implementation-local choices.
ADR 0025 fixes generated FlatBuffers schemas, bindings, verification,
conformance, and measured performance gates for TBEP and public reactive
frames.

### Active placement and writes

A database selects one through twenty active Regions. Every selected Region
stores every logical Partition and accepts reads and writes. The initial
catalog is GRU, SFO, and FRA.

Production uses two active Members per selected Region. The default
`RegionalDurable` write succeeds only after both local Members durably commit
the same `CommitId`. It does not wait for WAN replication. The optional
`GlobalDurable` level waits for `RegionalDurable` in every selected Region.
Backup is independently optional, read-only, immutable, and non-voting.

The accepting Member coordinates one request. No cluster, Region, Partition,
range, or Member has a permanent leader, master, primary, owner, or lease for
normal data work.

### Region replication

Trusted Members replicate native delta batches through direct engine-owned
QUIC. Selected Regions use full mesh through the supported limit of twenty.
Peers stream current commits, acknowledge durable ranges, compare causal gap
state after reconnect, and repair from snapshots when retained deltas are no
longer sufficient.

Anti-entropy and separate causal and materialized Merkle trees verify and
repair convergence independently from the live stream. Transport duplication,
reordering, delay, reconnect, and process restart cannot change the result.

### Reactive client sync

Region replication and public client sync are separate planes. Public clients
subscribe to bounded typed TQL queries. TerraBase tracks data, index-range, and
authorization dependencies, maintains simple results incrementally, and emits
insert, update, patch, remove, move, or reset operations.

Clients write online through public TerraScale APIs. They may show reversible
optimistic presentation, but they do not create offline CRDT mutations or
replication actors.

Client checkpoints are opaque authorized-view positions. They bind principal,
policy generation, normalized query, projection, view generation, result
revision, sparse Partition delivery positions, and retention generation. They
are not replication vectors. Realtime delivery is the fast path. Checkpointed
catch-up and authorized snapshots are the correctness path.

### Service boundary

`TerraScale.Database` is an independent C#/.NET 10 ASP.NET Core service and
the sole TBEP gateway. TBEP uses generated bounded FlatBuffers contracts over
QUIC with mTLS and workload identity, including same-host loopback. UDS and an
in-process C ABI are not supported service transports.

Dashboard remains one full-stack Vinext TypeScript project. It owns Better
Auth, Organizations, Projects, Users, Payments, and HTTP MCP. Public
TypeScript and .NET SDKs use versioned TerraScale APIs and never connect to
TerraBase Members.

### Authorization, tenancy, and usage

`TerraScale.Database` derives a short-lived `PrincipalContext` from a
Dashboard-signed authorization grant. TerraBase compiles typed database policy
into query, index, mutation, subscription, projection, and reconnect paths.
Policy changes create reactive dependencies and fail closed by generation.

All foreground and background work uses hierarchical tenant and database
resource accounting, bounded fair queues, admission control, backpressure, and
bounded spill. Orchestrator scaling follows sustained demand but is not the
first protection against a noisy neighbor.

TerraBase records authoritative usage in durable, idempotently exportable
segments. Telemetry and NATS are not billing ledgers.

### NATS

NATS is optional. It may provide configuration, discovery, or
non-authoritative notification when it makes the system simpler. TerraBase
must remain correct when NATS is unavailable or replaced. NATS never carries
database data and never decides commit, membership, replication, recovery,
sync, usage, or billing outcomes.

## Consequences

- Existing Loro, UDS, leader, range-owner, old checkpoint, and NATS-dependent
  code is migration debt.
- Existing Proof IDs for those paths do not qualify their replacements.
- One checked-in TBEP `.fbs` schema generates Rust and C# bindings with pinned
  tooling, a drift gate, structural verification, golden vectors, and
  bidirectional conformance tests.
- CRDT property tests and deterministic distributed simulation become release
  gates.
- TerraBase.Benchmark remains the only source for performance comparisons.
  Decision-grade comparison begins only after the relevant V2 correctness
  gates pass. PostgreSQL non-inferiority, the two-times RavenDB target, and
  multi-Region replication and sync baselines follow ADR 0003 and ADR 0025.
- ADRs 0004, 0005, 0008, 0011, 0012, 0013, 0014, and 0018 are superseded where
  they conflict with this record and the workspace goal.
