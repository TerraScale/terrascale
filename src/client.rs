//! Native document client and a credential-isolated authentication pipeline.

use crate::auth_idp::AuthIdpClient;
use crate::auth_routing::AuthRouter;
use crate::config::ClientConfig;
use crate::discovery::{AtlasDiscovery, DiscoveryClock, NativeRequirement};
use crate::documents::{DatabaseScope, DocumentsClient};
use crate::error::{Error, ErrorKind, Result};
use crate::subscriptions::SubscriptionsClient;
use crate::transactions::TransactionsClient;
use crate::transport::{
    HttpTransport, Method, Request, ReqwestTransport, Response, MAX_JSON_BYTES,
};
use serde::de::DeserializeOwned;
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
};
use std::time::Duration;

// Retained only as internal auth credential-isolation markers, never sent.
pub(crate) const SKIP_AUTH_MARKER: &str = "x-terrascale-sdk-skip-auth";
pub(crate) const SKIP_SESSION_MARKER: &str = "x-terrascale-sdk-skip-session";
const MAX_RETRY_AFTER: Duration = Duration::from_secs(30);

/// One budget created by the public native caller, shared by all of its awaits.
pub(crate) fn native_deadline() -> tokio::time::Instant {
    tokio::time::Instant::now() + Duration::from_secs(30)
}
pub(crate) fn check_native_deadline(deadline: tokio::time::Instant) -> Result<()> {
    if tokio::time::Instant::now() >= deadline {
        return Err(Error::transport("Native operation deadline exceeded."));
    }
    Ok(())
}

/// Per-call witness set only when dispatch polls the native transport future.
/// It does not prove delivery or commit; errors after this point may be uncertain.
#[derive(Debug, Default)]
pub(crate) struct NativeSendPhase(AtomicBool);
impl NativeSendPhase {
    fn start(&self) {
        self.0.store(true, Ordering::Release);
    }
    pub(crate) fn has_started(&self) -> bool {
        self.0.load(Ordering::Acquire)
    }
}

#[derive(Debug)]
pub(crate) struct Shared {
    transport: Arc<dyn HttpTransport>,
    config: ClientConfig,
    auth_router: Option<AuthRouter>,
    native_router: AtlasDiscovery,
}
pub(crate) type SharedClient = Arc<Shared>;

impl Shared {
    fn new(
        config: ClientConfig,
        transport: Arc<dyn HttpTransport>,
        native_router: AtlasDiscovery,
    ) -> Self {
        let auth_router = AuthRouter::for_config(
            config.auth_base_url(),
            config.auth_reverse_proxy(),
            config.atlas_base_url(),
        );
        Self {
            native_router,
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
        deadline: tokio::time::Instant,
    ) -> Result<String> {
        check_native_deadline(deadline)?;
        if let Some(tenant) = self.config.tenant_id() {
            if tenant != scope.tenant {
                return Err(Error::new(
                    ErrorKind::Authorization,
                    "scope_mismatch",
                    "Configured tenant does not match the native request scope.",
                ));
            }
        }
        match self.config.api_base_url() {
            Some(origin) => Ok(origin.to_owned()),
            None => Ok(self
                .native_router
                .resolve_until(scope, requirement, deadline)
                .await?
                .endpoint
                .url),
        }
    }

    async fn dispatch(
        &self,
        mut request: Request,
        native: Option<(&DatabaseScope, NativeRequirement, tokio::time::Instant)>,
        send_phase: Option<&NativeSendPhase>,
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
        loop {
            let mut attempt = request.clone();
            if let Some((scope, requirement, deadline)) = native {
                let origin = self.native_origin(scope, requirement, deadline).await?;
                rebase(&mut attempt, &origin)?;
                check_native_deadline(deadline)?;
            }
            if let Some(endpoints) = &endpoints {
                rebase(&mut attempt, &endpoints[endpoint_index])?;
            }
            // AuthIdpClient owns its public deadline and error code.
            // Native callers share the same outer deadline across preflight,
            // resolver wait/fetch, every dispatch and retry delay.
            let result = if auth || native.is_some() {
                let mut send = self.transport.send(attempt);
                if let Some(phase) = send_phase {
                    std::future::poll_fn(|context| {
                        phase.start();
                        std::future::Future::poll(send.as_mut(), context)
                    })
                    .await
                } else {
                    send.await
                }
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
                    let safe = request.method == Method::Get
                        || matches!(
                            request.route_name.as_str(),
                            "native.query" | "native.subscription" | "native.transaction_status"
                        );
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
        self.dispatch(request, None, None)
            .await?
            .json_body()
            .map_err(|mut error| {
                error.code = format!("{}.invalid_response", route_to_code(&route));
                error
            })
    }
    pub(crate) async fn send_response(&self, request: Request) -> Result<Response> {
        self.dispatch(request, None, None).await
    }
    pub(crate) async fn send_native_typed<T: DeserializeOwned>(
        &self,
        scope: &DatabaseScope,
        requirement: NativeRequirement,
        request: Request,
        deadline: tokio::time::Instant,
    ) -> Result<T> {
        self.send_native_typed_with_phase(scope, requirement, request, deadline, None)
            .await
    }
    pub(crate) async fn send_native_typed_with_phase<T: DeserializeOwned>(
        &self,
        scope: &DatabaseScope,
        requirement: NativeRequirement,
        request: Request,
        deadline: tokio::time::Instant,
        send_phase: Option<&NativeSendPhase>,
    ) -> Result<T> {
        check_native_deadline(deadline)?;
        let route = request.route_name.clone();
        let response = tokio::time::timeout_at(
            deadline,
            self.dispatch(request, Some((scope, requirement, deadline)), send_phase),
        )
        .await
        .map_err(|_| Error::transport("Native operation deadline exceeded."))??;
        response.json_body().map_err(|mut error| {
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
    // A caller may opt into bounded safe retries for transport/connectivity
    // errors. Each retry selects the current receiver rank without demotion.
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
        let router = AtlasDiscovery::new(config.clone(), transport.clone());
        Self {
            shared: Arc::new(Shared::new(config, transport, router)),
        }
    }
    /// Use a caller-owned transport and discovery clock. The clock must advance
    /// consistently in both wall and monotonic time; injection alone does not
    /// establish healthy-clock provenance for a deployed client.
    pub fn with_transport_and_clock(
        config: ClientConfig,
        transport: Arc<dyn HttpTransport>,
        clock: Arc<dyn DiscoveryClock>,
    ) -> Self {
        let router = AtlasDiscovery::with_clock(config.clone(), transport.clone(), clock);
        Self {
            shared: Arc::new(Shared::new(config, transport, router)),
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
