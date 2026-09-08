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
    pub fn try_new(region: RegionId, incarnation: u128) -> Result<Self, OriginLaneError> {
        if incarnation == 0 {
            return Err(OriginLaneError::ZeroIncarnation);
        }
        Ok(Self { region, incarnation })
    }

    /// Parses the canonical wire/storage spelling `lane1.<region>.<hex128>`.
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
        Self::try_new(region, incarnation)
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
}
