//! Authenticated scoped Atlas discovery. Only receiver-ranked, eligible direct
//! native origins can be selected. TLS verifies the receiver; Atlas owns signed
//! publication verification. This response is not a signature envelope.

use std::{
    collections::{HashMap, HashSet},
    sync::{Arc, Mutex},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
use serde::Deserialize;
use tokio::sync::Mutex as AsyncMutex;

use crate::{
    config::ClientConfig,
    documents::{DatabaseScope, ProtocolVersion},
    error::{Error, ErrorKind, Result},
    regions::{regional_origin, valid_region},
    transport::{HttpTransport, Method, Request, Response},
    values::{DatabaseId, DecimalU64, Digest, TenantId},
};

pub const ATLAS_BASE_URL: &str = "https://atlas.terrascale.tech";
const MAX_SCOPES: usize = 128;
const MAX_REGIONS: usize = 20;
const MAX_DISCOVERY_BYTES: usize = 64 * 1024;
const MAX_UNIX_SECONDS: u64 = 253_402_300_799;

/// Native operation requirements. Status only needs write access, including
/// reconciliation of a conditional/grouped operation after a capability change.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NativeRequirement {
    Point,
    Query,
    Subscription,
    TransactionStatus,
    Commit { conditional: bool, atomic: bool },
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NativeCapabilities {
    pub client_v1: bool,
    pub read: bool,
    pub query: bool,
    pub subscriptions: bool,
    pub write: bool,
    pub conditional_mutation: bool,
    pub atomic_groups: bool,
    pub security_state: bool,
}
impl NativeCapabilities {
    fn supports(&self, requirement: NativeRequirement) -> bool {
        self.client_v1
            && match requirement {
                NativeRequirement::Point => self.read,
                NativeRequirement::Query => self.read && self.query,
                NativeRequirement::Subscription => self.read && self.subscriptions,
                NativeRequirement::TransactionStatus => self.write,
                NativeRequirement::Commit {
                    conditional,
                    atomic,
                } => {
                    self.write
                        && (!conditional || self.conditional_mutation)
                        && (!atomic || self.atomic_groups)
                }
            }
    }
}

/// Optional measured RTT hint. It affects receiver ranking, never eligibility.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RegionRtt {
    pub region: String,
    pub milliseconds: u32,
}
impl RegionRtt {
    pub fn new(region: impl Into<String>, milliseconds: u32) -> Result<Self> {
        let hint = Self {
            region: region.into(),
            milliseconds,
        };
        if !valid_region(&hint.region) || !(1..=60_000).contains(&milliseconds) {
            return Err(validation(
                "invalid_region_rtt",
                "RTT needs an admitted region code and 1..60000 ms.",
            ));
        }
        Ok(hint)
    }
}

pub(crate) fn rtt_header(hints: &[RegionRtt]) -> Result<Option<String>> {
    if hints.len() > MAX_REGIONS {
        return Err(validation(
            "invalid_region_rtt",
            "At most 20 distinct RTT hints are allowed.",
        ));
    }
    let mut regions = HashSet::new();
    let mut pairs = Vec::with_capacity(hints.len());
    for hint in hints {
        RegionRtt::new(&hint.region, hint.milliseconds)?;
        if !regions.insert(&hint.region) {
            return Err(validation(
                "invalid_region_rtt",
                "RTT regions must be unique.",
            ));
        }
        pairs.push(format!("{}={}", hint.region, hint.milliseconds));
    }
    Ok((!pairs.is_empty()).then(|| pairs.join(",")))
}

/// Checked wall time and a monotonic duration from one fixed clock origin.
/// Implementations must provide measured healthy clock bounds/provenance for
/// runtime qualification. Merely injecting a clock is not such evidence.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DiscoveryTime {
    pub unix_millis: u64,
    pub monotonic: Duration,
}
pub trait DiscoveryClock: Send + Sync + std::fmt::Debug {
    fn now(&self) -> Result<DiscoveryTime>;
}
#[derive(Debug)]
struct SystemClock(Instant);
impl DiscoveryClock for SystemClock {
    fn now(&self) -> Result<DiscoveryTime> {
        let wall = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|_| clock_error())?;
        Ok(DiscoveryTime {
            unix_millis: u64::try_from(wall.as_millis()).map_err(|_| clock_error())?,
            monotonic: self.0.elapsed(),
        })
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct DiscoveryAuthority {
    pub deployment_id: String,
    pub cell: String,
    pub publisher_id: String,
    pub publisher_generation: DecimalU64,
}
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NativeEndpoint {
    pub region: String,
    pub url: String,
    pub catalog_digest: Digest,
    pub capabilities: NativeCapabilities,
    pub priority: usize,
}
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DiscoveryMetadata {
    pub scope: DatabaseScope,
    pub authority: DiscoveryAuthority,
    pub revision: DecimalU64,
    pub published_at: u64,
    pub expires_at: u64,
    /// Lease clamped down at reception, never increased from receiver TTL.
    pub refresh_seconds: u64,
    pub residency: Option<Vec<String>>,
    pub endpoints: Vec<NativeEndpoint>,
}
#[derive(Debug, Clone)]
pub struct ResolvedNativeOrigin {
    pub endpoint: NativeEndpoint,
    pub metadata: DiscoveryMetadata,
}
impl DiscoveryMetadata {
    fn select(&self, requirement: NativeRequirement) -> Result<ResolvedNativeOrigin> {
        self.endpoints
            .iter()
            .find(|endpoint| endpoint.capabilities.supports(requirement))
            .map(|endpoint| ResolvedNativeOrigin {
                endpoint: endpoint.clone(),
                metadata: self.clone(),
            })
            .ok_or_else(|| {
                Error::new(
                    ErrorKind::Unavailable,
                    "native_capability_unavailable",
                    "No currently admitted endpoint supports this native operation.",
                )
            })
    }
}

// A deserialize_with field is required even when its value may be null.
fn required_option<'de, D, T>(deserializer: D) -> std::result::Result<Option<T>, D::Error>
where
    D: serde::Deserializer<'de>,
    T: Deserialize<'de>,
{
    Option::<T>::deserialize(deserializer)
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct WireScope {
    tenant_id: TenantId,
    database_id: DatabaseId,
    shard_id: DecimalU64,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct WireCatalog {
    digest: Digest,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct WireEndpoint {
    region: String,
    url: String,
    catalog: WireCatalog,
    capabilities: NativeCapabilities,
    #[serde(deserialize_with = "required_option")]
    security_writer: Option<serde::de::IgnoredAny>,
    priority: usize,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct WireDiscovery {
    schema_version: ProtocolVersion,
    scope: WireScope,
    authority: DiscoveryAuthority,
    revision: DecimalU64,
    published_at: u64,
    expires_at: u64,
    #[serde(rename = "refreshSeconds")]
    refresh_seconds: u64,
    #[serde(deserialize_with = "required_option")]
    residency: Option<Vec<String>>,
    endpoints: Vec<WireEndpoint>,
}

fn decode(
    scope: &DatabaseScope,
    response: &Response,
    now: DiscoveryTime,
) -> Result<DiscoveryMetadata> {
    if response.body.len() > MAX_DISCOVERY_BYTES {
        return Err(Error::invalid_response("Atlas response exceeds 64 KiB."));
    }
    validate_json_media(response)?;
    if response.status != 200 {
        return Err(discovery_error(response));
    }
    let wire: WireDiscovery = serde_json::from_slice(&response.body).map_err(|_| {
        Error::invalid_response("Atlas response does not match its closed native contract.")
    })?;
    let _ = wire.schema_version;
    if wire.scope.tenant_id != scope.tenant
        || wire.scope.database_id != scope.database
        || wire.scope.shard_id != scope.shard
    {
        return Err(Error::invalid_response(
            "Atlas returned a different native scope.",
        ));
    }
    let authority = &wire.authority;
    if !valid_deployment(&authority.deployment_id)
        || !valid_token(&authority.cell)
        || !valid_token(&authority.publisher_id)
        || authority.publisher_generation.0 == 0
        || wire.revision.0 == 0
    {
        return Err(Error::invalid_response(
            "Atlas authority/revision is invalid.",
        ));
    }
    let now_seconds = now.unix_millis / 1000;
    if wire.published_at == 0
        || wire.published_at > MAX_UNIX_SECONDS
        || wire.expires_at > MAX_UNIX_SECONDS
        || wire.published_at > now_seconds
        || wire.expires_at <= now_seconds
        || wire.expires_at <= wire.published_at
        || wire.expires_at.saturating_sub(wire.published_at) > 10
        || !(1..=10).contains(&wire.refresh_seconds)
    {
        return Err(Error::invalid_response(
            "Atlas publication time/expiry/TTL is invalid.",
        ));
    }
    let expires_millis = wire
        .expires_at
        .checked_mul(1000)
        .ok_or_else(|| Error::invalid_response("Atlas expiry overflow."))?;
    let remaining_seconds = expires_millis.saturating_sub(now.unix_millis) / 1000;
    let refresh_seconds = wire.refresh_seconds.min(remaining_seconds);
    if refresh_seconds == 0 {
        return Err(Error::new(
            ErrorKind::Unavailable,
            "discovery_expired",
            "Atlas has no whole second of current validity remaining.",
        ));
    }
    let residency = wire.residency;
    if let Some(regions) = &residency {
        validate_region_set(regions)?;
    }
    if wire.endpoints.is_empty() || wire.endpoints.len() > MAX_REGIONS {
        return Err(Error::invalid_response(
            "Atlas needs 1..20 eligible endpoints.",
        ));
    }
    let mut regions = HashSet::new();
    let mut endpoints = Vec::with_capacity(wire.endpoints.len());
    for (index, endpoint) in wire.endpoints.into_iter().enumerate() {
        if !valid_region(&endpoint.region)
            || !regions.insert(endpoint.region.clone())
            || endpoint.url != regional_origin(&endpoint.region)
            || endpoint.priority != index + 1
            || endpoint.security_writer.is_some()
            || !endpoint.capabilities.client_v1
            || !(endpoint.capabilities.read || endpoint.capabilities.write)
            || endpoint.capabilities.conditional_mutation
            || endpoint.capabilities.atomic_groups
            || endpoint.capabilities.security_state
            || residency
                .as_ref()
                .is_some_and(|allowed| !allowed.contains(&endpoint.region))
        {
            return Err(Error::invalid_response(
                "Atlas endpoint origin/rank/residency/capabilities is invalid.",
            ));
        }
        endpoints.push(NativeEndpoint {
            region: endpoint.region,
            url: endpoint.url,
            catalog_digest: endpoint.catalog.digest,
            capabilities: endpoint.capabilities,
            priority: endpoint.priority,
        });
    }
    Ok(DiscoveryMetadata {
        scope: *scope,
        authority: wire.authority,
        revision: wire.revision,
        published_at: wire.published_at,
        expires_at: wire.expires_at,
        refresh_seconds,
        residency,
        endpoints,
    })
}

fn validate_json_media(response: &Response) -> Result<()> {
    let mut headers = response
        .headers
        .iter()
        .filter(|(name, _)| name.eq_ignore_ascii_case("content-type"));
    let valid = headers.next().is_some_and(|(_, value)| {
        let mut parts = value.split(';');
        let media = parts
            .next()
            .is_some_and(|media| media.trim().eq_ignore_ascii_case("application/json"));
        let charset = parts.next().is_none_or(|parameter| {
            parameter.split_once('=').is_some_and(|(name, value)| {
                name.trim().eq_ignore_ascii_case("charset")
                    && (value.trim().eq_ignore_ascii_case("utf-8")
                        || value.trim().eq_ignore_ascii_case("\"utf-8\""))
            })
        });
        media && charset && parts.next().is_none()
    }) && headers.next().is_none();
    if !valid {
        let mut error =
            Error::invalid_response("Atlas requires one JSON Content-Type response header.");
        error.status = Some(response.status);
        return Err(error);
    }
    Ok(())
}

fn validate_region_set(regions: &[String]) -> Result<()> {
    let mut seen = HashSet::new();
    if regions.is_empty()
        || regions.len() > MAX_REGIONS
        || regions
            .iter()
            .any(|region| !valid_region(region) || !seen.insert(region))
    {
        return Err(Error::invalid_response(
            "Atlas residency must be 1..20 unique region codes.",
        ));
    }
    Ok(())
}
