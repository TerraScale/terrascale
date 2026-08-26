# Proposed sharded-topology production limits

Status: proposal for SH-01 (`TerraScale/TerraBaseV2#47`), 2026-08-26.
This record resolves the charter's intentionally unresolved
"production topology limits" item **for sharded topologies**, as directed
by PD-2026-08-26-10 D3. It becomes binding only when the parent accepts
ADR 0011; until then every limit below is `unresolved` state, per the
charter's decision rules. ADR-0004 D-4 remains in force separately: no
artifact may state a production topology limit before this record is
accepted, and W4's three-synthetic-Region lane facts stay lane-scoped.

## Proposed limits

| Limit | Proposed value | Class |
| --- | --- | --- |
| Bucket space (per database, permanent) | 2^20 = 1,048,576 buckets | invariant — never configurable, never migrated |
| Maximum shards per database | 128 | production limit |
| Minimum initial bucket allocation per shard | 8,192 buckets | allocation guard (not a runtime invariant) |
| Bucket-range table entries | at most 4x shard count after compaction | soft bound, Platform-maintained |
| Concurrent bucket migrations per database | at most shards/2 in flight | migration guard, SH-03 enforces |
| Shard key length / hash identity | fixed by SH-02; identity-keyed default | contract, not a limit |

The two headline numbers are derived, not arbitrary:

- **2^20 buckets.** V1 parity (`ClientShardHelper.NumberOfBuckets`,
  pinned commit `fa99b925…`). The bucket of a shard key must be a
  permanent pure function of the key; a different bucket count re-routes
  every document at once, so changing it is a storage-format decision,
  never a configuration change. 2^20 keeps V1's distribution property
  (roughly 1 MB of shard size per TB of database at even spread).
- **128 shards.** The binding constraint is the allocation floor:
  2^20 / 8,192 = 128, so a fully balanced database cannot exceed 128
  shards without shards below the floor. The floor comes from RavenDB
  operational guidance relayed in the SH-01 brief (8,192 buckets per
  shard): below it, rebalancing granularity approaches per-bucket
  bookkeeping cost and the bucket-range table fragments faster than
  compaction can merge it. 128 shards also bounds what SH-04's query
  fan-out and SH-05's cross-shard transactions must qualify against, and
  keeps each view's `RangeOwnership` set (and its epoch history) small
  enough to validate and replicate cheaply.

### Why the floor is an allocation guard, not a runtime invariant

V1 moves buckets one at a time (`ShardHelper.MoveBucket` splits ranges
per bucket), so any rebalancing transiently leaves shards with
non-contiguous, sub-floor bucket sets. Requiring the floor at all times
would forbid mid-migration states that the migration state machine needs.
The guard applies when the Platform constructs or rebalances the initial
table; migration mechanics may transiently fragment, and range
compaction (adjacent-same-shard merge) restores the soft bound.

## Supported topologies (once accepted, and only after each wave's proofs)

| Topology | Status |
| --- | --- |
| Unsharded database (one shard covering [0, 2^20)) | supported today via the existing single-range seam; degenerate case of this model |
| 2..128 shards, one Region | supported after SH-02/SH-03 proofs |
| 2..128 shards, multiple Regions, followers spanning failure domains | supported after SH-05 multi-Region qualification |
| Shard leaders spread across Regions (placement policy) | supported — Platform placement; engine validates the resulting view only |
| Resharding and bucket migration in flight | supported after SH-03 fault-lane evidence |

## Not supported (rejected at validation)

- Bucket tables that do not partition [0, 2^20) exactly (gaps, overlaps,
  adjacent duplicates) — the engine refuses the view.
- More than 128 shards, or any shard without exactly one owner and
  epoch — refused at validation.
- A shard's ownership split across Members: D-1's one-owner rule applies
  per shard without exception.
- Failure domains spanning Regions (existing `with_member` rule).
- Region-free or member-free views (existing `validated` rule).

## Not yet supported (deferred, tracked)

- Prefixed routing and sticky ids (V1 behaviors recorded in
  `model.md`; SH-02 design decides follow or accept-difference).
- Cross-shard transactions and sharded fan-out queries (SH-04/SH-05;
  until then, operations are shard-routed only).
- Dynamically growing past 128 shards within one database lifetime —
  use a new database or await a limit revision with SH-05 evidence;
  this record sets the first production envelope and does not promise
  elasticity beyond it.

## Qualification obligations these limits create

- SH-02 proves validation behavior at the boundaries: 128 shards,
  129 refused, incomplete partitions refused, epoch regression refused.
- SH-03 proves migration guard behavior: concurrent-migration bound,
  restart mid-flight below floor, compaction restoring the soft bound.
- SH-05 qualifies the multi-Region envelope: leaders and followers of
  one shard across at least three Regions and distinct failure domains,
  region loss with the limits of ADR-0004 D-2, resharding across
  Regions, rejoin. Every receipt names contributing Members and Regions;
  no shard-local success is reported as database success (PB-5048).
- Any performance statement about sharded topologies requires a
  Benchmark Receipt under PD-2026-08-26-03; none is made here.

## Relation to the charter's unresolved list

"Production topology limits" in the charter's unresolved-decisions list
is resolved for sharded topologies by this record plus ADR 0011
acceptance. Non-sharded production limits (Region counts, member counts
per Region for the replication plane) remain governed by ADR-0004 D-4's
successor decision and are not claimed here.
