//! Validated lane identifiers and the caller-supplied membership view.
//!
//! The identifiers follow the engine crate's logical-identifier rules exactly
//! (non-empty, at most 255 bytes, no control characters, no leading or
//! trailing whitespace): the rules are restated here, not inherited, because
//! this crate stands alone by design (the engine workspace stays untouched
//! until the integrator wires the crate in).
//!
//! [`TopologyView`] is the same caller-supplied membership snapshot the W4
//! plane consumes: Regions, Member seats with failure domains, and per-range
//! ownership under raised epochs. It is an input, never a decision —
//! provisioning and membership policy stay with the control plane.

use std::collections::BTreeMap;
use std::fmt;

/// The maximum byte length of a lane identifier.
pub const MAX_IDENTIFIER_BYTES: usize = 255;

/// Validates one lane identifier text against the logical-identifier rules.
///
/// # Errors
///
/// Returns [`IdentityError::Empty`] for empty text and
/// [`IdentityError::Invalid`] for text over [`MAX_IDENTIFIER_BYTES`] bytes,
/// control characters, or leading/trailing whitespace.
pub fn validate_identifier(value: &str) -> Result<(), IdentityError> {
    if value.is_empty() {
        return Err(IdentityError::Empty);
    }
    if value.len() > MAX_IDENTIFIER_BYTES {
        return Err(IdentityError::Invalid);
    }
    if value.chars().any(char::is_control) {
        return Err(IdentityError::Invalid);
    }
    if value != value.trim() {
        return Err(IdentityError::Invalid);
    }
    Ok(())
}

/// A validated lane identifier failed its rules.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IdentityError {
    /// The identifier text was empty.
    Empty,
    /// The identifier text broke the length, character, or whitespace rule.
    Invalid,
}

macro_rules! lane_identifier {
    ($name:ident, $doc:literal) => {
        #[doc = $doc]
        #[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
        pub struct $name {
            identifier: String,
        }

        impl $name {
            /// Creates one validated identifier.
            ///
            /// # Errors
            ///
            /// Returns the [`IdentityError`] of the broken rule.
            pub fn try_new(value: &str) -> Result<Self, IdentityError> {
                validate_identifier(value)?;
                Ok(Self { identifier: value.to_owned() })
            }

            /// Returns the canonical identifier text.
            #[must_use]
            pub fn as_str(&self) -> &str {
                &self.identifier
            }
        }

        impl fmt::Display for $name {
            fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
                formatter.write_str(&self.identifier)
            }
        }
    };
}

lane_identifier!(RegionId, "A validated logical Region identifier (CONTEXT: Region).");
lane_identifier!(MemberId, "A validated logical Member identifier (CONTEXT: Member).");
lane_identifier!(TenantId, "A validated logical tenant identifier.");
lane_identifier!(OperationId, "A stable, validated operation identity.");
lane_identifier!(DocumentId, "A validated document identity.");
lane_identifier!(CollectionName, "A validated collection name.");

/// One Member's seat in a [`TopologyView`].
#[derive(Clone, Debug, Eq, PartialEq)]
struct MemberSeat {
    member: MemberId,
    region: RegionId,
    failure_domain: String,
}

/// One data range's write-path ownership in a [`TopologyView`].
#[derive(Clone, Debug, Eq, PartialEq)]
struct RangeOwnership {
    range: String,
    owner: MemberId,
    epoch: u64,
}

/// A caller-supplied membership view: Regions, Members, failure-domain
/// grouping, and per-range ownership under the adjudicated algorithm.
///
/// An immutable snapshot and an input, not a decision. Built through the
/// [`TopologyView::new`] builder chain and servable only after
/// [`TopologyView::validated`] accepts it.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct TopologyView {
    regions: Vec<RegionId>,
    members: Vec<MemberSeat>,
    owners: Vec<RangeOwnership>,
}

impl TopologyView {
    /// Creates an empty view.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Adds one Region.
    ///
    /// # Errors
    ///
    /// Returns [`TopologyError::InvalidRegion`] for invalid Region text and
    /// [`TopologyError::DuplicateRegion`] when the Region is already seated.
    pub fn with_region(mut self, region: &str) -> Result<Self, TopologyError> {
        let region = RegionId::try_new(region).map_err(|_| TopologyError::InvalidRegion)?;
        if self.regions.contains(&region) {
            return Err(TopologyError::DuplicateRegion);
        }
        self.regions.push(region);
        self.regions.sort();
        Ok(self)
    }

    /// Seats one Member in a Region inside a failure domain.
    ///
    /// # Errors
    ///
    /// Returns [`TopologyError::InvalidMember`], [`TopologyError::UnknownRegion`]
    /// when the Region is not seated, [`TopologyError::DuplicateMember`], or
    /// [`TopologyError::InvalidFailureDomain`].
    pub fn with_member(
        mut self,
        member: &str,
        region: &str,
        failure_domain: &str,
    ) -> Result<Self, TopologyError> {
        let member = MemberId::try_new(member).map_err(|_| TopologyError::InvalidMember)?;
        let region = RegionId::try_new(region).map_err(|_| TopologyError::InvalidRegion)?;
        if !self.regions.contains(&region) {
            return Err(TopologyError::UnknownRegion);
        }
        if self.members.iter().any(|seat| seat.member == member) {
            return Err(TopologyError::DuplicateMember);
        }
        validate_identifier(failure_domain).map_err(|_| TopologyError::InvalidFailureDomain)?;
        self.members.push(MemberSeat { member, region, failure_domain: failure_domain.to_owned() });
        self.members.sort_by(|left, right| left.member.cmp(&right.member));
        Ok(self)
    }

    /// Assigns one data range's write path to a Member at epoch 1.
    ///
    /// # Errors
    ///
    /// See [`TopologyView::with_owner_at_epoch`].
    pub fn with_owner(self, range: &str, member: &str) -> Result<Self, TopologyError> {
        self.with_owner_at_epoch(range, member, 1)
    }

    /// Assigns one data range's write path to a Member at `epoch`.
    ///
    /// The epoch is monotonically raised by every control-plane transition of
    /// the range, so a deposed owner can be fenced (D-1). A Member already
    /// owning the range may re-affirm at the same or higher epoch; a flip to
    /// another Member must raise the epoch.
    ///
    /// # Errors
    ///
    /// Returns [`TopologyError::InvalidRange`], [`TopologyError::UnknownMember`],
    /// or [`TopologyError::UnraisedEpoch`] for an owner flip without a raised
    /// epoch.
    pub fn with_owner_at_epoch(
        mut self,
        range: &str,
        member: &str,
        epoch: u64,
    ) -> Result<Self, TopologyError> {
        validate_identifier(range).map_err(|_| TopologyError::InvalidRange)?;
        if epoch == 0 {
            return Err(TopologyError::InvalidEpoch);
        }
        let member = MemberId::try_new(member).map_err(|_| TopologyError::InvalidMember)?;
        if !self.members.iter().any(|seat| seat.member == member) {
            return Err(TopologyError::UnknownMember);
        }
        if let Some(existing) = self.owners.iter_mut().find(|ownership| ownership.range == range) {
            let acceptable = existing.owner == member || epoch > existing.epoch;
            if !acceptable {
                return Err(TopologyError::UnraisedEpoch);
            }
            if epoch >= existing.epoch {
                existing.owner = member;
                existing.epoch = epoch;
            }
        } else {
            self.owners.push(RangeOwnership { range: range.to_owned(), owner: member, epoch });
            self.owners.sort_by(|left, right| left.range.cmp(&right.range));
        }
        Ok(self)
    }

    /// Validates the assembled view.
    ///
    /// # Errors
    ///
    /// Returns [`TopologyError::NoRegions`], [`TopologyError::NoMembers`], or
    /// [`TopologyError::MemberWithoutRegion`] when a Member is seated outside
    /// every seated Region.
    pub fn validated(self) -> Result<Self, TopologyError> {
        if self.regions.is_empty() {
            return Err(TopologyError::NoRegions);
        }
        if self.members.is_empty() {
            return Err(TopologyError::NoMembers);
        }
        for seat in &self.members {
            if !self.regions.contains(&seat.region) {
                return Err(TopologyError::MemberWithoutRegion);
            }
        }
        Ok(self)
    }

    /// Returns the seated Regions, in canonical order.
    pub fn regions(&self) -> impl Iterator<Item = &RegionId> {
        self.regions.iter()
    }

    /// Returns the seated Members, in canonical order.
    pub fn members(&self) -> impl Iterator<Item = &MemberId> {
        self.members.iter().map(|seat| &seat.member)
    }

    /// Reports whether the view seats `member`.
    #[must_use]
    pub fn contains_member(&self, member: &MemberId) -> bool {
        self.members.iter().any(|seat| &seat.member == member)
    }

    /// Returns the Region a Member is seated in.
    #[must_use]
    pub fn region_of(&self, member: &MemberId) -> Option<&RegionId> {
        self.members.iter().find(|seat| &seat.member == member).map(|seat| &seat.region)
    }

    /// Returns the failure domain a Member is seated in.
    #[must_use]
    pub fn failure_domain_of(&self, member: &MemberId) -> Option<&str> {
        self.members
            .iter()
            .find(|seat| &seat.member == member)
            .map(|seat| seat.failure_domain.as_str())
    }

    /// Returns each failure domain with its Members.
    #[must_use]
    pub fn failure_domains(&self) -> Vec<(String, Vec<MemberId>)> {
        let mut grouped: BTreeMap<String, Vec<MemberId>> = BTreeMap::new();
        for seat in &self.members {
            grouped.entry(seat.failure_domain.clone()).or_default().push(seat.member.clone());
        }
        grouped.into_iter().collect()
    }

    /// Returns the Members seated in one Region, in canonical order.
    #[must_use]
    pub fn members_of_region(&self, region: &RegionId) -> Vec<MemberId> {
        self.members
            .iter()
            .filter(|seat| &seat.region == region)
            .map(|seat| seat.member.clone())
            .collect()
    }

    /// Returns the owning Member of one data range.
    #[must_use]
    pub fn owner_of(&self, range: &str) -> Option<&MemberId> {
        self.owners.iter().find(|ownership| ownership.range == range).map(|o| &o.owner)
    }

    /// Returns the ownership epoch of one data range.
    #[must_use]
    pub fn owner_epoch_of(&self, range: &str) -> Option<u64> {
        self.owners.iter().find(|ownership| ownership.range == range).map(|o| o.epoch)
    }

    /// Returns every owned data range, in canonical order.
    pub fn owned_ranges(&self) -> impl Iterator<Item = &str> {
        self.owners.iter().map(|ownership| ownership.range.as_str())
    }

    /// Reports whether the view assigns any range.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.owners.is_empty()
    }
}

/// A membership-view input failed validation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TopologyError {
    /// A Region or Member identifier broke the identifier rules.
    InvalidRegion,
    /// A Member identifier broke the identifier rules.
    InvalidMember,
    /// A range or failure-domain text broke the identifier rules.
    InvalidRange,
    /// A failure-domain text broke the identifier rules.
    InvalidFailureDomain,
    /// An ownership epoch of zero was supplied.
    InvalidEpoch,
    /// The same Region was seated twice.
    DuplicateRegion,
    /// The same Member was seated twice.
    DuplicateMember,
    /// A Member was seated in a Region the view does not name.
    UnknownRegion,
    /// A Member the view does not seat was named an owner.
    UnknownMember,
    /// An owner flip without a raised epoch.
    UnraisedEpoch,
    /// The view names no Region.
    NoRegions,
    /// The view seats no Member.
    NoMembers,
    /// A Member is seated outside every seated Region.
    MemberWithoutRegion,
}

#[cfg(test)]
mod tests {
    use super::{IdentityError, TopologyError, TopologyView, validate_identifier};

    #[test]
    fn lane_identifiers_match_the_logical_identifier_rules() {
        assert_eq!(validate_identifier(""), Err(IdentityError::Empty));
        assert_eq!(validate_identifier(" leading"), Err(IdentityError::Invalid));
        assert_eq!(validate_identifier("trailing "), Err(IdentityError::Invalid));
        assert_eq!(validate_identifier("control\u{7}"), Err(IdentityError::Invalid));
        assert_eq!(validate_identifier(&"r".repeat(256)), Err(IdentityError::Invalid));
        assert!(validate_identifier("r-alpha_1.2").is_ok());
        assert!(validate_identifier(&"m".repeat(255)).is_ok());
    }

    #[test]
    fn an_owner_flip_without_a_raised_epoch_is_rejected() {
        let base = TopologyView::new()
            .with_region("r-alpha")
            .expect("region")
            .with_member("m-a1", "r-alpha", "fd-a")
            .expect("member")
            .with_member("m-a2", "r-alpha", "fd-a")
            .expect("member")
            .with_owner_at_epoch("main", "m-a1", 1)
            .expect("owner");
        assert_eq!(
            base.clone().with_owner_at_epoch("main", "m-a2", 1),
            Err(TopologyError::UnraisedEpoch)
        );
        assert!(base.with_owner_at_epoch("main", "m-a2", 2).is_ok());
    }
}
