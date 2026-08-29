# TerraBaseV2 language

This glossary defines current TerraBaseV2 terms. It is not an implementation
status report. The workspace charter at `../GOAL.md` resolves conflicts.

## Evidence

**V1 Parity Item**

A capability or observable contract from TerraBase V1 that V2 must implement,
omit through an accepted disposition, or keep unresolved.

Avoid: feature checkbox.

**Parity Matrix**

The versioned record connecting each V1 Parity Item to V1 evidence, V2 status,
ownership, contract semantics, Proof IDs, impact, and risk.

Avoid: feature list.

**Proof ID**

A stable identifier for evidence supporting one bounded claim.

Avoid: link, screenshot, confidence.

**Recovery Receipt**

A secret-safe record of an exact recovery scenario, source and data identity,
durability outcome, and known limits.

Avoid: restore log.

**Replication Receipt**

A secret-safe record of an exact mutation, requested acknowledgement,
Member observations, fault schedule, and convergence result.

Avoid: replication log.

**Benchmark Receipt**

A secret-safe result from the first-party benchmark harness. It records exact
source, workload, environment, statistical validity, baseline relation, and
known limits.

Avoid: local timing.

**Claim**

A statement about behavior, durability, correctness, scale, availability, or
performance that requires the matching evidence before publication.

Avoid: promise without a Proof ID.

**Current**

The exact selected source or artifact. It does not imply qualification.

Avoid: latest, stable.

**Qualified**

Supported by the required evidence for the stated scenario and assumptions.

Avoid: proven everywhere, production-ready without scope.

**Unresolved**

Known work or a decision without an accepted disposition.

Avoid: future enhancement.

## Placement and replication

**Region**

A named failure, residency, and latency domain in the TerraScale catalog.
A database owner selects one through twenty active Regions.

Avoid: primary Region, standby Region.

**Member**

A TerraBase engine participant with a `ReplicaId` and `ReplicaEpoch` seated
in one or more Partition replica sets.

Avoid: permanent shard owner, leader.

**Selected Active Region**

A customer-selected Region that stores every logical Partition of the database
and accepts reads and writes while its local replica set is healthy.

Avoid: primary, home Region.

**Request Coordinator**

The Member that accepts one operation, validates the current replica-set epoch,
mints or reuses its `CommitId`, sends it to eligible Members, and collects the
requested acknowledgements. It has no lease or authority over later requests.

Avoid: leader, master, coordinator Member.

**Leaderless**

Every active Member has the same normal write, read, replication, repair, and
recovery role. There is no permanent leader, primary, shard owner, or redirect
for ordinary data work.

Avoid: coordination-free.

**RegionalDurable**

The default production write result. Both active Members for the Partition in
the accepting Region durably commit the same `CommitId` under the same epoch.
No WAN acknowledgement is required.

Avoid: local receipt, one-WAL success.

**GlobalDurable**

An explicit WAN-bound result. `RegionalDurable` succeeds in every selected
active Region. With at least two selected Regions, the acknowledged write
survives complete loss of one selected Region.

Avoid: default durability, eventual replication.

**BackupDurable**

An optional additional receipt available only when immutable backup is
enabled. It follows the requested active acknowledgement and never rolls back
an already committed active write.

Avoid: active quorum vote.

**Convergence**

Members that hold the same accepted commit set produce identical canonical
CRDT state, materialized documents, and indexes under the applicable causal
cut.

Avoid: connected, caught up.

**Anti-Entropy**

The durable repair process that compares causal progress, requests missing
commit ranges or snapshots, and resumes after interruption. Live push is the
fast path; anti-entropy is the repair path.

Avoid: broker replay.

**Merkle Verification**

Independent comparison of causal and materialized hashes to locate divergent
Partition ranges and verify repair.

Avoid: version-vector replacement.

## Identity and causality

**CommitId**

The immutable server-issued identity
`DatabaseId, PartitionId, ReplicaId, ReplicaEpoch, OriginCounter`.
One commit may contain several canonical mutations within one Partition.

Avoid: UUID per field, client operation identity.

**Client Idempotency Identity**

A bounded caller identity used to return the same logical mutation outcome
after retry. It never becomes a permanent replication actor.

Avoid: client replica ID.

**Dotted Version Vector**

Partition-scoped causal metadata that records stable server-actor prefixes and
specific dots or gaps. It distinguishes domination from concurrency.

Avoid: global database vector, client checkpoint.

**Hybrid Logical Clock**

Physical and logical time metadata used after causal comparison and within a
bounded future-skew policy. It is not a global correctness clock.

Avoid: wall-clock last-write-wins.

**Replication Frontier**

The contiguous prefixes and out-of-order gaps a Member durably holds for one
Partition. It drives missing-range transfer and stable-frontier calculation.

Avoid: subscription checkpoint.

**Stable Frontier**

The causal position past which membership, snapshots, active replicas, optional
backup, PITR, audit, and client-retention pins permit safe summarization or
collection.

Avoid: transport acknowledgement.

## Sharding and storage

**Bucket**

One value in the stable `2^20` routing space. A Shard Key maps a document to a
Bucket. Rebalancing does not change the Bucket identity.

Avoid: physical Member.

**Shard Key**

The document attributes whose hash selects a Bucket. The document identity is
the default. Changing it is an explicit migration.

Avoid: primary key.

**Logical Partition**

A group of Bucket ranges with independent WAL, actor counters, causal state,
replication progress, snapshots, anti-entropy hashes, resource accounting, and
migration state. It is a logical unit, not a process or permanent Member
owner.

Avoid: shard leader, node.

**Partition Split**

An idempotent migration that moves whole Bucket ranges into a new logical
Partition while preserving document identity, commit identity, causal history,
and safe routing epochs.

Avoid: hot-shard leader move.

**Storage Tier**

An active storage class. Tier 1 contains WAL, mutable state, indexes, hot
segments, manifests, and caches on NVMe. Tier 2 contains encrypted,
checksum-addressed immutable cold ranges in S3-compatible object storage.

Avoid: backup. Tier 2 is active storage.

**Storage Tier Mover**

The bounded engine service that promotes or demotes immutable ranges under
tenant-aware policy, budgets, hysteresis, and fallback proof.

Avoid: per-document offloader.

**Immutable Backup**

An optional read-only, non-voting copy outside the active Region set. It never
accepts customer writes and cannot be promoted in place. Restore creates a new
active identity.

Avoid: standby, secondary Region.

**Logical Migration**

A manifest-verified V1 to V2 data transformation. V2 does not open V1 files or
claim V1 wire compatibility.

Avoid: in-place upgrade.

## Replication and client sync

**Replication Plane**

Trusted Member-to-Member exchange of native delta CRDT commits, causal
frontiers, acknowledgements, snapshots, and repair data over engine-owned QUIC.

Avoid: public client stream, NATS.

**Client Sync Plane**

Authorized server-to-client delivery of query results, patches, resets,
optimistic outcomes, causal session tokens, and view checkpoints.

Avoid: raw replication stream.

**Reactive Subscription**

A bounded typed TQL query whose data, index-range, and authorization
dependencies the engine tracks. The result may be incrementally patched or
reset from a new snapshot.

Avoid: collection change feed.

**View Generation**

The identity of one normalized query, projection, principal, and authorization
generation. Query or authorization changes create a new generation.

Avoid: database version.

**View Checkpoint**

An opaque, integrity-protected resume token bound to tenant, database,
principal, authorization generation, query, projection, view generation,
result revision, sparse Partition positions, and retention generation. It
means the client applied one authorized view, not every database mutation.

Avoid: version vector, log offset.

**Causal Session Token**

A sparse set of dependencies for Partitions touched by one client session. A
client may present it after moving Regions to request read-your-writes or
monotonic reads.

Avoid: replication actor.

**Optimistic Presentation**

A reversible client-side UI patch while a server mutation is in flight. It is
not durable, receives no `CommitId`, and does not advance causal state.

Avoid: offline CRDT mutation.

**Reset**

The correct bounded fallback when incremental result maintenance is unsafe,
too expensive, expired, or invalidated by a generation change.

Avoid: sync failure.

## Protocol and product boundaries

**TerraBase Engine Protocol**

TBEP is the private typed protocol between TerraBaseV2 and the independent
C#/.NET `TerraScale.Database` API. It uses generated MessagePack contracts
over QUIC with mTLS and workload identity, including same-host loopback.

Avoid: UDS, C ABI, public API, client sync protocol.

**TerraScale.Database**

The horizontally scalable C#/.NET 10 ASP.NET Core public database API and sole
TBEP gateway. It authenticates through Dashboard OIDC and OAuth contracts,
obtains typed authorization grants, issues bounded engine context, and owns no
identity or payment policy.

Avoid: Dashboard backend, Rust host.

**PrincipalContext**

A short-lived private engine context issued by `TerraScale.Database` from a
verified Dashboard authorization grant. It binds tenant, Project, database,
actions, claims, policy generation, audience, expiry, and Database API workload
identity.

Avoid: public bearer token, authorization bypass.

**Operations Studio**

TerraBaseV2's internal Vinext UI for documents, queries, indexes, families,
topology, and live engine views.

Avoid: customer Dashboard.

**Control Plane**

Orchestrator-owned topology, placement intent, Member identity allocation,
workload scheduling, certificates, capacity, repair orchestration, and
delivery. It is outside the normal database write path.

Avoid: mutation authority.

## Query and schema

**TQL**

TerraBaseV2's SQL-familiar, document-native query language. It parses to a
typed versioned AST and permits no arbitrary code execution.

Avoid: RQL, SQL wire protocol.

**Auto-Index**

An engine-created index selected, widened, superseded, or retired for dynamic
queries. Explicit indexes take precedence when equivalent.

Avoid: customer-required index.

**Index Selection Record**

The bounded recorded explanation of candidates, verdicts, tie breaks, selected
index, staleness basis, or Auto-Index creation.

Avoid: debug log.

**Document Schema**

An optional typed versioned validation contract for a collection. Documents
remain schema-free by default.

Avoid: mandatory table definition.

**Schema Migration**

An explicit, idempotent, resumable move between Document Schema versions with
recovery evidence.

Avoid: V1 Logical Migration.
