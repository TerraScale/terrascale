//! Shard loss assessment and ownership failover decisions (SH-05,
//! PB-5041/5048, PB-5017/5020 under `docs/sharding/dispositions.md`).
//!
//! Failover here is the engine-side validation of a Platform-issued
//! ownership transition under a declared Member loss — never the choice of
//! a successor. The Platform decides placement (ADR 0002, ADR 0011 D3);
//! the engine checks that what it is handed replaces only what was lost,
//! keeps healthy shards untouched, seats no declared-lost owner, and ends
//! every ownership change at a raised epoch so the existing
//! admission-time and placement-authorization checks fence the deposed
//! owner. Acknowledgement truth is per shard: a shard that lost its owner
//! refuses writes (nothing commits, nothing is acknowledged), a surviving
//! shard's commits stay shard-local facts, and no outcome of this module
//! presents either as database-wide success.

use crate::replication::{MemberId, RegionId, TopologyView};

use super::super::{BucketRange, BucketRangeTable, ShardId, ShardTopology, ShardingError};

/// One shard whose owning Member was declared lost, with the ownership and
/// placement facts a failover decision needs.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LostShard {
    shard: ShardId,
    owner: MemberId,
    region: Option<RegionId>,
    failure_domain: Option<String>,
    ranges: Vec<BucketRange>,
    buckets: u64,
}

impl LostShard {
    /// Returns the shard that lost its owner.
    #[must_use]
    pub const fn shard(&self) -> ShardId {
        self.shard
    }

    /// Returns the Member that owned the shard when it was lost.
    #[must_use]
    pub fn owner(&self) -> &MemberId {
        &self.owner
    }

    /// Returns the Region the lost owner sat in, when the view names it.
    #[must_use]
    pub fn region(&self) -> Option<&RegionId> {
        self.region.as_ref()
    }

    /// Returns the failure domain of the lost owner, when the view names it.
    #[must_use]
    pub fn failure_domain(&self) -> Option<&str> {
        self.failure_domain.as_deref()
    }

    /// Returns the bucket ranges the lost shard owned.
    #[must_use]
    pub fn ranges(&self) -> &[BucketRange] {
        &self.ranges
    }

    /// Returns the bucket count the lost shard owned.
    #[must_use]
    pub const fn bucket_count(&self) -> u64 {
        self.buckets
    }
}

/// One shard that keeps serving under the declared loss.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ServingShard {
    shard: ShardId,
    owner: MemberId,
}

impl ServingShard {
    /// Returns the shard that keeps serving.
    #[must_use]
    pub const fn shard(&self) -> ShardId {
        self.shard
    }

    /// Returns the Member that keeps owning the shard's write path.
    #[must_use]
    pub fn owner(&self) -> &MemberId {
        &self.owner
    }
}

/// The assessed truth of one declared Member loss against a seated shard
/// topology: which shards lost their owners (and where those owners sat),
/// which shards keep serving, and which Members survive.
///
/// The assessment is availability truth per shard only. It never composes a
/// database-wide verdict: a surviving shard's continued service is a
/// shard-local fact, and a lost shard's unavailability is recorded, never
/// papered over.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardLoss {
    lost: Vec<LostShard>,
    serving: Vec<ServingShard>,
    lost_members: Vec<MemberId>,
    surviving_members: Vec<MemberId>,
}

impl ShardLoss {
    /// Assesses one declared set of lost Members against a seated topology.
    ///
    /// # Errors
    ///
    /// Returns [`FailoverError::UnknownLostMember`] for a Member the view
    /// does not seat: a loss assessment never guesses about membership.
    pub fn assess(topology: &ShardTopology, lost: &[MemberId]) -> Result<Self, FailoverError> {
        for member in lost {
            if !topology.view().contains_member(member) {
                return Err(FailoverError::UnknownLostMember { member: member.clone() });
            }
        }
        let mut lost_members = lost.to_vec();
        lost_members.sort_by_key(|member| member.as_str().to_owned());
        lost_members.dedup();

        let mut lost_shards = Vec::new();
        let mut serving_shards = Vec::new();
        for shard in topology.table().shards() {
            let owner = topology
                .owner_of(shard)
                .ok_or(FailoverError::Routing(ShardingError::ShardWithoutOwner { shard }))?;
            if lost_members.contains(&owner) {
                lost_shards.push(LostShard {
                    shard,
                    owner: owner.clone(),
                    region: topology.view().region_of(&owner).cloned(),
                    failure_domain: topology.view().failure_domain_of(&owner).map(str::to_owned),
                    ranges: topology.table().ranges_of(shard),
                    buckets: topology.table().bucket_count_of(shard),
                });
            } else {
                serving_shards.push(ServingShard { shard, owner });
            }
        }

        let surviving_members = topology
            .view()
            .members()
            .filter(|member| !lost_members.contains(member))
            .cloned()
            .collect();

        Ok(Self { lost: lost_shards, serving: serving_shards, lost_members, surviving_members })
    }

    /// Returns the shards that lost their owners, in table order.
    #[must_use]
    pub fn lost_shards(&self) -> &[LostShard] {
        &self.lost
    }

    /// Returns the shards that keep serving, in table order.
    #[must_use]
    pub fn serving_shards(&self) -> &[ServingShard] {
        &self.serving
    }

    /// Returns the declared-lost Members, deduplicated in canonical order.
    #[must_use]
    pub fn lost_members(&self) -> &[MemberId] {
        &self.lost_members
    }

    /// Returns every seated Member that survived the declared loss.
    #[must_use]
    pub fn surviving_members(&self) -> &[MemberId] {
        &self.surviving_members
    }

    /// Reports whether the declared loss took no shard's owner.
    #[must_use]
    pub fn lost_no_shard(&self) -> bool {
        self.lost.is_empty()
    }
}

/// One validated ownership transfer of a lost shard.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardTransfer {
    shard: ShardId,
    from: MemberId,
    to: MemberId,
    from_epoch: u64,
    to_epoch: u64,
    buckets: u64,
}

impl ShardTransfer {
    /// Returns the transferred shard.
    #[must_use]
    pub const fn shard(&self) -> ShardId {
        self.shard
    }

    /// Returns the deposed (lost) owner.
    #[must_use]
    pub fn from(&self) -> &MemberId {
        &self.from
    }

    /// Returns the successor owner.
    #[must_use]
    pub fn to(&self) -> &MemberId {
        &self.to
    }

    /// Returns the epoch the deposed owner served at.
    #[must_use]
    pub const fn from_epoch(&self) -> u64 {
        self.from_epoch
    }

    /// Returns the (raised) epoch the successor serves at.
    #[must_use]
    pub const fn to_epoch(&self) -> u64 {
        self.to_epoch
    }

    /// Returns the bucket count that transferred with the shard.
    #[must_use]
    pub const fn buckets(&self) -> u64 {
        self.buckets
    }
}

/// One lost shard whose buckets were dispersed to other shards instead of
/// a successor being seated.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DispersedShard {
    shard: ShardId,
    from: MemberId,
    allocations: Vec<(ShardId, u64)>,
}

impl DispersedShard {
    /// Returns the dispersed (lost) shard.
    #[must_use]
    pub const fn shard(&self) -> ShardId {
        self.shard
    }

    /// Returns the lost owner whose buckets were dispersed.
    #[must_use]
    pub fn from(&self) -> &MemberId {
        &self.from
    }

    /// Returns where the dispersed buckets went: each receiving shard with
    /// the bucket count it received, in canonical shard order.
    #[must_use]
    pub fn allocations(&self) -> &[(ShardId, u64)] {
        &self.allocations
    }
}

/// The engine-side validation of a Platform failover under a declared
/// Member loss.
pub struct FailoverDecision;

impl FailoverDecision {
    /// Decides one failover: the next table/view pair offered by the
    /// Platform under `loss`, validated against the seated `topology`.
    ///
    /// The decision is failover-shaped or refused: a shard whose owner
    /// survived keeps its owner and never shrinks (owner changes and range
    /// moves on healthy shards are migration decisions, not failover), a
    /// lost shard is either transferred to a surviving successor at a
    /// raised epoch or dispersed into other shards, and the declared-lost
    /// owner is never re-seated. The seating and epoch rules of the
    /// sharding seam ([`ShardTopology::transition`]) apply unchanged.
    ///
    /// # Errors
    ///
    /// Returns [`FailoverError::FailoverWithoutLoss`] when no loss was
    /// declared, the seating and transition failures of the sharding seam
    /// as [`FailoverError::Routing`], and the failover-shape refusals
    /// [`FailoverError::HealthyShardOwnerReplaced`],
    /// [`FailoverError::HealthyShardShrunk`],
    /// [`FailoverError::SuccessorLost`], and
    /// [`FailoverError::LostOwnerRetained`].
    pub fn decide(
        topology: &ShardTopology,
        loss: &ShardLoss,
        next_table: BucketRangeTable,
        next_view: TopologyView,
    ) -> Result<FailoverPlan, FailoverError> {
        if loss.lost_members().is_empty() {
            return Err(FailoverError::FailoverWithoutLoss);
        }
        let lost_owners: Vec<MemberId> =
            loss.lost_shards().iter().map(LostShard::owner).cloned().collect();

        let next = ShardTopology::try_new(next_table, next_view).map_err(FailoverError::Routing)?;

        let mut retained = Vec::new();
        let mut transfers = Vec::new();
        let mut dispersed = Vec::new();
        for shard in topology.table().shards() {
            let owner = topology
                .owner_of(shard)
                .ok_or(FailoverError::Routing(ShardingError::ShardWithoutOwner { shard }))?;
            if !lost_owners.contains(&owner) {
                // A healthy shard keeps its owner and may only grow (by
                // receiving a lost shard's buckets); anything else is a
                // migration decision wearing a failover hat.
                match next.owner_of(shard) {
                    Some(next_owner) if next_owner == owner => {}
                    _ => {
                        return Err(FailoverError::HealthyShardOwnerReplaced {
                            shard,
                            owner: owner.clone(),
                        });
                    }
                }
                let held = topology.table().ranges_of(shard);
                let offered = next.table().ranges_of(shard);
                if !ranges_cover(&held, &offered) {
                    return Err(FailoverError::HealthyShardShrunk { shard });
                }
                retained.push(shard);
                continue;
            }

            let from_epoch = topology.epoch_of(shard).unwrap_or(1);
            if let Some(successor) = next.owner_of(shard) {
                if successor == owner {
                    return Err(FailoverError::LostOwnerRetained { shard, owner: owner.clone() });
                }
                if loss.lost_members().contains(&successor) {
                    return Err(FailoverError::SuccessorLost {
                        shard,
                        successor: successor.clone(),
                    });
                }
                transfers.push(ShardTransfer {
                    shard,
                    from: owner.clone(),
                    to: successor,
                    from_epoch,
                    to_epoch: next.epoch_of(shard).unwrap_or(1),
                    buckets: topology.table().bucket_count_of(shard),
                });
            } else {
                // The shard left the table: its buckets were dispersed.
                let mut allocations: Vec<(ShardId, u64)> = Vec::new();
                for range in topology.table().ranges_of(shard) {
                    allocate_range(next.table(), &range, &mut allocations);
                }
                dispersed.push(DispersedShard { shard, from: owner.clone(), allocations });
            }
        }

        // The fencing validation runs last: by now the transition is
        // failover-shaped, so any remaining refusal is an epoch or seating
        // regression the caller must fix.
        let topology_next = topology
            .transition(next.table().clone(), next.view().clone())
            .map_err(FailoverError::Routing)?;

        Ok(FailoverPlan {
            topology: topology_next,
            retained,
            transfers,
            dispersed,
            loss: loss.clone(),
        })
    }
}

/// The validated outcome of one failover decision.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FailoverPlan {
    topology: ShardTopology,
    retained: Vec<ShardId>,
    transfers: Vec<ShardTransfer>,
    dispersed: Vec<DispersedShard>,
    loss: ShardLoss,
}

impl FailoverPlan {
    /// Returns the seated topology this plan transitioned to: the view the
    /// successors serve under, with raised epochs on every changed shard.
    #[must_use]
    pub fn topology(&self) -> &ShardTopology {
        &self.topology
    }

    /// Returns the healthy shards that kept their owners, in table order.
    #[must_use]
    pub fn retained(&self) -> &[ShardId] {
        &self.retained
    }

    /// Returns the validated transfers, in table order.
    #[must_use]
    pub fn transfers(&self) -> &[ShardTransfer] {
        &self.transfers
    }

    /// Returns the shards dispersed into other shards, in table order.
    #[must_use]
    pub fn dispersed(&self) -> &[DispersedShard] {
        &self.dispersed
    }

    /// Returns the loss this plan was decided under.
    #[must_use]
    pub fn loss(&self) -> &ShardLoss {
        &self.loss
    }

    /// Renders the secret-safe failover receipt for this plan: identities,
    /// epochs, bucket counts, and limits only — no document data, no paths.
    #[must_use]
    pub fn receipt(&self) -> FailoverReceipt {
        FailoverReceipt {
            transfers: self.transfers.clone(),
            retained: self.retained.clone(),
            dispersed: self.dispersed.clone(),
            lost_members: self.loss.lost_members().to_vec(),
            lost_shards: self.loss.lost_shards().to_vec(),
            surviving_members: self.loss.surviving_members().to_vec(),
        }
    }
}

/// A secret-safe record of one failover decision: what was lost, what
/// transferred at which epochs, what was retained or dispersed, and the
/// explicit limits of the decision. It carries no document content,
/// credential, path, or timing fact.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FailoverReceipt {
    transfers: Vec<ShardTransfer>,
    retained: Vec<ShardId>,
    dispersed: Vec<DispersedShard>,
    lost_members: Vec<MemberId>,
    lost_shards: Vec<LostShard>,
    surviving_members: Vec<MemberId>,
}

impl FailoverReceipt {
    /// Returns the rendered receipt text, one fact per line.
    #[must_use]
    pub fn summary(&self) -> String {
        let mut lines = Vec::new();
        lines.push("scenario: sharded-failover".to_owned());
        let lost = self
            .lost_members
            .iter()
            .map(|member| member.as_str().to_owned())
            .collect::<Vec<_>>()
            .join(", ");
        lines.push(format!("lost members: {lost}"));
        for shard in &self.lost_shards {
            lines.push(format!(
                "shard lost: {} owner {} region {} domain {} buckets {}",
                shard.shard(),
                shard.owner(),
                shard.region().map_or("unknown", RegionId::as_str),
                shard.failure_domain().unwrap_or("unknown"),
                shard.bucket_count(),
            ));
        }
        for shard in &self.retained {
            lines.push(format!("shard retained: {shard}"));
        }
        for transfer in &self.transfers {
            lines.push(format!(
                "shard transferred: {} from {} epoch {} to {} epoch {} buckets {}",
                transfer.shard(),
                transfer.from(),
                transfer.from_epoch(),
                transfer.to(),
                transfer.to_epoch(),
                transfer.buckets(),
            ));
        }
        for dispersed in &self.dispersed {
            let allocations = dispersed
                .allocations()
                .iter()
                .map(|(shard, buckets)| format!("{shard}:{buckets}"))
                .collect::<Vec<_>>()
                .join(", ");
            lines.push(format!(
                "shard dispersed: {} from {} into {allocations}",
                dispersed.shard(),
                dispersed.from(),
            ));
        }
        let surviving = self
            .surviving_members
            .iter()
            .map(|member| member.as_str().to_owned())
            .collect::<Vec<_>>()
            .join(", ");
        lines.push(format!("surviving members: {surviving}"));
        for limit in Self::limits() {
            lines.push(format!("limit: {limit}"));
        }
        lines.join("\n")
    }

    /// Returns the recorded limits of every failover receipt.
    #[must_use]
    pub fn limits() -> &'static [&'static str] {
        &[
            "failover validates ownership transitions; catch-up and divergence repair are the replication plane's own qualified machinery",
            "a surviving shard's continued service is a shard-local fact, never database-wide acknowledgement",
            "the coordination seam observes no clock, so no detection, RTO, or convergence-interval fact is claimed here",
        ]
    }
}

/// Typed refusals of a loss assessment or failover decision.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum FailoverError {
    /// The assessment or decision named a Member the view does not seat.
    UnknownLostMember {
        /// The unseated Member.
        member: MemberId,
    },
    /// A transition was offered without any declared loss: an ownership
    /// change under no loss is a migration decision, not failover.
    FailoverWithoutLoss,
    /// A shard whose owner survived was given a new owner (or none) under
    /// a failover decision.
    HealthyShardOwnerReplaced {
        /// The healthy shard.
        shard: ShardId,
        /// The surviving owner the decision displaced.
        owner: MemberId,
    },
    /// A shard whose owner survived lost bucket ranges under a failover
    /// decision.
    HealthyShardShrunk {
        /// The healthy shard that was shrunk.
        shard: ShardId,
    },
    /// A lost shard was transferred to a successor that is itself declared
    /// lost.
    SuccessorLost {
        /// The shard whose successor is lost.
        shard: ShardId,
        /// The lost successor.
        successor: MemberId,
    },
    /// The offered view re-seated the declared-lost owner (even at a
    /// raised epoch).
    LostOwnerRetained {
        /// The shard kept under its lost owner.
        shard: ShardId,
        /// The declared-lost owner the view retained.
        owner: MemberId,
    },
    /// The seating or transition refused the offered pair; the sharding
    /// seam's own epoch and validation rules stand unchanged.
    Routing(ShardingError),
}

/// Reports whether `offered` covers every bucket interval of `held`:
/// a healthy shard may grow (or merge its ranges) but never lose a bucket.
fn ranges_cover(held: &[BucketRange], offered: &[BucketRange]) -> bool {
    held.iter().all(|range| {
        let covered: u64 = offered
            .iter()
            .map(|candidate| {
                let start = candidate.start().max(range.start());
                let end = candidate.end().min(range.end());
                u64::from(end.saturating_sub(start))
            })
            .sum();
        covered == u64::from(range.end() - range.start())
    })
}

/// Accumulates where one bucket range's buckets live in the next table,
/// merging per receiving shard.
fn allocate_range(
    table: &BucketRangeTable,
    range: &BucketRange,
    allocations: &mut Vec<(ShardId, u64)>,
) {
    for entry in table.entries() {
        let overlap_start = entry.start().max(range.start());
        let overlap_end = entry.end().min(range.end());
        if overlap_start < overlap_end {
            let buckets = u64::from(overlap_end - overlap_start);
            match allocations.iter_mut().find(|(shard, _)| *shard == entry.shard()) {
                Some((_, count)) => *count += buckets,
                None => allocations.push((entry.shard(), buckets)),
            }
        }
    }
}
