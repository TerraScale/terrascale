//! Seating a bucket-range table against a `TopologyView` and routing
//! documents through it.
//!
//! [`ShardTopology`] is the engine-side composition of ADR 0011's model: the
//! Platform issues a bucket-range table (which buckets each shard owns) and
//! a `TopologyView` (which Member owns each shard range at which epoch); the
//! engine validates the pair and routes documents deterministically —
//! shard key -> bucket -> shard -> owner Member and epoch. Nothing here
//! provisions, places, or rebalances: those are Platform decisions the
//! engine only validates the results of.

use crate::replication::{MemberId, TopologyView};
use crate::types::{CollectionName, Document, DocumentId, DocumentValue, TenantId};

use super::{Bucket, BucketRange, BucketRangeTable, ShardId, ShardKeySet, ShardingError};

/// A validated bucket-range table seated against a `TopologyView`: one
/// `RangeOwnership` per shard, ready to route.
///
/// Seating validates the pair in both directions: every shard the table maps
/// buckets to has exactly one owner and epoch in the view (the view builder
/// enforces one ownership per range text), and every owned range of the view
/// is a canonical shard identity the table maps buckets to. Write admission
/// and fencing stay with the existing replication seam: a
/// [`RoutePlacement`] names the owner and epoch the existing
/// admission-time ownership validation and apply-time epoch checks act on.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardTopology {
    table: BucketRangeTable,
    view: TopologyView,
    keys: ShardKeySet,
}

impl ShardTopology {
    /// Seats one table against one view.
    ///
    /// # Errors
    ///
    /// Returns [`ShardingError::ViewNotServable`] when the view is not
    /// servable membership input,
    /// [`ShardingError::ShardIdentityOutsideCanonicalForm`] when the view
    /// owns a range outside the `shard/<number>` convention,
    /// [`ShardingError::ShardWithoutOwner`] when the table maps buckets to a
    /// shard the view gives no owner to, and
    /// [`ShardingError::OwnedRangeWithoutBuckets`] when the view owns a
    /// shard the table gives no buckets.
    pub fn try_new(table: BucketRangeTable, view: TopologyView) -> Result<Self, ShardingError> {
        if !view.is_servable() {
            return Err(ShardingError::ViewNotServable);
        }
        let mut owned: Vec<ShardId> = Vec::new();
        for range in view.owned_ranges() {
            let shard = ShardId::from_range_text(range).ok_or_else(|| {
                ShardingError::ShardIdentityOutsideCanonicalForm { range: range.to_owned() }
            })?;
            owned.push(shard);
        }
        let seated = table.shards();
        for shard in &seated {
            if !owned.contains(shard) {
                return Err(ShardingError::ShardWithoutOwner { shard: *shard });
            }
        }
        for shard in &owned {
            if !seated.contains(shard) {
                return Err(ShardingError::OwnedRangeWithoutBuckets { shard: *shard });
            }
        }
        Ok(Self { table, view, keys: ShardKeySet::identity() })
    }

    /// Names the per-collection shard-key declarations this topology routes
    /// by (identity-keyed everywhere by default). A value transformation:
    /// the caller that changes a declaration owns the migration-scale
    /// consequences of re-keying a collection.
    #[must_use]
    pub fn with_shard_keys(mut self, keys: ShardKeySet) -> Self {
        self.keys = keys;
        self
    }

    /// Returns the seated bucket-range table.
    #[must_use]
    pub fn table(&self) -> &BucketRangeTable {
        &self.table
    }

    /// Returns the membership view the table is seated against.
    #[must_use]
    pub fn view(&self) -> &TopologyView {
        &self.view
    }

    /// Returns the per-collection shard-key declarations in force.
    #[must_use]
    pub fn shard_keys(&self) -> &ShardKeySet {
        &self.keys
    }

    /// Returns the shard one bucket maps to.
    ///
    /// # Panics
    ///
    /// Cannot panic for a seated table: a validated table covers every
    /// bucket of the fixed space.
    #[must_use]
    pub fn shard_of(&self, bucket: Bucket) -> ShardId {
        self.table.shard_of(bucket)
    }

    /// Returns the Member that owns one shard's write path.
    #[must_use]
    pub fn owner_of(&self, shard: ShardId) -> Option<MemberId> {
        self.view.owner_of(&shard.as_range()).cloned()
    }

    /// Returns the ownership epoch one shard is owned at.
    #[must_use]
    pub fn epoch_of(&self, shard: ShardId) -> Option<u64> {
        self.view.owner_epoch_of(&shard.as_range())
    }

    /// Routes one document request to its owning Member.
    ///
    /// Routing is deterministic and testable: a pure function of the
    /// shard-key declarations, the seated table, and the view — the same
    /// request always yields the same placement, and the bucket never
    /// depends on topology state.
    ///
    /// # Errors
    ///
    /// Returns the resolution failures of
    /// [`ShardKeySet::resolve`](crate::sharding::ShardKeySet::resolve)
    /// (field-keyed collection without a value, missing field, unsupported
    /// field value) or [`ShardingError::ShardWithoutOwner`] if the seated
    /// pair stopped covering the routed shard.
    pub fn route(&self, request: &RoutingRequest<'_>) -> Result<RoutePlacement, ShardingError> {
        let key = self.keys.resolve(request.collection(), request.document(), request.value())?;
        let bucket = key.bucket();
        let shard = self.table.shard_of(bucket);
        let owner = self.owner_of(shard).ok_or(ShardingError::ShardWithoutOwner { shard })?;
        let epoch = self.epoch_of(shard).ok_or(ShardingError::ShardWithoutOwner { shard })?;
        Ok(RoutePlacement { tenant: request.tenant().clone(), bucket, shard, owner, epoch })
    }

    /// Checks one placement against this topology under the named tenant.
    ///
    /// A placement is honored only for the tenant it was routed for, only
    /// while its bucket still routes to its shard, and only while its owner
    /// and epoch are still seated — so a transferred bucket or a raised
    /// epoch fences stale placements exactly as the replication seam's
    /// apply-time checks fence stale owners.
    ///
    /// # Errors
    ///
    /// Returns [`ShardingError::TenantMismatch`] when the placement is
    /// presented under a foreign tenant,
    /// [`ShardingError::PlacementStale`] when its bucket moved,
    /// [`ShardingError::PlacementOwnerSuperseded`] when its shard changed
    /// leader, and [`ShardingError::PlacementEpochSuperseded`] when its
    /// shard's epoch was raised.
    pub fn authorize_placement(
        &self,
        placement: &RoutePlacement,
        tenant: &TenantId,
    ) -> Result<(), ShardingError> {
        if placement.tenant() != tenant {
            return Err(ShardingError::TenantMismatch {
                routed_for: placement.tenant().clone(),
                requested_by: tenant.clone(),
            });
        }
        let now_shard = self.table.shard_of(placement.bucket());
        if now_shard != placement.shard() {
            return Err(ShardingError::PlacementStale {
                bucket: placement.bucket(),
                was_shard: placement.shard(),
                now_shard,
            });
        }
        let now_owner = self
            .owner_of(placement.shard())
            .ok_or(ShardingError::ShardWithoutOwner { shard: placement.shard() })?;
        if now_owner != *placement.owner() {
            return Err(ShardingError::PlacementOwnerSuperseded {
                shard: placement.shard(),
                was: placement.owner().clone(),
                now: now_owner,
            });
        }
        let now_epoch = self
            .epoch_of(placement.shard())
            .ok_or(ShardingError::ShardWithoutOwner { shard: placement.shard() })?;
        if now_epoch != placement.epoch() {
            return Err(ShardingError::PlacementEpochSuperseded {
                shard: placement.shard(),
                was: placement.epoch(),
                now: now_epoch,
            });
        }
        Ok(())
    }

    /// Seats the next table/view pair as a transition from this topology.
    ///
    /// The next pair must validate on its own, and the transition must keep
    /// the fencing facts: a shard identity's epoch never decreases, and a
    /// shard whose buckets or owner changed must raise its epoch — the
    /// ownership transfer that ends a bucket move (ADR 0011 D3). Shards that
    /// leave the table (drained) are transitions of the shards that took
    /// their buckets. A shard identity the seated table does not name — a
    /// new or returning identity entering the table — must seat above the
    /// fencing bar of the buckets it takes (the highest epoch this topology
    /// holds over them): re-identifying a shard never resets the fencing
    /// facts of its buckets.
    ///
    /// # Errors
    ///
    /// Returns the seating failures of [`ShardTopology::try_new`],
    /// [`ShardingError::EpochRegression`] when a shard's offered epoch is
    /// below the held one, [`ShardingError::EpochNotRaised`] when a
    /// shard's buckets or owner changed without a raised epoch, and
    /// [`ShardingError::EnteringShardEpochNotRaised`] when an entering
    /// shard identity seats at or below the fencing bar of its buckets.
    pub fn transition(
        &self,
        table: BucketRangeTable,
        view: TopologyView,
    ) -> Result<Self, ShardingError> {
        let next = Self::try_new(table, view)?;
        for shard in self.table.shards() {
            let held = self.epoch_of(shard);
            let offered = next.epoch_of(shard);
            if let (Some(held), Some(offered)) = (held, offered) {
                if offered < held {
                    return Err(ShardingError::EpochRegression { shard, held, offered });
                }
                let buckets_changed = self.table.ranges_of(shard) != next.table.ranges_of(shard);
                let owner_changed = self.owner_of(shard) != next.owner_of(shard);
                if (buckets_changed || owner_changed) && offered == held {
                    return Err(ShardingError::EpochNotRaised { shard, held, offered });
                }
            }
        }
        let seated_shards = self.table.shards();
        for shard in next.table().shards() {
            if seated_shards.contains(&shard) {
                continue;
            }
            let offered = next.epoch_of(shard).unwrap_or(1);
            let expectation = self.fencing_expectation_over(&next.table().ranges_of(shard));
            if offered <= expectation {
                return Err(ShardingError::EnteringShardEpochNotRaised {
                    shard,
                    expectation,
                    offered,
                });
            }
        }
        Ok(next)
    }

    /// Returns the fencing bar of one set of bucket ranges: the highest
    /// ownership epoch this topology holds over them.
    ///
    /// A shard identity entering the table over these buckets must seat at
    /// an epoch above this bar — the entering identity fences every
    /// ownership the buckets held under the identities before it, so a
    /// re-identified shard cannot re-seat stale ownership at the epoch it
    /// was deposed from.
    pub(crate) fn fencing_expectation_over(&self, ranges: &[BucketRange]) -> u64 {
        let mut expectation = 0;
        for held_shard in self.table.shards() {
            let Some(epoch) = self.epoch_of(held_shard) else {
                continue;
            };
            let overlaps = self.table.ranges_of(held_shard).iter().any(|held| {
                ranges.iter().any(|offered| {
                    offered.start().max(held.start()) < offered.end().min(held.end())
                })
            });
            if overlaps {
                expectation = expectation.max(epoch);
            }
        }
        expectation
    }
}

/// One document request to route: the tenant it is routed for, the
/// collection (whose declared shard key applies), the document identity, and
/// — for field-keyed collections — the document value the fields are read
/// from.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RoutingRequest<'a> {
    tenant: &'a TenantId,
    collection: &'a CollectionName,
    document: &'a DocumentId,
    value: Option<&'a DocumentValue>,
}

impl<'a> RoutingRequest<'a> {
    /// Starts one request by identity: the routing input every collection
    /// accepts (field-keyed collections attach the value with
    /// [`RoutingRequest::with_value`]).
    #[must_use]
    pub const fn by_identity(
        tenant: &'a TenantId,
        collection: &'a CollectionName,
        document: &'a DocumentId,
    ) -> Self {
        Self { tenant, collection, document, value: None }
    }

    /// Starts one request from a whole document (the write-path form): the
    /// value rides along for field-keyed collections.
    #[must_use]
    pub fn of_document(tenant: &'a TenantId, document: &'a Document) -> Self {
        Self {
            tenant,
            collection: document.collection(),
            document: document.id(),
            value: Some(document.value()),
        }
    }

    /// Attaches the document value a field shard key reads from.
    #[must_use]
    pub const fn with_value(mut self, value: &'a DocumentValue) -> Self {
        self.value = Some(value);
        self
    }

    /// Returns the tenant this request is routed for.
    #[must_use]
    pub const fn tenant(&self) -> &'a TenantId {
        self.tenant
    }

    /// Returns the collection whose declared shard key applies.
    #[must_use]
    pub const fn collection(&self) -> &'a CollectionName {
        self.collection
    }

    /// Returns the document identity.
    #[must_use]
    pub const fn document(&self) -> &'a DocumentId {
        self.document
    }

    /// Returns the document value, when the request carries one.
    #[must_use]
    pub const fn value(&self) -> Option<&'a DocumentValue> {
        self.value
    }
}

/// The deterministic routing outcome for one request: the tenant it is bound
/// to, its bucket, its shard, and the owning Member at the ownership epoch
/// the write path must validate against.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RoutePlacement {
    tenant: TenantId,
    bucket: Bucket,
    shard: ShardId,
    owner: MemberId,
    epoch: u64,
}

impl RoutePlacement {
    /// Returns the tenant this placement was routed for. A placement is
    /// never honored under another tenant's identity.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the routed document's bucket (its permanent routing
    /// identity).
    #[must_use]
    pub const fn bucket(&self) -> Bucket {
        self.bucket
    }

    /// Returns the shard the bucket maps to.
    #[must_use]
    pub const fn shard(&self) -> ShardId {
        self.shard
    }

    /// Returns the Member that owns the shard's write path.
    #[must_use]
    pub fn owner(&self) -> &MemberId {
        &self.owner
    }

    /// Returns the ownership epoch the placement names.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }
}
