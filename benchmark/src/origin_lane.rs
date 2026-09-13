//! Stable causal origin identity for one physical shard lane.
//!
//! A Region is placement metadata.  It is not a unique causal actor once a
//! database has more than one SQLite shard in that Region.

use std::fmt;

use crate::types::RegionId;

const PREFIX: &str = "lane1";

/// One immutable causal origin lane, persisted with a physical shard file.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct OriginLaneId {
    region: RegionId,
    incarnation: u128,
}

impl OriginLaneId {
    /// Constructs an origin lane from the accepted Region and durable lane
    /// incarnation. Zero is reserved for an absent or uninitialized lane.
    ///
    /// # Errors
    ///
    /// Returns [`OriginLaneError::ZeroIncarnation`] when `incarnation` is zero.
    pub fn try_new(region: RegionId, incarnation: u128) -> Result<Self, OriginLaneError> {
        if incarnation == 0 {
            return Err(OriginLaneError::ZeroIncarnation);
        }
        Ok(Self { region, incarnation })
    }

    /// Parses the canonical wire/storage spelling `lane1.<region>.<hex128>`.
    ///
    /// # Errors
    ///
    /// Returns [`OriginLaneError::Malformed`] when `value` is not a canonical
    /// lane token, or [`OriginLaneError::ZeroIncarnation`] when its
    /// incarnation is zero.
    pub fn parse(value: &str) -> Result<Self, OriginLaneError> {
        let mut parts = value.split('.');
        if parts.next() != Some(PREFIX) {
            return Err(OriginLaneError::Malformed);
        }
        let region = RegionId::try_new(parts.next().ok_or(OriginLaneError::Malformed)?)
            .map_err(|_| OriginLaneError::Malformed)?;
        let hex = parts.next().ok_or(OriginLaneError::Malformed)?;
        if parts.next().is_some()
            || hex.len() != 32
            || !hex.bytes().all(|byte| byte.is_ascii_hexdigit())
        {
            return Err(OriginLaneError::Malformed);
        }
        let incarnation = u128::from_str_radix(hex, 16).map_err(|_| OriginLaneError::Malformed)?;
        let lane = Self::try_new(region, incarnation)?;
        if lane.as_str() != value {
            return Err(OriginLaneError::Malformed);
        }
        Ok(lane)
    }

    /// Returns the placement Region.
    #[must_use]
    pub fn region(&self) -> &RegionId {
        &self.region
    }

    /// Returns the durable lane incarnation.
    #[must_use]
    pub const fn incarnation(&self) -> u128 {
        self.incarnation
    }

    /// Returns the canonical token used in publication, repair, and session
    /// maps. The token is distinct from `RegionId` by type and spelling.
    #[must_use]
    pub fn as_str(&self) -> String {
        format!("{PREFIX}.{}.{:032x}", self.region, self.incarnation)
    }
}

impl fmt::Display for OriginLaneId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{PREFIX}.{}.{:032x}", self.region, self.incarnation)
    }
}

/// Malformed or uninitialized origin-lane identity.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum OriginLaneError {
    /// The lane incarnation is zero.
    ZeroIncarnation,
    /// The token is not the canonical lane spelling.
    Malformed,
}

impl fmt::Display for OriginLaneError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::ZeroIncarnation => "origin lane incarnation is zero",
            Self::Malformed => "malformed origin lane token",
        })
    }
}

impl std::error::Error for OriginLaneError {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn canonical_token_round_trips_and_distinguishes_same_region_lanes() {
        let region = RegionId::try_new("GRU").unwrap();
        let first = OriginLaneId::try_new(region.clone(), 1).unwrap();
        let second = OriginLaneId::try_new(region, 2).unwrap();
        assert_ne!(first.as_str(), second.as_str());
        assert_eq!(OriginLaneId::parse(&first.as_str()).unwrap(), first);
        assert!(OriginLaneId::parse("GRU").is_err());
    }

    #[test]
    fn parser_rejects_every_noncanonical_or_overflowing_shape() {
        let valid = "lane1.GRU.00000000000000000000000000000001";
        for value in [
            "",
            "lane1",
            "lane1.GRU",
            "lane1.GRU.1",
            "lane1.GRU.00000000000000000000000000000000",
            "lane1.GRU.0000000000000000000000000000000G",
            "lane1.GRU.00000000000000000000000000000001.extra",
            "lane1.GRU.0000000000000000000000000000000a ",
            "lane2.GRU.00000000000000000000000000000001",
        ] {
            assert!(OriginLaneId::parse(value).is_err(), "accepted malformed lane {value:?}");
        }
        assert!(OriginLaneId::parse(valid).is_ok());
        let lowercase_hex = "lane1.GRU.0000000000000000000000000000000a";
        assert!(OriginLaneId::parse(lowercase_hex).is_ok());
        assert!(OriginLaneId::parse(&lowercase_hex.replace('a', "A")).is_err());
    }

    #[test]
    fn lane_constructor_rejects_zero_and_preserves_maximum_incarnation() {
        let region = RegionId::try_new("remote").unwrap();
        assert_eq!(OriginLaneId::try_new(region.clone(), 0), Err(OriginLaneError::ZeroIncarnation));
        let lane = OriginLaneId::try_new(region, u128::MAX).unwrap();
        assert_eq!(OriginLaneId::parse(&lane.as_str()).unwrap(), lane);
        assert_eq!(lane.as_str().len(), "lane1.remote.".len() + 32);
    }

    #[test]
    fn lane_order_is_deterministic_by_region_then_incarnation() {
        let region = RegionId::try_new("GRU").unwrap();
        let mut lanes = [
            OriginLaneId::try_new(region.clone(), 2).unwrap(),
            OriginLaneId::try_new(region.clone(), 1).unwrap(),
            OriginLaneId::try_new(RegionId::try_new("FRA").unwrap(), 1).unwrap(),
        ];
        lanes.sort();
        assert_eq!(lanes[0].region().as_str(), "FRA");
        assert_eq!(lanes[1].incarnation(), 1);
        assert_eq!(lanes[2].incarnation(), 2);
    }
}
