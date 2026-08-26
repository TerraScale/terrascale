# V2 sharding model

Status: proposed decision record for SH-01 (`TerraScale/TerraBaseV2#47`),
2026-08-26. Authority: charter PD-2026-08-26-10 D3/D5, ADR-0004 (accepted
D-1..D-4), ADR 0011 (proposed), and the pinned V1 source identity
`fa99b9257776dbe625e6a2388c74c9ce916637ee`. This record proposes a model;
it implements nothing and resolves no parity row by itself.

The question SH-01 answers: what are shards and buckets in V2, given that
V2 already accepted a replication and ownership model in ADR-0004? The
answer proposed here is that sharding is **not a separate layer**. A shard
is an owned data range in the existing `TopologyView`; a bucket is the
stable routing identity inside that range. Everything else in V1's
sharding — the separate shard databases, the orchestrator, and the
cluster-consensus machinery — is replaced by mapping onto seams V2 already
has.

## V1 reference model (pinned evidence)

Facts from the pinned V1 checkout, cited so later waves can re-verify:

- Total bucket space is fixed at `1024 * 1024` = 2^20 = 1,048,576 buckets
  (`ClientShardHelper.NumberOfBuckets`,
  `src/TerraBase.Client/Util/ClientShardHelper.cs`; re-exported by
  `src/TerraBase.Server/Utils/ShardHelper.cs`). The comment states the
  intent: a 20-bit bucket gives roughly 1 MB of shard size per TB of
  database at even distribution.
- A document's bucket is `XXHash64(lowercased document id) % 2^20`
  (`ClientShardHelper.GetBucketFor`). The bucket is the stable routing
  identity for documents, attachments, tombstones, and per-bucket storage
  accounting (PB-5037).
- The shard mapping is a table of `ShardBucketRange { BucketRangeStart,
  ShardNumber }` entries: ordered, non-overlapping, first entry starts at
  0, adjacent entries never share a shard number (merged-continuous rule,
  `ShardHelper.ValidateBucketsMapping`). A shard may own several disjoint
  ranges; `ShardHelper.MoveBucket` splits and merges ranges one bucket at
  a time.
- A V1 shard is a whole database (`databaseName$shardNumber`) with its own
  replication factor and its own Rachis-managed topology; a separate
  orchestrator node set (`AllOrchestratorNodesExecutor`,
  `OrchestratorTopologyUpdater`, `ClusterObserver`) coordinates database
  records and migrations (PB-5036, PB-5039).
- Bucket migration is orchestrated cluster state:
  `StartBucketMigrationCommand` creates a `ShardBucketMigration` cluster
  task with status `Pending -> Moving -> Moved -> OwnershipTransferred`,
  per-destination confirmations, source-cleanup confirmations, a cluster
  confirmation index, and a last-source change vector;
  `BucketMigrationReplication` streams the bucket's documents;
  `ShardedDocumentsMigrator` drives source and destination to completion
  (PB-5038).
- Prefixed sharding settings pin id prefixes to explicit shards using
  virtual bucket ranges starting above 2^20
  (`AddPrefixedShardingSettingCommand`), and sticky ids give per-document
  affinity (`ShardHelper.GenerateStickyId`).

## Proposed V2 model

### 1. A shard is an owned data range

The unit of distribution and ownership in V2 is already defined by
ADR-0004 D-1 and `src/replication/topology.rs`: a data range with exactly
one owning Member (the write-path leader), an ownership epoch for fencing,
and asynchronous follower catch-up through the publication history. The
proposal:

- **Shard = data range.** Each shard appears in the `TopologyView` as one
  `RangeOwnership { range, owner, epoch }` entry. The owner is the
  shard's single leader; followers replicate it exactly as any other
  range. No new ownership concept is introduced.
- **Shard identity is stable.** The range identifier carried in the view
  is the shard's stable identity (proposed canonical form
  `shard/<shard-id>`), not its bucket boundaries. Resharding changes
  which buckets the shard owns, never the shard's identity, so epochs and
  fencing history stay attached to a durable identity.
- The existing builder and validation seam is unchanged: `TopologyView`
  keeps treating range text as an opaque validated identifier
  (`with_owner_at_epoch`), so this model adds no code to `topology.rs`
  now and constrains no later representation beyond the canonical form.

### 2. A bucket is the stable routing identity inside a range

- **Bucket space is fixed at 2^20**, matching V1. This is permanent per
  database: the bucket of a shard key must never change, because a
  different bucket count re-routes every existing document at once. A
  change would be a new storage-format-scale decision, not a
  configuration knob.
- **Bucket = hash of the shard key, reduced into [0, 2^20)**. The default
  shard key is the document identity, lowercased, hashed as in V1
  (XXHash64 family); the exact hash identity is fixed by SH-02 with a
  V1-parity comparison test, because the bucket value itself is not
  observable parity (only stability and distribution are). A
  non-identity shard key is a per-collection typed option owned by the
  engine; SH-02 defines it.
- **The bucket-range table maps buckets to shards.** It is an ordered,
  non-overlapping set of bucket ranges whose union is exactly
  [0, 2^20), each entry naming one shard, with adjacent-same-shard
  entries merged (V1's rule). The engine validates these invariants;
  the Platform constructs the table.

### 3. How the two structures compose

The control plane (Platform/Orchestrator) derives, from the bucket-range
table, one `RangeOwnership` per shard and issues the resulting
`TopologyView`. The engine validates, and refuses otherwise:

- every bucket in [0, 2^20) maps to exactly one shard (complete
  partition, no gaps, no overlaps, no adjacent duplicates);
- every shard named by the table has exactly one owner and one epoch in
  the view;
- epochs never decrease for a shard identity (the existing D-1 rule);
- the view is otherwise servable (regions non-empty, every region seats a
  member, failure domains do not span regions).

Routing is then two deterministic lookups: shard key -> bucket -> shard
(bucket-range table) -> owner Member and epoch (view). Write admission
uses the existing admission-time ownership validation plus apply-time
epoch checks; nothing new is added to the write path.

An unsharded V2 database is the degenerate case: the whole key space is
one shard with a one-entry bucket-range table (`0..2^20 -> shard/0`). The
model has a single shape, which is what keeps the multi-region contract
uniform.

### 4. Composition with multi-region replication

ADR-0004's replication decisions apply per shard, unchanged:

- Each shard has one leader (its owning Member, placed by the Platform in
  some Region) and followers that catch up asynchronously through the
  publication history. The acknowledgement lattice
  (`Local`, `RegionQuorum`, `MajorityRegions`, `AllRegions`) applies to a
  write routed to one shard exactly as it applies today.
- Follower placement across Regions and failure domains is Platform
  placement policy over the same `TopologyView` inputs. The engine only
  validates the view; it never chooses placement.
- **A shard-local acknowledgement is never whole-database success**
  (PB-5048's rule). Operations that span shards (cluster transactions,
  fan-out queries) are SH-04/SH-05 work; they compose per-shard results
  with typed outcomes and never infer database-wide durability from one
  shard's ack.
- Bucket migration between shards that live in different Regions reuses
  the replication plane: the source shard's owner streams the bucket's
  committed records to the destination shard's owner through the same
  bounded, idempotent catch-up machinery (`MAX_CATCH_UP_BATCH`), under
  the destination's epoch fencing. There is no separate migration
  transport; there is a migration orchestration state machine (SH-03)
  riding on the replication plane.
- Resharding across Regions is therefore a ownership transition per
  ADR-0004: the Platform raises the affected shards' epochs, migration
  copies the bucket, ownership transfers, and the source's post-transfer
  writes to that bucket are fenced by epoch rather than silently lost.

### 5. Resharding and bucket migration (shape only; SH-03 owns detail)

The V1 state machine is the right observable contract and is kept:
pending, moving (source streams to destination, restartable, idempotent),
moved (destination confirmed complete against the source's last change
identity), ownership transferred (bucket-range table updated, epochs
raised, source cleanup). SH-03 owns the typed operations, progress
reporting (source, destination, bucket range, resolved records), and
interruption/restart evidence against PB-5038's semantics.

### 6. Platform ownership boundary

| Concern | Owner | Notes |
| --- | --- | --- |
| Bucket hashing and shard-key resolution | engine | stable routing identity; fixity is a contract |
| Bucket-range table invariants and validation | engine | refuses non-partitions |
| Routing a document to its shard owner | engine | write admission and reads |
| Bucket-scoped statistics and storage accounting | engine | PB-5037 |
| Migration execution (streaming, idempotence, progress, restart) | engine | over the replication plane |
| Epoch/ownership enforcement and fencing | engine | existing D-1 mechanism |
| Shard count, bucket-range construction, rebalancing decisions | Platform | issues the table and the view |
| Placement of shard leaders/followers across Regions and failure domains | Platform | placement policy, never engine |
| When to reshard; migration scheduling | Platform | drives typed engine operations |
| Cross-shard and cross-database orchestration surfaces | Platform | composes typed engine operations |

This is ADR-0002's separation applied to sharding: the engine owns
database behavior (routing, durability, migration mechanics), the
Platform owns provisioning, placement, and lifecycle. The engine must not
grow an orchestrator that decides topology; the Platform must not reach
past the typed operations into engine internals.

## Invariants (testable once SH-02 lands)

1. The bucket of a shard key is a pure function of the shard key; it
   never depends on topology state.
2. A valid configuration partitions [0, 2^20) exactly; every shard has
   exactly one owner and epoch; epochs are monotonically non-decreasing
   per shard identity.
3. A write is admitted only by the current owner at the current epoch of
   the routed shard; deposed owners are fenced by the existing mechanism.
4. A bucket has at most one owning shard at any epoch; during migration
   the destination is not authoritative until the ownership transfer.
5. An operation's acknowledgement names the Members and Regions that
   contributed, per shard; nothing reports a shard-local fact as
   database-wide.

## Deliberately not decided here

- Exact hash identity and non-identity shard keys (SH-02).
- Migration orchestration operations and their evidence (SH-03).
- Sharded query and index execution (SH-04; PB-4036/PB-4060).
- Sharded failover, cluster transactions, replication/backup/rejoin, and
  the consensus-row dispositions' final proofs (SH-05).
- Prefixed routing and sticky ids: V1 behaviors noted above are deferred
   to SH-02 design; until then they are not supported (see
  `topology-limits.md`).
- Production topology limits are proposed in `topology-limits.md` and
  accepted with ADR 0011, not here.
