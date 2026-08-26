# Proposed CONTEXT.md sharding terms

Status: proposed additions for SH-01 (`TerraScale/TerraBaseV2#47`),
2026-08-26, companion to ADR 0011 (proposed) and
`docs/sharding/model.md`. These entries are written here, not into
`CONTEXT.md`; the integrator promotes them when the parent accepts the
ADR, keeping `CONTEXT.md` a single-writer shared file per the charter's
serialized-shared-file rule. The format follows `CONTEXT.md`: term,
definition, `_Avoid_` with reasons where a synonym would change contract
meaning.

## Sharding

**Shard**:
The unit of data distribution and ownership: a set of buckets owned by
exactly one Member as one owned data range in the TopologyView, with one
ownership epoch, replicated as a unit under ADR-0004. A shard is a range
and its stable identity, never a separate database or process.
_Avoid_: shard database, node (a Member is the participant; a shard is
data), partition (a shard's bucket set may be non-contiguous),
replica set (replication follows ADR-0004's range ownership, not
per-shard election)

**Bucket**:
The permanent routing identity of a document, derived from its Shard Key
hash into the fixed [0, 2^20) space. A bucket belongs to exactly one
shard at any epoch; it is the unit of migration and of bucket-scoped
storage accounting. Its value is a pure function of the shard key and
never changes.
_Avoid_: slot, vbucket, chunk (migration moves whole buckets; there is
no smaller chunk), range (a range is a set of buckets — saying "range"
for one bucket blurs the Bucket Range term), hash (the bucket is the
reduced value, not the hash itself)

**Shard Key**:
The document attribute(s) whose hash determines the document's Bucket;
the document identity, lowercased, by default. Declared per collection
through a typed operation; changing a collection's shard key is a
migration-scale decision, never a silent reroute.
_Avoid_: partition key, routing key (routing is the two-step lookup
shard key -> bucket -> shard; calling the key "routing" hides the bucket
step), primary key (the identity is the default shard key, not the
definition)

**Bucket Range**:
One entry of the bucket-range table: a contiguous [start, end) interval
of buckets mapped to one Shard, such that the table partitions
[0, 2^20) exactly with adjacent-same-shard entries merged.
_Avoid_: shard range (the range belongs to the table, not the shard's
identity; a shard may own several disjoint ranges), slot range

**Bucket Migration**:
The explicit, idempotent, restartable engine operation that moves one
Bucket from a source Shard to a destination Shard through the replication
plane, ending in a bucket-range ownership transfer with raised epochs.
Its progress is observable (source, destination, resolved records).
_Avoid_: resharding (the Platform-level activity of many migrations plus
table changes; a single migration is the engine operation), rebalance
(a Platform policy decision, not an engine operation), move chunk

## Note for promotion

On acceptance of ADR 0011, add the terms above under a new
`## Sharding` section of `CONTEXT.md` between "Topology and replication"
and "Sync engine and engine protocol" (they build on Region, Member, and
the ownership model). Review the cross-references: `Failure Domain`
already reserves "outage bucket" as an avoided synonym, which stays
correct — that "bucket" is not this Bucket.
