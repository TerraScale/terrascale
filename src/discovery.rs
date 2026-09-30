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
    fn select(
        &self,
        requirement: NativeRequirement,
        preferred: Option<&str>,
        failed: &HashSet<String>,
    ) -> Result<ResolvedNativeOrigin> {
        let eligible = |endpoint: &&NativeEndpoint| {
            endpoint.capabilities.supports(requirement) && !failed.contains(&endpoint.region)
        };
        self.endpoints
            .iter()
            .filter(eligible)
            .find(|endpoint| Some(endpoint.region.as_str()) == preferred)
            .or_else(|| self.endpoints.iter().find(eligible))
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
            || endpoint.url
                != format!(
                    "https://{}.api.terrascale.tech",
                    endpoint.region.to_ascii_lowercase()
                )
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
pub(crate) fn valid_region(region: &str) -> bool {
    let bytes = region.as_bytes();
    !bytes.is_empty()
        && bytes.len() <= 16
        && bytes[0].is_ascii_alphanumeric()
        && bytes
            .iter()
            .all(|byte| byte.is_ascii_uppercase() || byte.is_ascii_digit() || *byte == b'-')
        && ![
            "API", "ATLAS", "AUTH", "SYNC", "STATUS", "WWW", "APP", "DASH", "DOCS", "GIT", "CDN",
            "ADMIN", "MCP", "MAIL",
        ]
        .contains(&region)
}
fn valid_token(token: &str) -> bool {
    let bytes = token.as_bytes();
    !bytes.is_empty()
        && bytes.len() <= 63
        && (bytes[0].is_ascii_lowercase() || bytes[0].is_ascii_digit())
        && bytes
            .iter()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || *byte == b'-')
}
fn valid_deployment(value: &str) -> bool {
    let bytes = value.as_bytes();
    !bytes.is_empty()
        && bytes.len() <= 128
        && bytes[0].is_ascii_alphanumeric()
        && bytes
            .iter()
            .all(|byte| byte.is_ascii_alphanumeric() || b"._:-".contains(byte))
}
fn validation(code: &str, message: &str) -> Error {
    Error::new(ErrorKind::Validation, code, message)
}
fn clock_error() -> Error {
    Error::new(
        ErrorKind::Unavailable,
        "discovery_clock_invalid",
        "Atlas discovery requires a current nonregressing wall and monotonic clock.",
    )
}

fn credential(key: Option<&str>) -> Result<&str> {
    let invalid = || {
        validation(
            "invalid_discovery_credential",
            "Atlas requires one canonical native TBK1 credential.",
        )
    };
    let key = key.ok_or_else(invalid)?;
    if key.len() + 7 > 128 {
        return Err(invalid());
    }
    let mut parts = key.split('.');
    if parts.next() != Some("tbk1") {
        return Err(invalid());
    }
    for size in [16, 32] {
        let encoded = parts.next().ok_or_else(invalid)?;
        let raw = URL_SAFE_NO_PAD.decode(encoded).map_err(|_| invalid())?;
        if raw.len() != size || URL_SAFE_NO_PAD.encode(raw) != encoded {
            return Err(invalid());
        }
    }
    if parts.next().is_some() {
        return Err(invalid());
    }
    Ok(key)
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct WireError {
    code: String,
    message: String,
    fix: String,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct WireErrorEnvelope {
    error: WireError,
}
fn discovery_error(response: &Response) -> Error {
    let parsed = serde_json::from_slice::<WireErrorEnvelope>(&response.body).ok();
    if let Some(envelope) = parsed {
        let code = &envelope.error.code;
        let allowed = match response.status {
            400 => ["invalid_request", "invalid_scope", "invalid_cursor"].contains(&code.as_str()),
            403 => code == "discovery_forbidden",
            503 => [
                "discovery_unconfigured",
                "no_eligible_region",
                "routing_observation_regressed",
            ]
            .contains(&code.as_str()),
            _ => false,
        };
        if allowed {
            let mut error =
                Error::for_status(response.status, envelope.error.code, envelope.error.message);
            error.fix = Some(envelope.error.fix.into_boxed_str());
            return error;
        }
    }
    let mut error = Error::invalid_response("Unexpected Atlas HTTP status/error envelope.");
    error.status = Some(response.status);
    error
}

pub(crate) type ScopeKey = (TenantId, DatabaseId, DecimalU64);
#[derive(Debug, Default)]
struct Entry {
    metadata: Option<DiscoveryMetadata>,
    lease_until: Option<Duration>,
    pinned: Option<String>,
    failed: HashSet<String>,
}
impl Entry {
    fn select(
        &mut self,
        requirement: NativeRequirement,
        preferred: Option<&str>,
    ) -> Result<ResolvedNativeOrigin> {
        let preferred = self
            .pinned
            .as_deref()
            .filter(|region| {
                self.metadata.as_ref().is_some_and(|metadata| {
                    metadata.endpoints.iter().any(|endpoint| {
                        endpoint.region == *region
                            && endpoint.capabilities.supports(requirement)
                            && !self.failed.contains(*region)
                    })
                })
            })
            .or(preferred);
        let resolved = self
            .metadata
            .as_ref()
            .ok_or_else(|| Error::invalid_response("Missing discovery metadata."))?
            .select(requirement, preferred, &self.failed)?;
        self.pinned = Some(resolved.endpoint.region.clone());
        Ok(resolved)
    }
}
#[derive(Debug, Default)]
struct ScopeEntry {
    state: Arc<AsyncMutex<Entry>>,
    resolvers: usize,
    observed: bool,
}
#[derive(Debug)]
struct Inner {
    config: ClientConfig,
    transport: Arc<dyn HttpTransport>,
    clock: Arc<dyn DiscoveryClock>,
    last_time: Mutex<Option<DiscoveryTime>>,
    scopes: Mutex<HashMap<ScopeKey, ScopeEntry>>,
}

/// Keeps an unobserved scope reserved while its refresher or any waiter lives.
struct ScopeReservation {
    owner: Arc<Inner>,
    key: ScopeKey,
    state: Arc<AsyncMutex<Entry>>,
}
impl ScopeReservation {
    fn retain_observation(&self) -> Result<()> {
        let mut scopes = self.owner.scopes.lock().map_err(|_| clock_error())?;
        let entry = scopes
            .get_mut(&self.key)
            .filter(|entry| Arc::ptr_eq(&entry.state, &self.state))
            .ok_or_else(clock_error)?;
        entry.observed = true;
        Ok(())
    }
}
impl Drop for ScopeReservation {
    fn drop(&mut self) {
        let Ok(mut scopes) = self.owner.scopes.lock() else {
            return;
        };
        let Some(entry) = scopes
            .get_mut(&self.key)
            .filter(|entry| Arc::ptr_eq(&entry.state, &self.state))
        else {
            return;
        };
        let Some(remaining) = entry.resolvers.checked_sub(1) else {
            return;
        };
        entry.resolvers = remaining;
        if remaining == 0 && !entry.observed {
            scopes.remove(&self.key);
        }
    }
}
/// Dedicated native discovery client. It does not use authentication discovery,
/// does not follow redirects, and retains observation floors through lease expiry.
/// Its bounded scope table fails closed when full; no eviction erases a floor.
#[derive(Debug, Clone)]
pub struct AtlasDiscovery(Arc<Inner>);
impl AtlasDiscovery {
    #[must_use]
    pub fn new(config: ClientConfig, transport: Arc<dyn HttpTransport>) -> Self {
        Self::with_clock(config, transport, Arc::new(SystemClock(Instant::now())))
    }
    #[must_use]
    pub fn with_clock(
        config: ClientConfig,
        transport: Arc<dyn HttpTransport>,
        clock: Arc<dyn DiscoveryClock>,
    ) -> Self {
        Self(Arc::new(Inner {
            config,
            transport,
            clock,
            last_time: Mutex::new(None),
            scopes: Mutex::new(HashMap::new()),
        }))
    }
    fn now(&self) -> Result<DiscoveryTime> {
        let mut previous = self.0.last_time.lock().map_err(|_| clock_error())?;
        let time = self.0.clock.now()?;
        if time.unix_millis == 0
            || time.unix_millis / 1000 > MAX_UNIX_SECONDS
            || previous.is_some_and(|last| {
                time.unix_millis < last.unix_millis || time.monotonic < last.monotonic
            })
        {
            return Err(clock_error());
        }
        *previous = Some(time);
        Ok(time)
    }
    pub async fn resolve(
        &self,
        scope: &DatabaseScope,
        requirement: NativeRequirement,
    ) -> Result<ResolvedNativeOrigin> {
        if self.0.config.discovery_base_url() != Some(ATLAS_BASE_URL) {
            return Err(validation(
                "invalid_native_discovery_origin",
                "Native credentials can only be sent to canonical Atlas discovery.",
            ));
        }
        if self
            .0
            .config
            .tenant_id()
            .is_some_and(|tenant| tenant != scope.tenant)
        {
            return Err(Error::new(
                ErrorKind::Authorization,
                "scope_mismatch",
                "Configured tenant does not match discovery scope.",
            ));
        }
        if scope.shard.0 == 0
            || scope.tenant.bytes() == &[0; 16]
            || scope.database.bytes() == &[0; 16]
        {
            return Err(validation(
                "invalid_discovery_scope",
                "Managed Atlas discovery needs nonnil UUIDs and shard 1..MAX.",
            ));
        }
        let key = credential(self.0.config.api_key())?;
        let rtt = rtt_header(self.0.config.region_rtt())?;
        let reservation = {
            let mut scopes = self.0.scopes.lock().map_err(|_| clock_error())?;
            let key = (scope.tenant, scope.database, scope.shard);
            if !scopes.contains_key(&key) && scopes.len() >= MAX_SCOPES {
                return Err(Error::new(
                    ErrorKind::Unavailable,
                    "discovery_scope_capacity",
                    "Discovery's retained scope floor table is full.",
                ));
            }
            let entry = scopes.entry(key).or_default();
            entry.resolvers = entry.resolvers.checked_add(1).ok_or_else(clock_error)?;
            ScopeReservation {
                owner: self.0.clone(),
                key,
                state: entry.state.clone(),
            }
        };
        // One refresh per scope, including atomic observation-floor publication.
        let mut state = reservation.state.lock().await;
        let start = self.now()?;
        if let (Some(metadata), Some(until)) = (&state.metadata, state.lease_until) {
            if start.monotonic < until && start.unix_millis / 1000 < metadata.expires_at {
                return state.select(requirement, self.0.config.preferred_region());
            }
        }
        let mut url = reqwest::Url::parse(&format!("{ATLAS_BASE_URL}/api/v1/discovery"))
            .map_err(|_| Error::invalid_response("Invalid canonical Atlas URL."))?;
        {
            let mut query = url.query_pairs_mut();
            query
                .append_pair("tenant_id", &scope.tenant.to_string())
                .append_pair("database_id", &scope.database.to_string())
                .append_pair("shard_id", &scope.shard.to_string());
            if let Some(metadata) = &state.metadata {
                query
                    .append_pair("observed_deployment_id", &metadata.authority.deployment_id)
                    .append_pair("observed_cell", &metadata.authority.cell)
                    .append_pair(
                        "observed_publisher_generation",
                        &metadata.authority.publisher_generation.to_string(),
                    )
                    .append_pair("rev", &metadata.revision.to_string());
            }
        }
        let mut headers = vec![
            ("authorization".into(), format!("Bearer {key}")),
            ("accept".into(), "application/json".into()),
        ];
        if let Some(rtt) = rtt {
            headers.push(("x-terrascale-region-rtt".into(), rtt));
        }
        let response = tokio::time::timeout(
            self.0.config.discovery_timeout(),
            self.0.transport.send(Request {
                route_name: "native.discovery".into(),
                method: Method::Get,
                url: url.into(),
                headers,
                body: None,
            }),
        )
        .await
        .map_err(|_| Error::transport("Atlas request deadline exceeded."))??;
        let end = self.now()?;
        let metadata = decode(scope, &response, end)?;
        if let Some(old) = &state.metadata {
            if metadata.authority.deployment_id != old.authority.deployment_id
                || metadata.authority.cell != old.authority.cell
                || metadata.authority.publisher_generation < old.authority.publisher_generation
                || metadata.revision < old.revision
                || (metadata.authority.publisher_generation == old.authority.publisher_generation
                    && metadata.revision == old.revision
                    && metadata.authority.publisher_id != old.authority.publisher_id)
            {
                return Err(Error::new(
                    ErrorKind::Unavailable,
                    "routing_observation_regressed",
                    "Atlas controlling identity or observed generation/revision regressed.",
                ));
            }
        }
        let until = start
            .monotonic
            .checked_add(Duration::from_secs(metadata.refresh_seconds))
            .ok_or_else(clock_error)?;
        // Retain a successfully validated observation even if transport consumed
        // its entire conservative lease. It cannot be selected as fresh.
        reservation.retain_observation()?;
        state.metadata = Some(metadata.clone());
        state.lease_until = Some(until);
        state.pinned = None;
        state.failed.clear();
        if end.monotonic >= until {
            return Err(Error::new(
                ErrorKind::Unavailable,
                "discovery_expired",
                "Atlas request consumed its conservative lease.",
            ));
        }
        state.select(requirement, self.0.config.preferred_region())
    }

    /// Demote a failed endpoint for the remaining document lifetime and re-pin.
    pub(crate) async fn failover(
        &self,
        scope: &DatabaseScope,
        origin: &str,
        requirement: NativeRequirement,
    ) -> bool {
        let entry = self.0.scopes.lock().ok().and_then(|scopes| {
            scopes
                .get(&(scope.tenant, scope.database, scope.shard))
                .map(|entry| entry.state.clone())
        });
        let Some(entry) = entry else {
            return false;
        };
        let mut state = entry.lock().await;
        let region = state.metadata.as_ref().and_then(|metadata| {
            metadata
                .endpoints
                .iter()
                .find(|endpoint| endpoint.url == origin)
                .map(|endpoint| endpoint.region.clone())
        });
        let Some(region) = region else {
            return false;
        };
        state.failed.insert(region);
        state.pinned = None;
        state
            .select(requirement, self.0.config.preferred_region())
            .is_ok()
    }
}
