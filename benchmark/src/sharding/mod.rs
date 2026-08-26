//! Sharding as owned ranges over the bucket-range table (ADR 0011,
//! PB-5036/5037, SH-02).
//!
//! Sharding here is routing, validation, and table accounting over the
//! accepted replication plane — not a new layer and not an engine-internal
//! orchestrator. A [`ShardId`] names an owned data range in a caller-supplied
//! [`TopologyView`](crate::replication::TopologyView) with one owning Member
//! and one ownership epoch; a [`Bucket`] is the permanent routing identity of
//! a document, derived from its shard key into the fixed 2^20 bucket space;
//! the [`BucketRangeTable`] is the Platform-constructed, engine-validated
//! partition of that space; and [`ShardTopology`] seats a table against a
//! view and routes documents deterministically:
//! shard key -> bucket -> shard -> owner Member and epoch.
//!
//! SH-02 decisions recorded here (the SH-01 records left them open):
//!
//! - **Hash identity.** The bucket is `XXH64(shard-key bytes, seed 0)`'s
//!   value reduced modulo 2^20. The engine carries its own fixed, safe-Rust
//!   XXH64 (the private `hash` module); the digests are anchored to
//!   reference-implementation vectors in `src/sharding/tests.rs`, because
//!   the bucket value itself is not observable parity — only stability and
//!   distribution are.
//! - **Canonical shard identity.** The range text the Platform and the
//!   engine agree on is `shard/<number>` (decimal `u32`, no leading zeros):
//!   exactly one text per identity, independent of the shard's bucket
//!   boundaries. Views whose owned ranges are not in this form refuse to
//!   seat.
//! - **Identity shard key.** The default shard key is the document identity,
//!   lowercased with the Unicode-aware lowercase mapping (V1 hashes the
//!   lowercased identity), hashed as UTF-8 bytes. A collection may instead
//!   declare a field shard key through a typed declaration
//!   ([`CollectionShardKey`]); see [`ShardKeyKind`]. Changing a collection's
//!   shard key is a migration-scale decision, never a silent reroute.
//! - **Prefixed routing and sticky ids: deferred, not supported.** V1 pins
//!   id prefixes to explicit shards through virtual bucket ranges above 2^20
//!   and gives per-document sticky affinity. Both are refused structurally
//!   by this model: a [`Bucket`] or [`BucketRange`] can never leave
//!   [0, 2^20), and the bucket is a pure function of the shard key with no
//!   per-document override. Following either V1 behavior requires a new
//!   decision record; until then the rows stay unresolved.
//!
//! Boundaries: the Platform constructs tables and issues views (placement
//! and rebalancing policy are never engine decisions); the engine validates
//! invariants, routes, and executes bucket moves as explicit operations
//! ([`BucketRangeTable::with_bucket_moved`]). This seam's accounting is the
//! routing-level kind — which shard owns which buckets, and how many
//! ([`BucketRangeTable::bucket_count_of`]); per-bucket storage accounting
//! (bytes per bucket) waits until the storage seam carries bucket identity,
//! and is not claimed here. Migration streaming, sharded query fan-out, and
//! failover qualification belong to SH-03..SH-05 and are not claimed here.
//! No sharded performance claim is made or implied.

mod bucket;
pub(crate) mod hash;
mod routing;
mod table;
#[cfg(test)]
mod tests;

use crate::replication::MemberId;
use crate::types::{CollectionName, TenantId};

pub use bucket::{
    Bucket, CollectionShardKey, ShardId, ShardKey, ShardKeyField, ShardKeyKind, ShardKeySet,
};
pub use routing::{RoutePlacement, RoutingRequest, ShardTopology};
pub use table::{BucketRange, BucketRangeTable};

/// The per-database bucket space: 2^20 = 1,048,576 buckets.
///
/// A permanent invariant, never a configuration knob: the bucket of a shard
/// key must never change, because a different bucket count re-routes every
/// existing document at once (ADR 0011 D2, V1 parity).
pub const BUCKET_SPACE: u32 = 1 << 20;

/// The maximum number of shards one database's bucket-range table may name.
///
/// The binding constraint is the 8,192-bucket initial allocation floor
/// (`MIN_INITIAL_BUCKET_ALLOCATION`): 2^20 / 8,192 = 128, so a balanced
/// database cannot exceed 128 shards without shards below the floor
/// (`docs/sharding/topology-limits.md`, accepted with ADR 0011).
pub const MAX_SHARDS: usize = 128;

/// The minimum bucket allocation a shard receives when the Platform
/// constructs or rebalances the initial table.
///
/// An allocation guard, not a runtime invariant: mid-migration states may
/// transiently fall below it, and only the initial-allocation guard
/// ([`BucketRangeTable::initial_allocation_guard`]) enforces it.
pub const MIN_INITIAL_BUCKET_ALLOCATION: u64 = 8_192;

/// Typed sharding failure categories.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ShardingError {
    /// A bucket value or bucket-range bound lies outside the fixed
    /// [0, 2^20) space. This is the structural refusal of any scheme that
    /// would route through virtual buckets above the space.
    BucketAboveSpace {
        /// The refused value.
        value: u32,
    },
    /// A bucket-range table was empty: a table must partition the space, so
    /// at least one entry exists.
    EmptyTable,
    /// A bucket range was empty (`start == end`): an entry must hold at
    /// least one bucket.
    EmptyBucketRange {
        /// The refused start bound.
        start: u32,
        /// The refused end bound.
        end: u32,
    },
    /// The table's first entry does not start at bucket zero, so the
    /// partition has a gap at the bottom of the space.
    TableDoesNotStartAtZero {
        /// The first entry's start bound.
        first_start: u32,
    },
    /// The table's last entry does not end at the space end, so the
    /// partition has a gap at the top of the space.
    TableDoesNotCoverBucketSpace {
        /// The last entry's end bound.
        last_end: u32,
    },
    /// Two entries overlap at this bucket.
    TableOverlap {
        /// The bucket both entries claim.
        at: u32,
    },
    /// Two entries leave a gap starting at this bucket.
    TableGap {
        /// The first bucket no entry covers.
        at: u32,
    },
    /// Two adjacent entries name the same shard: the Platform must hand over
    /// tables with adjacent-same-shard entries merged (V1's rule).
    AdjacentShardsNotMerged {
        /// The bucket boundary where the duplicate begins.
        at: u32,
    },
    /// The table names more shards than the accepted production limit
    /// (`MAX_SHARDS`).
    TooManyShards {
        /// The refused shard count.
        count: usize,
    },
    /// An initial allocation left a shard below the
    /// `MIN_INITIAL_BUCKET_ALLOCATION` floor.
    InitialAllocationBelowFloor {
        /// The under-allocated shard.
        shard: ShardId,
        /// The buckets it received.
        buckets: u64,
        /// The floor it fell short of.
        floor: u64,
    },
    /// A view's owned range is not a canonical shard identity (`shard/<number>`).
    ShardIdentityOutsideCanonicalForm {
        /// The refused range text.
        range: String,
    },
    /// The table names a shard the view gives no owner to.
    ShardWithoutOwner {
        /// The unseated shard.
        shard: ShardId,
    },
    /// The view owns a shard the table gives no buckets.
    OwnedRangeWithoutBuckets {
        /// The bucket-less owned shard.
        shard: ShardId,
    },
    /// The caller-supplied view is not servable membership input.
    ViewNotServable,
    /// A bucket move named a source shard that does not own the bucket.
    BucketNotOwnedBySource {
        /// The bucket the move named.
        bucket: Bucket,
        /// The source the move claimed.
        source: ShardId,
        /// The shard that actually owns the bucket.
        owner: ShardId,
    },
    /// A collection declaration was made twice for one collection.
    DuplicateCollectionShardKey {
        /// The collection declared twice.
        collection: CollectionName,
    },
    /// A collection or field name was not a valid logical identifier.
    InvalidShardKeyName {
        /// The refused text.
        name: String,
    },
    /// A field shard key was used without the document value that carries
    /// the fields.
    ShardKeyValueRequired {
        /// The field-keyed collection.
        collection: CollectionName,
    },
    /// A declared shard-key field is absent from the document: field keys
    /// fail closed, never silently re-key.
    ShardKeyFieldMissing {
        /// The field-keyed collection.
        collection: CollectionName,
        /// The absent field's name.
        field: String,
    },
    /// A declared shard-key field holds a value kind the key cannot render.
    ShardKeyFieldUnsupported {
        /// The field-keyed collection.
        collection: CollectionName,
        /// The unsupported field's name.
        field: String,
    },
    /// A placement was presented under a tenant it was not routed for.
    TenantMismatch {
        /// The tenant the placement is bound to.
        routed_for: TenantId,
        /// The tenant that presented it.
        requested_by: TenantId,
    },
    /// A placement's bucket no longer routes to its shard (a bucket move
    /// transferred it).
    PlacementStale {
        /// The placement's bucket.
        bucket: Bucket,
        /// The shard the placement names.
        was_shard: ShardId,
        /// The shard that owns the bucket now.
        now_shard: ShardId,
    },
    /// A placement names a superseded owner: the shard's leadership moved.
    PlacementOwnerSuperseded {
        /// The placement's shard.
        shard: ShardId,
        /// The owner the placement names.
        was: MemberId,
        /// The owner the topology seats now.
        now: MemberId,
    },
    /// A placement names a superseded epoch: the shard's ownership epoch was
    /// raised, fencing everything seated below it.
    PlacementEpochSuperseded {
        /// The placement's shard.
        shard: ShardId,
        /// The epoch the placement names.
        was: u64,
        /// The epoch the topology seats now.
        now: u64,
    },
    /// A view transition offered a lower epoch for a shard identity than
    /// the topology holds.
    EpochRegression {
        /// The shard whose epoch would decrease.
        shard: ShardId,
        /// The epoch the topology holds.
        held: u64,
        /// The epoch the transition offered.
        offered: u64,
    },
    /// A transition changed a shard's buckets or owner without raising its
    /// epoch: ownership transfers end with raised epochs so a stale owner is
    /// fenced.
    EpochNotRaised {
        /// The shard whose epoch did not rise.
        shard: ShardId,
        /// The epoch the topology holds.
        held: u64,
        /// The epoch the transition offered.
        offered: u64,
    },
}
