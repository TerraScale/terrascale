//! Region names and reviewed origin validation.
//!
//! Every region is an equal active-active peer: each one accepts reads and
//! writes for every database placed in it, and none is a primary or home.

use std::fmt;
use std::str::FromStr;

use serde::{Deserialize, Serialize};

use crate::error::{Error, Result};
use crate::origin::{service_origin, API};

/// Launch regions (GOAL D-05), all equal active-active peers.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "UPPERCASE")]
pub enum RegionCode {
    Gru,
    Sfo,
    Fra,
}

impl RegionCode {
    /// Every region, in canonical order.
    pub const ALL: [Self; 3] = [Self::Fra, Self::Gru, Self::Sfo];

    /// The other regions, each an equal active-active peer of this one.
    pub fn peers(self) -> impl Iterator<Item = Self> {
        Self::ALL.into_iter().filter(move |region| *region != self)
    }

    /// Parses a complete region set: case-insensitive, deduplicated and in
    /// canonical order. An empty set is refused.
    pub fn parse_set<S: AsRef<str>>(values: &[S], field: &str) -> Result<Vec<Self>> {
        let mut regions = values
            .iter()
            .map(|value| Self::parse(value.as_ref(), field))
            .collect::<Result<Vec<_>>>()?;
        regions.sort_by_key(|region| region.as_str());
        regions.dedup();
        if regions.is_empty() {
            return Err(Error::new(
                crate::error::ErrorKind::Validation,
                "invalid_region",
                format!("{field} must name at least one region."),
            ));
        }
        Ok(regions)
    }

    /// Lowercase wire form, e.g. `gru`.
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Gru => "gru",
            Self::Sfo => "sfo",
            Self::Fra => "fra",
        }
    }

    /// Canonical uppercase wire form used by region request headers.
    #[must_use]
    pub fn as_header_str(self) -> &'static str {
        match self {
            Self::Gru => "GRU",
            Self::Sfo => "SFO",
            Self::Fra => "FRA",
        }
    }

    /// Parse a user-supplied region string, case-insensitively.
    pub fn parse(value: &str, field: &str) -> Result<Self> {
        value.trim().to_lowercase().as_str().parse().map_err(|()| {
            Error::new(
                crate::error::ErrorKind::Validation,
                "invalid_region",
                format!("{field} must be one of gru, sfo, fra."),
            )
        })
    }
}

impl FromStr for RegionCode {
    type Err = ();

    fn from_str(value: &str) -> std::result::Result<Self, Self::Err> {
        match value.trim().to_lowercase().as_str() {
            "gru" => Ok(Self::Gru),
            "sfo" => Ok(Self::Sfo),
            "fra" => Ok(Self::Fra),
            _ => Err(()),
        }
    }
}

impl fmt::Display for RegionCode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

/// Normalizes a TerraBase API base URL to its origin.
pub fn normalize_base_url(base_url: &str) -> Result<String> {
    service_origin(base_url, "base URL", &API)
        .map(|url| url.origin().ascii_serialization())
        .map_err(|message| {
            Error::new(
                crate::error::ErrorKind::Validation,
                "invalid_base_url",
                message,
            )
        })
}

#[cfg(test)]
mod tests {
    use super::RegionCode;

    #[test]
    fn regions_are_equal_peers() {
        assert_eq!(
            RegionCode::Gru.peers().collect::<Vec<_>>(),
            [RegionCode::Fra, RegionCode::Sfo]
        );
        assert_eq!(
            RegionCode::parse_set(&["SFO", "gru", "sfo"], "regions").ok(),
            Some(vec![RegionCode::Gru, RegionCode::Sfo])
        );
        assert!(RegionCode::parse_set::<&str>(&[], "regions").is_err());
    }
}
