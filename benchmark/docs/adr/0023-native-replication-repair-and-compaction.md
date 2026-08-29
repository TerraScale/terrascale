---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# Native replication, repair, and compaction

## Context

The workspace goal selects leaderless active Regions, RF=2 local durability,
direct QUIC replication, anti-entropy, Merkle repair, snapshots, and
Partition-scoped causality. ADR 0022 defines the native commit and CRDT state.
This record fixes the state machines that move and retain those commits. Every
TerraBase entity named here is a ULID under ADR 0027.

Transport delivery is never the correctness boundary. Durable canonical
commits, native join semantics, causal progress, replica-set epochs, verified
snapshots, and stable-frontier rules are the correctness boundary.

## Progress vocabulary

Each Member records three positions for every Database and Partition:

| Position | Meaning |
| --- | --- |
| `stored` | The canonical commit and required atomic metadata survived the WAL durability boundary. |
| `applied` | CRDT materialization, document state, mandatory index deltas, authorization dependencies, publication facts, and usage facts include the commit. |
| `verified` | A named frontier has a verified snapshot or state image whose causal and materialized Merkle roots match the manifest. |

Each position is an ADR 0022 Partition clock with contiguous prefixes and
explicit received intervals. `applied` cannot exceed `stored`. `verified`
names a frontier and snapshot generation, not the latest received counter.

An acknowledgement always names its position. An unqualified `ACK` is invalid.
Write durability may rely on `stored`. A read or subscription may expose a
commit only after `applied`. Destructive log or tombstone collection requires
a verified replacement and the stable-frontier proof below.

## Local RF=2 write state machine

Every selected production Region has two active Members for each Partition.
The Member receiving a request is the request coordinator and the origin actor
for a newly minted conflict-free commit.

### Admission

Before allocating a counter, the coordinator:

1. verifies the signed principal, authorization generation, tenant, database,
   and Partition;
2. verifies that both local seats agree on the effective replica-set epoch;
3. resolves the client-request ULID against both local Members;
4. validates schema, datatype, causal dependencies, transaction boundary, and
   resource admission; and
5. confirms that both Members can enter the bounded durable lane.

If an idempotency record exists on either Member, the coordinator recovers that
record before it may mint a new commit. A different request digest fails.

### Durable commit

The origin sequencer assigns the next counter in WAL order and creates one
canonical commit. The coordinator sends the exact bytes and request digest to
both local Members. Each Member validates the bytes independently and appends
one atomic WAL batch containing the commit, idempotency outcome, and all facts
required to replay application.

The two appends may run in parallel. The origin WAL preserves the counter rule
in ADR 0022. A Member returns `Stored` only after the required file data and
manifest boundary are durable under the configured storage contract.

If the peer stores first and the origin crashes, the peer's bytes remain a
partial commit. Before the actor resumes counter allocation, restart recovery
compares both local stored clocks and canonical commit hashes, copies the
peer-only commit to the origin, and advances the sequencer from that recovered
prefix. The Region stays write-unavailable if it cannot perform this RF=2
reconciliation. It never reuses the counter or discards the peer-only commit.

`RegionalDurable` succeeds only when both current local seats return `Stored`
for the same commit bytes, request digest, and replica-set epoch. The
coordinator must also reach `Applied` locally and publish the result before it
replies. The peer may apply after its durable acknowledgement. It cannot serve
a causal token containing the commit until it reaches `Applied`.

An index that is a rebuildable Auto-Index may lag if its generation records the
gap. Identity lookup, uniqueness facts, authorization dependencies, reactive
publication facts, and any index required by the acknowledged operation remain
inside the atomic application boundary.

### Partial outcomes and retry

There is no rollback of an immutable CRDT commit after either Member stores it.
The failure matrix is:

| Durable observation | Required behavior |
| --- | --- |
| Neither Member stored the commit | The same client-request ULID may retry. A new commit is minted only after both available Members prove absence. |
| Exactly one Member stored the commit | Return or retain `PartialCommit`. Repair the same bytes to the other current seat. Do not mint a replacement. |
| Both stored, coordinator did not reply | Retry discovers the same commit, completes required application, and returns the recorded outcome. |
| Both stored and the client received success | Ordinary replay returns the same logical result and never reapplies the mutation. |
| One seat is unavailable | The Region refuses new `RegionalDurable` writes for that Partition and routes to another selected healthy Region when policy allows. |

A blind retry under a new client-request ULID is a new logical write and the
SDK must not perform it automatically. Unknown and partial outcomes include
the original client-request and commit ULIDs plus the durable ADR 0028
operation.

### Global durability

`GlobalDurable` uses the same immutable commit. It requires `RegionalDurable`
in every selected active Region under the same effective placement generation.
A remote Region stores the original actor dot; it does not remint the commit.

Failure after any Region stores the commit is a partial global outcome. The
commit continues through repair and cannot be rolled back. Retry resumes the
same commit and reports exactly which Regions reached `stored`, `applied`, and
`verified`. A placement change cannot lower the quorum for an in-flight global
request.

## Application and publication

WAL replay and live apply use the same idempotent state machine:

1. verify commit identity and canonical hash;
2. join native CRDT deltas;
3. materialize affected documents and relationships;
4. update required indexes and query dependency summaries;
5. record authorization and usage facts;
6. append the publication record; and
7. advance `applied`.

Crash recovery repeats incomplete steps by commit identity. A publication
record is emitted once logically even if recovery executes the step more than
once. Subscribers use result revisions, not an assumption of exactly-once
notification.

## Member replication protocol

### Connection and authentication

Members use TLS 1.3 QUIC with mutual workload certificates. Certificate
identity binds the TerraScale environment, cluster, Member, allowed role, and
expiry. Membership state must also admit the Member. A valid certificate alone
cannot create a replica seat or authorize an old epoch.

Mutation traffic forbids QUIC 0-RTT. Connection resumption does not resume an
application session without a fresh nonce, capability negotiation, and
replica-set epoch check.

Selected Regions form a direct full mesh. Members pool a bounded number of
authenticated QUIC connections for each Region pair and multiplex every tenant,
database, and Partition over them. They do not open one connection or stream
per tenant, database, Partition, or mutation. Independent bounded lanes carry
control, current writes, live replication, repair, and snapshot traffic.

### Capability negotiation

The connection handshake exchanges:

```text
ConnectionHello {
    protocol_major_range
    protocol_minor_features
    native_codec_majors
    compression_algorithms
    maximum_frame_bytes
    maximum_uncompressed_bytes
    cluster_ulid
    member_ulid
    certificate_identity_digest
    connection_nonce
}
```

The selected major and required features are immutable for the connection.
Unknown required features, cluster mismatch, identity mismatch, or no common
major closes the connection. Minor negotiation may disable optional
optimizations but cannot change commit, merge, acknowledgement, or collection
semantics.

### Partition session

A logical Partition session begins with:

```text
PartitionOpen {
    database_ulid
    partition_ulid
    placement_generation
    replica_set_epoch
    stored_progress
    applied_progress
    snapshot_generation
    retained_floor
}
```

Both sides verify that the peer was an admitted source for the commit epochs
it offers. A stale Member may supply authenticated historical data for repair
when the membership record permits it, but it cannot originate new commits or
acknowledge current durability.

The receiver computes missing actor intervals. Normal session messages are:

```text
ProgressSummary
NeedIntervals
CommitBatch
ProgressAck
RepairRequired
SnapshotOffer
SnapshotAccept
SnapshotChunk
SnapshotCommit
MerkleProbe
MerkleRangeRequest
MerkleRangeState
SessionError
```

Every message has a protocol version, session ULID, bounded body length,
payload hash, and replica-set epoch. Each sender maintains a strictly monotone
message sequence for its direction within that session. The sequence detects
replay and reordering in that direction but does not define commit ordering.

### Commit batches

A `CommitBatch` groups canonical ADR 0022 commits by Database, Partition, and
compatible codec. It may cover several actors and interval fragments. The
header names exact intervals and the uncompressed canonical payload hash.

The receiver:

1. authenticates and bounds the envelope;
2. bounds decompression and validates the native payload;
3. validates every commit and epoch before changing progress;
4. deduplicates by `CommitUlid` and canonical hash;
5. atomically stores accepted commits in WAL order local to the receiver;
6. persists updated received intervals; and
7. emits `ProgressAck(position = stored)`.

The same `CommitUlid` with different canonical bytes is corruption or a
Byzantine identity violation. The receiver rejects the frame, isolates the
lane, records a secret-safe corruption receipt, and never selects a winner or
advances progress.

Application may follow storage asynchronously under bounded queues. Applied
acknowledgements advance only after the application state machine completes.

### Flow control and backpressure

Application credit is measured in compressed bytes, maximum uncompressed
bytes, commit count, and outstanding durable batches. QUIC flow control alone
is not enough. A receiver grants bounded credit per traffic class and charges
work to tenant, database, Partition, source Region, and repair class.

Interactive traffic outranks ordinary live replication, which outranks catch-up
and snapshots. Priority cannot starve repair forever; each class has a bounded
minimum share. A slow peer spills only canonical retained batches to bounded
disk queues. If the queue reaches its limit, the sender discards the derived
queue and later resumes from causal progress. It never drops the authoritative
WAL because a network queue filled.

## Anti-entropy

Live push is a latency path. Anti-entropy is the repair path and runs:

- periodically with deterministic jitter;
- after connection reestablishment;
- after process or disk recovery;
- after snapshot install;
- during membership addition or retirement;
- after a Merkle or checksum mismatch; and
- on an authorized operator request.

Peers first compare normalized causal progress. Missing retained intervals use
`NeedIntervals` and `CommitBatch`. An interval below the sender's retained
floor returns `RepairRequired` with suitable snapshot generations. A sender
never claims a missing interval is empty merely because it compacted the WAL.

Anti-entropy is bounded by bytes, work, concurrency, deadline, and tenant
budget. Failure backs off per peer and Partition without suppressing current
traffic or repair with another healthy source.

## Canonical Merkle verification

Every verified Partition frontier has two independent BLAKE3 trees using the
canonical encoding and domain separation from ADR 0022.

### Causal tree

The causal tree covers:

- normalized actor progress and summarized retired-actor floors;
- object and field causal kernels;
- tombstones and incarnations;
- datatype policy and schema version; and
- retained live CRDT payload required for future joins.

### Materialized tree

The materialized tree covers canonical visible document, relationship, and
mandatory index state at the same frontier. Entries sort by tenant, database,
collection, canonical object ID, incarnation, and field path. Rebuildable
Auto-Indexes have separately named roots and do not make equal document state
unequal.

Peers compare roots only when they name the same codec major, schema catalog,
Partition range generation, and causal frontier. Otherwise they first repair
progress or select a common verified snapshot frontier.

A differing root descends through fixed logical Bucket and canonical key
ranges. Tree fanout and physical node layout may change without changing the
logical leaf hash. Repair transfers native joinable state with its causal proof
and source chunk hash. It never overwrites with visible JSON.

After repair, both peers recompute causal and materialized roots at the named
frontier. Repair succeeds only when both agree. An index mismatch repairs or
rebuilds that index and cannot conceal a document mismatch.

## Snapshots

### Manifest

An immutable Partition snapshot has a canonical signed manifest:

```text
PartitionSnapshotManifest {
    cluster_ulid
    tenant_ulid
    database_ulid
    partition_ulid
    bucket_range_generation
    replica_set_epoch
    snapshot_generation
    native_codec
    schema_catalog_digest
    causal_frontier
    retained_history_floor
    causal_merkle_root
    materialized_merkle_root
    required_index_roots[]
    chunks[] {
        ordinal
        logical_key_range
        uncompressed_bytes
        stored_bytes
        compression
        encryption_key_id
        plaintext_canonical_hash
        ciphertext_hash
    }
    manifest_hash
    creator_identity
}
```

The signing key is workload-scoped and rotated by the Orchestrator. Signatures
prove source and manifest integrity. Storage encryption and key rotation remain
governed by the storage ADRs.

### Creation

Snapshot creation pins one MVCC cut and its causal frontier. It serializes
canonical CRDT and materialized state, verifies all chunk hashes, builds both
Merkle roots, and atomically publishes the manifest only after every chunk is
durable. An interrupted unpublished snapshot is garbage.

### Transfer and resume

The receiver accepts only a compatible manifest from an admitted source. It
requests missing chunks by ordinal and hash, writes them to staging, verifies
each chunk before marking it present, and may resume after restart. It never
publishes a partial manifest.

Snapshot traffic has its own bounded streams and cannot consume interactive or
live-replication reservations.

### Installation and merge

An empty or freshly erased replica may atomically install a verified snapshot,
then replay post-snapshot commits before activation.

A replica with local commits may replace local state only when the incoming
frontier causally includes every local stored commit and membership permits the
replacement. Otherwise it joins verified native state or keeps local state and
repairs both ways. Snapshot installation never discards an unacknowledged local
dot merely because the donor is newer by HLC or wall time.

Installation writes a new crash-safe manifest, swaps it atomically, then
reclaims staging and obsolete files after recovery can prove the new manifest
complete.

## Stable frontier and collection

### Stable causal frontier

For one effective replica-set epoch, the stable causal frontier is the
componentwise minimum `applied` prefix known durably across:

- every active Member seat in every selected active Region;
- every joining seat required by an in-progress joint configuration;
- explicit durable backup send cursors, snapshot pins, and `BackupDurable`
  operations that still require retained causal state under ADR 0017; and
- any explicit recovery or legal-hold pin that names causal state.

An absent actor entry contributes zero. Sparse received intervals above a
prefix do not advance the stable frontier. A disconnected active Region blocks
the relevant component until it catches up or a completed membership decision
retires it. An Orchestrator health opinion, NATS lease expiry, or elapsed time
cannot remove that pin.

Client checkpoints do not pin causal stability or raw mutation logs. They have
their own bounded delivery retention and reset when expired.

### History retention floor

Causal stability and history retention are different. A stable event may still
be retained for PITR, audit, idempotency, backup, or client delivery. Each
Partition records a history floor derived from every policy pin. Compaction
uses the older of the safe causal collection point and the required history
floor.

### CRDT and tombstone collection

Collection is datatype-aware. It may:

- remove dominated register payloads while retaining their summarized dots;
- fold stable retired counter components into a versioned base;
- remove stable set and map tags after their absence is represented in a
  summarized causal floor;
- remove document tombstone payload and history after retention while keeping
  enough incarnation and causal summary to reject late old-incarnation data;
  and
- discard WAL segments only after a verified snapshot or equivalent state
  fragment covers every commit in them.

The summary format has a version and participates in the causal Merkle root.
No collector may rely only on peer network acknowledgement, elapsed wall time,
or visible state equality.

## Membership and actor lifecycle

Membership is a durable versioned state machine in the coordinated lane. A
configuration names selected Regions, active and joining seats per Partition,
replica ULIDs, actor epochs, and a placement generation.

Every Region value is the registered uppercase airport `RegionCode` from ADR
0033. Membership, replication, snapshots, checkpoints, TBEP, and receipts never
store an OCI Region name, provider alias, or Region ULID.

### Add or replace

1. The old quorum records a joint configuration containing the new seat.
2. Membership allocates a new actor epoch. Erased state never reuses an epoch.
3. The new Member installs a verified snapshot and catches up post-snapshot
   commits.
4. Old and new quorums attest the required stored, applied, and verified
   frontiers.
5. The coordinated lane activates the new configuration.
6. Only then may the new actor originate normal commits.

### Retire or remove

1. The coordinated lane records the joint old and new sets.
2. The retiring actor stops originating new commits at a sealed final counter.
3. Every required seat attests that final actor frontier.
4. The new configuration becomes effective.
5. The actor remains in causal summaries until the stable-frontier and
   retention rules permit compaction.

Adding or removing a Region applies this protocol to every Partition. It is
not a routing-table edit. Add, replace, retire, remove, snapshot bootstrap,
repair, and disaster recovery are durable long-running operations under ADR
0028 with exact per-Partition and aggregate progress.

### Lost quorum

If the old quorum is irrecoverably lost, ordinary membership cannot continue.
An explicitly authorized disaster-recovery procedure chooses a surviving
verified frontier, creates a new cluster and configuration generation, assigns
new actor epochs, and records the exact lost boundary in a Recovery Receipt.
It never pretends the transition was quorum safe.

## Corruption and conflicting identity

A canonical hash mismatch, same `CommitUlid` with different bytes, invalid
snapshot root, impossible counter regression, or inconsistent membership proof
is not an ordinary CRDT conflict. The Member quarantines the source and
affected state range, stops advancing progress, and repairs from another
verified source. If no source can establish one valid history, service for the
affected Partition fails closed and requires recovery authority.

## Implementation ownership

- `terrabase-causal` owns progress clocks and causal proofs.
- `terrabase-crdt` owns joinable state and collection rules.
- storage owns WAL durability, snapshots, manifests, and atomic installation.
- `terrabase-replication-core` owns the replication and repair state machines.
- `terrabase-member-transport` owns bounded QUIC lanes and workload identity.
- `terrabase-cluster` owns durable configuration application, not NATS truth.
- TBEP exposes bounded operations and observations but never defines Member
  convergence.

No external broker stores an authoritative replication queue. NATS may
advertise routes or configuration hints behind a replaceable adapter.

## Performance qualification

ADR 0026 owns the decision-grade method, baselines, metrics, latency budgets,
and completion rule. Initial production readiness covers one, two, and three
selected Regions with real GRU/SFO/FRA WAN evidence and two Members per Region.
Twenty-Region qualification begins only after the three-Region product is
production-ready and operating.

The implementation uses direct event-driven fanout and the pooled QUIC lanes
defined here. Copies, encoding passes, scheduling delay, durability delay,
acknowledgement delay, bytes, CPU, allocations, backlog, and foreground
interference are measured guardrails rather than subjective “avoidable” work.
No timing may weaken RF=2 durability, causal or materialized verification,
fairness, bounded queues, backpressure, repair, or recovery.

## Required evidence

Before P3 exits, deterministic and crash tests must cover:

- every coordinator failure point before and after both RF=2 stores;
- idempotent recovery of one-sided and two-sided partial outcomes;
- writes through either Member in every selected Region;
- duplicate, corrupt, delayed, missing, reordered, and conflicting frames;
- bounded out-of-order interval growth and forced snapshot fallback;
- one-, two-, and three-Region full meshes with bounded pooled connection and
  lane counts;
- process restart between store, apply, publish, and acknowledgement;
- snapshot interruption at every manifest and chunk boundary;
- snapshot adoption with local extra dots;
- causal and materialized Merkle mismatch and range repair;
- old/new joint membership partitions and actor retirement;
- complete Region loss under each durability level;
- causal garbage collection followed by late replay from a retired actor;
- NATS deletion and Orchestrator outage; and
- one tenant saturating replication, repair, and snapshot lanes while another
  remains inside the measured isolation budget.

Every scenario records exact stored, applied, verified, membership, and
recovery frontiers. A green connection indicator is not convergence evidence.
