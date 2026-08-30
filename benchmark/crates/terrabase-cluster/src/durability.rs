//! Deterministic acknowledgement state for active replica sets.
//!
//! This is the cluster boundary's acknowledgement ledger.  It deliberately
//! does not model a leader: the `coordinator` on a receipt is the Member that
//! accepted that request only.  The durable facts are the individual Member
//! observations, fenced by the membership generation and replica-set epoch.
//!
//! The storage and transport crates provide the real WAL and QUIC paths.  The
//! small state machine here makes the acknowledgement contract explicit at
//! their integration boundary and gives the fault lane a deterministic model:
//! RF=2 means both distinct active local Members, while GlobalDurable means
//! that same boundary in every selected Region.

use std::collections::{BTreeMap, BTreeSet};

use terrabase::replication::{MemberId, RegionId};

/// The two active acknowledgement levels owned by this boundary.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum DurabilityLevel {
    /// Both distinct active Members in the accepting Region are stored.
    RegionalDurable,
    /// RegionalDurable is satisfied in every selected active Region.
    GlobalDurable,
}

/// One Region's RF=2 active seats.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RegionMembers {
    region: RegionId,
    members: [MemberId; 2],
}

impl RegionMembers {
    /// Builds a Region with exactly two distinct Members.
    pub fn new(region: RegionId, members: [MemberId; 2]) -> Result<Self, MembershipError> {
        if members[0] == members[1] {
            return Err(MembershipError::DuplicateMember { member: members[0].clone() });
        }
        Ok(Self { region, members })
    }

    /// Builds a Region from validated identifier text.
    pub fn try_new(region: &str, members: [&str; 2]) -> Result<Self, MembershipError> {
        let region = RegionId::try_new(region).map_err(|_| MembershipError::InvalidIdentifier)?;
        let members = [
            MemberId::try_new(members[0]).map_err(|_| MembershipError::InvalidIdentifier)?,
            MemberId::try_new(members[1]).map_err(|_| MembershipError::InvalidIdentifier)?,
        ];
        Self::new(region, members)
    }

    /// Returns the Region identity.
    #[must_use]
    pub fn region(&self) -> &RegionId {
        &self.region
    }

    /// Returns the two active seats in canonical declaration order.
    #[must_use]
    pub fn members(&self) -> &[MemberId; 2] {
        &self.members
    }
}

/// A versioned selected active replica membership.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Membership {
    generation: u64,
    epoch: u64,
    regions: BTreeMap<RegionId, [MemberId; 2]>,
}

impl Membership {
    /// Validates an RF=2 selected Region shape.
    pub fn new(
        generation: u64,
        epoch: u64,
        regions: Vec<RegionMembers>,
    ) -> Result<Self, MembershipError> {
        if generation == 0 || epoch == 0 {
            return Err(MembershipError::InvalidVersion);
        }
        if regions.is_empty() {
            return Err(MembershipError::EmptyMembership);
        }
        let mut selected = BTreeMap::new();
        let mut members = BTreeSet::new();
        for region in regions {
            if selected.insert(region.region.clone(), region.members.clone()).is_some() {
                return Err(MembershipError::DuplicateRegion { region: region.region });
            }
            for member in region.members {
                if !members.insert(member.clone()) {
                    return Err(MembershipError::DuplicateMember { member });
                }
            }
        }
        Ok(Self { generation, epoch, regions: selected })
    }

    /// Returns the configuration generation.
    #[must_use]
    pub const fn generation(&self) -> u64 {
        self.generation
    }

    /// Returns the replica-set epoch.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }

    /// Returns selected Regions in canonical order.
    #[must_use]
    pub fn regions(&self) -> Vec<RegionId> {
        self.regions.keys().cloned().collect()
    }

    /// Returns the two seats of a selected Region.
    #[must_use]
    pub fn members_of(&self, region: &RegionId) -> Option<&[MemberId; 2]> {
        self.regions.get(region)
    }

    /// Finds the selected Region for a Member.
    #[must_use]
    pub fn region_of(&self, member: &MemberId) -> Option<RegionId> {
        self.regions
            .iter()
            .find_map(|(region, seats)| seats.contains(member).then(|| region.clone()))
    }

    /// Returns every selected Member in canonical Region/Member order.
    #[must_use]
    pub fn all_members(&self) -> Vec<MemberId> {
        self.regions.values().flat_map(|seats| seats.iter().cloned()).collect()
    }

    /// Returns whether this configuration shares a Member with another one.
    #[must_use]
    pub fn overlaps(&self, other: &Self) -> bool {
        self.all_members().into_iter().any(|member| other.region_of(&member).is_some())
    }
}

/// Why a membership shape or transition was refused.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum MembershipError {
    /// An identifier was invalid.
    InvalidIdentifier,
    /// Generation or epoch zero is not a usable configuration.
    InvalidVersion,
    /// At least one selected Region is required.
    EmptyMembership,
    /// The same Region was selected twice.
    DuplicateRegion { region: RegionId },
    /// A Member cannot occupy two selected seats.
    DuplicateMember { member: MemberId },
    /// A replacement did not advance both fencing values.
    StaleVersion { generation: u64, epoch: u64 },
    /// A replacement without overlap would lose the old quorum boundary.
    NoOverlap,
}

/// One durable Member observation.  Acknowledgement identity includes the
/// operation and both fencing values; a transport ACK without these fields is
/// not a durability fact.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DurableAck {
    operation: String,
    member: MemberId,
    region: RegionId,
    generation: u64,
    epoch: u64,
}

impl DurableAck {
    /// Creates an observation from validated identities.
    #[must_use]
    pub fn new(
        operation: String,
        member: MemberId,
        region: RegionId,
        generation: u64,
        epoch: u64,
    ) -> Self {
        Self { operation, member, region, generation, epoch }
    }

    /// Returns the immutable operation identity.
    #[must_use]
    pub fn operation(&self) -> &str {
        &self.operation
    }

    /// Returns the Member that durably stored the operation.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// Returns the Region of that Member.
    #[must_use]
    pub fn region(&self) -> &RegionId {
        &self.region
    }

    /// Returns the configuration generation observed by the Member.
    #[must_use]
    pub const fn generation(&self) -> u64 {
        self.generation
    }

    /// Returns the replica-set epoch observed by the Member.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }
}

/// A successful, explicitly requested durability result.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DurabilityReceipt {
    operation: String,
    level: DurabilityLevel,
    coordinator: MemberId,
    generation: u64,
    epoch: u64,
    contributors: Vec<DurableAck>,
}

impl DurabilityReceipt {
    /// Returns the operation identity.
    #[must_use]
    pub fn operation(&self) -> &str {
        &self.operation
    }

    /// Returns the requested and achieved level.
    #[must_use]
    pub const fn level(&self) -> DurabilityLevel {
        self.level
    }

    /// Returns the request-scoped coordinator, never a permanent owner.
    #[must_use]
    pub fn coordinator(&self) -> &MemberId {
        &self.coordinator
    }

    /// Returns the fencing generation and epoch.
    #[must_use]
    pub const fn generation(&self) -> u64 {
        self.generation
    }

    /// Returns the fencing epoch.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }

    /// Returns every durable contributor, once, in canonical order.
    #[must_use]
    pub fn contributors(&self) -> &[DurableAck] {
        &self.contributors
    }
}

/// A partial observation returned when a requested boundary was not met.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PartialObservation {
    operation: String,
    generation: u64,
    epoch: u64,
    stored: Vec<DurableAck>,
    missing_members: Vec<MemberId>,
    missing_regions: Vec<RegionId>,
}

impl PartialObservation {
    /// Returns the operation whose immutable bytes may already be stored.
    #[must_use]
    pub fn operation(&self) -> &str {
        &self.operation
    }

    /// Returns the fencing generation and epoch.
    #[must_use]
    pub const fn generation(&self) -> u64 {
        self.generation
    }

    /// Returns the stored durable observations.
    #[must_use]
    pub fn stored(&self) -> &[DurableAck] {
        &self.stored
    }

    /// Returns local seats not yet stored for the required boundary.
    #[must_use]
    pub fn missing_members(&self) -> &[MemberId] {
        &self.missing_members
    }

    /// Returns Regions whose RF=2 boundary was not met.
    #[must_use]
    pub fn missing_regions(&self) -> &[RegionId] {
        &self.missing_regions
    }
}

/// Why an acknowledgement attempt did not produce a durability receipt.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum DurabilityFailure {
    /// `GlobalDurable` needs at least two selected active Regions.
    InsufficientRegions {
        /// The number of selected active Regions in the accepted membership.
        selected: usize,
        /// The minimum number required by the durability contract.
        minimum: usize,
    },
    /// No required store was observed before the deterministic deadline.
    Timeout(PartialObservation),
    /// At least one store was observed, but the requested boundary was not.
    Partial(PartialObservation),
    /// The request coordinator was not an active selected Member.
    InvalidCoordinator { coordinator: MemberId },
    /// A previously started operation belongs to an older configuration.
    StaleOperation { operation: String, generation: u64, epoch: u64 },
}

impl DurabilityFailure {
    /// Returns the durable partial observation, regardless of failure kind.
    #[must_use]
    pub fn observation(&self) -> Option<&PartialObservation> {
        match self {
            Self::Timeout(observation) | Self::Partial(observation) => Some(observation),
            Self::InsufficientRegions { .. }
            | Self::InvalidCoordinator { .. }
            | Self::StaleOperation { .. } => None,
        }
    }

    /// Failure variants never claim RegionalDurable or GlobalDurable.
    #[must_use]
    pub const fn is_durable(&self) -> bool {
        false
    }
}

/// Why a supplied durable observation was refused.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum DurabilityError {
    /// The ACK does not belong to the current generation/epoch.
    StaleMembership { expected_generation: u64, expected_epoch: u64 },
    /// The ACK names a Member not seated in its Region.
    WrongSeat,
    /// The same Member ACK was supplied twice for one operation.
    DuplicateAcknowledgement { member: MemberId },
    /// The operation identity is empty.
    EmptyOperation,
    /// The named Member is not selected.
    UnknownMember { member: MemberId },
    /// A Member was not active when persistence was attempted.
    MemberUnavailable { member: MemberId },
    /// The Member belongs to a Region that is WAN-partitioned.
    RegionUnreachable { region: RegionId },
}

/// Cluster-owned deterministic durability collector.
pub struct DurabilityCoordinator {
    membership: Membership,
    active_members: BTreeSet<MemberId>,
    reachable_regions: BTreeSet<RegionId>,
    stored: BTreeMap<String, BTreeMap<MemberId, DurableAck>>,
    operation_versions: BTreeMap<String, (u64, u64)>,
}

impl DurabilityCoordinator {
    /// Opens a collector with every selected seat active and every WAN link up.
    #[must_use]
    pub fn new(membership: Membership) -> Self {
        let active_members = membership.all_members().into_iter().collect();
        let reachable_regions = membership.regions().into_iter().collect();
        Self {
            membership,
            active_members,
            reachable_regions,
            stored: BTreeMap::new(),
            operation_versions: BTreeMap::new(),
        }
    }

    /// Returns the currently accepted membership snapshot.
    #[must_use]
    pub fn membership(&self) -> &Membership {
        &self.membership
    }

    /// Marks one Member unavailable or active for the deterministic fault lane.
    pub fn set_member_active(
        &mut self,
        member: &MemberId,
        active: bool,
    ) -> Result<(), DurabilityError> {
        if !self.membership.all_members().contains(member) {
            return Err(DurabilityError::UnknownMember { member: member.clone() });
        }
        if active {
            self.active_members.insert(member.clone());
        } else {
            self.active_members.remove(member);
        }
        Ok(())
    }

    /// Marks a Region's WAN path reachable or partitioned.
    pub fn set_region_reachable(
        &mut self,
        region: &RegionId,
        reachable: bool,
    ) -> Result<(), DurabilityError> {
        if self.membership.members_of(region).is_none() {
            return Err(DurabilityError::RegionUnreachable { region: region.clone() });
        }
        if reachable {
            self.reachable_regions.insert(region.clone());
        } else {
            self.reachable_regions.remove(region);
        }
        Ok(())
    }

    /// Installs a newer, overlapping membership and fences old observations.
    pub fn advance_membership(&mut self, next: Membership) -> Result<(), MembershipError> {
        if next.generation <= self.membership.generation || next.epoch <= self.membership.epoch {
            return Err(MembershipError::StaleVersion {
                generation: next.generation,
                epoch: next.epoch,
            });
        }
        if !self.membership.overlaps(&next) {
            return Err(MembershipError::NoOverlap);
        }
        self.membership = next;
        self.active_members = self.membership.all_members().into_iter().collect();
        self.reachable_regions = self.membership.regions().into_iter().collect();
        Ok(())
    }

    /// Persists one Member's ACK, fencing stale membership and duplicate ACKs.
    pub fn record_ack(&mut self, ack: DurableAck) -> Result<(), DurabilityError> {
        if ack.operation.is_empty() {
            return Err(DurabilityError::EmptyOperation);
        }
        if ack.generation != self.membership.generation || ack.epoch != self.membership.epoch {
            return Err(DurabilityError::StaleMembership {
                expected_generation: self.membership.generation,
                expected_epoch: self.membership.epoch,
            });
        }
        if self
            .membership
            .members_of(&ack.region)
            .is_none_or(|members| !members.contains(&ack.member))
        {
            return Err(DurabilityError::WrongSeat);
        }
        if !self.active_members.contains(&ack.member) {
            return Err(DurabilityError::MemberUnavailable { member: ack.member });
        }
        let operation_version = self
            .operation_versions
            .entry(ack.operation.clone())
            .or_insert((ack.generation, ack.epoch));
        if *operation_version != (ack.generation, ack.epoch) {
            return Err(DurabilityError::StaleMembership {
                expected_generation: operation_version.0,
                expected_epoch: operation_version.1,
            });
        }
        let entries = self.stored.entry(ack.operation.clone()).or_default();
        if entries.contains_key(&ack.member) {
            return Err(DurabilityError::DuplicateAcknowledgement { member: ack.member });
        }
        entries.insert(ack.member.clone(), ack);
        Ok(())
    }

    /// Records a local store on the coordinator's Region only.
    pub fn persist_local(
        &mut self,
        operation: &str,
        coordinator: &MemberId,
    ) -> Result<(), DurabilityFailure> {
        let region = self
            .membership
            .region_of(coordinator)
            .filter(|_| self.active_members.contains(coordinator))
            .ok_or_else(|| DurabilityFailure::InvalidCoordinator {
                coordinator: coordinator.clone(),
            })?;
        let seats = self.membership.members_of(&region).expect("region_of returned a seat").clone();
        for member in seats {
            self.persist_one(operation, &member, &region);
        }
        Ok(())
    }

    /// Attempts a request and reports only a receipt when its boundary is met.
    pub fn submit(
        &mut self,
        operation: &str,
        coordinator: &MemberId,
        level: DurabilityLevel,
    ) -> Result<DurabilityReceipt, DurabilityFailure> {
        self.submit_inner(operation, coordinator, level, false)
    }

    /// Same as [`Self::submit`], but reports an explicit timeout when no store
    /// was observed by the caller's deterministic deadline.
    pub fn submit_until_deadline(
        &mut self,
        operation: &str,
        coordinator: &MemberId,
        level: DurabilityLevel,
    ) -> Result<DurabilityReceipt, DurabilityFailure> {
        self.submit_inner(operation, coordinator, level, true)
    }

    /// Completes a previously partially persisted operation, allowing a new
    /// request coordinator to recover it without minting a replacement.
    pub fn recover(
        &mut self,
        operation: &str,
        coordinator: &MemberId,
        level: DurabilityLevel,
    ) -> Result<DurabilityReceipt, DurabilityFailure> {
        self.submit(operation, coordinator, level)
    }

    fn submit_inner(
        &mut self,
        operation: &str,
        coordinator: &MemberId,
        level: DurabilityLevel,
        deadline: bool,
    ) -> Result<DurabilityReceipt, DurabilityFailure> {
        let Some(local_region) = self.membership.region_of(coordinator) else {
            return Err(DurabilityFailure::InvalidCoordinator { coordinator: coordinator.clone() });
        };
        if !self.active_members.contains(coordinator) {
            if deadline {
                return Err(DurabilityFailure::Timeout(self.observation(
                    operation,
                    &local_region,
                    level,
                )));
            }
            return Err(DurabilityFailure::InvalidCoordinator { coordinator: coordinator.clone() });
        }
        if operation.is_empty() {
            return Err(DurabilityFailure::Partial(self.observation(
                operation,
                &local_region,
                level,
            )));
        }
        if let Some((generation, epoch)) = self.operation_versions.get(operation) {
            if (*generation, *epoch) != (self.membership.generation, self.membership.epoch) {
                return Err(DurabilityFailure::StaleOperation {
                    operation: operation.to_owned(),
                    generation: *generation,
                    epoch: *epoch,
                });
            }
        }

        if level == DurabilityLevel::GlobalDurable {
            let selected = self.membership.regions().len();
            if selected < 2 {
                return Err(DurabilityFailure::InsufficientRegions { selected, minimum: 2 });
            }
        }

        let mut regions = vec![local_region.clone()];
        if level == DurabilityLevel::GlobalDurable {
            regions.extend(
                self.membership.regions().into_iter().filter(|region| *region != local_region),
            );
        }
        for region in regions {
            if !self.reachable_regions.contains(&region) {
                continue;
            }
            let seats =
                self.membership.members_of(&region).expect("selected region has seats").clone();
            for member in seats {
                self.persist_one(operation, &member, &region);
            }
        }
        self.complete(operation, coordinator, &local_region, level, deadline)
    }

    fn persist_one(&mut self, operation: &str, member: &MemberId, region: &RegionId) {
        if operation.is_empty() || !self.active_members.contains(member) {
            return;
        }
        let ack = DurableAck::new(
            operation.to_owned(),
            member.clone(),
            region.clone(),
            self.membership.generation,
            self.membership.epoch,
        );
        // Existing storage is idempotent. A duplicate delivery is not a new
        // contributor and must never inflate a boundary count.
        if self.stored.get(operation).is_some_and(|entries| entries.contains_key(member)) {
            return;
        }
        let _ = self.record_ack(ack);
    }

    fn complete(
        &self,
        operation: &str,
        coordinator: &MemberId,
        local_region: &RegionId,
        level: DurabilityLevel,
        deadline: bool,
    ) -> Result<DurabilityReceipt, DurabilityFailure> {
        let observation = self.observation(operation, local_region, level);
        let stored = self.stored.get(operation);
        let local_ok = self.regional_ok(stored, local_region);
        let global_ok = level == DurabilityLevel::RegionalDurable
            || self
                .membership
                .regions()
                .into_iter()
                .all(|region| self.regional_ok(stored, &region));
        if local_ok && global_ok {
            let mut contributors: Vec<DurableAck> = stored
                .into_iter()
                .flat_map(|entries| entries.values().cloned())
                .filter(|ack| {
                    level == DurabilityLevel::GlobalDurable || ack.region == *local_region
                })
                .collect();
            contributors.sort_by(|left, right| {
                left.region
                    .as_str()
                    .cmp(right.region.as_str())
                    .then_with(|| left.member.as_str().cmp(right.member.as_str()))
            });
            return Ok(DurabilityReceipt {
                operation: operation.to_owned(),
                level,
                coordinator: coordinator.clone(),
                generation: self.membership.generation,
                epoch: self.membership.epoch,
                contributors,
            });
        }
        if deadline && observation.stored.is_empty() {
            Err(DurabilityFailure::Timeout(observation))
        } else {
            Err(DurabilityFailure::Partial(observation))
        }
    }

    fn regional_ok(
        &self,
        stored: Option<&BTreeMap<MemberId, DurableAck>>,
        region: &RegionId,
    ) -> bool {
        let Some(seats) = self.membership.members_of(region) else { return false };
        seats.iter().all(|member| {
            self.active_members.contains(member)
                && stored.is_some_and(|entries| entries.contains_key(member))
        })
    }

    fn observation(
        &self,
        operation: &str,
        local_region: &RegionId,
        level: DurabilityLevel,
    ) -> PartialObservation {
        let stored: Vec<DurableAck> = self
            .stored
            .get(operation)
            .into_iter()
            .flat_map(|entries| entries.values().cloned())
            .collect();
        let required_regions = if level == DurabilityLevel::RegionalDurable {
            vec![local_region.clone()]
        } else {
            self.membership.regions()
        };
        let missing_regions: Vec<RegionId> = required_regions
            .iter()
            .filter(|region| !self.regional_ok(self.stored.get(operation), region))
            .cloned()
            .collect();
        let missing_members = missing_regions
            .iter()
            .flat_map(|region| self.membership.members_of(region).into_iter().flatten())
            .filter(|member| !stored.iter().any(|ack| ack.member == **member))
            .cloned()
            .collect();
        PartialObservation {
            operation: operation.to_owned(),
            generation: self.membership.generation,
            epoch: self.membership.epoch,
            stored,
            missing_members,
            missing_regions,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn membership(region_names: &[&str]) -> Membership {
        let regions = region_names
            .iter()
            .enumerate()
            .map(|(index, region)| {
                RegionMembers::try_new(region, [&format!("m{index}a"), &format!("m{index}b")])
                    .unwrap()
            })
            .collect();
        Membership::new(1, 1, regions).unwrap()
    }

    fn id(value: &str) -> MemberId {
        MemberId::try_new(value).unwrap()
    }

    #[test]
    fn regional_requires_both_members_in_one_region() {
        let mut coordinator = DurabilityCoordinator::new(membership(&["r1"]));
        let receipt =
            coordinator.submit("op", &id("m0a"), DurabilityLevel::RegionalDurable).unwrap();
        assert_eq!(receipt.contributors().len(), 2);
        assert_eq!(receipt.level(), DurabilityLevel::RegionalDurable);
    }

    #[test]
    fn global_refuses_one_region_and_requires_all_regions_for_larger_shapes() {
        let mut one = DurabilityCoordinator::new(membership(&["r1"]));
        assert_eq!(
            one.submit("op", &id("m0a"), DurabilityLevel::GlobalDurable),
            Err(DurabilityFailure::InsufficientRegions { selected: 1, minimum: 2 })
        );

        for names in [&["r1", "r2"][..], &["r1", "r2", "r3"][..]] {
            let mut coordinator = DurabilityCoordinator::new(membership(names));
            let receipt =
                coordinator.submit("op", &id("m0a"), DurabilityLevel::GlobalDurable).unwrap();
            assert_eq!(receipt.contributors().len(), names.len() * 2);
        }
    }

    #[test]
    fn member_failure_is_partial_and_recovery_uses_the_same_operation() {
        let mut coordinator = DurabilityCoordinator::new(membership(&["r1"]));
        coordinator.set_member_active(&id("m0b"), false).unwrap();
        let failure =
            coordinator.submit("op", &id("m0a"), DurabilityLevel::RegionalDurable).unwrap_err();
        assert!(matches!(failure, DurabilityFailure::Partial(_)));
        assert!(!failure.is_durable());
        coordinator.set_member_active(&id("m0b"), true).unwrap();
        let receipt =
            coordinator.recover("op", &id("m0b"), DurabilityLevel::RegionalDurable).unwrap();
        assert_eq!(receipt.contributors().len(), 2);
    }

    #[test]
    fn wan_partition_never_claims_global_durability() {
        let mut coordinator = DurabilityCoordinator::new(membership(&["r1", "r2"]));
        coordinator.set_region_reachable(&RegionId::try_new("r2").unwrap(), false).unwrap();
        let failure =
            coordinator.submit("op", &id("m0a"), DurabilityLevel::GlobalDurable).unwrap_err();
        assert!(matches!(failure, DurabilityFailure::Partial(_)));
        assert_eq!(
            failure.observation().unwrap().missing_regions(),
            &[RegionId::try_new("r2").unwrap()]
        );
    }

    #[test]
    fn stale_membership_and_duplicate_ack_are_typed_refusals() {
        let mut coordinator = DurabilityCoordinator::new(membership(&["r1"]));
        let ack = DurableAck::new("op".into(), id("m0a"), RegionId::try_new("r1").unwrap(), 1, 1);
        coordinator.record_ack(ack.clone()).unwrap();
        assert!(matches!(
            coordinator.record_ack(ack),
            Err(DurabilityError::DuplicateAcknowledgement { .. })
        ));
        let next =
            Membership::new(2, 2, vec![RegionMembers::try_new("r1", ["m0a", "m0b"]).unwrap()])
                .unwrap();
        coordinator.advance_membership(next).unwrap();
        assert!(matches!(
            coordinator.record_ack(DurableAck::new(
                "new".into(),
                id("m0a"),
                RegionId::try_new("r1").unwrap(),
                1,
                1
            )),
            Err(DurabilityError::StaleMembership { .. })
        ));
    }

    #[test]
    fn duplicate_members_and_regions_are_refused() {
        assert!(matches!(
            RegionMembers::try_new("r1", ["m", "m"]),
            Err(MembershipError::DuplicateMember { .. })
        ));
        let duplicate = vec![
            RegionMembers::try_new("r1", ["m1", "m2"]).unwrap(),
            RegionMembers::try_new("r1", ["m3", "m4"]).unwrap(),
        ];
        assert!(matches!(
            Membership::new(1, 1, duplicate),
            Err(MembershipError::DuplicateRegion { .. })
        ));
    }

    #[test]
    fn coordinator_loss_after_local_persistence_recovers_without_ownership() {
        let mut coordinator = DurabilityCoordinator::new(membership(&["r1", "r2"]));
        coordinator.persist_local("op", &id("m0a")).unwrap();
        coordinator.set_region_reachable(&RegionId::try_new("r2").unwrap(), false).unwrap();
        let failed =
            coordinator.submit("op", &id("m0b"), DurabilityLevel::GlobalDurable).unwrap_err();
        assert!(matches!(failed, DurabilityFailure::Partial(_)));
        coordinator.set_region_reachable(&RegionId::try_new("r2").unwrap(), true).unwrap();
        let recovered =
            coordinator.recover("op", &id("m1a"), DurabilityLevel::GlobalDurable).unwrap();
        assert_eq!(recovered.contributors().len(), 4);
        assert_eq!(recovered.coordinator(), &id("m1a"));
    }

    #[test]
    fn timeout_is_distinct_from_partial_when_nothing_was_stored() {
        let mut coordinator = DurabilityCoordinator::new(membership(&["r1"]));
        coordinator.set_member_active(&id("m0a"), false).unwrap();
        coordinator.set_member_active(&id("m0b"), false).unwrap();
        let failure = coordinator
            .submit_until_deadline("op", &id("m0b"), DurabilityLevel::RegionalDurable)
            .unwrap_err();
        assert!(matches!(failure, DurabilityFailure::Timeout(_)));
        assert!(!failure.is_durable());
    }
}
