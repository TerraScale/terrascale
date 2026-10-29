//! Credential-isolated ts-auth client transport.

use crate::auth_idp::AuthIdpClient;
use crate::config::ClientConfig;
use crate::error::{Error, ErrorKind, Result};
use crate::transport::{
    HttpTransport, Method, Request, ReqwestTransport, Response, MAX_JSON_BYTES,
};
use serde::de::DeserializeOwned;
use std::sync::Arc;
use std::time::Duration;

// Retained only as internal auth credential-isolation markers, never sent.
pub(crate) const SKIP_AUTH_MARKER: &str = "x-terrascale-sdk-skip-auth";
pub(crate) const SKIP_SESSION_MARKER: &str = "x-terrascale-sdk-skip-session";
const MAX_RETRY_AFTER: Duration = Duration::from_secs(30);

#[derive(Debug)]
pub(crate) struct Shared {
    transport: Arc<dyn HttpTransport>,
    config: ClientConfig,
}
pub(crate) type SharedClient = Arc<Shared>;

impl Shared {
    fn new(config: ClientConfig, transport: Arc<dyn HttpTransport>) -> Self {
        Self { transport, config }
    }
    pub(crate) fn config(&self) -> &ClientConfig {
        &self.config
    }

    async fn dispatch(&self, mut request: Request) -> Result<Response> {
        let auth = request.route_name.starts_with("auth_idp.");
        request.headers.retain(|(name, _)| {
            !name.eq_ignore_ascii_case(SKIP_AUTH_MARKER)
                && !name.eq_ignore_ascii_case(SKIP_SESSION_MARKER)
        });
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
        let mut retries = 0;
        loop {
            let attempt = request.clone();
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
                    Ok(response)
                } else {
                    Err(decode_error(&request.route_name, &response))
                }
            });
            match result {
                Ok(response) => return Ok(response),
                Err(error) => {
                    let safe = request.method == Method::Get;
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
        self.dispatch(request)
            .await?
            .json_body()
            .map_err(|mut error| {
                error.code = format!("{}.invalid_response", route_to_code(&route));
                error
            })
    }
    pub(crate) async fn send_response(&self, request: Request) -> Result<Response> {
        self.dispatch(request).await
    }
}

fn route_to_code(route: &str) -> String {
    route.replace('.', "_")
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

/// The ts-auth client. TerraBase public APIs use [`crate::v1::Client`].
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
    pub fn auth_idp(&self, slug: &str) -> Result<AuthIdpClient> {
        AuthIdpClient::new(self.shared.clone(), slug)
    }
}
