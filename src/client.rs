//! Client root and the shared request pipeline.
//!
//! [`TerraScaleClient`] mirrors the `@terrascale/sdk` root: document CRUD,
//! typed SQL, reactive subscribe/execute, tenants, and management, with
//! region pinning, session tokens (read-your-writes + monotonic reads), and
//! `421` placement repair shared by every sub-client.

use std::sync::{Arc, RwLock};
use std::time::Duration;

use serde::de::DeserializeOwned;

use crate::config::ClientConfig;
use crate::documents::{DatabaseScope, DocumentsClient};
use crate::error::{Error, ErrorKind, Result};
use crate::management::ManagementClient;
use crate::reactive::ReactiveClient;
use crate::regions::{normalize_base_url, pin_base_url, RegionAffinity, RegionCode};
use crate::sql::SqlClient;
use crate::tenants::TenantsClient;
use crate::transport::{HttpTransport, Request, ReqwestTransport, Response};

/// REST root for the public API (GOAL D-34).
pub const API_ROOT: &str = "/api/v1";

/// Placement header returned with a 421 (GOAL D-58).
pub const PLACEMENT_HEADER: &str = "x-terrascale-placement";

/// Canonical session-frontier header used on public API requests.
pub const SESSION_HEADER: &str = "X-TerraScale-Session";

/// Maximum length accepted for a caller-provided idempotency key.
pub const MAX_IDEMPOTENCY_KEY_LENGTH: usize = 128;

/// Internal marker removed by [`Shared::decorate`] before transport. It lets
/// a per-request eventual-consistency option suppress the shared causal
/// session header without exposing an unsupported wire header.
pub(crate) const SKIP_SESSION_MARKER: &str = "x-terrascale-sdk-skip-session";

/// Shareable pipeline state behind every sub-client.
#[derive(Debug)]
pub(crate) struct Shared {
    transport: Arc<dyn HttpTransport>,
    config: ClientConfig,
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
        Self {
            transport,
            config,
            placement: RwLock::new(None),
            session_token: RwLock::new(None),
            idempotency_key: RwLock::new(None),
        }
    }

    /// Global (unpinned) base URL for first contact and global routes.
    pub fn global_base_url(&self) -> String {
        format!("{}{API_ROOT}", self.config.api_base_url())
    }

    /// Base URL for pinned REST traffic, honoring a per-request hint, the
    /// repaired 421 placement, the preferred region, and affinity.
    ///
    /// An explicit hint or a learned 421 placement pins directly. Otherwise a
    /// preferred region pins under strict affinity and uses first contact
    /// under failover (the proxy routes, then a 421 teaches the pin).
    pub fn pinned_base_url(&self, region_hint: Option<RegionCode>) -> Result<String> {
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

    fn decorate(&self, request: &mut Request) {
        let skip_session = request
            .headers
            .iter()
            .any(|(name, _)| name.eq_ignore_ascii_case(SKIP_SESSION_MARKER));
        request
            .headers
            .retain(|(name, _)| !name.eq_ignore_ascii_case(SKIP_SESSION_MARKER));
        if let Some(api_key) = self.config.api_key() {
            request
                .headers
                .push(("authorization".to_owned(), format!("Bearer {api_key}")));
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
        if (self.config.preferred_region().is_some() || self.pinned_region().is_some())
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
        if is_mutation(request)
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
        // First-contact responses identify the member that served the
        // request. Learn that region only when no configured preference or
        // repaired placement exists; a strict/preferred pin must not be
        // silently replaced by an informational response header.
        if (200..300).contains(&response.status)
            && self.config.preferred_region().is_none()
            && self.pinned_region().is_none()
        {
            let region = response
                .header("x-terrascale-serving-region")
                .or_else(|| response.header("x-terrascale-region"))
                .and_then(|value| value.trim().parse().ok());
            if let Some(region) = region {
                if let Ok(mut guard) = self.placement.write() {
                    *guard = Some(region);
                }
            }
        }
        if let Some(placement) = response.header(PLACEMENT_HEADER) {
            if let Some(region) = parse_region_header(placement) {
                if let Ok(mut guard) = self.placement.write() {
                    *guard = Some(region);
                }
            }
        }
    }

    fn should_advertise_region_hint(&self, request: &Request) -> bool {
        if self.config.region_affinity() != RegionAffinity::Strict {
            return true;
        }
        // A strict request to a first-party regional host is already routed
        // directly. Custom proxies still need the typed hint header.
        let Some(host) = reqwest::Url::parse(&request.url)
            .ok()
            .and_then(|url| url.host_str().map(str::to_ascii_lowercase))
        else {
            return true;
        };
        !matches!(
            host.as_str(),
            "api.terrascale.tech"
                | "gru.api.terrascale.tech"
                | "sfo.api.terrascale.tech"
                | "fra.api.terrascale.tech"
        )
    }

    fn check(&self, route_name: &str, response: &Response) -> Result<()> {
        if (200..300).contains(&response.status)
            || (response.status == 304 && route_name == "reactive.poll")
        {
            return Ok(());
        }
        if response.status == 421 {
            let placement = response.header(PLACEMENT_HEADER).map(str::to_owned);
            if let Some(ref placement) = placement {
                if let Some(region) = parse_region_header(placement) {
                    if let Ok(mut guard) = self.placement.write() {
                        *guard = Some(region);
                    }
                }
            }
            let mut error = Error::for_status(
                421,
                "misdirected_region".to_owned(),
                "The region does not host this database; the SDK repaired its pin from X-TerraScale-Placement. Retry the request.".to_owned(),
            );
            error.placement = placement;
            return Err(error);
        }
        Err(decode_error(route_name, response))
    }

    /// Send a request and decode a JSON body. Read requests may retry bounded
    /// transient failures; mutations retry only when a stable idempotency key
    /// is present on the request.
    pub async fn send_typed<T: DeserializeOwned>(&self, mut request: Request) -> Result<T> {
        self.decorate(&mut request);
        let route = request.route_name.clone();
        let mut retries = 0;
        loop {
            match self.transport.send(request.clone()).await {
                Ok(response) => {
                    self.observe(&response);
                    match self.check(&route, &response) {
                        Ok(()) => {
                            return response.json_body().map_err(|mut e: Error| {
                                e.code = format!("{}.invalid_response", route_to_code(&route));
                                e
                            });
                        }
                        Err(error) if self.can_retry(&request, &error, retries) => {
                            retries += 1;
                            self.wait_before_retry(retries).await;
                        }
                        Err(error) => return Err(error),
                    }
                }
                Err(error) if self.can_retry(&request, &error, retries) => {
                    retries += 1;
                    self.wait_before_retry(retries).await;
                }
                Err(error) => return Err(error),
            }
        }
    }

    /// Send a request and expect an empty success body, with the same bounded
    /// retry policy as [`Shared::send_typed`].
    pub async fn send_empty(&self, mut request: Request) -> Result<()> {
        self.decorate(&mut request);
        let route = request.route_name.clone();
        let mut retries = 0;
        loop {
            match self.transport.send(request.clone()).await {
                Ok(response) => {
                    self.observe(&response);
                    match self.check(&route, &response) {
                        Ok(()) => return Ok(()),
                        Err(error) if self.can_retry(&request, &error, retries) => {
                            retries += 1;
                            self.wait_before_retry(retries).await;
                        }
                        Err(error) => return Err(error),
                    }
                }
                Err(error) if self.can_retry(&request, &error, retries) => {
                    retries += 1;
                    self.wait_before_retry(retries).await;
                }
                Err(error) => return Err(error),
            }
        }
    }

    fn can_retry(&self, request: &Request, error: &Error, retries: u8) -> bool {
        // Reactive polling owns its own attempt budget and deadline; applying
        // the general client retry budget here would multiply those bounds.
        if request.route_name == "reactive.poll" {
            return false;
        }
        if retries >= self.config.max_retry_attempts() || !is_retryable(error) {
            return false;
        }
        !is_mutation(request)
            || request
                .headers
                .iter()
                .any(|(name, _)| name.eq_ignore_ascii_case("idempotency-key"))
    }

    async fn wait_before_retry(&self, retries: u8) {
        let delay = u64::from(self.config.retry_delay_ms()) * u64::from(retries);
        tokio::time::sleep(Duration::from_millis(delay)).await;
    }
}

fn route_to_code(route: &str) -> String {
    route.replace('.', "_")
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

fn validate_idempotency_key(key: &str) -> Result<()> {
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

#[derive(Debug, Default, serde::Deserialize)]
struct ErrorBody {
    #[serde(default)]
    code: Option<String>,
    #[serde(default)]
    message: Option<String>,
    #[serde(default)]
    fix: Option<String>,
}

#[derive(Debug, Default, serde::Deserialize)]
struct ErrorResponse {
    #[serde(default)]
    error: Option<ErrorBody>,
    #[serde(flatten)]
    body: ErrorBody,
}

fn decode_error(route_name: &str, response: &Response) -> Error {
    const MAX_ERROR_BODY_BYTES: usize = 64 * 1024;
    let body = (response.body.len() <= MAX_ERROR_BODY_BYTES)
        .then(|| serde_json::from_slice::<ErrorResponse>(&response.body).ok())
        .flatten();
    let details = body
        .as_ref()
        .and_then(|body| body.error.as_ref())
        .or_else(|| body.as_ref().map(|body| &body.body));
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
    error
}

fn is_retryable(error: &Error) -> bool {
    error.kind == ErrorKind::Transport || matches!(error.status, Some(408 | 429 | 500..=504))
}

fn encode_path_segment(value: &str) -> String {
    let mut encoded = String::with_capacity(value.len());
    for byte in value.as_bytes() {
        if byte.is_ascii_alphanumeric() || b"-_.~".contains(byte) {
            encoded.push(char::from(*byte));
        } else {
            encoded.push_str(&format!("%{byte:02X}"));
        }
    }
    encoded
}

/// Root client. Build with [`ClientConfig::builder`] then branch into
/// `documents()`, `sql()`, `reactive()`, `tenants()`, or `management()`.
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
}
