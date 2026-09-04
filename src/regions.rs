//! Region codes, affinity, and URL pinning.
//!
//! The SDK pins REST (and optional WebSocket) traffic to a healthy hosting
//! region (`<region>.api.terrascale.tech`) and keeps reactive HTTP on the
//! global `sync.api.` entry point. A `421` with `X-TerraScale-Placement`
//! repairs a stale pin.

use std::fmt;
use std::str::FromStr;

use serde::{Deserialize, Serialize};

use crate::error::{Error, Result};

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

/// How strictly the client sticks to its preferred region.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum RegionAffinity {
    /// Pin to the preferred region, fail over once on 421/unreachable.
    #[default]
    Failover,
    /// Never leave the preferred region; fail closed instead.
    Strict,
}

/// Global REST entry point (GOAL D-24).
pub const GLOBAL_API_BASE_URL: &str = "https://api.terrascale.tech";

/// Regional hostname for pinned REST traffic.
#[must_use]
pub fn regional_host(region: RegionCode) -> String {
    format!("{}.api.terrascale.tech", region.as_str())
}

/// Resolve the base URL for pinned traffic.
///
/// A per-request `hint` wins over the configured preferred region. Strict
/// affinity requires one of the two to be set.
pub fn pin_base_url(
    global_base_url: &str,
    preferred: Option<RegionCode>,
    hint: Option<RegionCode>,
    affinity: RegionAffinity,
) -> Result<String> {
    let region = hint.or(preferred);
    match (affinity, region) {
        (_, None) if affinity == RegionAffinity::Strict => Err(Error::new(
            crate::error::ErrorKind::Validation,
            "region_required",
            "Strict regional affinity requires a preferredRegion or per-request regionHint.",
        )),
        (_, None) => Ok(normalize_base_url(global_base_url)?),
        (RegionAffinity::Failover, Some(_)) if hint.is_none() => {
            Ok(normalize_base_url(global_base_url)?)
        }
        (_, Some(region)) => {
            let base = normalize_base_url(global_base_url)?;
            apply_region_pin(&base, region)
        }
    }
}

/// Normalize a base URL (no trailing slash, non-empty).
pub fn normalize_base_url(base_url: &str) -> Result<String> {
    let trimmed = base_url.trim().trim_end_matches('/').to_owned();
    if trimmed.is_empty() {
        return Err(Error::new(
            crate::error::ErrorKind::Validation,
            "invalid_base_url",
            "API base URL cannot be empty.",
        ));
    }
    Ok(trimmed)
}

fn apply_region_pin(base: &str, region: RegionCode) -> Result<String> {
    let global = normalize_base_url(GLOBAL_API_BASE_URL)?;
    if base == global {
        return Ok(format!("https://{}", regional_host(region)));
    }
    let expected = format!("https://{}", regional_host(region));
    if base == expected {
        return Ok(base.to_owned());
    }
    Err(Error::new(
        crate::error::ErrorKind::Validation,
        "region_pin_mismatch",
        "Strict regional affinity requires the global TerraScale API URL or the selected regional URL.",
    ))
}
