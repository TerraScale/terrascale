//! Durable change-stream webhooks (S-07, D-39, D-64).
//!
//! This module deliberately contains no storage or networking implementation.
//! Production composition must supply all five ports.  In particular, a
//! missing port is a hard error; this surface never quietly substitutes an
//! in-memory queue, an in-process secret, or a direct URL client.

use std::collections::BTreeMap;
use std::fmt;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use axum::Router;
use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::http::header::{CONTENT_TYPE, HeaderMap, HeaderValue};
use axum::http::{Method, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{delete, get, post};
use serde::{Deserialize, Serialize};

/// Maximum endpoint URL length accepted by the service.
pub const MAX_ENDPOINT_BYTES: usize = 2_048;
/// Maximum webhook request body accepted by the HTTP router.
pub const MAX_WEBHOOK_BODY_BYTES: usize = 64 * 1024;
/// Maximum durable publication body sent to a webhook.
pub const MAX_DELIVERY_BYTES: usize = 1_048_576;
/// Maximum publications read in one journal page.
pub const MAX_JOURNAL_PAGE: usize = 256;
/// Maximum attempts, including the first delivery attempt.
pub const MAX_ATTEMPTS: u32 = 8;
/// The retry horizon.  A publication never remains retryable forever.
pub const RETRY_HORIZON_SECONDS: u64 = 24 * 60 * 60;
/// The largest retry delay.
pub const MAX_RETRY_DELAY_SECONDS: u64 = 60 * 60;

/// The authenticated tenant/project/database scope of one API operation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TenantScope {
    /// Tenant identity from the authenticated control-plane principal.
    pub tenant: String,
    /// Project identity from the authenticated control-plane principal.
    pub project: String,
    /// Database named by the route.
    pub database: String,
}

impl TenantScope {
    /// Creates a scope, rejecting empty identities.
    pub fn new(
        tenant: impl Into<String>,
        project: impl Into<String>,
        database: impl Into<String>,
    ) -> Result<Self, WebhookError> {
        let scope =
            Self { tenant: tenant.into(), project: project.into(), database: database.into() };
        if scope.tenant.is_empty() || scope.project.is_empty() || scope.database.is_empty() {
            return Err(WebhookError::invalid_request("tenant scope is incomplete"));
        }
        Ok(scope)
    }
}

/// A webhook endpoint as stored durably.  Secrets are never part of this
/// value, serialized responses, delivery logs, or error messages.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct WebhookEndpoint {
    /// Stable durable endpoint identity.
    pub id: String,
    /// Canonical HTTPS destination.
    pub endpoint: String,
    /// Creation time in unix seconds.
    pub created_at: u64,
    /// Whether new deliveries are disabled by deletion.
    pub deleted: bool,
}

/// Input for endpoint creation.
#[derive(Clone, Debug, Deserialize)]
pub struct CreateWebhook {
    /// HTTPS destination.  `url` is accepted for old clients.
    #[serde(alias = "url")]
    pub endpoint: String,
}

/// One durable publication from the shared revision/journal stream.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Publication {
    /// Stable journal sequence, used as the cursor and part of idempotency.
    pub revision: u64,
    /// Stable operation identity from the publication journal.
    pub event_id: String,
    /// Canonical revision bytes, already encoded by the shared view engine.
    pub body: Vec<u8>,
}

/// Durable delivery state and the latest redacted outcome.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct DeliveryRecord {
    /// Stable idempotency identity (`endpoint/event`).
    pub id: String,
    /// Publication event identity.
    pub event_id: String,
    /// Journal revision.
    pub revision: u64,
    /// Number of attempts made.
    pub attempts: u32,
    /// Next eligible attempt, or zero when terminal/succeeded.
    pub next_attempt_at: u64,
    /// Last HTTP status, if the transport received one.
    pub status: Option<u16>,
    /// `true` after a 2xx response.
    pub delivered: bool,
    /// `true` when no further retries will be attempted.
    pub terminal: bool,
}

/// A durable delivery log page.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct DeliveryLog {
    /// Entries in durable attempt order.
    pub deliveries: Vec<DeliveryRecord>,
}

/// A redacted durable-store failure.  Implementations may retain detailed
/// diagnostics in private structured logs, but must not return them here.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StorageError {
    /// The durable store is unavailable or rejected an operation.
    Unavailable,
    /// The named record does not exist.
    NotFound,
    /// An idempotency conflict was detected.
    Conflict,
}

/// A redacted secret-provider failure.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SecretError {
    /// Secret provider is unavailable or denied placement.
    Unavailable,
    /// No secret exists for the endpoint.
    NotFound,
}

/// A redacted network delivery failure.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DeliveryError {
    /// The target failed the transport's DNS/network egress policy.
    TargetRejected,
    /// The request could not be completed; it may be retried.
    Retryable,
    /// The transport refused the request permanently.
    Permanent,
}

/// Durable endpoint and delivery state port.  This must be backed by SQLite
/// in production; the service does not provide a memory implementation.
pub trait WebhookStorage: Send + Sync + 'static {
    /// Creates one endpoint durably and atomically with its stable id.
    fn create_endpoint(
        &self,
        scope: &TenantScope,
        endpoint: &str,
        now: u64,
    ) -> Result<WebhookEndpoint, StorageError>;
    /// Removes an endpoint during create rollback or explicit deletion.
    fn delete_endpoint(
        &self,
        scope: &TenantScope,
        id: &str,
    ) -> Result<WebhookEndpoint, StorageError>;
    /// Finds one non-secret endpoint.
    fn endpoint(
        &self,
        scope: &TenantScope,
        id: &str,
    ) -> Result<Option<WebhookEndpoint>, StorageError>;
    /// Lists endpoints, including deleted records when the caller asks for them.
    fn list_endpoints(
        &self,
        scope: &TenantScope,
        include_deleted: bool,
    ) -> Result<Vec<WebhookEndpoint>, StorageError>;
    /// Ensures one durable delivery row exists.  Repeated calls return the
    /// same row and therefore provide idempotency after process crashes.
    fn ensure_delivery(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        publication: &Publication,
        now: u64,
    ) -> Result<DeliveryRecord, StorageError>;
    /// Persists the result of one attempt before any subsequent attempt.
    fn record_attempt(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        delivery_id: &str,
        attempts: u32,
        next_attempt_at: u64,
        status: Option<u16>,
        delivered: bool,
        terminal: bool,
    ) -> Result<(), StorageError>;
    /// Reads the durable delivery log.
    fn delivery_log(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        limit: usize,
    ) -> Result<DeliveryLog, StorageError>;
    /// Marks one delivery eligible immediately for a replay.
    fn replay_delivery(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        delivery_id: &str,
        now: u64,
    ) -> Result<DeliveryRecord, StorageError>;
}

/// Durable shared publication journal port.  The page must be derived from
/// the same revision line used by HTTP views and WebSockets.
pub trait WebhookJournal: Send + Sync + 'static {
    /// Reads publications strictly after `cursor` for one database.
    fn page(
        &self,
        scope: &TenantScope,
        cursor: u64,
        limit: usize,
    ) -> Result<Vec<Publication>, StorageError>;
}

/// Placement-aware secret port.  Secret bytes must live in the configured
/// secret manager/Vault and never in endpoint rows or this service.
pub trait WebhookSecrets: Send + Sync + 'static {
    /// Provisions a secret after durable endpoint creation.
    fn provision(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        region: &str,
    ) -> Result<(), SecretError>;
    /// Destroys a secret after durable endpoint deletion.
    fn destroy(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        region: &str,
    ) -> Result<(), SecretError>;
    /// Reads the secret only in an admitted placement.
    fn get(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        region: &str,
    ) -> Result<Vec<u8>, SecretError>;
}

/// D-64 endpoint guard port.  The implementation may adapt
/// [`crate::guards::AbuseGuards`].
pub trait WebhookGuard: Send + Sync + 'static {
    /// Reserves one endpoint identity.  Called only after durable creation.
    fn reserve(&self, scope: &TenantScope, endpoint: &str) -> Result<(), ()>;
    /// Releases an endpoint identity after durable deletion.
    fn release(&self, scope: &TenantScope, endpoint: &str);
}

/// Network egress port.  It owns DNS resolution and must reject every private,
/// loopback, link-local, metadata, and otherwise non-public destination after
/// resolution.  This explicit check prevents a DNS-to-private SSRF shortcut.
pub trait WebhookDelivery: Send + Sync + 'static {
    /// Validates the target and its resolved address policy before create.
    fn validate_target(&self, endpoint: &str) -> Result<(), DeliveryError>;
    /// Sends one signed request with bounded transport timeouts.
    fn send(
        &self,
        endpoint: &str,
        headers: &BTreeMap<String, String>,
        body: &[u8],
    ) -> Result<DeliveryResponse, DeliveryError>;
}

/// Network response used to classify retry behavior.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DeliveryResponse {
    /// HTTP response status.
    pub status: u16,
}

/// Injectable time source for retry scheduling and deterministic tests.
pub trait WebhookClock: Send + Sync + 'static {
    /// Current unix seconds.
    fn now(&self) -> u64;
}

/// System clock used by production composition.
#[derive(Clone, Copy, Debug, Default)]
pub struct SystemWebhookClock;

impl WebhookClock for SystemWebhookClock {
    fn now(&self) -> u64 {
        SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |duration| duration.as_secs())
    }
}

/// Authorization callback for the HTTP adapter.  Parent authentication must
/// provide this; absent or failing authorization denies the request.
pub trait WebhookAuthorizer: Send + Sync + 'static {
    /// Checks the authenticated tenant/project and requested action.
    fn authorize(
        &self,
        headers: &HeaderMap,
        method: &Method,
        database: &str,
    ) -> Result<TenantScope, WebhookError>;
}

fn authorize_scope(
    authorizer: &dyn WebhookAuthorizer,
    headers: &HeaderMap,
    method: &Method,
    database: &str,
) -> Result<TenantScope, WebhookError> {
    let scope = authorizer.authorize(headers, method, database)?;
    if scope.database == database { Ok(scope) } else { Err(WebhookError::forbidden()) }
}

/// Durable webhook service composed from explicit production ports.
pub struct WebhookService {
    storage: Arc<dyn WebhookStorage>,
    journal: Arc<dyn WebhookJournal>,
    secrets: Arc<dyn WebhookSecrets>,
    guard: Arc<dyn WebhookGuard>,
    delivery: Arc<dyn WebhookDelivery>,
    clock: Arc<dyn WebhookClock>,
    region: String,
}

impl WebhookService {
    /// Builds the service.  All ports and a non-empty placement region are
    /// mandatory, so a production caller cannot accidentally use a fallback.
    pub fn new(
        storage: Arc<dyn WebhookStorage>,
        journal: Arc<dyn WebhookJournal>,
        secrets: Arc<dyn WebhookSecrets>,
        guard: Arc<dyn WebhookGuard>,
        delivery: Arc<dyn WebhookDelivery>,
        clock: Arc<dyn WebhookClock>,
        region: impl Into<String>,
    ) -> Result<Self, WebhookError> {
        let region = region.into();
        if region.is_empty() {
            return Err(WebhookError::misconfigured());
        }
        Ok(Self { storage, journal, secrets, guard, delivery, clock, region })
    }

    /// Creates an HTTPS endpoint.  Durable create precedes secret provisioning
    /// and D-64 reservation; every later failure rolls back durable state.
    pub fn create(
        &self,
        scope: &TenantScope,
        input: &CreateWebhook,
    ) -> Result<WebhookEndpoint, WebhookError> {
        let endpoint = validate_endpoint(&input.endpoint)?;
        self.delivery.validate_target(&endpoint).map_err(WebhookError::delivery)?;
        let record = self
            .storage
            .create_endpoint(scope, &endpoint, self.clock.now())
            .map_err(WebhookError::storage)?;
        if let Err(error) = self.secrets.provision(scope, &record.id, &self.region) {
            let _ = self.storage.delete_endpoint(scope, &record.id);
            return Err(WebhookError::secret(error));
        }
        if self.guard.reserve(scope, &record.endpoint).is_err() {
            let _ = self.secrets.destroy(scope, &record.id, &self.region);
            let _ = self.storage.delete_endpoint(scope, &record.id);
            return Err(WebhookError::guard());
        }
        Ok(record)
    }

    /// Lists durable endpoints, omitting deleted records by default.
    pub fn list(&self, scope: &TenantScope) -> Result<Vec<WebhookEndpoint>, WebhookError> {
        self.storage.list_endpoints(scope, false).map_err(WebhookError::storage)
    }

    /// Deletes one endpoint durably and then releases its D-64 reservation.
    pub fn delete(&self, scope: &TenantScope, id: &str) -> Result<WebhookEndpoint, WebhookError> {
        let endpoint = self
            .storage
            .endpoint(scope, id)
            .map_err(WebhookError::storage)?
            .ok_or_else(WebhookError::not_found)?;
        let deleted = self.storage.delete_endpoint(scope, id).map_err(WebhookError::storage)?;
        self.guard.release(scope, &endpoint.endpoint);
        let _ = self.secrets.destroy(scope, id, &self.region);
        Ok(deleted)
    }

    /// Replays one durable delivery immediately, then performs it once.
    pub async fn replay(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        delivery_id: &str,
    ) -> Result<DeliveryRecord, WebhookError> {
        let now = self.clock.now();
        let record = self
            .storage
            .replay_delivery(scope, endpoint_id, delivery_id, now)
            .map_err(WebhookError::storage)?;
        self.attempt(scope, endpoint_id, &record, now).await?;
        self.storage.delivery_log(scope, endpoint_id, 1).map_err(WebhookError::storage).and_then(
            |log| {
                log.deliveries
                    .into_iter()
                    .find(|item| item.id == record.id)
                    .ok_or_else(WebhookError::not_found)
            },
        )
    }

    /// Consumes one durable journal page, creating idempotent delivery rows and
    /// attempting all rows due at `now`.  Re-running after a crash is safe.
    pub async fn dispatch(
        &self,
        scope: &TenantScope,
        cursor: u64,
    ) -> Result<DispatchReport, WebhookError> {
        let endpoints = self.list(scope)?;
        let publications =
            self.journal.page(scope, cursor, MAX_JOURNAL_PAGE).map_err(WebhookError::storage)?;
        let mut report = DispatchReport::default();
        for endpoint in endpoints {
            if endpoint.deleted {
                continue;
            }
            for publication in &publications {
                if publication.body.len() > MAX_DELIVERY_BYTES {
                    report.skipped = report.skipped.saturating_add(1);
                    continue;
                }
                let row = self
                    .storage
                    .ensure_delivery(scope, &endpoint.id, publication, self.clock.now())
                    .map_err(WebhookError::storage)?;
                if row.delivered || row.terminal || row.next_attempt_at > self.clock.now() {
                    continue;
                }
                self.attempt(scope, &endpoint.id, &row, self.clock.now()).await?;
                report.attempted = report.attempted.saturating_add(1);
            }
        }
        report.publications = publications.len();
        Ok(report)
    }

    /// Reads the durable delivery log for dashboard inspection.
    pub fn delivery_log(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        limit: usize,
    ) -> Result<DeliveryLog, WebhookError> {
        self.storage
            .delivery_log(scope, endpoint_id, limit.min(MAX_JOURNAL_PAGE))
            .map_err(WebhookError::storage)
    }

    async fn attempt(
        &self,
        scope: &TenantScope,
        endpoint_id: &str,
        row: &DeliveryRecord,
        now: u64,
    ) -> Result<(), WebhookError> {
        let endpoint = self
            .storage
            .endpoint(scope, endpoint_id)
            .map_err(WebhookError::storage)?
            .ok_or_else(WebhookError::not_found)?;
        if endpoint.deleted {
            return Ok(());
        }
        let secret =
            self.secrets.get(scope, endpoint_id, &self.region).map_err(WebhookError::secret)?;
        if secret.is_empty() {
            return Err(WebhookError::misconfigured());
        }
        let publication = self
            .journal
            .page(scope, row.revision.saturating_sub(1), 1)
            .map_err(WebhookError::storage)?
            .into_iter()
            .find(|item| item.revision == row.revision)
            .ok_or_else(WebhookError::not_found)?;
        let timestamp = now.to_string();
        let signature = sign_delivery(&secret, &timestamp, &publication.body);
        let mut headers = BTreeMap::new();
        headers.insert("content-type".into(), "application/msgpack".into());
        headers.insert("user-agent".into(), "TerraScale-Webhook/1".into());
        headers.insert("x-terrascale-webhook-id".into(), endpoint.id.clone());
        headers.insert("x-terrascale-webhook-event".into(), publication.event_id.clone());
        headers.insert("x-terrascale-webhook-timestamp".into(), timestamp);
        headers.insert("x-terrascale-webhook-signature".into(), format!("v1={signature}"));
        headers.insert("idempotency-key".into(), row.id.clone());
        let result = self.delivery.send(&endpoint.endpoint, &headers, &publication.body);
        let attempt = row.attempts.saturating_add(1);
        let (status, delivered, terminal, next) = match result {
            Ok(response) if (200..300).contains(&response.status) => {
                (Some(response.status), true, true, 0)
            }
            Ok(response) => retry_state(response.status, attempt, now),
            Err(DeliveryError::TargetRejected | DeliveryError::Permanent) => (None, false, true, 0),
            Err(DeliveryError::Retryable) => retry_state(0, attempt, now),
        };
        self.storage
            .record_attempt(scope, endpoint_id, &row.id, attempt, next, status, delivered, terminal)
            .map_err(WebhookError::storage)
    }
}

/// One dispatch result; delivery itself remains unbilled.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize)]
pub struct DispatchReport {
    /// Publications read from the shared journal.
    pub publications: usize,
    /// Attempts issued to the delivery port.
    pub attempted: usize,
    /// Oversized publications skipped safely.
    pub skipped: usize,
}

/// Public HTTP state used by the parent API router.
#[derive(Clone)]
pub struct WebhookHttpState {
    /// Durable webhook service.
    pub service: Arc<WebhookService>,
    /// Parent authentication/scope adapter.
    pub authorizer: Arc<dyn WebhookAuthorizer>,
}

/// Mounts list/create/delete plus delivery-log/replay routes into a parent
/// axum router.  The authorizer is mandatory and runs before any storage call.
#[must_use = "the returned router must be merged into the public API"]
pub fn router(service: Arc<WebhookService>, authorizer: Arc<dyn WebhookAuthorizer>) -> Router {
    let state = WebhookHttpState { service, authorizer };
    Router::new()
        .route("/api/v1/databases/{database}/webhooks", get(list_webhooks).post(create_webhook))
        .route("/api/v1/databases/{database}/webhooks/{id}", delete(delete_webhook))
        .route("/api/v1/databases/{database}/webhooks/{id}/deliveries", get(list_deliveries))
        .route(
            "/api/v1/databases/{database}/webhooks/{id}/deliveries/{delivery_id}/replay",
            post(replay_webhook),
        )
        .layer(axum::extract::DefaultBodyLimit::max(MAX_WEBHOOK_BODY_BYTES))
        .with_state(Arc::new(state))
}

/// Alias used by parent HTTP composition modules.
#[must_use = "the returned router must be merged into the public API"]
pub fn http_router(service: Arc<WebhookService>, authorizer: Arc<dyn WebhookAuthorizer>) -> Router {
    router(service, authorizer)
}

/// Parent-callable list handler.
pub async fn list_webhooks(
    State(state): State<Arc<WebhookHttpState>>,
    Path(database): Path<String>,
    headers: HeaderMap,
) -> Response {
    let scope = match authorize_scope(state.authorizer.as_ref(), &headers, &Method::GET, &database)
    {
        Ok(scope) => scope,
        Err(error) => return error.into_response(),
    };
    match state.service.list(&scope) {
        Ok(webhooks) => json_response(StatusCode::OK, &serde_json::json!({ "webhooks": webhooks })),
        Err(error) => error.into_response(),
    }
}

/// Parent-callable create handler.
pub async fn create_webhook(
    State(state): State<Arc<WebhookHttpState>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    body: Bytes,
) -> Response {
    let scope = match authorize_scope(state.authorizer.as_ref(), &headers, &Method::POST, &database)
    {
        Ok(scope) => scope,
        Err(error) => return error.into_response(),
    };
    let Ok(input) = serde_json::from_slice::<CreateWebhook>(&body) else {
        return WebhookError::invalid_request("webhook request is invalid").into_response();
    };
    match state.service.create(&scope, &input) {
        Ok(webhook) => json_response(StatusCode::CREATED, &webhook),
        Err(error) => error.into_response(),
    }
}

/// Parent-callable delete handler.
pub async fn delete_webhook(
    State(state): State<Arc<WebhookHttpState>>,
    Path((database, id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let scope =
        match authorize_scope(state.authorizer.as_ref(), &headers, &Method::DELETE, &database) {
            Ok(scope) => scope,
            Err(error) => return error.into_response(),
        };
    match state.service.delete(&scope, &id) {
        Ok(webhook) => json_response(StatusCode::OK, &webhook),
        Err(error) => error.into_response(),
    }
}

/// Parent-callable delivery-log handler.
pub async fn list_deliveries(
    State(state): State<Arc<WebhookHttpState>>,
    Path((database, id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let scope = match authorize_scope(state.authorizer.as_ref(), &headers, &Method::GET, &database)
    {
        Ok(scope) => scope,
        Err(error) => return error.into_response(),
    };
    match state.service.delivery_log(&scope, &id, MAX_JOURNAL_PAGE) {
        Ok(log) => json_response(StatusCode::OK, &log),
        Err(error) => error.into_response(),
    }
}

/// Parent-callable replay handler.
pub async fn replay_webhook(
    State(state): State<Arc<WebhookHttpState>>,
    Path((database, id, delivery_id)): Path<(String, String, String)>,
    headers: HeaderMap,
) -> Response {
    let scope = match authorize_scope(state.authorizer.as_ref(), &headers, &Method::POST, &database)
    {
        Ok(scope) => scope,
        Err(error) => return error.into_response(),
    };
    match state.service.replay(&scope, &id, &delivery_id).await {
        Ok(record) => json_response(StatusCode::OK, &record),
        Err(error) => error.into_response(),
    }
}

fn json_response<T: Serialize>(status: StatusCode, value: &T) -> Response {
    match serde_json::to_string(value) {
        Ok(body) => (status, [(CONTENT_TYPE, HeaderValue::from_static("application/json"))], body)
            .into_response(),
        Err(_) => WebhookError::misconfigured().into_response(),
    }
}

/// Typed public failure.  Detail is intentionally not exposed in the HTTP
/// response, preventing endpoint, secret, and network oracle leakage.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WebhookError {
    code: &'static str,
    status: StatusCode,
    detail: &'static str,
}

impl WebhookError {
    /// Constructs a malformed-request failure for an authorization adapter.
    pub fn invalid_request(detail: &'static str) -> Self {
        Self { code: "invalid_request", status: StatusCode::BAD_REQUEST, detail }
    }
    /// Constructs an authentication failure for an authorization adapter.
    pub fn unauthorized() -> Self {
        Self {
            code: "unauthorized",
            status: StatusCode::UNAUTHORIZED,
            detail: "authorization failed",
        }
    }
    /// Constructs a scope failure for an authorization adapter.
    pub fn forbidden() -> Self {
        Self {
            code: "forbidden",
            status: StatusCode::FORBIDDEN,
            detail: "authorization scope does not admit this database",
        }
    }
    fn not_found() -> Self {
        Self { code: "not_found", status: StatusCode::NOT_FOUND, detail: "webhook not found" }
    }
    fn guard() -> Self {
        Self {
            code: "webhook_limit",
            status: StatusCode::TOO_MANY_REQUESTS,
            detail: "webhook endpoint budget exceeded",
        }
    }
    fn misconfigured() -> Self {
        Self {
            code: "webhook_unavailable",
            status: StatusCode::SERVICE_UNAVAILABLE,
            detail: "webhook dependencies unavailable",
        }
    }
    fn storage(_: StorageError) -> Self {
        Self::misconfigured()
    }
    fn secret(_: SecretError) -> Self {
        Self::misconfigured()
    }
    fn delivery(_: DeliveryError) -> Self {
        Self {
            code: "endpoint_rejected",
            status: StatusCode::UNPROCESSABLE_ENTITY,
            detail: "endpoint rejected by egress policy",
        }
    }

    /// Stable machine-readable error code.
    #[must_use]
    pub const fn code(&self) -> &'static str {
        self.code
    }

    /// HTTP status returned by the adapter for this failure.
    #[must_use]
    pub const fn status(&self) -> StatusCode {
        self.status
    }
}

impl IntoResponse for WebhookError {
    fn into_response(self) -> Response {
        let body = serde_json::json!({ "error": { "code": self.code, "message": self.detail, "fix": "follow the webhook contract and retry when safe" } });
        json_response(self.status, &body)
    }
}

impl fmt::Display for WebhookError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(self.code)
    }
}

impl std::error::Error for WebhookError {}

/// Validates and canonicalizes an endpoint without performing DNS.  The
/// delivery port must perform the second, resolved-address policy check.
pub fn validate_endpoint(raw: &str) -> Result<String, WebhookError> {
    let endpoint = raw.trim();
    if endpoint.len() > MAX_ENDPOINT_BYTES
        || endpoint.is_empty()
        || !endpoint.is_ascii()
        || endpoint.chars().any(char::is_control)
        || endpoint.contains('#')
    {
        return Err(WebhookError::invalid_request("endpoint is invalid"));
    }
    let Some(authority_and_path) = endpoint.strip_prefix("https://") else {
        return Err(WebhookError::invalid_request("endpoint must use HTTPS"));
    };
    if authority_and_path.is_empty()
        || authority_and_path.starts_with('/')
        || authority_and_path.starts_with('?')
        || authority_and_path.starts_with('#')
    {
        return Err(WebhookError::invalid_request("endpoint authority is missing"));
    }
    let authority_end =
        authority_and_path.find(['/', '?', '#']).unwrap_or(authority_and_path.len());
    let authority = &authority_and_path[..authority_end];
    if authority.is_empty() || authority.contains('@') || authority.contains(char::is_whitespace) {
        return Err(WebhookError::invalid_request("endpoint authority is invalid"));
    }
    let (host, port) = parse_authority(authority)?;
    if port == Some(0) {
        return Err(WebhookError::invalid_request("endpoint port is invalid"));
    }
    if host.eq_ignore_ascii_case("localhost")
        || host.ends_with(".localhost")
        || host.ends_with(".local")
        || host.ends_with(".internal")
        || host.ends_with(".home.arpa")
    {
        return Err(WebhookError::invalid_request("endpoint host is not public"));
    }
    if host.starts_with('.')
        || host.ends_with('.')
        || host.starts_with('-')
        || host.ends_with('-')
        || host.contains("..")
    {
        return Err(WebhookError::invalid_request("endpoint host is invalid"));
    }
    if let Ok(address) = host.parse::<IpAddr>() {
        if !is_public_ip(address) {
            return Err(WebhookError::invalid_request("endpoint host is not public"));
        }
    }
    if host.bytes().any(|byte| {
        !(byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'[' | b']' | b':'))
    }) {
        return Err(WebhookError::invalid_request("endpoint host is invalid"));
    }
    Ok(endpoint.to_owned())
}

fn parse_authority(authority: &str) -> Result<(&str, Option<u16>), WebhookError> {
    if let Some(rest) = authority.strip_prefix('[') {
        let end = rest
            .find(']')
            .ok_or_else(|| WebhookError::invalid_request("endpoint host is invalid"))?;
        let host = &rest[..end];
        if host.parse::<Ipv6Addr>().is_err() {
            return Err(WebhookError::invalid_request("endpoint host is invalid"));
        }
        let suffix = &rest[end + 1..];
        let port = if suffix.is_empty() {
            None
        } else {
            Some(
                suffix
                    .strip_prefix(':')
                    .ok_or_else(|| WebhookError::invalid_request("endpoint port is invalid"))?
                    .parse()
                    .map_err(|_| WebhookError::invalid_request("endpoint port is invalid"))?,
            )
        };
        return Ok((host, port));
    }
    let (host, port) = if let Some((host, port)) = authority.rsplit_once(':') {
        let port =
            port.parse().map_err(|_| WebhookError::invalid_request("endpoint port is invalid"))?;
        (host, Some(port))
    } else {
        (authority, None)
    };
    if host.is_empty() || host.contains(':') {
        return Err(WebhookError::invalid_request("endpoint host is invalid"));
    }
    if host.bytes().all(|byte| byte.is_ascii_digit() || byte == b'.') {
        return Err(WebhookError::invalid_request("endpoint host is invalid"));
    }
    Ok((host, port))
}

fn is_public_ip(address: IpAddr) -> bool {
    match address {
        IpAddr::V4(ip) => {
            let octets = ip.octets();
            let shared = octets[0] == 100 && (64..=127).contains(&octets[1]);
            let benchmark = octets[0] == 198 && (18..=19).contains(&octets[1]);
            !shared
                && !benchmark
                && !ip.is_loopback()
                && !ip.is_link_local()
                && !ip.is_unspecified()
                && !ip.is_broadcast()
                && !is_ipv4_documentation(ip)
        }
        IpAddr::V6(ip) => {
            if let Some(mapped) = ip.to_ipv4_mapped() {
                return is_public_ip(IpAddr::V4(mapped));
            }
            let segments = ip.segments();
            !ip.is_loopback()
                && !ip.is_unspecified()
                && !(segments[0] & 0xfe00 == 0xfc00)
                && !(segments[0] & 0xffc0 == 0xfe80)
                && !ip.is_multicast()
                && !is_ipv6_documentation(ip)
        }
    }
}

fn is_ipv4_documentation(ip: Ipv4Addr) -> bool {
    ip.octets()[0] == 192 && ip.octets()[1] == 0 && ip.octets()[2] == 2
        || ip.octets()[0] == 198 && ip.octets()[1] == 51 && ip.octets()[2] == 100
        || ip.octets()[0] == 203 && ip.octets()[1] == 0 && ip.octets()[2] == 113
}
fn is_ipv6_documentation(ip: Ipv6Addr) -> bool {
    ip.segments()[0] == 0x2001 && ip.segments()[1] == 0x0db8
}

/// Computes the `v1` delivery MAC over `timestamp + "." + body`.
///
/// BLAKE3 keyed mode is used because it is already part of the server's
/// audited dependency set.  The secret is first normalized to BLAKE3's
/// fixed-size key, allowing secret managers to return any high-entropy key
/// size without weakening the MAC.
pub fn sign_delivery(secret: &[u8], timestamp: &str, body: &[u8]) -> String {
    let key = blake3::hash(secret);
    let mut message = Vec::with_capacity(timestamp.len() + 1 + body.len());
    message.extend_from_slice(timestamp.as_bytes());
    message.push(b'.');
    message.extend_from_slice(body);
    blake3::keyed_hash(key.as_bytes(), &message).to_hex().to_string()
}

fn retry_state(status: u16, attempt: u32, now: u64) -> (Option<u16>, bool, bool, u64) {
    if !matches!(status, 0 | 408 | 425 | 429) && !(500..600).contains(&status) {
        return (Some(status).filter(|value| *value != 0), false, true, 0);
    }
    if attempt >= MAX_ATTEMPTS {
        return (Some(status).filter(|value| *value != 0), false, true, 0);
    }
    let delay = 1_u64
        .checked_shl(attempt.saturating_sub(1))
        .unwrap_or(MAX_RETRY_DELAY_SECONDS)
        .min(MAX_RETRY_DELAY_SECONDS);
    let next = now.saturating_add(delay);
    if next > now.saturating_add(RETRY_HORIZON_SECONDS) {
        (Some(status).filter(|value| *value != 0), false, true, 0)
    } else {
        (Some(status).filter(|value| *value != 0), false, false, next)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn endpoint_policy_rejects_ssrf_forms() {
        for endpoint in [
            "http://example.com/hook",
            "https://localhost/hook",
            "https://127.0.0.1/hook",
            "https://10.0.0.1/hook",
            "https://[::1]/hook",
            "https://169.254.169.254/hook",
            "https://user@example.com/hook",
            "https://example.com:0/hook",
        ] {
            assert!(validate_endpoint(endpoint).is_err(), "{endpoint} must fail");
        }
        assert!(validate_endpoint("https://hooks.example.com/events").is_ok());
    }

    #[test]
    fn retry_schedule_is_bounded_and_terminal() {
        let first = retry_state(503, 1, 100);
        assert_eq!(first.3, 101);
        let last = retry_state(503, MAX_ATTEMPTS, 100);
        assert!(last.2);
        assert_eq!(retry_state(404, 1, 100).2, true);
    }

    #[test]
    fn signing_is_stable_and_body_bound() {
        let one = sign_delivery(b"secret", "42", br#"{"revision":1}"#);
        let two = sign_delivery(b"secret", "42", br#"{"revision":1}"#);
        assert_eq!(one, two);
        assert_eq!(one.len(), 64);
        assert_ne!(one, sign_delivery(b"other", "42", br#"{"revision":1}"#));
    }
}
