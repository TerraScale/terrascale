//! Region names and reviewed origin validation.

use std::fmt;
use std::str::FromStr;

use serde::{Deserialize, Serialize};

use crate::error::{Error, Result};
use crate::origin::{service_origin, API};

/// Launch regions (GOAL D-05).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "UPPERCASE")]
pub enum RegionCode {
    Gru,
    Sfo,
    Fra,
}

impl RegionCode {
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
