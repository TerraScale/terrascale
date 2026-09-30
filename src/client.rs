//! Client root and the shared request pipeline.
//!
//! [`TerraScaleClient`] mirrors the `@terrascale/sdk` root: document CRUD,
//! typed SQL, reactive subscribe/execute, tenants, and management, with
//! region pinning, session tokens (read-your-writes + monotonic reads), and
//! `421` placement repair shared by every sub-client.

use std::sync::{Arc, RwLock};
use std::time::{Duration, Instant};

use serde::de::DeserializeOwned;

use crate::auth_idp::AuthIdpClient;
use crate::auth_routing::AuthRouter;
use crate::config::ClientConfig;
use crate::discovery::{self, RouteKind, Router};
use crate::documents::{DatabaseScope, DocumentsClient};
use crate::error::{Error, ErrorKind, Result};
use crate::files::FilesClient;
use crate::management::ManagementClient;
use crate::reactive::ReactiveClient;
use crate::regions::{normalize_base_url, pin_base_url, RegionAffinity, RegionCode};
use crate::sql::SqlClient;
use crate::tenants::TenantsClient;
use crate::transport::{HttpTransport, Method, Request, ReqwestTransport, Response};

/// REST root for the public API (GOAL D-34).
pub const API_ROOT: &str = "/api/v1";

/// Placement header returned with a 421 (GOAL D-58).
pub const PLACEMENT_HEADER: &str = "x-terrascale-placement";

/// Canonical session-frontier header used on public API requests.
pub const SESSION_HEADER: &str = "X-TerraScale-Session";

/// Maximum length accepted for a caller-provided idempotency key.
pub const MAX_IDEMPOTENCY_KEY_LENGTH: usize = 128;

/// Retry sleeps never exceed this; a server-sent `Retry-After` asking for
/// more is surfaced to the caller as an error instead of retried (matching
/// the .NET and TS SDKs).
const MAX_RETRY_AFTER: Duration = Duration::from_secs(30);

/// Internal marker removed by [`Shared::decorate`] before transport. It lets
/// a per-request eventual-consistency option suppress the shared causal
/// session header without exposing an unsupported wire header.
pub(crate) const SKIP_SESSION_MARKER: &str = "x-terrascale-sdk-skip-session";
/// Internal marker removed before transport. Capability-authenticated
/// reactive delivery must not receive the API bearer credential.
pub(crate) const SKIP_AUTH_MARKER: &str = "x-terrascale-sdk-skip-auth";
/// Internal marker tagging a request with its routing database when the wire
/// path carries none (reactive attach). Discovery routes the request to the
/// database's regional endpoint; the header never reaches the transport.
pub(crate) const ROUTING_DATABASE_MARKER: &str = "x-terrascale-sdk-routing-database";

/// Shareable pipeline state behind every sub-client.
#[derive(Debug)]
pub(crate) struct Shared {
    transport: Arc<dyn HttpTransport>,
    config: ClientConfig,
    /// Atlas discovery router; `None` keeps direct-origin routing (strict
    /// affinity, custom origins, local emulator).
    router: Option<Arc<Router>>,
    auth_router: Option<AuthRouter>,
    placement: RwLock<Option<RegionCode>>,
    session_token: RwLock<Option<String>>,
    /// Optional key used by all mutation requests made through this client.
    ///
    /// Keeping this on the shared pipeline means handles returned by
    /// `documents()`, `tenants()`, and `management()` all observe the same
    /// caller-selected identity. Explicit request headers (management's
    /// `OperationOptions`, for example) take precedence.
    idempotency_key: RwLock<Option<String>>,
}

/// Shareable handle to the pipeline.
pub(crate) type SharedClient = Arc<Shared>;

impl Shared {
    fn new(config: ClientConfig, transport: Arc<dyn HttpTransport>) -> Self {
        let auth_router = AuthRouter::for_config(
            config.auth_base_url(),
            config.auth_reverse_proxy(),
            config.atlas_base_url(),
        );
        let router = config.atlas_base_url().map(|atlas_origin| {
            Arc::new(Router::new(
                atlas_origin.to_owned(),
                config.api_base_url(),
                config.project_id().map(str::to_owned),
                config.tenant_id().map(str::to_owned),
                config.preferred_region(),
            ))
        });
        Self {
            transport,
            config,
            router,
            auth_router,
            placement: RwLock::new(None),
            session_token: RwLock::new(None),
            idempotency_key: RwLock::new(None),
        }
    }

    /// Runtime configuration, including the reactive delivery origin.
    pub(crate) fn config(&self) -> &ClientConfig {
        &self.config
    }

    /// Base URL for product routes and direct-origin traffic.
    pub fn global_base_url(&self) -> String {
        format!("{}{API_ROOT}", self.config.api_base_url())
    }

    pub(crate) fn api_key(&self) -> Option<&str> {
        self.config.api_key()
    }

    /// Base URL for REST traffic, honoring a per-request hint, the repaired
    /// 421 placement, the preferred region, and affinity.
    ///
    /// An explicit hint or a learned 421 placement pins directly. Otherwise a
    /// preferred region pins under strict affinity and uses the configured
    /// origin under failover. When discovery is active, member routing is
    /// resolved per request in [`Shared::dispatch`]; region hints stay
    /// advisory headers and never steer the base URL.
    pub fn pinned_base_url(&self, region_hint: Option<RegionCode>) -> Result<String> {
        if self.router.is_some() {
            return Ok(format!("{}{API_ROOT}", self.config.api_base_url()));
        }
        let repaired = self.placement.read().ok().and_then(|guard| *guard);
        if let Some(region) = region_hint.or(repaired) {
            let global = normalize_base_url(self.config.api_base_url())?;
            let pinned = pin_base_url(&global, Some(region), Some(region), RegionAffinity::Strict)?;
            return Ok(format!("{pinned}{API_ROOT}"));
        }
        let base = pin_base_url(
            self.config.api_base_url(),
            self.config.preferred_region(),
            None,
            self.config.region_affinity(),
        )?;
        Ok(format!("{base}{API_ROOT}"))
    }

    /// Explicit regional member origin (health probes and other
    /// region-targeted calls); discovery never reroutes it.
    pub fn regional_base_url(&self, region: RegionCode) -> Result<String> {
        let global = normalize_base_url(self.config.api_base_url())?;
        let pinned = pin_base_url(&global, Some(region), Some(region), RegionAffinity::Strict)?;
        Ok(format!("{pinned}{API_ROOT}"))
    }

    /// The member origin for a database-scoped direct connection (the
    /// reactive WebSocket): the discovered regional endpoint, or the
    /// catalog-selected origin when discovery cannot answer.
    pub(crate) async fn member_base_url(
        &self,
        database: &str,
        region_hint: Option<RegionCode>,
        needs_write: bool,
    ) -> Result<String> {
        let Some(router) = &self.router else {
            return self.pinned_base_url(region_hint);
        };
        if let Some((base, _region)) = router.resolve(&self.transport, database, needs_write).await
        {
            return Ok(base);
        }
        router
            .catalog_base(&self.transport)
            .await
            .map_err(routing_unavailable)
    }

    /// Database-scoped root for document, SQL, and reactive routes.
    pub fn database_root(&self, scope: &DatabaseScope) -> Result<String> {
        self.database_root_in(scope, None)
    }

    /// Database-scoped root pinned to one region for this request.
    pub fn database_root_in(
        &self,
        scope: &DatabaseScope,
        region_hint: Option<RegionCode>,
    ) -> Result<String> {
        if scope.database_id.trim().is_empty() {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_database_id",
                "Database ids cannot be blank.",
            ));
        }
        let id = encode_path_segment(&scope.database_id);
        Ok(format!(
            "{}/databases/{id}",
            self.pinned_base_url(region_hint)?
        ))
    }

    /// Collection names follow the schema builder contract.
    pub fn require_collection(&self, collection: &str) -> Result<String> {
        if collection.trim().is_empty() {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_collection",
                "Collection names cannot be empty.",
            ));
        }
        Ok(collection.to_owned())
    }

    /// Currently pinned region, if the SDK learned or configured one.
    #[must_use]
    pub fn pinned_region(&self) -> Option<RegionCode> {
        self.placement
            .read()
            .ok()
            .and_then(|guard| *guard)
            .or(self.config.preferred_region())
    }

    /// Current session token (causal frontier for read-your-writes).
    #[must_use]
    pub fn session_token(&self) -> Option<String> {
        self.session_token
            .read()
            .ok()
            .and_then(|guard| guard.clone())
    }

    /// Install a session token; it is carried on subsequent requests and
    /// refreshed from server responses.
    pub fn set_session_token(&self, token: Option<String>) {
        if let Ok(mut guard) = self.session_token.write() {
            match token {
                None => *guard = None,
                Some(token) => {
                    if let Some(token) = normalize_session_token(&token) {
                        *guard = Some(token);
                    }
                }
            }
        }
    }

    /// Apply shared authentication and observability headers. Public within
    /// the crate so direct-delivery transports (the reactive stream) send
    /// exactly the headers the pipelined routes send.
    pub(crate) fn decorate(&self, request: &mut Request) {
        let skip_auth = request
            .headers
            .iter()
            .any(|(name, _)| name.eq_ignore_ascii_case(SKIP_AUTH_MARKER));
        let skip_session = request
            .headers
            .iter()
            .any(|(name, _)| name.eq_ignore_ascii_case(SKIP_SESSION_MARKER));
        request.headers.retain(|(name, _)| {
            !name.eq_ignore_ascii_case(SKIP_SESSION_MARKER)
                && !name.eq_ignore_ascii_case(SKIP_AUTH_MARKER)
                && !name.eq_ignore_ascii_case(ROUTING_DATABASE_MARKER)
        });
        if !skip_auth {
            if let Some(api_key) = self.config.api_key() {
                request
                    .headers
                    .push(("authorization".to_owned(), format!("Bearer {api_key}")));
            }
        }
        // ts-auth routes share only the transport: TerraBase tenant, project,
        // and region headers must not leak to a separate service.
        let is_auth_idp = request.route_name.starts_with("auth_idp.");
        if !is_auth_idp {
            for (header, value) in [
                ("x-terrascale-tenant-id", self.config.tenant_id()),
                ("x-terrascale-project-id", self.config.project_id()),
            ] {
                if let Some(value) = value {
                    request
                        .headers
                        .retain(|(name, _)| !name.eq_ignore_ascii_case(header));
                    request.headers.push((header.to_owned(), value.to_owned()));
                }
            }
        }
        if !request
            .headers
            .iter()
            .any(|(k, _)| k.eq_ignore_ascii_case("accept"))
        {
            request
                .headers
                .push(("accept".to_owned(), "application/json".to_owned()));
        }
        if !skip_session
            && !request
                .headers
                .iter()
                .any(|(k, _)| k.eq_ignore_ascii_case(SESSION_HEADER))
        {
            if let Some(token) = self.session_token() {
                request.headers.push((SESSION_HEADER.to_owned(), token));
            }
        }
        if !is_auth_idp
            && (self.config.preferred_region().is_some() || self.pinned_region().is_some())
            && !request
                .headers
                .iter()
                .any(|(k, _)| k.eq_ignore_ascii_case("x-terrascale-preferred-region"))
            && self.should_advertise_region_hint(request)
        {
            if let Some(region) = self.pinned_region() {
                request.headers.push((
                    "x-terrascale-preferred-region".to_owned(),
                    region.as_header_str().to_owned(),
                ));
            }
        }
        // ts-auth mutations carry no TerraBase idempotency identity: the key
        // scopes one logical TerraBase mutation, and replaying it against a
        // different service would be meaningless (or worse).
        if is_mutation(request)
            && !request.route_name.starts_with("auth_idp.")
            && request.route_name != "files.upload"
            && !request
                .headers
                .iter()
                .any(|(k, _)| k.eq_ignore_ascii_case("idempotency-key"))
        {
            if let Some(idempotency) = self.idempotency_key() {
                request
                    .headers
                    .push(("idempotency-key".to_owned(), idempotency));
            }
        }
    }

    /// Current caller-selected idempotency key, if configured.
    #[must_use]
    pub fn idempotency_key(&self) -> Option<String> {
        self.idempotency_key
            .read()
            .ok()
            .and_then(|guard| guard.clone())
    }

    /// Install a caller-selected idempotency key for subsequent mutations.
    pub fn set_idempotency_key(&self, key: impl Into<String>) -> Result<()> {
        let key = key.into();
        validate_idempotency_key(&key)?;
        if let Ok(mut guard) = self.idempotency_key.write() {
            *guard = Some(key.trim().to_owned());
        }
        Ok(())
    }

    /// Clear the caller-selected key. Subsequent mutations carry no
    /// idempotency identity unless the operation supplies an explicit header.
    /// Explicit request headers remain unaffected.
    pub fn clear_idempotency_key(&self) {
        if let Ok(mut guard) = self.idempotency_key.write() {
            *guard = None;
        }
    }

    fn observe(&self, response: &Response) {
        if let Some(token) = response.header("x-terrascale-session") {
            if let Some(token) = normalize_session_token(token) {
                self.set_session_token(Some(token));
            }
        }
    }

    fn should_advertise_region_hint(&self, request: &Request) -> bool {
        if self.config.region_affinity() != RegionAffinity::Strict {
            return true;
        }
        // A strict request to a first-party regional host is already routed
        // directly. Custom origins still need the typed hint header.
        let Some(host) = reqwest::Url::parse(&request.url)
            .ok()
            .and_then(|url| url.host_str().map(str::to_ascii_lowercase))
        else {
            return true;
        };
        !matches!(
            host.as_str(),
            "api.terrascale.tech"
                | "gru.terrascale.tech"
                | "sfo.terrascale.tech"
                | "fra.terrascale.tech"
        )
    }

    fn check(&self, route_name: &str, response: &Response, database: Option<&str>) -> Result<()> {
        if (200..300).contains(&response.status)
            || (response.status == 304 && route_name == "reactive.poll")
        {
            return Ok(());
        }
        // An unhealthy member answers liveness with HTTP 503 but keeps the
        // canonical typed HealthResponse body. Accept the status so callers
        // can inspect `healthy` instead of losing the producer's response.
        if response.status == 503 && matches!(route_name, "regions.health" | "regions.status") {
            return Ok(());
        }
        if response.status == 421 {
            let placement = response.header(PLACEMENT_HEADER).map(str::to_owned);
            if let Some(router) = &self.router {
                // The member is authoritative (ADR 0043 §2): adopt its
                // placement into the scope's routing cache and schedule a
                // corrective `stale=1` rediscovery (ADR 0050 §5.4).
                if let Some(database) = database {
                    router.note_placement(
                        database,
                        &parse_placement_regions(placement.as_deref()),
                        0,
                    );
                }
            } else if self.config.region_affinity() != RegionAffinity::Strict {
                if let Some(region) = placement.as_deref().and_then(parse_region_header) {
                    if let Ok(mut guard) = self.placement.write() {
                        *guard = Some(region);
                    }
                }
            }
            let mut error = decode_error(route_name, response);
            error.placement = placement;
            return Err(error);
        }
        Err(decode_error(route_name, response))
    }

    /// Resolve the origin for one attempt when discovery is active (ADR
    /// 0050 §1/§8). `routing_database` carries the marker-tagged database for
    /// routes whose wire path has none. Returns the scope that served the
    /// attempt so failures can demote the regional endpoint.
    async fn route(&self, request: &mut Request, routing_database: Option<&str>) -> Result<Routed> {
        let Some(router) = &self.router else {
            return Ok(Routed::default());
        };
        // Only requests the SDK built against the configured product origin
        // are re-routable; advertised reactive delivery URLs keep their own
        // origin (ADR 0050 §5.7).
        let base = self.config.api_base_url();
        let class = match request
            .url
            .strip_prefix(base)
            .filter(|rest| rest.starts_with('/'))
        {
            None => return Ok(Routed::default()),
            Some(path) => {
                match routing_database.filter(|database| discovery::is_database_name(database)) {
                    Some(database) => RouteKind::Database(database.to_owned()),
                    // Advertised reactive delivery URLs keep their own origin.
                    None if path.starts_with("/api/v1/reactive/") => return Ok(Routed::default()),
                    None => discovery::classify_path(path),
                }
            }
        };
        match class {
            RouteKind::Database(database) => {
                let origin = match router
                    .resolve(&self.transport, &database, needs_write(request))
                    .await
                {
                    Some((origin, region)) => {
                        rebase(request, &origin)?;
                        return Ok(Routed {
                            database: Some(database),
                            region: Some(region),
                        });
                    }
                    None => router
                        .catalog_base(&self.transport)
                        .await
                        .map_err(routing_unavailable)?,
                };
                rebase(request, &origin)?;
                Ok(Routed {
                    database: Some(database),
                    region: None,
                })
            }
            RouteKind::Catalog => {
                // `/api/v1/regions` exactly is the catalog itself, served by
                // the discovery origin, never the product origin.
                rebase(request, router.atlas_origin())?;
                Ok(Routed::default())
            }
            // Product routes target the configured product origin.
            RouteKind::Unrouted => Ok(Routed::default()),
            RouteKind::MemberGlobal => {
                let origin = router
                    .catalog_base(&self.transport)
                    .await
                    .map_err(routing_unavailable)?;
                rebase(request, &origin)?;
                Ok(Routed::default())
            }
        }
    }

    /// Send one request through the shared pipeline: decoration, Atlas
    /// routing, bounded retries, and 421 placement repair. Read requests may
    /// retry bounded transient failures; mutations retry only when a stable
    /// idempotency key is present on the request.
    async fn dispatch(&self, mut request: Request) -> Result<Response> {
        let routing_database = request
            .headers
            .iter()
            .find(|(name, _)| name.eq_ignore_ascii_case(ROUTING_DATABASE_MARKER))
            .map(|(_, value)| value.clone());
        self.decorate(&mut request);
        let route = request.route_name.clone();
        let auth_endpoints = if route.starts_with("auth_idp.") {
            match &self.auth_router {
                Some(router) => Some(router.endpoints(&self.transport).await?),
                None => None,
            }
        } else {
            None
        };
        let mut auth_index = 0;
        let mut retries = 0;
        loop {
            let mut attempt = request.clone();
            if let Some(endpoints) = &auth_endpoints {
                rebase(&mut attempt, &endpoints[auth_index])?;
            }
            let routed = self
                .route(&mut attempt, routing_database.as_deref())
                .await?;
            let started = Instant::now();
            match self.transport.send(attempt).await {
                Ok(response) => {
                    if request.method == Method::Get
                        && auth_endpoints
                            .as_ref()
                            .is_some_and(|endpoints| auth_index + 1 < endpoints.len())
                        && matches!(response.status, 502..=504)
                    {
                        auth_index += 1;
                        continue;
                    }
                    routed.observe_rtt(&self.router, response.status, started.elapsed());
                    self.observe(&response);
                    match self.check(&route, &response, routed.database.as_deref()) {
                        Ok(()) => return Ok(response),
                        Err(error) => {
                            routed.demote_on(&self.router, &error);
                            if self.can_retry(&request, &error, retries) {
                                retries += 1;
                                self.wait_before_retry(retries, error.retry_after).await;
                            } else {
                                return Err(error);
                            }
                        }
                    }
                }
                Err(error) => {
                    if request.method == Method::Get
                        && auth_endpoints
                            .as_ref()
                            .is_some_and(|endpoints| auth_index + 1 < endpoints.len())
                    {
                        auth_index += 1;
                        continue;
                    }
                    routed.demote_on(&self.router, &error);
                    if self.can_retry(&request, &error, retries) {
                        retries += 1;
                        self.wait_before_retry(retries, None).await;
                    } else {
                        return Err(error);
                    }
                }
            }
        }
    }

    /// Send a request and decode a JSON body.
    pub async fn send_typed<T: DeserializeOwned>(&self, request: Request) -> Result<T> {
        let route = request.route_name.clone();
        let response = self.dispatch(request).await?;
        response.json_body().map_err(|mut e: Error| {
            e.code = format!("{}.invalid_response", route_to_code(&route));
            e
        })
    }

    /// Send a request and expect an empty success body, with the same bounded
    /// retry policy as [`Shared::send_typed`].
    pub async fn send_empty(&self, request: Request) -> Result<()> {
        self.dispatch(request).await.map(|_| ())
    }

    pub async fn send_response(&self, request: Request) -> Result<Response> {
        self.dispatch(request).await
    }

    fn can_retry(&self, request: &Request, error: &Error, retries: u8) -> bool {
        // Reactive polling owns its own attempt budget and deadline; applying
        // the general client retry budget here would multiply those bounds.
        // Secret rotation has no replay contract, even with an idempotency header.
        if matches!(
            request.route_name.as_str(),
            "reactive.poll" | "webhooks.rotate_secret" | "files.upload"
        ) {
            return false;
        }
        // A server-sent Retry-After longer than the sleep cap is returned to
        // the caller instead of retried.
        if error
            .retry_after
            .is_some_and(|delay| delay > MAX_RETRY_AFTER)
        {
            return false;
        }
        if retries >= self.config.max_retry_attempts() || !is_retryable(error) {
            return false;
        }
        // Belt and braces for the decorate-side exclusion: a ts-auth mutation
        // never becomes replay-safe, even if a caller smuggles a key header in.
        if is_mutation(request) && request.route_name.starts_with("auth_idp.") {
            return false;
        }
        !is_mutation(request)
            || request
                .headers
                .iter()
                .any(|(name, _)| name.eq_ignore_ascii_case("idempotency-key"))
    }

    /// Wait before the next attempt. A server-sent `Retry-After` (delays
    /// beyond [`MAX_RETRY_AFTER`] never reach this point; they surface as
    /// errors) wins; otherwise the delay is `retry_delay_ms * 2^(attempt -
    /// 1)`. The sleep is capped at [`MAX_RETRY_AFTER`].
    async fn wait_before_retry(&self, retries: u8, retry_after: Option<Duration>) {
        let delay = match retry_after {
            Some(delay) => delay,
            None => {
                let base = u64::from(self.config.retry_delay_ms());
                let shift = u32::from(retries.saturating_sub(1)).min(5);
                Duration::from_millis(base.saturating_mul(1_u64 << shift))
            }
        };
        tokio::time::sleep(delay.min(MAX_RETRY_AFTER)).await;
    }
}

fn route_to_code(route: &str) -> String {
    route.replace('.', "_")
}

/// The scope that served one dispatched attempt.
#[derive(Debug, Default, Clone)]
struct Routed {
    database: Option<String>,
    region: Option<String>,
}

impl Routed {
    /// A retryable failure against a discovered regional endpoint moves that
    /// region behind the eligible alternatives (ADR 0050 §5.3).
    fn demote_on(&self, router: &Option<Arc<Router>>, error: &Error) {
        if is_retryable(error) {
            if let (Some(router), Some(database), Some(region)) =
                (router, self.database.as_deref(), self.region.as_deref())
            {
                router.demote(database, region);
            }
        }
    }

    /// A successful round trip against a discovered regional endpoint feeds
    /// the per-region RTT tracker (ADR 0050 §2); the measurement goes to
    /// discovery ranking only, never onto a member request.
    fn observe_rtt(&self, router: &Option<Arc<Router>>, status: u16, elapsed: Duration) {
        if status < 500 {
            if let (Some(router), Some(region)) = (router, self.region.as_deref()) {
                router.observe_rtt(region, elapsed.as_secs_f64() * 1_000.0);
            }
        }
    }
}

/// Whether the operation needs a write-capable endpoint (ADR 0050 §5.3):
/// capability selection is method-based, unlike the idempotency gating in
/// [`is_mutation`].
fn needs_write(request: &Request) -> bool {
    matches!(
        request.method,
        Method::Post | Method::Put | Method::Patch | Method::Delete
    )
}

/// Replace a request's origin, keeping its path and query.
fn rebase(request: &mut Request, origin: &str) -> Result<()> {
    let suffix = request
        .url
        .split_once("://")
        .and_then(|(_, rest)| rest.find('/').map(|index| &rest[index..]))
        .unwrap_or("/");
    request.url = format!("{origin}{suffix}");
    Ok(())
}

/// Discovery and the region catalog both failed: member routes have no
/// regional endpoint left to try (ADR 0050 §5.8 — there is no proxy fallback).
fn routing_unavailable(cause: Error) -> Error {
    Error::new(
        ErrorKind::Unavailable,
        "routing_unavailable",
        format!("No TerraScale regional endpoint is available for the member route: {cause}"),
    )
}

fn is_mutation(request: &Request) -> bool {
    // POST is also used for read-only SQL and reactive view attachment. Those
    // requests must not claim a durable mutation identity. All document,
    // tenant, and management POSTs are durable mutations; PUT/PATCH/DELETE
    // are mutation methods by definition.
    if matches!(
        request.method,
        crate::transport::Method::Put
            | crate::transport::Method::Patch
            | crate::transport::Method::Delete
    ) {
        return true;
    }
    matches!(request.method, crate::transport::Method::Post)
        && !request.route_name.starts_with("sql.")
        && !request.route_name.starts_with("reactive.")
}

pub(crate) fn validate_idempotency_key(key: &str) -> Result<()> {
    if key.trim().is_empty() {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_idempotency_key",
            "Idempotency keys cannot be blank.",
        ));
    }
    if key.trim().len() > MAX_IDEMPOTENCY_KEY_LENGTH {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_idempotency_key",
            format!("Idempotency keys must not exceed {MAX_IDEMPOTENCY_KEY_LENGTH} characters."),
        ));
    }
    Ok(())
}

fn normalize_session_token(value: &str) -> Option<String> {
    let token = value.trim();
    if token.is_empty()
        || token.len() > 16_384
        || token.len() % 4 == 1
        || !token
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-'))
    {
        return None;
    }
    Some(token.to_owned())
}

fn parse_region_header(value: &str) -> Option<RegionCode> {
    value
        .split(',')
        .find_map(|candidate| candidate.trim().parse().ok())
}

/// All regions in a placement header, order preserved (home first, ADR 0043 §1).
fn parse_placement_regions(value: Option<&str>) -> Vec<RegionCode> {
    let Some(value) = value else {
        return Vec::new();
    };
    let mut regions = Vec::new();
    for candidate in value.split(',') {
        if let Ok(region) = candidate.trim().parse() {
            if !regions.contains(&region) {
                regions.push(region);
            }
        }
    }
    regions
}

#[derive(Debug, Default, serde::Deserialize)]
struct ErrorBody {
    #[serde(default)]
    code: Option<String>,
    #[serde(default)]
    message: Option<String>,
    #[serde(default)]
    fix: Option<String>,
}

/// Error envelopes seen on the wire, tried in order: TerraBase's nested
/// `{"error":{"code","message"}}`, ts-auth OAuth's flat RFC 6749 shape
/// `{"error","error_description"}`, and a bare top-level `{code,message}`.
#[derive(Debug, serde::Deserialize)]
#[serde(untagged)]
enum WireErrorEnvelope {
    Nested {
        error: ErrorBody,
    },
    Flat {
        error: String,
        #[serde(default)]
        error_description: Option<String>,
    },
    Bare(ErrorBody),
}

/// Decode the standard JSON ErrorResponse shape. Public within the crate so
/// direct-delivery transports report pre-stream failures identically.
pub(crate) fn decode_error(route_name: &str, response: &Response) -> Error {
    const MAX_ERROR_BODY_BYTES: usize = 64 * 1024;
    let envelope = (response.body.len() <= MAX_ERROR_BODY_BYTES)
        .then(|| serde_json::from_slice::<WireErrorEnvelope>(&response.body).ok())
        .flatten();
    let details = envelope.map(|envelope| match envelope {
        WireErrorEnvelope::Nested { error } => error,
        WireErrorEnvelope::Flat {
            error,
            error_description,
        } => ErrorBody {
            code: Some(error.clone()),
            message: error_description.or(Some(error)),
            fix: None,
        },
        WireErrorEnvelope::Bare(body) => body,
    });
    let code = details
        .as_ref()
        .and_then(|b| b.code.clone())
        .unwrap_or_else(|| route_to_code(route_name));
    let mut message = details
        .as_ref()
        .and_then(|b| b.message.clone())
        .unwrap_or_else(|| {
            format!(
                "Request {route_name} failed with status {}.",
                response.status
            )
        });
    let fix = details
        .as_ref()
        .and_then(|body| body.fix.clone())
        .filter(|fix| !fix.trim().is_empty())
        .map(|fix| fix.trim().to_owned());
    if let Some(fix) = fix.as_deref() {
        if !fix.trim().is_empty() {
            message.push_str(" Fix: ");
            message.push_str(fix.trim());
        }
    }
    let mut error = Error::for_status(response.status, code, message);
    error.fix = fix.map(String::into_boxed_str);
    error.request_id = response.header("x-request-id").map(str::to_owned);
    error.retry_after = response
        .header("retry-after")
        .and_then(parse_retry_after_seconds);
    error
}

/// Parse the seconds form of a `Retry-After` header; the HTTP-date form and
/// non-numeric values yield no delay. The value is stored uncapped on
/// [`Error::retry_after`]; the retry loop caps the sleep and returns delays
/// beyond [`MAX_RETRY_AFTER`] to the caller.
fn parse_retry_after_seconds(value: &str) -> Option<Duration> {
    let seconds: u64 = value.trim().parse().ok()?;
    Some(Duration::from_secs(seconds))
}

fn is_retryable(error: &Error) -> bool {
    // 520-524 are Cloudflare's origin-connectivity class (returned before the
    // request reaches the member); they demote and fail over like a
    // transport error (ADR 0050 §5.3).
    error.kind == ErrorKind::Transport
        || matches!(error.status, Some(408 | 429 | 500..=504 | 520..=524))
}

pub(crate) fn encode_path_segment(value: &str) -> String {
    const HEX: &[u8; 16] = b"0123456789ABCDEF";
    let mut encoded = String::with_capacity(value.len());
    for byte in value.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_' | b'~') {
            encoded.push(char::from(byte));
        } else {
            encoded.push('%');
            encoded.push(char::from(HEX[(byte >> 4) as usize]));
            encoded.push(char::from(HEX[(byte & 0x0f) as usize]));
        }
    }
    encoded
}

/// Root client. Build with [`ClientConfig::builder`] then branch into
/// `documents()`, `files()`, `sql()`, `reactive()`, `tenants()`, or
/// `management()`.
///
/// ```rust,no_run
/// use terrascale::{ClientConfig, RegionCode, TerraScaleClient};
///
/// # async fn example() -> terrascale::Result<()> {
/// let client = TerraScaleClient::new(
///     ClientConfig::builder()
///         .api_key("ts_test_key")
///         .preferred_region(RegionCode::Gru)
///         .build()?,
/// );
/// let _docs = client.documents();
/// # Ok(())
/// # }
/// ```
#[derive(Debug, Clone)]
pub struct TerraScaleClient {
    shared: SharedClient,
}

impl TerraScaleClient {
    /// Create a client with the production HTTPS transport.
    #[must_use]
    pub fn new(config: ClientConfig) -> Self {
        Self::with_transport(config, Arc::new(ReqwestTransport::new()))
    }

    /// Create a client with an injected transport (mocks, custom stacks).
    pub fn with_transport(config: ClientConfig, transport: Arc<dyn HttpTransport>) -> Self {
        Self {
            shared: Arc::new(Shared::new(config, transport)),
        }
    }

    /// Effective configuration.
    #[must_use]
    pub fn config(&self) -> &ClientConfig {
        &self.shared.config
    }

    /// Currently pinned region, if any.
    #[must_use]
    pub fn pinned_region(&self) -> Option<RegionCode> {
        self.shared.pinned_region()
    }

    /// Override the region pin directly (e.g. from out-of-band placement).
    pub fn pin_region(&self, region: RegionCode) {
        if let Ok(mut guard) = self.shared.placement.write() {
            *guard = Some(region);
        }
    }

    /// Current session token (causal frontier).
    #[must_use]
    pub fn session_token(&self) -> Option<String> {
        self.shared.session_token()
    }

    /// Install a session token.
    pub fn set_session_token(&self, token: Option<String>) {
        self.shared.set_session_token(token);
    }

    /// Build a client whose mutation requests use the caller-provided stable
    /// idempotency key. Reuse this client only for one logical mutation (or
    /// clear/change the key before starting another one).
    pub fn with_idempotency_key(self, key: impl Into<String>) -> Result<Self> {
        self.shared.set_idempotency_key(key)?;
        Ok(self)
    }

    /// Current caller-selected mutation identity, if configured.
    #[must_use]
    pub fn idempotency_key(&self) -> Option<String> {
        self.shared.idempotency_key()
    }

    /// Set a stable mutation identity on this shared client pipeline.
    pub fn set_idempotency_key(&self, key: impl Into<String>) -> Result<()> {
        self.shared.set_idempotency_key(key)
    }

    /// Clear the caller-selected mutation identity.
    pub fn clear_idempotency_key(&self) {
        self.shared.clear_idempotency_key();
    }
    /// Document CRUD.
    #[must_use]
    pub fn documents(&self) -> DocumentsClient {
        DocumentsClient::new(self.shared.clone())
    }

    /// Provider-independent file upload, metadata, and download.
    #[must_use]
    pub fn files(&self) -> FilesClient {
        FilesClient::new(self.shared.clone())
    }

    /// Typed + raw read-only SQL.
    #[must_use]
    pub fn sql(&self) -> SqlClient {
        SqlClient::new(self.shared.clone())
    }

    /// Reactive subscribe / execute, checkpoints, typed resets.
    #[must_use]
    pub fn reactive(&self) -> ReactiveClient {
        ReactiveClient::new(self.shared.clone())
    }

    /// Database-per-tenant provisioning and tenant keys.
    #[must_use]
    pub fn tenants(&self) -> TenantsClient {
        TenantsClient::new(self.shared.clone())
    }

    /// Database, schema, backup, export, usage, and region management.
    #[must_use]
    pub fn management(&self) -> ManagementClient {
        ManagementClient::new(self.shared.clone())
    }

    /// ts-auth (hosted IdP) management client for one instance slug. Requires
    /// `auth_base_url` on the config; management routes additionally require
    /// `auth_api_key`. ts-auth routes are not part of the TerraBase producer
    /// contract.
    pub fn auth_idp(&self, slug: &str) -> Result<AuthIdpClient> {
        AuthIdpClient::new(self.shared.clone(), slug)
    }
}
