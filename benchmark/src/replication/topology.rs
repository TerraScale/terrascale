//! Topology views and replication request configuration.
//!
//! A [`TopologyView`] is caller-supplied membership input, never a decision:
//! the Platform/Orchestrator control plane owns provisioning, placement,
//! routing, lifecycle, and membership policy, and this module only validates
//! what it is handed against the adjudicated data-plane rules (ADR-0004
//! D-1: one Member owns each data range's write path). A
//! [`ReplicationConfig`] pairs a validated view with the explicit policy
//! inputs a replication request names; it authorizes and routes nothing.

use super::{
    AcknowledgementLevel, MemberId, MemberSeat, RangeOwnership, RegionId, ReplicationConfig,
    ReplicationError, TopologyView, identifier::validate_topology_identifier,
    member::DEFAULT_CATCH_UP_BATCH,
};

/// The largest catch-up batch one replication request may name.
///
/// The bound keeps one catch-up round's delivery bounded in memory: a member
/// requests at most this many committed operations per round and re-requests
/// until its progress reaches the source's high water.
pub const MAX_CATCH_UP_BATCH: u32 = 1024;

impl TopologyView {
    /// Starts an empty membership view under construction.
    ///
    /// An empty view is never servable: [`TopologyView::validated`] rejects
    /// it with [`ReplicationError::InvalidTopology`]. The builder chain
    /// (`with_region`, `with_member`, `with_owner`) is a sequence of value
    /// transformations, so every intermediate view is an immutable snapshot.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Names one Region of the membership view.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicationError::InvalidTopology`] for invalid identifier
    /// text or a Region already named by this view.
    pub fn with_region(mut self, region: &str) -> Result<Self, ReplicationError> {
        let validated = RegionId::try_new(region).map_err(|_| ReplicationError::InvalidTopology)?;
        if self.regions.contains(&validated) {
            return Err(ReplicationError::InvalidTopology);
        }
        self.regions.push(validated);
        Ok(self)
    }

    /// Seats one Member in a Region inside one Failure Domain.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicationError::InvalidTopology`] for invalid identifier
    /// text, an unknown Region, a Member already seated by this view, or a
    /// failure domain that would span more than one Region.
    pub fn with_member(
        mut self,
        member: &str,
        region: &str,
        failure_domain: &str,
    ) -> Result<Self, ReplicationError> {
        let member = MemberId::try_new(member).map_err(|_| ReplicationError::InvalidTopology)?;
        let region = RegionId::try_new(region).map_err(|_| ReplicationError::InvalidTopology)?;
        validate_topology_identifier(failure_domain)
            .map_err(|_| ReplicationError::InvalidTopology)?;
        if !self.regions.contains(&region) {
            return Err(ReplicationError::InvalidTopology);
        }
        if self.members.iter().any(|seat| seat.member == member) {
            return Err(ReplicationError::InvalidTopology);
        }
        // A Failure Domain groups Members whose correlated loss is one
        // failure. Each Region is its own failure domain boundary, so a
        // domain may not span Regions: that would model two Regions lost as
        // one event.
        if self
            .members
            .iter()
            .any(|seat| seat.failure_domain == failure_domain && seat.region != region)
        {
            return Err(ReplicationError::InvalidTopology);
        }
        self.members.push(MemberSeat { member, region, failure_domain: failure_domain.to_owned() });
        Ok(self)
    }

    /// Assigns one data range's write path to an owning Member at the first
    /// ownership epoch.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicationError::InvalidTopology`] for invalid range text,
    /// a Member not seated by this view, or a range that already has an
    /// owner: under D-1 exactly one Member owns each data range's write
    /// path, and re-assignment is a control-plane transition, not a builder
    /// step.
    pub fn with_owner(self, range: &str, member: &str) -> Result<Self, ReplicationError> {
        self.with_owner_at_epoch(range, member, 1)
    }

    /// Assigns one data range's write path to an owning Member at an
    /// explicit ownership epoch.
    ///
    /// The epoch is the fencing fact of D-1: a leader transition is the
    /// control plane raising a range's epoch, members refuse views that
    /// lower an epoch they hold, and followers reject replication batches
    /// stamped below the highest epoch they have seen for a range.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicationError::InvalidTopology`] for invalid range text,
    /// an epoch below one, a Member not seated by this view, or a range that
    /// already has an owner in this view.
    pub fn with_owner_at_epoch(
        mut self,
        range: &str,
        member: &str,
        epoch: u64,
    ) -> Result<Self, ReplicationError> {
        validate_topology_identifier(range).map_err(|_| ReplicationError::InvalidTopology)?;
        if epoch < 1 {
            return Err(ReplicationError::InvalidTopology);
        }
        let member = MemberId::try_new(member).map_err(|_| ReplicationError::InvalidTopology)?;
        if !self.members.iter().any(|seat| seat.member == member) {
            return Err(ReplicationError::InvalidTopology);
        }
        if self.owners.iter().any(|ownership| ownership.range == range) {
            return Err(ReplicationError::InvalidTopology);
        }
        self.owners.push(RangeOwnership { range: range.to_owned(), owner: member, epoch });
        Ok(self)
    }

    /// Validates the assembled view and returns it as servable membership
    /// input.
    ///
    /// A servable view names at least one Region, seats at least one Member
    /// per named Region, and hands every named data range exactly one owner
    /// (enforced structurally by [`TopologyView::with_owner`]).
    ///
    /// # Errors
    ///
    /// Returns [`ReplicationError::InvalidTopology`] when any of those
    /// invariants is unmet.
    pub fn validated(self) -> Result<Self, ReplicationError> {
        if self.regions.is_empty() || self.members.is_empty() {
            return Err(ReplicationError::InvalidTopology);
        }
        if self.regions.iter().any(|region| !self.members.iter().any(|seat| seat.region == *region))
        {
            return Err(ReplicationError::InvalidTopology);
        }
        Ok(self)
    }

    /// Returns the Regions named by this view, in canonical order.
    pub fn regions(&self) -> impl Iterator<Item = &RegionId> {
        self.regions.iter()
    }

    /// Reports whether this view names the supplied Region.
    #[must_use]
    pub fn contains_region(&self, region: &RegionId) -> bool {
        self.regions.contains(region)
    }

    /// Returns the Members seated by this view, in canonical order.
    pub fn members(&self) -> impl Iterator<Item = &MemberId> {
        self.members.iter().map(|seat| &seat.member)
    }

    /// Reports whether this view seats the supplied Member.
    #[must_use]
    pub fn contains_member(&self, member: &MemberId) -> bool {
        self.members.iter().any(|seat| seat.member == *member)
    }

    /// Returns the Region one Member is seated in.
    #[must_use]
    pub fn region_of(&self, member: &MemberId) -> Option<&RegionId> {
        self.members.iter().find(|seat| seat.member == *member).map(|seat| &seat.region)
    }

    /// Returns the Failure Domain one Member belongs to.
    #[must_use]
    pub fn failure_domain_of(&self, member: &MemberId) -> Option<&str> {
        self.members
            .iter()
            .find(|seat| seat.member == *member)
            .map(|seat| seat.failure_domain.as_str())
    }

    /// Returns the distinct Failure Domains this view groups Members into,
    /// in canonical order.
    #[must_use]
    pub fn failure_domains(&self) -> Vec<&str> {
        let mut domains: Vec<&str> =
            self.members.iter().map(|seat| seat.failure_domain.as_str()).collect();
        domains.sort_unstable();
        domains.dedup();
        domains
    }

    /// Returns the Members seated in one Region, in canonical order.
    #[must_use]
    pub fn members_of_region(&self, region: &RegionId) -> Vec<MemberId> {
        self.members
            .iter()
            .filter(|seat| seat.region == *region)
            .map(|seat| seat.member.clone())
            .collect()
    }

    /// Returns the Member that owns one data range's write path.
    #[must_use]
    pub fn owner_of(&self, range: &str) -> Option<&MemberId> {
        self.owners.iter().find(|ownership| ownership.range == range).map(|o| &o.owner)
    }

    /// Returns the ownership epoch one data range is owned at.
    #[must_use]
    pub fn owner_epoch_of(&self, range: &str) -> Option<u64> {
        self.owners.iter().find(|ownership| ownership.range == range).map(|o| o.epoch)
    }

    /// Returns the data ranges this view assigns an owner to.
    pub fn owned_ranges(&self) -> impl Iterator<Item = &str> {
        self.owners.iter().map(|ownership| ownership.range.as_str())
    }

    /// Reports whether this view holds no membership facts at all.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.regions.is_empty() && self.members.is_empty() && self.owners.is_empty()
    }

    /// Reports whether the view satisfies the servable invariants.
    #[must_use]
    pub fn is_servable(&self) -> bool {
        self.clone().validated().is_ok()
    }
}

impl ReplicationConfig {
    /// Validates a topology view and pairs it with the `Local` default
    /// acknowledgement level.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicationError::InvalidTopology`] when the view is not a
    /// servable membership input.
    pub fn try_new(topology: TopologyView) -> Result<Self, ReplicationError> {
        let topology = topology.validated()?;
        Ok(Self {
            topology,
            acknowledgement: AcknowledgementLevel::default(),
            catch_up_batch: DEFAULT_CATCH_UP_BATCH,
        })
    }

    /// Names the acknowledgement level this configuration's requests carry.
    ///
    /// Naming is the only way a request carries a level: there is no
    /// inference from a local durability fact, and the level is a
    /// per-request input with a `Local` default (ADR-0004 D-2). R-1 accepts
    /// the name without evaluating any level above `Local`.
    #[must_use]
    pub fn with_acknowledgement(mut self, level: AcknowledgementLevel) -> Self {
        self.acknowledgement = level;
        self
    }

    /// Returns the acknowledgement level this configuration names.
    #[must_use]
    pub fn acknowledgement(&self) -> AcknowledgementLevel {
        self.acknowledgement
    }

    /// Returns the validated topology view this configuration applies.
    #[must_use]
    pub fn topology(&self) -> &TopologyView {
        &self.topology
    }

    /// Bounds the number of commits one catch-up round requests.
    ///
    /// # Errors
    ///
    /// Returns [`ReplicationError::InvalidTopology`] for a zero batch or one
    /// above [`MAX_CATCH_UP_BATCH`]: a catch-up round is never unbounded and
    /// never empty.
    #[allow(
        dead_code,
        reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
    )]
    pub(crate) fn with_catch_up_batch(mut self, batch: u32) -> Result<Self, ReplicationError> {
        if batch == 0 || batch > MAX_CATCH_UP_BATCH {
            return Err(ReplicationError::InvalidTopology);
        }
        self.catch_up_batch = batch;
        Ok(self)
    }

    /// Returns the bounded catch-up batch this configuration names.
    #[must_use]
    #[allow(
        dead_code,
        reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
    )]
    pub(crate) fn catch_up_batch(&self) -> u32 {
        self.catch_up_batch
    }
}
