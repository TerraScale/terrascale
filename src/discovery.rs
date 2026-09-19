//! SDK-driven routing discovery and region-catalog fallback (ADR 0050).
//!
//! Discovery is the first contact for database-scoped traffic: one metadata
//! GET per routing scope against `atlas.terrascale.tech`, then direct
//! regional connections. The per-request proxy is retired (ADR 0050 §8): a
//! discovery failure degrades to region-catalog selection, never to a proxy.
//!
//! Discovery is active only for failover-affinity clients targeting the
//! public global origin (or an explicit [`crate::ClientConfigBuilder::atlas_base_url`]
//! override). Strict affinity and custom origins keep their direct ADR 0043
//! behavior with no discovery.

use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use serde_json::Value;

use crate::error::{Error, ErrorKind, Result};
use crate::management::{RegionCatalogResponse, RegionDetails};
use crate::regions::{RegionCode, GLOBAL_API_HOST};
use crate::transport::{HttpTransport, Method, Request};

/// Default discovery/catalog origin (ADR 0050 §8): `ts-atlas` serves
/// `/api/v1/discovery` and `/api/v1/regions` and forwards nothing.
pub const ATLAS_BASE_URL: &str = "https://atlas.terrascale.tech";

/// The only branch dimension the wire contract currently carries.
pub const DEFAULT_ROUTING_BRANCH: &str = "main";

/// Bounded discovery/catalog request timeout (ADR 0050 §5.8).
const REQUEST_TIMEOUT: Duration = Duration::from_secs(3);

/// A scope whose discovery just failed waits this long (ms) before retrying.
const FAILURE_BACKOFF_MS: u64 = 1_000;

/// Region-catalog cache lifetime in milliseconds (ADR 0050 §5.8).
const CATALOG_CACHE_MS: u64 = 300_000;

/// EWMA halflife for measured client-to-region round trips (ADR 0050 §2).
const RTT_HALFLIFE_MS: f64 = 60_000.0;

/// Accepted measured-round-trip bounds in milliseconds (ADR 0050 §2).
const RTT_MIN_MS: f64 = 1.0;
const RTT_MAX_MS: f64 = 60_000.0;

/// Maximum `REGION=ms` pairs in one `X-TerraScale-Region-Rtt` header.
const MAX_RTT_HEADER_ENTRIES: usize = 20;

/// Refresh bounds applied to every accepted document (ADR 0050 §5.2).
pub const MIN_REFRESH_SECONDS: u64 = 60;
pub const MAX_REFRESH_SECONDS: u64 = 3_600;

/// Maximum endpoints in one document or catalog.
const MAX_ENDPOINTS: usize = 20;

/// What one regional endpoint currently accepts (ADR 0050 §2).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum RoutingCapability {
    Read,
    Write,
    Sql,
    Reactive,
}

impl RoutingCapability {
    fn parse(value: &str) -> Option<Self> {
        match value {
            "read" => Some(Self::Read),
            "write" => Some(Self::Write),
            "sql" => Some(Self::Sql),
            "reactive" => Some(Self::Reactive),
            _ => None,
        }
    }
}

/// One ranked regional endpoint of a routing document.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RoutingEndpoint {
    /// Uppercase region code as published by the catalog.
    pub region: String,
    /// Validated absolute origin, e.g. `https://fra.api.terrascale.tech`.
    pub url: String,
    /// Rank within the document (1 = first choice).
    pub priority: u32,
    /// Operations this endpoint currently accepts.
    pub capabilities: Vec<RoutingCapability>,
}

impl RoutingEndpoint {
    /// Whether the endpoint covers one capability.
    #[must_use]
    pub fn supports(&self, capability: RoutingCapability) -> bool {
        self.capabilities.contains(&capability)
    }
}

/// Residency constraint of a routing document (ADR 0043 §1 semantics).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Residency {
    /// Policy label such as `eu-only`.
    Label(String),
    /// Explicit region list.
    Regions(Vec<String>),
}

/// Whether discovery knows the database's placement (ADR 0050 §2).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Placement {
    Known,
    Unknown,
}

/// One validated routing document (ADR 0050 §2).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RoutingDocument {
    pub database: String,
    pub branch: String,
    pub revision: u64,
    pub residency: Option<Residency>,
    pub placement: Placement,
    /// Clamped to 60-3600 seconds.
    pub refresh_seconds: u64,
    /// Ranked best-first; the only alternatives the SDK may fail over to.
    pub endpoints: Vec<RoutingEndpoint>,
}

/// Uppercase region-code shape accepted from untrusted metadata.
#[must_use]
pub fn is_region_code(code: &str) -> bool {
    let bytes = code.as_bytes();
    if bytes.is_empty() || bytes.len() > 16 {
        return false;
    }
    let first = bytes[0];
    if !first.is_ascii_uppercase() && !first.is_ascii_digit() {
        return false;
    }
    bytes[1..]
        .iter()
        .all(|byte| byte.is_ascii_uppercase() || byte.is_ascii_digit() || *byte == b'-')
}

/// Producer database-name format accepted by discovery (ADR 0050 §2).
#[must_use]
pub fn is_database_name(name: &str) -> bool {
    let bytes = name.as_bytes();
    if bytes.is_empty() || bytes.len() > 63 {
        return false;
    }
    let alnum = |byte: u8| byte.is_ascii_lowercase() || byte.is_ascii_digit();
    if !alnum(bytes[0]) || !alnum(bytes[bytes.len() - 1]) {
        return false;
    }
    bytes.len() == 1
        || bytes[1..bytes.len() - 1]
            .iter()
            .all(|byte| alnum(*byte) || *byte == b'-')
}

/// Loopback and `*.localhost` dev origins: `terrascale dev` serves its pinned
/// regional members there.
#[must_use]
pub fn is_dev_host(host: &str) -> bool {
    let host = host.trim_start_matches('[').trim_end_matches(']');
    host == "localhost"
        || host.ends_with(".localhost")
        || host
            .parse::<std::net::IpAddr>()
            .is_ok_and(|address| address.is_loopback())
}

/// Validate a discovered endpoint URL at the trust boundary. A hostile or
/// sloppy document must never steer traffic outside the regional API family
/// (or dev origins when the client itself runs against loopback).
#[must_use]
pub fn validate_endpoint_url(raw: &str, region: &str, loopback_client: bool) -> Option<String> {
    if raw.is_empty() || raw.len() > 2_048 {
        return None;
    }
    let url = reqwest::Url::parse(raw).ok()?;
    if !url.username().is_empty() || url.password().is_some() {
        return None;
    }
    if url.query().is_some() || url.fragment().is_some() {
        return None;
    }
    if url.path() != "/" && !url.path().is_empty() {
        return None;
    }
    let host = url.host_str()?.to_ascii_lowercase();
    let expected = format!("{}.{}", region.to_ascii_lowercase(), GLOBAL_API_HOST);
    if url.scheme() == "https" && host == expected {
        return Some(format!("https://{expected}"));
    }
    if loopback_client && url.scheme() == "http" && is_dev_host(&host) {
        return Some(format!(
            "http://{}{}",
            host,
            url.port()
                .map_or_else(String::new, |port| format!(":{port}"))
        ));
    }
    None
}

/// Strictly validate a discovery response. Any structural doubt rejects the
/// document; the caller degrades to catalog selection and routing never
/// widens on malformed input.
#[must_use]
pub fn parse_routing_document(
    payload: &Value,
    database: &str,
    branch: &str,
    loopback_client: bool,
) -> Option<RoutingDocument> {
    let doc = payload.as_object()?;
    if doc.get("v").and_then(Value::as_u64) != Some(1) {
        return None;
    }
    if doc.get("database").and_then(Value::as_str) != Some(database)
        || doc.get("branch").and_then(Value::as_str) != Some(branch)
    {
        return None;
    }
    let residency = match doc.get("residency") {
        None | Some(Value::Null) => None,
        Some(Value::String(label)) => {
            if label.is_empty() || label.len() > 256 {
                return None;
            }
            Some(Residency::Label(label.clone()))
        }
        Some(Value::Array(regions)) => {
            if regions.is_empty() || regions.len() > MAX_ENDPOINTS {
                return None;
            }
            let mut seen = BTreeSet::new();
            let mut codes = Vec::with_capacity(regions.len());
            for region in regions {
                let code = region.as_str()?;
                if !is_region_code(code) || !seen.insert(code.to_owned()) {
                    return None;
                }
                codes.push(code.to_owned());
            }
            Some(Residency::Regions(codes))
        }
        Some(_) => return None,
    };
    let placement = match doc.get("placement").and_then(Value::as_str) {
        Some("known") => Placement::Known,
        Some("unknown") => Placement::Unknown,
        _ => return None,
    };
    let revision = doc.get("revision").and_then(Value::as_u64)?;
    let refresh_seconds = doc.get("refreshSeconds").and_then(Value::as_u64)?;
    if !(1..=86_400).contains(&refresh_seconds) {
        return None;
    }
    let raw_endpoints = doc.get("endpoints")?.as_array()?;
    if raw_endpoints.is_empty() || raw_endpoints.len() > MAX_ENDPOINTS {
        return None;
    }
    let mut endpoints = Vec::with_capacity(raw_endpoints.len());
    let mut seen_regions = BTreeSet::new();
    let mut has_first = false;
    for raw in raw_endpoints {
        let entry = raw.as_object()?;
        let region = entry.get("region").and_then(Value::as_str)?;
        if !is_region_code(region) || !seen_regions.insert(region.to_owned()) {
            return None;
        }
        let priority = entry.get("priority").and_then(Value::as_u64)?;
        if !(1..=MAX_ENDPOINTS as u64).contains(&priority) {
            return None;
        }
        has_first |= priority == 1;
        let url = validate_endpoint_url(
            entry.get("url").and_then(Value::as_str)?,
            region,
            loopback_client,
        )?;
        let raw_capabilities = entry.get("capabilities")?.as_array()?;
        if raw_capabilities.is_empty() {
            return None;
        }
        let mut capabilities = Vec::with_capacity(raw_capabilities.len());
        for capability in raw_capabilities {
            let capability = RoutingCapability::parse(capability.as_str()?)?;
            if !capabilities.contains(&capability) {
                capabilities.push(capability);
            }
        }
        endpoints.push(RoutingEndpoint {
            region: region.to_owned(),
            url,
            priority: priority as u32,
            capabilities,
        });
    }
    if !has_first {
        return None;
    }
    Some(RoutingDocument {
        database: database.to_owned(),
        branch: branch.to_owned(),
        revision,
        residency,
        placement,
        refresh_seconds: refresh_seconds.clamp(MIN_REFRESH_SECONDS, MAX_REFRESH_SECONDS),
        endpoints,
    })
}

/// Strictly validate a region-catalog response. Hosts obey the same
/// endpoint-URL trust rules as routing documents.
#[must_use]
pub fn parse_region_catalog(
    payload: &Value,
    loopback_client: bool,
) -> Option<RegionCatalogResponse> {
    let doc = payload.as_object()?;
    let raw_regions = doc.get("regions")?.as_array()?;
    if raw_regions.is_empty() || raw_regions.len() > MAX_ENDPOINTS {
        return None;
    }
    let mut regions = Vec::with_capacity(raw_regions.len());
    let mut codes = BTreeSet::new();
    for raw in raw_regions {
        let entry = raw.as_object()?;
        let code = entry.get("code").and_then(Value::as_str)?;
        if !is_region_code(code) || !codes.insert(code.to_owned()) {
            return None;
        }
        let name = entry.get("name").and_then(Value::as_str)?;
        let country = entry.get("country").and_then(Value::as_str)?;
        if name.is_empty() || name.len() > 128 || country.is_empty() || country.len() > 96 {
            return None;
        }
        let latitude = entry.get("latitude").and_then(Value::as_f64)?;
        let longitude = entry.get("longitude").and_then(Value::as_f64)?;
        if !latitude.is_finite()
            || !longitude.is_finite()
            || latitude.abs() > 90.0
            || longitude.abs() > 180.0
        {
            return None;
        }
        regions.push(RegionDetails {
            code: code.to_owned(),
            name: name.to_owned(),
            country: country.to_owned(),
            latitude,
            longitude,
            available: entry.get("available").and_then(Value::as_bool)?,
        });
    }
    let raw_hosts = doc.get("hosts")?.as_object()?;
    let mut hosts = BTreeMap::new();
    for (code, raw) in raw_hosts {
        let url = validate_endpoint_url(raw.as_str()?, code, loopback_client)?;
        hosts.insert(code.clone(), url);
    }
    if regions
        .iter()
        .any(|region| !hosts.contains_key(&region.code))
    {
        return None;
    }
    Some(RegionCatalogResponse { regions, hosts })
}

/// One measured region round trip folded into an EWMA.
struct RttSample {
    value_ms: f64,
    at_ms: u64,
}

/// Per-region measured client-to-region round trips (ADR 0050 §2): a
/// timestamp-decayed EWMA with a 60 s halflife, so one slow outlier cannot
/// permanently demote a healthy region. Samples are bounded; an
/// out-of-order timestamp resets the estimate instead of blending backwards.
#[derive(Default)]
struct RegionRttTracker {
    samples: Mutex<HashMap<String, RttSample>>,
}

impl RegionRttTracker {
    /// Fold one bounded observation in at `now_ms`.
    fn observe(&self, region: &str, rtt_ms: f64, now_ms: u64) {
        if !is_region_code(region) || !rtt_ms.is_finite() || !(0.0..=RTT_MAX_MS).contains(&rtt_ms) {
            return;
        }
        let mut samples = self.samples.lock().expect("rtt lock");
        let value_ms = match samples.get(region) {
            None => rtt_ms,
            Some(previous) if previous.at_ms > now_ms => rtt_ms,
            Some(previous) => {
                let decay = 2.0f64.powf(-((now_ms - previous.at_ms) as f64) / RTT_HALFLIFE_MS);
                previous.value_ms * decay + rtt_ms * (1.0 - decay)
            }
        };
        samples.insert(
            region.to_owned(),
            RttSample {
                value_ms,
                at_ms: now_ms,
            },
        );
    }

    /// Current estimates as the `X-TerraScale-Region-Rtt` value: `REGION=ms`
    /// pairs, bounded 1..=60000 ms after rounding, at most 20 entries,
    /// deterministically ordered.
    fn header(&self) -> Option<String> {
        let samples = self.samples.lock().expect("rtt lock");
        let mut pairs: Vec<String> = samples
            .iter()
            .filter(|(_, sample)| (RTT_MIN_MS..=RTT_MAX_MS).contains(&sample.value_ms))
            .map(|(region, sample)| format!("{region}={:.0}", sample.value_ms.round()))
            .collect();
        pairs.sort();
        pairs.truncate(MAX_RTT_HEADER_ENTRIES);
        if pairs.is_empty() {
            None
        } else {
            Some(pairs.join(","))
        }
    }
}

/// Wall-clock milliseconds for routing state; matches the reference SDK's
/// `Date.now()` semantics.
fn unix_now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |since| since.as_millis() as u64)
}

/// How one `/api/v1` path routes when discovery is active (ADR 0050 §1/§8).
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum RouteKind {
    /// `/api/v1/databases/{name}…`: discovery resolves the regional endpoint.
    Database(String),
    /// `/api/v1/regions` exactly: the catalog itself, served by the atlas origin.
    Catalog,
    /// Database-less member route: catalog-selected regional endpoint.
    MemberGlobal,
    /// Dashboard-owned product route or non-API path: keeps its origin.
    Unrouted,
}

/// Classify one request path. `path` is the portion after the API base origin
/// and starts with `/`.
pub(crate) fn classify_path(path: &str) -> RouteKind {
    let mut segments = path.trim_start_matches('/').split('/');
    if segments.next() != Some("api") || segments.next() != Some("v1") {
        return RouteKind::Unrouted;
    }
    match segments.next() {
        None => RouteKind::Unrouted,
        Some("databases") => match segments.next() {
            // `/api/v1/databases` itself (create/list) is member-global.
            Some(name) if is_database_name(name) => RouteKind::Database(name.to_owned()),
            _ => RouteKind::MemberGlobal,
        },
        // `/api/v1/regions` exactly is the catalog itself; `regions/{code}`
        // is a dashboard-owned product route.
        Some("regions") => match segments.next() {
            None => RouteKind::Catalog,
            Some(_) => RouteKind::Unrouted,
        },
        // Product routes (ADR 0050 §8): organizations and projects target the
        // configured product origin.
        Some("organizations" | "projects") => RouteKind::Unrouted,
        _ => RouteKind::MemberGlobal,
    }
}

/// A single-flight result shared between the initiator and joiners.
struct Flight<T: Clone> {
    result: tokio::sync::watch::Sender<Option<T>>,
    receiver: tokio::sync::watch::Receiver<Option<T>>,
}

impl<T: Clone> Flight<T> {
    fn new() -> Arc<Self> {
        let (result, receiver) = tokio::sync::watch::channel(None);
        Arc::new(Self { result, receiver })
    }

    async fn wait(self: Arc<Self>) -> Option<T> {
        let mut receiver = self.receiver.clone();
        receiver
            .wait_for(Option::is_some)
            .await
            .ok()
            .and_then(|value| value.clone())
    }
}

type DocumentFlight = Flight<std::result::Result<Arc<RoutingDocument>, Error>>;
type CatalogFlight = Flight<std::result::Result<Arc<RegionCatalogResponse>, Error>>;

/// Per-scope cached routing state (ADR 0050 §5.2/§5.5). All timestamps are
/// milliseconds from the router clock.
#[derive(Default)]
struct ScopeState {
    /// Cached document plus its expiry. An expired entry is kept as the
    /// scope's last-known document — its revision rides the next discovery
    /// fetch as `rev` — until a fresher one replaces it.
    document: Option<(Arc<RoutingDocument>, u64)>,
    /// Sticky selected region; moves only on failure, 421, or eligibility exit.
    pinned: Option<String>,
    /// Regions pushed behind the alternatives by routing failures.
    demoted: BTreeSet<String>,
    /// The next discovery request must be corrective (`stale=1`).
    stale: bool,
    last_failure: Option<u64>,
}

/// The discovery router owned by one client pipeline. All state is interior
/// mutable; no lock is held across an await.
pub(crate) struct Router {
    atlas_origin: String,
    /// Host of the configured product origin; gates placement synthesis.
    api_host: String,
    loopback_client: bool,
    project_id: Option<String>,
    tenant_id: Option<String>,
    preferred_region: Option<RegionCode>,
    /// Milliseconds since the Unix epoch; injectable for deterministic tests.
    clock: Box<dyn Fn() -> u64 + Send + Sync>,
    rtt: RegionRttTracker,
    scopes: Mutex<HashMap<String, ScopeState>>,
    discovery_flights: Mutex<HashMap<String, Arc<DocumentFlight>>>,
    catalog: Mutex<Option<(Arc<RegionCatalogResponse>, u64)>>,
    catalog_flight: Mutex<Option<Arc<CatalogFlight>>>,
}

impl std::fmt::Debug for Router {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Router")
            .field("atlas_origin", &self.atlas_origin)
            .finish_non_exhaustive()
    }
}

impl Router {
    pub(crate) fn new(
        atlas_origin: String,
        api_base_url: &str,
        project_id: Option<String>,
        tenant_id: Option<String>,
        preferred_region: Option<RegionCode>,
    ) -> Self {
        Self::build(
            atlas_origin,
            api_base_url,
            project_id,
            tenant_id,
            preferred_region,
            Box::new(unix_now_ms),
        )
    }

    /// Deterministic-clock construction for tests.
    #[cfg(test)]
    fn with_clock(
        atlas_origin: String,
        api_base_url: &str,
        project_id: Option<String>,
        tenant_id: Option<String>,
        preferred_region: Option<RegionCode>,
        clock: Box<dyn Fn() -> u64 + Send + Sync>,
    ) -> Self {
        Self::build(
            atlas_origin,
            api_base_url,
            project_id,
            tenant_id,
            preferred_region,
            clock,
        )
    }

    fn build(
        atlas_origin: String,
        api_base_url: &str,
        project_id: Option<String>,
        tenant_id: Option<String>,
        preferred_region: Option<RegionCode>,
        clock: Box<dyn Fn() -> u64 + Send + Sync>,
    ) -> Self {
        let loopback_client = reqwest::Url::parse(&atlas_origin)
            .ok()
            .and_then(|url| url.host_str().map(str::to_owned))
            .is_some_and(|host| is_dev_host(&host));
        Self {
            atlas_origin,
            api_host: reqwest::Url::parse(api_base_url)
                .ok()
                .and_then(|url| url.host_str().map(str::to_ascii_lowercase))
                .unwrap_or_default(),
            loopback_client,
            project_id,
            tenant_id,
            preferred_region,
            clock,
            rtt: RegionRttTracker::default(),
            scopes: Mutex::new(HashMap::new()),
            discovery_flights: Mutex::new(HashMap::new()),
            catalog: Mutex::new(None),
            catalog_flight: Mutex::new(None),
        }
    }

    /// The discovery/catalog origin.
    #[must_use]
    pub(crate) fn atlas_origin(&self) -> &str {
        &self.atlas_origin
    }

    fn scope_key(&self, database: &str) -> String {
        format!(
            "{}|{}|{}|p={}|t={}",
            self.atlas_origin,
            database,
            DEFAULT_ROUTING_BRANCH,
            self.project_id.as_deref().unwrap_or(""),
            self.tenant_id.as_deref().unwrap_or(""),
        )
    }

    fn with_scope<R>(&self, key: &str, action: impl FnOnce(&mut ScopeState) -> R) -> R {
        let mut scopes = self.scopes.lock().expect("routing scope lock");
        action(scopes.entry(key.to_owned()).or_default())
    }

    /// Resolve the regional endpoint for one database-scoped operation.
    /// `None` means discovery cannot answer; the caller degrades to catalog
    /// selection (ADR 0050 §5.8).
    pub(crate) async fn resolve(
        self: &Arc<Self>,
        transport: &Arc<dyn HttpTransport>,
        database: &str,
        needs_write: bool,
    ) -> Option<(String, String)> {
        let key = self.scope_key(database);
        if let Some(document) = self.cached_document(&key) {
            return self.select(&key, &document, needs_write);
        }
        let now = (self.clock)();
        if self.with_scope(&key, |scope| {
            scope
                .last_failure
                .is_some_and(|failed| failed + FAILURE_BACKOFF_MS > now)
        }) {
            return None;
        }
        let (stale, revision) = self.with_scope(&key, |scope| {
            (
                std::mem::take(&mut scope.stale),
                scope
                    .document
                    .as_ref()
                    .map(|(document, _)| document.revision),
            )
        });
        let flight = {
            let mut flights = self.discovery_flights.lock().expect("flight lock");
            match flights.get(&key) {
                Some(existing) => Either::Join(Arc::clone(existing)),
                None => {
                    let flight = DocumentFlight::new();
                    flights.insert(key.clone(), Arc::clone(&flight));
                    Either::Initiate(flight)
                }
            }
        };
        match flight {
            Either::Join(joined) => joined
                .wait()
                .await
                .and_then(|outcome| outcome.ok())
                .and_then(|document| self.select(&key, &document, needs_write)),
            Either::Initiate(flight) => {
                let outcome = self
                    .fetch_document(transport, database, stale, revision)
                    .await
                    .map(Arc::new);
                match &outcome {
                    Ok(document) => self.store_document(&key, Arc::clone(document)),
                    Err(_) => {
                        self.with_scope(&key, |scope| scope.last_failure = Some((self.clock)()))
                    }
                }
                flight.result.send_replace(Some(outcome.clone()));
                self.discovery_flights
                    .lock()
                    .expect("flight lock")
                    .remove(&key);
                outcome
                    .ok()
                    .and_then(|document| self.select(&key, &document, needs_write))
            }
        }
    }

    fn cached_document(&self, key: &str) -> Option<Arc<RoutingDocument>> {
        let now = (self.clock)();
        self.with_scope(key, |scope| {
            scope
                .document
                .as_ref()
                .filter(|(_, expires)| *expires > now)
                .map(|(document, _)| Arc::clone(document))
        })
    }

    fn store_document(&self, key: &str, document: Arc<RoutingDocument>) {
        let expires = (self.clock)() + document.refresh_seconds * 1_000;
        self.with_scope(key, |scope| {
            // A refresh never re-pins a healthy connection (§5.5): the sticky
            // region survives; only the fallback ranking is replaced.
            scope.document = Some((Arc::clone(&document), expires));
            scope.demoted.clear();
        });
    }

    /// Pick the endpoint for one operation: the scope's sticky region while
    /// it stays in the document and capability-eligible, otherwise the
    /// configured preference or the first eligible endpoint.
    fn select(
        &self,
        key: &str,
        document: &RoutingDocument,
        needs_write: bool,
    ) -> Option<(String, String)> {
        let required = if needs_write {
            RoutingCapability::Write
        } else {
            RoutingCapability::Read
        };
        let mut eligible: Vec<&RoutingEndpoint> = document
            .endpoints
            .iter()
            .filter(|endpoint| endpoint.supports(required))
            .collect();
        if eligible.is_empty() {
            return None;
        }
        let (pinned, demoted) =
            self.with_scope(key, |scope| (scope.pinned.clone(), scope.demoted.clone()));
        if !demoted.is_empty() {
            let undemoted: Vec<&RoutingEndpoint> = eligible
                .iter()
                .copied()
                .filter(|endpoint| !demoted.contains(&endpoint.region))
                .collect();
            if !undemoted.is_empty() {
                eligible = undemoted;
            }
        }
        if let Some(pinned) = pinned
            .as_deref()
            .and_then(|region| eligible.iter().find(|e| e.region == region))
        {
            return Some((pinned.url.clone(), pinned.region.clone()));
        }
        let preferred = self
            .preferred_region
            .map(|region| region.as_header_str().to_owned());
        let chosen = preferred
            .as_deref()
            .and_then(|region| eligible.iter().find(|e| e.region == region))
            .copied()
            .or_else(|| eligible.first().copied())?;
        self.with_scope(key, |scope| scope.pinned = Some(chosen.region.clone()));
        Some((chosen.url.clone(), chosen.region.clone()))
    }

    async fn fetch_document(
        &self,
        transport: &Arc<dyn HttpTransport>,
        database: &str,
        stale: bool,
        revision: Option<u64>,
    ) -> Result<RoutingDocument> {
        let mut url = format!(
            "{}/api/v1/discovery?database={}&branch={}",
            self.atlas_origin, database, DEFAULT_ROUTING_BRANCH
        );
        if let Some(revision) = revision {
            url.push_str(&format!("&rev={revision}"));
        }
        if stale {
            url.push_str("&stale=1");
        }
        let mut headers = vec![("accept".to_owned(), "application/json".to_owned())];
        // Measured client-to-region latencies (ADR 0050 §2) go to the
        // discovery origin only — never to a regional member.
        if let Some(rtt) = self.rtt.header() {
            headers.push(("x-terrascale-region-rtt".to_owned(), rtt));
        }
        let request = Request {
            route_name: "discovery.route".to_owned(),
            method: Method::Get,
            url,
            headers,
            body: None,
        };
        let response = tokio::time::timeout(REQUEST_TIMEOUT, transport.send(request))
            .await
            .map_err(|_| {
                Error::new(
                    ErrorKind::Unavailable,
                    "discovery_timeout",
                    "The discovery request timed out.",
                )
            })??;
        if response.status != 200 {
            return Err(Error::new(
                ErrorKind::Unavailable,
                "discovery_unavailable",
                format!("Discovery answered {}.", response.status),
            ));
        }
        let payload: Value = serde_json::from_slice(&response.body).map_err(|_| {
            Error::new(
                ErrorKind::InvalidResponse,
                "invalid_routing_document",
                "Discovery returned a non-JSON body.",
            )
        })?;
        parse_routing_document(
            &payload,
            database,
            DEFAULT_ROUTING_BRANCH,
            self.loopback_client,
        )
        .ok_or_else(|| {
            Error::new(
                ErrorKind::InvalidResponse,
                "invalid_routing_document",
                "Discovery returned a malformed routing document.",
            )
        })
    }

    /// The catalog-selected regional origin (ADR 0050 §5.8/§8): the discovery
    /// failure fallback and the base for database-less member routes.
    pub(crate) async fn catalog_base(
        self: &Arc<Self>,
        transport: &Arc<dyn HttpTransport>,
    ) -> Result<String> {
        if let Some(host) = self.cached_catalog() {
            return Ok(host);
        }
        let flight = {
            let mut guard = self.catalog_flight.lock().expect("catalog flight lock");
            match guard.as_ref() {
                Some(existing) => Either::Join(Arc::clone(existing)),
                None => {
                    let flight = CatalogFlight::new();
                    *guard = Some(Arc::clone(&flight));
                    Either::Initiate(flight)
                }
            }
        };
        let outcome = match flight {
            Either::Join(joined) => joined
                .wait()
                .await
                .ok_or_else(catalog_unavailable)?
                .map_err(|_| catalog_unavailable()),
            Either::Initiate(flight) => {
                let outcome = self.fetch_catalog(transport).await.map(Arc::new);
                if let Ok(catalog) = &outcome {
                    *self.catalog.lock().expect("catalog lock") =
                        Some((Arc::clone(catalog), (self.clock)() + CATALOG_CACHE_MS));
                }
                flight.result.send_replace(Some(outcome.clone()));
                *self.catalog_flight.lock().expect("catalog flight lock") = None;
                outcome
            }
        };
        outcome
            .ok()
            .and_then(|catalog| first_available_host(&catalog))
            .ok_or_else(catalog_unavailable)
    }

    fn cached_catalog(&self) -> Option<String> {
        let now = (self.clock)();
        let mut guard = self.catalog.lock().expect("catalog lock");
        if guard.as_ref().is_some_and(|(_, expires)| *expires <= now) {
            *guard = None;
        }
        guard
            .as_ref()
            .and_then(|(catalog, _)| first_available_host(catalog))
    }

    async fn fetch_catalog(
        &self,
        transport: &Arc<dyn HttpTransport>,
    ) -> Result<RegionCatalogResponse> {
        let request = Request {
            route_name: "discovery.catalog".to_owned(),
            method: Method::Get,
            url: format!("{}/api/v1/regions", self.atlas_origin),
            headers: vec![("accept".to_owned(), "application/json".to_owned())],
            body: None,
        };
        let response = tokio::time::timeout(REQUEST_TIMEOUT, transport.send(request))
            .await
            .map_err(|_| {
                Error::new(
                    ErrorKind::Unavailable,
                    "region_catalog_timeout",
                    "The region catalog request timed out.",
                )
            })??;
        if response.status != 200 {
            return Err(Error::new(
                ErrorKind::Unavailable,
                "region_catalog_unavailable",
                format!("The region catalog answered {}.", response.status),
            ));
        }
        let payload: Value = serde_json::from_slice(&response.body).map_err(|_| {
            Error::new(
                ErrorKind::InvalidResponse,
                "invalid_region_catalog",
                "The region catalog returned a non-JSON body.",
            )
        })?;
        parse_region_catalog(&payload, self.loopback_client).ok_or_else(|| {
            Error::new(
                ErrorKind::InvalidResponse,
                "invalid_region_catalog",
                "The region catalog returned a malformed response.",
            )
        })
    }

    /// A member 421 or placement header teaches placement without a round
    /// trip: synthesize a provisional fully-capable document, drop the
    /// remembered region, and mark the scope for one corrective lookup.
    pub(crate) fn note_placement(&self, database: &str, regions: &[RegionCode], revision: u64) {
        let key = self.scope_key(database);
        if self.api_host != GLOBAL_API_HOST {
            // A custom product origin must not cache production regional URLs.
            self.invalidate(database);
            return;
        }
        let mut unique = Vec::new();
        for region in regions {
            if !unique.contains(region) {
                unique.push(*region);
            }
        }
        if unique.is_empty() {
            self.invalidate(database);
            return;
        }
        let endpoints: Vec<RoutingEndpoint> = unique
            .iter()
            .take(MAX_ENDPOINTS)
            .enumerate()
            .map(|(index, region)| RoutingEndpoint {
                region: region.as_header_str().to_owned(),
                url: format!("https://{}", crate::regions::regional_host(*region)),
                priority: index as u32 + 1,
                capabilities: vec![
                    RoutingCapability::Read,
                    RoutingCapability::Write,
                    RoutingCapability::Sql,
                    RoutingCapability::Reactive,
                ],
            })
            .collect();
        let document = RoutingDocument {
            database: database.to_owned(),
            branch: DEFAULT_ROUTING_BRANCH.to_owned(),
            revision,
            residency: None,
            placement: Placement::Known,
            refresh_seconds: MIN_REFRESH_SECONDS,
            endpoints,
        };
        self.with_scope(&key, |scope| {
            scope.document = Some((
                Arc::new(document),
                (self.clock)() + MIN_REFRESH_SECONDS * 1_000,
            ));
            scope.pinned = None;
            scope.demoted.clear();
            scope.stale = true;
        });
    }

    /// Fold one measured round trip against a discovered regional endpoint
    /// into the per-region EWMA (ADR 0050 §2).
    pub(crate) fn observe_rtt(&self, region: &str, rtt_ms: f64) {
        self.rtt.observe(region, rtt_ms, (self.clock)());
    }

    /// A regional transport failure demotes that region behind the
    /// alternatives (ADR 0050 §5.3).
    pub(crate) fn demote(&self, database: &str, region: &str) {
        let key = self.scope_key(database);
        self.with_scope(&key, |scope| {
            scope.demoted.insert(region.to_owned());
        });
    }

    fn invalidate(&self, database: &str) {
        let key = self.scope_key(database);
        self.with_scope(&key, |scope| {
            scope.document = None;
            scope.pinned = None;
            scope.demoted.clear();
            scope.stale = true;
        });
    }
}

enum Either<T> {
    Join(Arc<T>),
    Initiate(Arc<T>),
}

fn first_available_host(catalog: &RegionCatalogResponse) -> Option<String> {
    catalog
        .regions
        .iter()
        .find(|region| region.available)
        .and_then(|region| catalog.hosts.get(&region.code))
        .cloned()
}

fn catalog_unavailable() -> Error {
    Error::new(
        ErrorKind::Unavailable,
        "region_catalog_unavailable",
        "No regional endpoint is available from the region catalog.",
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::transport::Response;
    use async_trait::async_trait;
    use serde_json::json;
    use std::collections::VecDeque;

    fn document() -> Value {
        json!({
            "v": 1,
            "database": "orders",
            "branch": "main",
            "revision": 42,
            "updated": 1725300000,
            "residency": "eu-only",
            "placement": "known",
            "refreshSeconds": 300,
            "endpoints": [
                { "region": "FRA", "url": "https://fra.api.terrascale.tech",
                  "priority": 1, "capabilities": ["read", "write", "sql", "reactive"] },
                { "region": "GRU", "url": "https://gru.api.terrascale.tech",
                  "priority": 2, "capabilities": ["read", "sql", "reactive"] },
            ],
        })
    }

    #[test]
    fn routing_document_accepts_the_published_shape_and_clamps_refresh() {
        let parsed =
            parse_routing_document(&document(), "orders", "main", false).expect("valid document");
        assert_eq!(parsed.revision, 42);
        assert_eq!(parsed.refresh_seconds, 300);
        assert_eq!(parsed.endpoints[0].region, "FRA");
        assert_eq!(
            parsed.residency,
            Some(Residency::Label("eu-only".to_owned()))
        );
        let mut clamped = document();
        clamped["refreshSeconds"] = json!(5);
        assert_eq!(
            parse_routing_document(&clamped, "orders", "main", false)
                .expect("valid")
                .refresh_seconds,
            MIN_REFRESH_SECONDS
        );
        clamped["refreshSeconds"] = json!(10_000);
        assert_eq!(
            parse_routing_document(&clamped, "orders", "main", false)
                .expect("valid")
                .refresh_seconds,
            MAX_REFRESH_SECONDS
        );
    }

    #[test]
    fn routing_document_rejects_structural_doubt() {
        for mutate in [
            |doc: &mut Value| doc["v"] = json!(2),
            |doc: &mut Value| doc["database"] = json!("other"),
            |doc: &mut Value| doc["placement"] = json!("elsewhere"),
            |doc: &mut Value| doc["revision"] = json!(-1),
            |doc: &mut Value| doc["refreshSeconds"] = json!(0),
            |doc: &mut Value| doc["endpoints"] = json!([]),
            |doc: &mut Value| doc["endpoints"][0]["url"] = json!("https://evil.example"),
            |doc: &mut Value| doc["endpoints"][0]["url"] = json!("http://fra.api.terrascale.tech"),
            |doc: &mut Value| doc["endpoints"][0]["region"] = json!("fra"),
            |doc: &mut Value| doc["endpoints"][0]["capabilities"] = json!([]),
            |doc: &mut Value| doc["endpoints"][0]["capabilities"] = json!(["admin"]),
            |doc: &mut Value| doc["endpoints"][0]["priority"] = json!(0),
            |doc: &mut Value| doc["endpoints"][0]["priority"] = json!(99),
            |doc: &mut Value| doc["residency"] = json!([]),
            |doc: &mut Value| doc["endpoints"][1]["region"] = json!("FRA"),
        ] {
            let mut doc = document();
            mutate(&mut doc);
            assert!(
                parse_routing_document(&doc, "orders", "main", false).is_none(),
                "mutated document must be rejected"
            );
        }
        // No endpoint ranked first.
        let mut doc = document();
        doc["endpoints"][0]["priority"] = json!(2);
        doc["endpoints"][1]["priority"] = json!(2);
        assert!(parse_routing_document(&doc, "orders", "main", false).is_none());
    }

    #[test]
    fn loopback_clients_admit_dev_endpoint_urls_only() {
        let mut doc = document();
        doc["endpoints"][0]["url"] = json!("http://localhost:8787");
        assert!(parse_routing_document(&doc, "orders", "main", false).is_none());
        let parsed = parse_routing_document(&doc, "orders", "main", true).expect("dev document");
        assert_eq!(parsed.endpoints[0].url, "http://localhost:8787");
        doc["endpoints"][0]["url"] = json!("http://localhost:8787/x");
        assert!(parse_routing_document(&doc, "orders", "main", true).is_none());
    }

    #[test]
    fn region_catalog_accepts_only_complete_trusted_hosts() {
        let catalog = json!({
            "regions": [
                {"code": "GRU", "name": "São Paulo", "country": "BR",
                 "latitude": -23.5, "longitude": -46.6, "available": true},
            ],
            "hosts": {"GRU": "https://gru.api.terrascale.tech"},
        });
        let parsed = parse_region_catalog(&catalog, false).expect("catalog");
        assert_eq!(parsed.hosts["GRU"], "https://gru.api.terrascale.tech");

        let mut bad_host = catalog.clone();
        bad_host["hosts"]["GRU"] = json!("https://evil.example");
        assert!(parse_region_catalog(&bad_host, false).is_none());

        let mut missing_host = catalog.clone();
        missing_host["hosts"] = json!({});
        assert!(parse_region_catalog(&missing_host, false).is_none());

        let mut unavailable = catalog.clone();
        unavailable["regions"][0]["available"] = json!(false);
        assert!(parse_region_catalog(&unavailable, false).is_some());
    }

    #[test]
    fn paths_classify_per_the_routing_table() {
        assert_eq!(
            classify_path("/api/v1/databases/orders/documents"),
            RouteKind::Database("orders".to_owned())
        );
        assert_eq!(
            classify_path("/api/v1/databases/orders"),
            RouteKind::Database("orders".to_owned())
        );
        assert_eq!(classify_path("/api/v1/databases"), RouteKind::MemberGlobal);
        assert_eq!(classify_path("/api/v1/regions"), RouteKind::Catalog);
        assert_eq!(classify_path("/api/v1/regions/FRA"), RouteKind::Unrouted);
        assert_eq!(classify_path("/api/v1/organizations"), RouteKind::Unrouted);
        assert_eq!(
            classify_path("/api/v1/organizations/org-1/projects"),
            RouteKind::Unrouted
        );
        assert_eq!(classify_path("/api/v1/operations"), RouteKind::MemberGlobal);
        assert_eq!(
            classify_path("/api/v1/authorization/leases/acme"),
            RouteKind::MemberGlobal
        );
        assert_eq!(classify_path("/health"), RouteKind::Unrouted);
        // Database ids outside the producer name format stay member-global.
        assert_eq!(
            classify_path("/api/v1/databases/Weird%20Name/documents"),
            RouteKind::MemberGlobal
        );
    }

    #[test]
    fn database_names_follow_the_producer_format() {
        assert!(is_database_name("orders"));
        assert!(is_database_name("a"));
        assert!(is_database_name("0-x-9"));
        assert!(!is_database_name("-orders"));
        assert!(!is_database_name("orders-"));
        assert!(!is_database_name("Orders"));
        assert!(!is_database_name(""));
        assert!(!is_database_name(&"x".repeat(64)));
        assert!(!is_database_name("two words"));
    }

    #[test]
    fn ewma_decays_toward_new_samples_with_a_60s_halflife() {
        let tracker = RegionRttTracker::default();
        tracker.observe("FRA", 100.0, 0);
        // One halflife later the old sample weighs half: 100*0.5 + 200*0.5.
        tracker.observe("FRA", 200.0, 60_000);
        assert_eq!(tracker.header().as_deref(), Some("FRA=150"));
        tracker.observe("FRA", 100.0, 120_000);
        assert_eq!(tracker.header().as_deref(), Some("FRA=125"));
    }

    #[test]
    fn ewma_resets_when_a_timestamp_arrives_out_of_order() {
        let tracker = RegionRttTracker::default();
        tracker.observe("FRA", 100.0, 10_000);
        // An older timestamp resets the estimate instead of blending backwards.
        tracker.observe("FRA", 40.0, 5_000);
        assert_eq!(tracker.header().as_deref(), Some("FRA=40"));
        tracker.observe("FRA", 60.0, 65_000);
        assert_eq!(tracker.header().as_deref(), Some("FRA=50"));
    }

    #[test]
    fn ewma_ignores_unbounded_samples_and_non_region_keys() {
        let tracker = RegionRttTracker::default();
        for bad in [f64::NAN, f64::INFINITY, -1.0, 60_001.0] {
            tracker.observe("FRA", bad, 0);
        }
        tracker.observe("fra", 100.0, 0);
        tracker.observe("TOO-LONG-REGION-CODE", 100.0, 0);
        assert_eq!(tracker.header(), None);
        tracker.observe("GRU", 60_000.0, 0);
        assert_eq!(tracker.header().as_deref(), Some("GRU=60000"));
    }

    #[test]
    fn rtt_header_keeps_bounded_pairs_and_caps_the_entry_count() {
        let tracker = RegionRttTracker::default();
        tracker.observe("FRA", 0.5, 0); // sub-millisecond: below the header floor
        tracker.observe("GRU", 120.4, 0);
        tracker.observe("SFO", 59_999.9, 0);
        assert_eq!(tracker.header().as_deref(), Some("GRU=120,SFO=60000"));
        for index in 0..25 {
            tracker.observe(&format!("R{index:02}"), 100.0, 0);
        }
        let header = tracker.header().expect("header");
        assert_eq!(header.split(',').count(), MAX_RTT_HEADER_ENTRIES);
    }

    /// A mock transport recording every request for router-level tests.
    #[derive(Debug, Default)]
    struct RecordingTransport {
        requests: Mutex<Vec<Request>>,
        responses: Mutex<VecDeque<Result<Response>>>,
    }

    impl RecordingTransport {
        fn push(&self, response: Result<Response>) {
            self.responses
                .lock()
                .expect("test lock")
                .push_back(response);
        }

        fn take_requests(&self) -> Vec<Request> {
            std::mem::take(&mut *self.requests.lock().expect("test lock"))
        }
    }

    #[async_trait]
    impl HttpTransport for RecordingTransport {
        async fn send(&self, request: Request) -> Result<Response> {
            self.requests.lock().expect("test lock").push(request);
            self.responses
                .lock()
                .expect("test lock")
                .pop_front()
                .unwrap_or_else(|| Err(Error::transport("mock exhausted")))
        }
    }

    #[tokio::test]
    async fn refresh_fetches_carry_the_cached_revision_and_measured_rtt() {
        let time = Arc::new(Mutex::new(10_000_u64));
        let clock = {
            let time = Arc::clone(&time);
            Box::new(move || *time.lock().expect("test clock"))
                as Box<dyn Fn() -> u64 + Send + Sync>
        };
        let recorder = Arc::new(RecordingTransport::default());
        let transport: Arc<dyn HttpTransport> = recorder.clone();
        recorder.push(Ok(Response::json(200, &document())));
        recorder.push(Ok(Response::json(200, &document())));
        let router = Arc::new(Router::with_clock(
            "https://atlas.terrascale.tech".to_owned(),
            "https://api.terrascale.tech",
            None,
            None,
            None,
            clock,
        ));
        let (origin, region) = router
            .resolve(&transport, "orders", false)
            .await
            .expect("resolved");
        assert_eq!(origin, "https://fra.api.terrascale.tech");
        assert_eq!(region, "FRA");
        let cold = recorder.take_requests();
        assert_eq!(cold.len(), 1);
        assert!(!cold[0].url.contains("rev="));
        assert!(cold[0]
            .headers
            .iter()
            .all(|(name, _)| !name.eq_ignore_ascii_case("x-terrascale-region-rtt")));

        // A measured FRA round trip plus document expiry: the refresh fetch
        // carries both signals to the discovery origin (ADR 0050 §2).
        router.observe_rtt("FRA", 120.0);
        *time.lock().expect("test clock") += 301_000;
        router
            .resolve(&transport, "orders", false)
            .await
            .expect("resolved again");
        let refresh = recorder.take_requests();
        assert_eq!(
            refresh[0].url,
            "https://atlas.terrascale.tech/api/v1/discovery?database=orders&branch=main&rev=42"
        );
        assert_eq!(
            refresh[0]
                .headers
                .iter()
                .find(|(name, _)| name.eq_ignore_ascii_case("x-terrascale-region-rtt"))
                .map(|(_, value)| value.as_str()),
            Some("FRA=120")
        );
    }
}
