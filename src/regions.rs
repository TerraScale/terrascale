//! Region codes, affinity, and URL pinning.
//!
//! The SDK pins REST (and optional WebSocket) traffic to a healthy hosting
//! region (`<region>.api.terrascale.tech`) and keeps reactive HTTP on the
//! global `sync.api.` entry point. A `421` with `X-TerraScale-Placement`
//! repairs a stale pin.

use std::fmt;
use std::net::IpAddr;
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

/// Default reactive delivery origin.
pub const GLOBAL_SYNC_BASE_URL: &str = "https://sync.api.terrascale.tech";

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

/// Normalize and validate a base URL (no trailing slash).
///
/// API traffic carries bearer credentials, so non-loopback endpoints must use
/// HTTPS. Plain HTTP is intentionally limited to local development and the
/// reserved test domains. URL userinfo, query strings, and fragments are
/// rejected so credentials and routing cannot be smuggled through a base URL.
pub fn normalize_base_url(base_url: &str) -> Result<String> {
    let trimmed = base_url.trim().trim_end_matches('/').to_owned();
    if trimmed.is_empty() {
        return Err(Error::new(
            crate::error::ErrorKind::Validation,
            "invalid_base_url",
            "API base URL cannot be empty.",
        ));
    }
    let url = reqwest::Url::parse(&trimmed).map_err(|error| {
        Error::new(
            crate::error::ErrorKind::Validation,
            "invalid_base_url",
            format!("API base URL must be an absolute HTTP(S) URL: {error}."),
        )
    })?;
    if !matches!(url.scheme(), "http" | "https") || url.host_str().is_none() {
        return Err(Error::new(
            crate::error::ErrorKind::Validation,
            "invalid_base_url",
            "API base URL must be an absolute HTTP(S) URL.",
        ));
    }
    if !url.username().is_empty() || url.password().is_some() {
        return Err(Error::new(
            crate::error::ErrorKind::Validation,
            "unsafe_base_url",
            "API base URL cannot contain userinfo.",
        ));
    }
    if url.query().is_some() || url.fragment().is_some() {
        return Err(Error::new(
            crate::error::ErrorKind::Validation,
            "unsafe_base_url",
            "API base URL cannot contain a query or fragment.",
        ));
    }
    let host = url.host_str().unwrap_or_default().to_ascii_lowercase();
    if has_private_component(&host) || has_private_component(url.path()) {
        return Err(Error::new(
            crate::error::ErrorKind::Validation,
            "unsafe_base_url",
            "TerraScale SDK clients may target only the public API, not TBEP or TerraBase.",
        ));
    }
    if url.scheme() == "http" {
        let host_without_brackets = host.trim_start_matches('[').trim_end_matches(']');
        let loopback = host_without_brackets.eq_ignore_ascii_case("localhost")
            || host_without_brackets
                .parse::<IpAddr>()
                .is_ok_and(|address| address.is_loopback());
        let test_host = host_without_brackets.eq_ignore_ascii_case("test")
            || host_without_brackets.ends_with(".test");
        if !loopback && !test_host {
            return Err(Error::new(
                crate::error::ErrorKind::Validation,
                "insecure_base_url",
                "API endpoints must use HTTPS, except explicit loopback/test HTTP endpoints.",
            ));
        }
    }
    Ok(trimmed)
}

/// Return whether a host/path contains a complete private-protocol component.
///
/// Matching complete components keeps legitimate custom origins such as
/// `https://notterrabase.example` usable while still rejecting obvious
/// TBEP/TerraBase endpoints (including hyphenated and path-separated forms).
fn has_private_component(value: &str) -> bool {
    value
        .split(|character: char| !character.is_ascii_alphanumeric())
        .any(|component| matches!(component, "tbep" | "terrabase"))
}

fn apply_region_pin(base: &str, region: RegionCode) -> Result<String> {
    let mut url = reqwest::Url::parse(base).map_err(|error| {
        Error::new(
            crate::error::ErrorKind::Validation,
            "invalid_base_url",
            format!("API base URL must be an absolute HTTP(S) URL: {error}."),
        )
    })?;
    let host = url.host_str().unwrap_or_default().to_ascii_lowercase();
    if host == "api.terrascale.tech" {
        url.set_host(Some(&regional_host(region))).map_err(|_| {
            Error::new(
                crate::error::ErrorKind::Validation,
                "invalid_base_url",
                "Unable to construct the regional TerraScale API URL.",
            )
        })?;
        return Ok(url.to_string().trim_end_matches('/').to_owned());
    }
    if let Some(selected) = [RegionCode::Gru, RegionCode::Sfo, RegionCode::Fra]
        .into_iter()
        .find(|candidate| host == regional_host(*candidate))
    {
        if selected == region {
            return Ok(base.to_owned());
        }
        return Err(Error::new(
            crate::error::ErrorKind::Validation,
            "region_pin_mismatch",
            "The API base URL is pinned to a different TerraScale region.",
        ));
    }
    // Custom origins (proxies and local test servers) remain unchanged; the
    // selected region is carried by X-TerraScale-Preferred-Region.
    Ok(base.to_owned())
}
