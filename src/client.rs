//! Native document client and a credential-isolated authentication pipeline.

use crate::auth_idp::AuthIdpClient;
use crate::auth_routing::AuthRouter;
use crate::config::{ClientConfig, DEFAULT_API_BASE_URL};
use crate::discovery::{AtlasDiscovery, NativeRequirement, ScopeKey};
use crate::documents::{DatabaseScope, DocumentsClient};
use crate::error::{Error, ErrorKind, Result};
use crate::subscriptions::SubscriptionsClient;
use crate::transactions::TransactionsClient;
use crate::transport::{
    HttpTransport, Method, Request, ReqwestTransport, Response, MAX_JSON_BYTES,
};
use serde::de::DeserializeOwned;
use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

// Retained only as internal auth credential-isolation markers, never sent.
pub(crate) const SKIP_AUTH_MARKER: &str = "x-terrascale-sdk-skip-auth";
pub(crate) const SKIP_SESSION_MARKER: &str = "x-terrascale-sdk-skip-session";
const MAX_RETRY_AFTER: Duration = Duration::from_secs(30);

#[derive(Debug)]
pub(crate) struct Shared {
    transport: Arc<dyn HttpTransport>,
    config: ClientConfig,
    auth_router: Option<AuthRouter>,
    native_router: AtlasDiscovery,
    discovery_failed_until: Mutex<HashMap<ScopeKey, Instant>>,
}
pub(crate) type SharedClient = Arc<Shared>;

impl Shared {
    fn new(config: ClientConfig, transport: Arc<dyn HttpTransport>) -> Self {
        let auth_router = AuthRouter::for_config(
            config.auth_base_url(),
            config.auth_reverse_proxy(),
            config.discovery_base_url(),
        );
        Self {
            native_router: AtlasDiscovery::new(config.clone(), transport.clone()),
            discovery_failed_until: Mutex::new(HashMap::new()),
            transport,
            config,
            auth_router,
        }
    }
    pub(crate) fn config(&self) -> &ClientConfig {
        &self.config
    }

    pub(crate) async fn native_origin(
        &self,
        scope: &DatabaseScope,
        requirement: NativeRequirement,
    ) -> Result<String> {
        if let Some(tenant) = self.config.tenant_id() {
            if tenant != scope.tenant {
                return Err(Error::new(
                    ErrorKind::Authorization,
                    "scope_mismatch",
                    "Configured tenant does not match the native request scope.",
                ));
            }
        }
        if !self.config.discovery_enabled() {
            return Ok(self
                .config
                .api_base_url()
                .unwrap_or(DEFAULT_API_BASE_URL)
                .to_owned());
        }
        let scope_key = (scope.tenant, scope.database, scope.shard);
        if self
            .discovery_failed_until
            .lock()
            .ok()
            .is_some_and(|until| {
                until
                    .get(&scope_key)
                    .is_some_and(|until| Instant::now() < *until)
            })
        {
            return Ok(DEFAULT_API_BASE_URL.to_owned());
        }
        match self.native_router.resolve(scope, requirement).await {
            Ok(resolved) => Ok(resolved.endpoint.url),
            Err(error)
                if error.kind == ErrorKind::Transport
                    || error.kind == ErrorKind::InvalidResponse
                    || (error.status.is_some()
                        && error.code != "routing_observation_regressed") =>
            {
                if let Ok(mut until) = self.discovery_failed_until.lock() {
                    let now = Instant::now();
                    until.retain(|_, expires| *expires > now);
                    if until.len() >= 128 {
                        if let Some(oldest) = until
                            .iter()
                            .min_by_key(|(_, expires)| **expires)
                            .map(|(key, _)| *key)
                        {
                            until.remove(&oldest);
                        }
                    }
                    until.insert(scope_key, now + Duration::from_secs(1));
                }
                Ok(DEFAULT_API_BASE_URL.to_owned())
            }
            Err(error) => Err(error),
        }
    }

    async fn dispatch(
        &self,
        mut request: Request,
        native: Option<(&DatabaseScope, NativeRequirement)>,
    ) -> Result<Response> {
        let auth = request.route_name.starts_with("auth_idp.");
        let skip_auth = request
            .headers
            .iter()
            .any(|(name, _)| name.eq_ignore_ascii_case(SKIP_AUTH_MARKER));
        request.headers.retain(|(name, _)| {
            !name.eq_ignore_ascii_case(SKIP_AUTH_MARKER)
                && !name.eq_ignore_ascii_case(SKIP_SESSION_MARKER)
        });
        if !auth && !skip_auth {
            if let Some(key) = self.config.api_key() {
                request
                    .headers
                    .push(("authorization".into(), format!("Bearer {key}")));
            }
        }
        if !request
            .headers
            .iter()
            .any(|(name, _)| name.eq_ignore_ascii_case("accept"))
        {
            request
                .headers
                .push(("accept".into(), "application/json".into()));
        }
        if request
            .body
            .as_ref()
            .is_some_and(|body| body.len() > MAX_JSON_BYTES)
        {
            return Err(Error::new(
                ErrorKind::Validation,
                "request_capacity",
                "JSON request exceeds 4 MiB.",
            ));
        }
        let endpoints = if auth {
            match &self.auth_router {
                Some(router) => Some(router.endpoints(&self.transport).await?),
                None => None,
            }
        } else {
            None
        };
        let mut endpoint_index = 0;
        let mut retries = 0;
        let mut failed_origins = std::collections::HashSet::new();
        loop {
            let mut attempt = request.clone();
            let mut native_origin = None;
            if let Some((scope, requirement)) = native {
                let origin = self.native_origin(scope, requirement).await?;
                rebase(&mut attempt, &origin)?;
                native_origin = Some(origin);
            }
            if let Some(endpoints) = &endpoints {
                rebase(&mut attempt, &endpoints[endpoint_index])?;
            }
            // AuthIdpClient owns its public deadline and error code.
            let result = if auth {
                self.transport.send(attempt).await
            } else {
                tokio::time::timeout(Duration::from_secs(30), self.transport.send(attempt))
                    .await
                    .map_err(|_| Error::transport("Request deadline exceeded."))
                    .and_then(|result| result)
            }
            .and_then(|response| {
                if response.body.len() > MAX_JSON_BYTES {
                    return Err(response.invalid_response("JSON response exceeds 4 MiB."));
                }
                if (200..300).contains(&response.status) {
                    if request.route_name.starts_with("native.") && response.status != 200 {
                        return Err(response.invalid_response(format!(
                            "Native response requires HTTP 200, received {}.",
                            response.status
                        )));
                    }
                    Ok(response)
                } else {
                    Err(decode_error(&request.route_name, &response))
                }
            });
            match result {
                Ok(response) => return Ok(response),
                Err(error) => {
                    let safe = request.method == Method::Get
                        || matches!(
                            request.route_name.as_str(),
                            "native.query" | "native.subscription" | "native.transaction_status"
                        );
                    let routing_failure = error.kind == ErrorKind::Transport
                        || error
                            .status
                            .is_some_and(|status| status == 421 || status >= 500);
                    if routing_failure && self.config.discovery_enabled() {
                        if let (Some((scope, requirement)), Some(origin)) = (native, native_origin)
                        {
                            if failed_origins.insert(origin.clone()) {
                                let alternative = self
                                    .native_router
                                    .failover(scope, &origin, requirement)
                                    .await;
                                if alternative
                                    && safe
                                    && error
                                        .retry_after
                                        .is_none_or(|delay| delay <= MAX_RETRY_AFTER)
                                {
                                    if let Some(delay) = error.retry_after {
                                        tokio::time::sleep(delay).await;
                                    }
                                    continue;
                                }
                            }
                        }
                    }
                    // GET authentication discovery can fail over. Mutations never replay.
                    if request.method == Method::Get
                        && endpoints
                            .as_ref()
                            .is_some_and(|list| endpoint_index + 1 < list.len())
                        && is_retryable(&error)
                    {
                        endpoint_index += 1;
                        continue;
                    }
                    if !safe
                        || retries >= self.config.max_retry_attempts()
                        || !is_retryable(&error)
                        || error
                            .retry_after
                            .is_some_and(|delay| delay > MAX_RETRY_AFTER)
                    {
                        return Err(error);
                    }
                    retries += 1;
                    let delay = error.retry_after.unwrap_or_else(|| {
                        Duration::from_millis(
                            u64::from(self.config.retry_delay_ms())
                                .saturating_mul(1 << u32::from(retries - 1)),
                        )
                    });
                    tokio::time::sleep(delay.min(MAX_RETRY_AFTER)).await;
                }
            }
        }
    }

    pub(crate) async fn send_typed<T: DeserializeOwned>(&self, request: Request) -> Result<T> {
        let route = request.route_name.clone();
        self.dispatch(request, None)
            .await?
            .json_body()
            .map_err(|mut error| {
                error.code = format!("{}.invalid_response", route_to_code(&route));
                error
            })
    }
    pub(crate) async fn send_response(&self, request: Request) -> Result<Response> {
        self.dispatch(request, None).await
    }
    pub(crate) async fn send_native_typed<T: DeserializeOwned>(
        &self,
        scope: &DatabaseScope,
        requirement: NativeRequirement,
        request: Request,
    ) -> Result<T> {
        let route = request.route_name.clone();
        self.dispatch(request, Some((scope, requirement)))
            .await?
            .json_body()
            .map_err(|mut error| {
                error.code = format!("{}.invalid_response", route_to_code(&route));
                error
            })
    }
}

fn route_to_code(route: &str) -> String {
    route.replace('.', "_")
}

fn rebase(request: &mut Request, origin: &str) -> Result<()> {
    let url = reqwest::Url::parse(&request.url)
        .map_err(|_| Error::invalid_response("Invalid request URL."))?;
    let suffix = url.query().map_or_else(
        || url.path().to_owned(),
        |query| format!("{}?{query}", url.path()),
    );
    request.url = format!("{origin}{suffix}");
    Ok(())
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
    // Keep malformed responses terminal even when their HTTP status would
    // otherwise permit retry or endpoint failover.
    if error.kind == ErrorKind::InvalidResponse {
        return false;
    }
    // 520-524 are Cloudflare's origin-connectivity class (returned before the
    // request reaches the member); they demote and fail over like a
    // transport error (ADR 0050 §5.3).
    error.kind == ErrorKind::Transport
        || matches!(error.status, Some(408 | 429 | 500..=504 | 520..=524))
}

/// Native scoped documents, atomic transactions, subscriptions, and ts-auth.
#[derive(Debug, Clone)]
pub struct TerraScaleClient {
    shared: SharedClient,
}

impl TerraScaleClient {
    #[must_use]
    pub fn new(config: ClientConfig) -> Self {
        Self::with_transport(config, Arc::new(ReqwestTransport::new()))
    }
    pub fn with_transport(config: ClientConfig, transport: Arc<dyn HttpTransport>) -> Self {
        Self {
            shared: Arc::new(Shared::new(config, transport)),
        }
    }
    #[must_use]
    pub fn config(&self) -> &ClientConfig {
        &self.shared.config
    }
    #[must_use]
    pub fn discovery(&self) -> AtlasDiscovery {
        self.shared.native_router.clone()
    }
    #[must_use]
    pub fn documents(&self) -> DocumentsClient {
        DocumentsClient::new(self.shared.clone())
    }
    #[must_use]
    pub fn transactions(&self) -> TransactionsClient {
        TransactionsClient::new(self.shared.clone())
    }
    #[must_use]
    pub fn subscriptions(&self) -> SubscriptionsClient {
        SubscriptionsClient::new(self.shared.clone())
    }
    pub fn auth_idp(&self, slug: &str) -> Result<AuthIdpClient> {
        AuthIdpClient::new(self.shared.clone(), slug)
    }
}
