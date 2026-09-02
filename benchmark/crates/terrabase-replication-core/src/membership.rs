//! The composed member's membership vocabulary.
//!
//! One versioned replica set (the eligible voting Members per Region) and
//! the acknowledgement lattice a composed write reports. The main crate's
//! quorum runtime is deleted (GOAL D-35: intra-region durability is a
//! physical WAL-frame follower's fsynced ack; cross-region is a journal
//! tailer); these value types stay because the composed-member composition
//! and its receipts are stated in them. The M1 replication build reshapes
//! this vocabulary.

use std::collections::BTreeMap;

use terrabase::types::{MemberId, RegionId};

/// The composed acknowledgement lattice.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum LeaderlessLevel {
    /// Durable acceptance by a majority of eligible voting Members in
    /// the accepting Region. The minimum successful write
    /// acknowledgement.
    RegionalQuorum,
    /// `RegionalQuorum` in every selected active Region,
    /// including the accepting Region.
    GlobalQuorum,
}

/// One versioned replica set: the eligible voting Members per Region.
///
/// The epoch is the accepted configuration epoch, never a value a Member
/// assigns alone. Every Member of the set holds the same write, read,
/// repair, and recovery role — there is no distinguished replica and no
/// per-range assignment inside this set.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicaSet {
    epoch: u64,
    voters: BTreeMap<RegionId, Vec<MemberId>>,
}

impl ReplicaSet {
    /// Builds one replica set at a configuration epoch.
    #[must_use]
    pub fn new(epoch: u64, voters: BTreeMap<RegionId, Vec<MemberId>>) -> Self {
        Self { epoch, voters }
    }

    /// Returns the replica-set epoch.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }

    /// Returns the Regions holding eligible voting Members, in canonical
    /// order.
    #[must_use]
    pub fn regions(&self) -> Vec<RegionId> {
        self.voters.keys().cloned().collect()
    }

    /// Returns the eligible voting Members of one Region.
    #[must_use]
    pub fn voters_of(&self, region: &RegionId) -> &[MemberId] {
        self.voters.get(region).map_or(&[], Vec::as_slice)
    }

    /// Reports whether one Member is an eligible voter in the set.
    #[must_use]
    pub fn is_voter(&self, member: &MemberId) -> bool {
        self.voters.values().any(|members| members.contains(member))
    }

    /// Returns the Region a Member votes in, when it is eligible.
    #[must_use]
    pub fn region_of(&self, member: &MemberId) -> Option<RegionId> {
        self.voters
            .iter()
            .find(|(_, members)| members.contains(member))
            .map(|(region, _)| region.clone())
    }

    /// The majority threshold of one Region's eligible voters.
    #[must_use]
    pub fn regional_threshold(&self, region: &RegionId) -> usize {
        self.voters_of(region).len() / 2 + 1
    }

    /// The number of selected active Regions required for global durability.
    #[must_use]
    pub fn global_threshold(&self) -> usize {
        self.regions().len()
    }
}
