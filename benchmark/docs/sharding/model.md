# TerraBaseV2 sharding and logical Partition model

Status: accepted target contract under the workspace `GOAL.md` and ADR 0019.
Implementation and production limits remain evidence-gated.

## Stable routing

Each database keeps a permanent `2^20` Bucket space. A collection's Shard Key
maps deterministically to one Bucket. The document identity is the default
Shard Key. Hash identity and normalization are versioned storage-format facts,
not deployment configuration.

The Bucket identity does not contain a Region, Member, logical Partition, or
physical storage address. Rebalancing never changes document identity.

The Bucket range map is ordered, complete, non-overlapping, and covers
`[0, 2^20)`. Adjacent entries for the same logical Partition are merged when
safe.

## Logical Partitions

A logical Partition is a set of Bucket ranges with independent:

- replica-set and membership epoch;
- selected active Region seats;
- server actor counter domains;
- WAL and mutation history;
- causal and gap state;
- CRDT materialization;
- index deltas;
- snapshots and compaction floor;
- causal and materialized Merkle trees;
- resource accounting; and
- split, merge, and move state.

A Partition is not a process, Member, Region, database, permanent range owner,
leader, master, primary, or lease holder.

Every selected active Region stores a replica of every logical Partition. In
production, each selected Region seats two active Members for each Partition.
The accepting Member coordinates one request only.

## Initial shape

A new database starts with one logical Partition spanning the full Bucket
space. This keeps small databases cheap. The system does not preallocate many
empty shards.

The catalog may split a Partition after measured heat, size, causal metadata,
query, compaction, or recovery thresholds are crossed. It may merge cold
adjacent Partitions when the resulting work stays within safety and latency
budgets.

Thresholds are configuration with recorded reason and receipt. They are not
hard-coded product claims.

## Routing

Routing is:

~~~text
Shard Key -> Bucket -> logical Partition -> current replica-set epoch
~~~

Any eligible active Member in the request Region may accept the operation.
The Member validates the current epoch, mints or reuses the stable `CommitId`,
fans the commit to the Region's active Members, and collects the requested
acknowledgement.

There is no owner lookup, leader redirect, primary endpoint, or shard lease.

## Split

A split creates a new Partition identity and moves whole Bucket ranges from the
source. The state machine is idempotent and restartable:

1. record a coordinated split intent and target epoch;
2. establish a causally consistent source cut;
3. stream a verified snapshot and post-cut commits through the replication
   plane;
4. build materialized state and indexes in every selected Region;
5. verify causal and materialized Merkle roots;
6. fence the routing generation and publish the new Bucket map;
7. replay or redirect operations that raced the publication boundary by stable
   idempotency identity; and
8. retain the source range until active, backup, PITR, and recovery pins allow
   collection.

No document receives a new identity. Existing commit identities and causal
history remain valid.

## Move and rebalance

Physical placement changes do not change the logical Partition identity.
Orchestrator selects capacity and failure domains. TerraBase executes typed,
checkpointed snapshot and delta transfer, verifies the new seat, publishes the
overlapping membership epoch, then retires the old seat.

A Member with erased storage receives a new `ReplicaEpoch`. It cannot reuse an
old actor counter.

## Merge

A merge is allowed only for adjacent Bucket ranges whose combined mutation,
causal, snapshot, index, compaction, and recovery load fits current budgets.
The merge is coordinated, restartable, and verified like a split. It cannot
discard causal actors, tombstones, checkpoints, PITR history, or authorization
dependencies prematurely.

## Cross-Partition operations

Ordinary document operations stay inside one Partition. Queries may fan out
with bounded parallelism and merge deterministic results.

Uniqueness, compare-and-swap, one-time consumption, and transactions crossing
Partitions use the explicit coordinated lane. They do not elect a permanent
Partition leader.

## Ownership split

| Concern | Owner |
| --- | --- |
| Hashing, Bucket map validation, routing, causal state, replication, transfer, verification, and migration recovery | TerraBaseV2 |
| Customer placement intent, Member capacity, failure domains, scheduling, and repair orchestration | TerraScale.Orchestrator |
| Public create, update, status, and typed administrative contracts | TerraScale.Database |
| Customer UI and product workflow | TerraScale.Dashboard through `@terrascale/sdk` |

NATS may distribute accepted configuration or discovery hints. It cannot own
the Bucket map, grant a replica seat, publish an effective epoch, or carry
transfer data.

## Required properties

- The same Shard Key always maps to the same Bucket under one storage-format
  version.
- The Bucket map covers the complete space exactly once.
- Every selected active Region stores every logical Partition.
- A routing generation never maps one Bucket to two writable Partitions.
- Split, move, merge, retry, restart, and duplicate delivery are idempotent.
- Equal accepted commit sets produce equal canonical and materialized state.
- A stale epoch fails closed without discarding a commit that may already be
  durable.
- Resource limits apply to migration and repair as well as foreground work.
- A local Partition result is never reported as whole-database or
  all-Region success.

## Open measured calibration

The production maximum number of logical Partitions, automatic split and merge
thresholds, migration concurrency, Bucket-range fragmentation bound, and query
fan-out budget remain measured calibration. The product must not publish the
former 128-shard proposal as a qualified limit until the new native causal
architecture passes its resource and recovery receipts.
