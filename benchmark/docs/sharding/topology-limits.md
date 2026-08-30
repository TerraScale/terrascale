# TerraBase topology limits

Status: current accepted envelope and unqualified calibration.

Authority: workspace `GOAL.md`, ADR 0019, and
`docs/sharding/model.md`.

## Accepted product envelope

| Dimension | Contract |
| --- | --- |
| Active Regions per database | One through twenty, constrained by the current catalog and residency policy |
| Initial Region catalog | GRU, SFO, FRA |
| Active Members per selected Region and Partition | Two in production |
| Default production acknowledgement | `RegionalDurable`, both local active Members |
| WAN-bound acknowledgement | `GlobalDurable`, every selected active Region |
| Backup | Independent opt-in, read-only, immutable, non-voting |
| Bucket space | `2^20` per database, permanent within a storage-format version |
| Initial logical Partitions | One spanning the full Bucket space |
| Region replication topology | Direct full mesh through twenty Regions |
| DevHost topology | GRU/SFO/FRA RF=1, development evidence only |

A request arriving outside the selected active Region set routes to an eligible
selected Region or fails with a typed placement result. It cannot claim local
durability in a Region that stores no database replica.

## Limits that are not qualified

The following values need deterministic resource, recovery, and benchmark
receipts before they become public production limits:

- maximum logical Partitions per database;
- automatic split and merge thresholds;
- maximum Bucket-range table entries;
- concurrent split, merge, and move operations;
- maximum fan-out for queries and coordinated operations;
- maximum active Members sharing one physical host;
- per-Member tenant, database, subscription, and dependency cardinality; and
- catch-up and snapshot concurrency.

The former 128-shard proposal depended on a permanent single-owner shard model.
It is superseded and cannot be quoted as a native causal architecture limit.

## Admission rules

Until calibration passes, configuration admission must fail before allocation
when a requested shape exceeds the qualified envelope. It must not guess a
limit from available memory or let an unbounded map grow.

Every admitted limit records:

- engine and protocol version;
- workload and data distribution;
- active Region and Member shape;
- tenant count and resource shares;
- memory, CPU, disk, I/O, and network budgets;
- fault and recovery schedule;
- result and confidence interval; and
- the Recovery or Benchmark Receipt.

Increasing a limit requires evidence at the new boundary and one step beyond
it that fails safely. A passing happy path is not enough.

## Noisy-neighbor qualification

Topology capacity is valid only if one adversarial tenant cannot prevent a
small healthy tenant from meeting its admitted service budget. Qualification
must exercise:

- write and query CPU saturation;
- cache and result-buffer pressure;
- WAL, compaction, and temporary spill pressure;
- many subscriptions and dependency entries;
- replication catch-up and Merkle repair;
- snapshot, backup, and restore traffic; and
- Orchestrator scale-out delay.

TerraBase admission and backpressure protect the Member before new capacity
arrives. Autoscaling cannot be the only noisy-neighbor control.

## Region admission

A Region enters the public catalog only after Dashboard, Database API,
TerraBase system Project replicas, public routing, workload identity,
authorization, replication, recovery, and failure receipts pass. Catalog
admission policy must remain explicit. Merely discovering a Region through
NATS does not make it writable.
