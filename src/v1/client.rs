//! The v1 HTTPS client: configuration, retry policy and the request loop.

use std::fmt;
use std::net::IpAddr;
use std::sync::Arc;
use std::time::Duration;

use futures_util::StreamExt;
use reqwest::header::{HeaderMap, HeaderValue, AUTHORIZATION, CONTENT_TYPE, ETAG, RETRY_AFTER};
use reqwest::{Method, StatusCode, Url};
use serde::de::DeserializeOwned;
use serde::Serialize;

use super::control::ControlClient;
use super::documents::Database;
use super::errors::{self, Error, Result};
use super::ids::IdempotencyKey;

/// The server's request body limit (4 MiB).
pub const MAX_REQUEST_BYTES: usize = 4 << 20;
/// Default bound on one response body (64 MiB).
pub const DEFAULT_MAX_RESPONSE_BYTES: usize = 64 << 20;

/// Retry policy for the v1 client.
///
/// * `429 rate_limited` is always retried (the request was not admitted),
///   after `Retry-After` capped at `max_retry_after`.
/// * Transport errors and the transient 503s (`capacity`,
///   `storage_unavailable`, `dependencies_unavailable`, `commit_uncertain`)
///   are retried only when the request is replay-safe: reads, queries and
///   mutations carrying an `Idempotency-Key`.
/// * `500 internal_error` is retried only for requests with an
///   `Idempotency-Key`.
/// * `stored_corruption` and every other error are never retried.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RetryPolicy {
    /// Retries after the first attempt; 0 disables retries.
    pub max_retries: u32,
    /// First backoff delay; doubled per attempt, with jitter.
    pub base_delay: Duration,
    /// Cap on one backoff delay.
    pub max_delay: Duration,
    /// Cap on a server-provided `Retry-After`.
    pub max_retry_after: Duration,
}

impl Default for RetryPolicy {
    fn default() -> Self {
        Self {
            max_retries: 3,
            base_delay: Duration::from_millis(100),
            max_delay: Duration::from_secs(2),
            max_retry_after: Duration::from_secs(30),
        }
    }
}

impl RetryPolicy {
    /// A policy that never retries.
    #[must_use]
    pub fn none() -> Self {
        Self {
            max_retries: 0,
            ..Self::default()
        }
    }
}

/// Builds a [`Client`].
pub struct ClientBuilder {
    base_url: String,
    token: String,
    root_cas: Vec<Vec<u8>>,
    timeout: Option<Duration>,
    connect_timeout: Option<Duration>,
    retry: RetryPolicy,
    max_response_bytes: usize,
    user_agent: Option<String>,
}

impl fmt::Debug for ClientBuilder {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ClientBuilder")
            .field("base_url", &self.base_url)
            .field("token", &"<redacted>")
            .field("root_cas", &self.root_cas.len())
            .field("timeout", &self.timeout)
            .field("connect_timeout", &self.connect_timeout)
            .field("retry", &self.retry)
            .finish_non_exhaustive()
    }
}

impl ClientBuilder {
    /// `base_url` is the TerraBase origin (HTTPS; plain HTTP only for loopback
    /// hosts; a path prefix is kept). `token` is a `tbk1.<id>.<secret>` key: a
    /// data key for documents, a control key for [`ControlClient`].
    pub fn new(base_url: impl Into<String>, token: impl Into<String>) -> Self {
        Self {
            base_url: base_url.into(),
            token: token.into(),
            root_cas: Vec::new(),
            timeout: Some(Duration::from_secs(30)),
            connect_timeout: Some(Duration::from_secs(10)),
            retry: RetryPolicy::default(),
            max_response_bytes: DEFAULT_MAX_RESPONSE_BYTES,
            user_agent: None,
        }
    }

    /// Trusts an extra root CA (PEM; one or more certificates), for example a
    /// local development CA. The platform's web PKI roots stay trusted.
    #[must_use]
    pub fn add_root_ca_pem(mut self, pem: impl AsRef<[u8]>) -> Self {
        self.root_cas.push(pem.as_ref().to_vec());
        self
    }

    /// Per-attempt request timeout (default 30 s). `None` disables it.
    #[must_use]
    pub fn timeout(mut self, timeout: Option<Duration>) -> Self {
        self.timeout = timeout;
        self
    }

    /// Connect timeout (default 10 s). `None` disables it.
    #[must_use]
    pub fn connect_timeout(mut self, timeout: Option<Duration>) -> Self {
        self.connect_timeout = timeout;
        self
    }

    /// Retry policy (default [`RetryPolicy::default`]).
    #[must_use]
    pub fn retry_policy(mut self, retry: RetryPolicy) -> Self {
        self.retry = retry;
        self
    }

    /// Bound on one response body (default 64 MiB).
    #[must_use]
    pub fn max_response_bytes(mut self, bytes: usize) -> Self {
        self.max_response_bytes = bytes;
        self
    }

    /// Overrides the `User-Agent` header.
    #[must_use]
    pub fn user_agent(mut self, user_agent: impl Into<String>) -> Self {
        self.user_agent = Some(user_agent.into());
        self
    }

    /// Validates the configuration and builds the client.
    pub fn build(self) -> Result<Client> {
        let base = parse_base_url(&self.base_url)?;
        if !valid_token(&self.token) {
            return Err(Error::InvalidInput(
                "token must be a tbk1.<22-char id>.<43-char secret> key".into(),
            ));
        }
        let mut auth = HeaderValue::from_str(&format!("Bearer {}", self.token))
            .map_err(|_| Error::InvalidInput("token is not a valid header value".into()))?;
        auth.set_sensitive(true);
        let mut http = reqwest::Client::builder()
            .use_rustls_tls()
            .min_tls_version(reqwest::tls::Version::TLS_1_3)
            .redirect(reqwest::redirect::Policy::none())
            .user_agent(
                self.user_agent
                    .unwrap_or_else(|| format!("terrascale-rust/{} v1", env!("CARGO_PKG_VERSION"))),
            );
        if let Some(t) = self.timeout {
            http = http.timeout(t);
        }
        if let Some(t) = self.connect_timeout {
            http = http.connect_timeout(t);
        }
        for pem in &self.root_cas {
            let certs = reqwest::Certificate::from_pem_bundle(pem)
                .map_err(|e| Error::InvalidInput(format!("root CA PEM: {e}")))?;
            if certs.is_empty() {
                return Err(Error::InvalidInput(
                    "root CA PEM holds no certificate".into(),
                ));
            }
            for cert in certs {
                http = http.add_root_certificate(cert);
            }
        }
        let http = http
            .build()
            .map_err(|e| Error::InvalidInput(format!("HTTP client: {e}")))?;
        Ok(Client {
            inner: Arc::new(Inner {
                http,
                base,
                auth,
                retry: self.retry,
                max_response: self.max_response_bytes,
            }),
        })
    }

    /// Builds a [`ControlClient`]; the token must be a control key.
    pub fn build_control(self) -> Result<ControlClient> {
        Ok(ControlClient::new(self.build()?))
    }
}

fn valid_token(token: &str) -> bool {
    let mut parts = token.split('.');
    let b64 = |s: &str, n: usize| {
        s.len() == n
            && s.bytes()
                .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
    };
    matches!(
        (parts.next(), parts.next(), parts.next(), parts.next()),
        (Some("tbk1"), Some(id), Some(secret), None) if b64(id, 22) && b64(secret, 43)
    )
}

fn parse_base_url(raw: &str) -> Result<Url> {
    let mut url =
        Url::parse(raw).map_err(|e| Error::InvalidInput(format!("base URL {raw:?}: {e}")))?;
    if !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
    {
        return Err(Error::InvalidInput(
            "base URL must not carry user info, a query or a fragment".into(),
        ));
    }
    match url.scheme() {
        "https" => {}
        "http" => {
            let loopback = match url.host() {
                Some(url::Host::Domain(d)) => d == "localhost",
                Some(url::Host::Ipv4(ip)) => IpAddr::V4(ip).is_loopback(),
                Some(url::Host::Ipv6(ip)) => IpAddr::V6(ip).is_loopback(),
                None => false,
            };
            if !loopback {
                return Err(Error::InvalidInput(
                    "plain HTTP base URL is allowed only for loopback hosts".into(),
                ));
            }
        }
        other => {
            return Err(Error::InvalidInput(format!(
                "base URL scheme {other:?} is not https"
            )))
        }
    }
    if url.host().is_none() {
        return Err(Error::InvalidInput("base URL has no host".into()));
    }
    let trimmed = url.path().trim_end_matches('/').to_owned();
    url.set_path(&trimmed);
    Ok(url)
}

struct Inner {
    http: reqwest::Client,
    base: Url,
    auth: HeaderValue,
    retry: RetryPolicy,
    max_response: usize,
}

/// A v1 client for one key. Cheap to clone; safe for concurrent use.
#[derive(Clone)]
pub struct Client {
    inner: Arc<Inner>,
}

impl fmt::Debug for Client {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Client")
            .field("base_url", &self.inner.base.as_str())
            .field("retry", &self.inner.retry)
            .finish_non_exhaustive()
    }
}

impl Client {
    /// Starts a [`ClientBuilder`].
    pub fn builder(base_url: impl Into<String>, token: impl Into<String>) -> ClientBuilder {
        ClientBuilder::new(base_url, token)
    }

    /// A handle to the named database. Performs no I/O; the name is validated
    /// on each request.
    #[must_use]
    pub fn database(&self, name: impl Into<String>) -> Database {
        Database::new(self.clone(), name.into())
    }

    /// The control-plane API with this client's key (must be a control key).
    #[must_use]
    pub fn control(&self) -> ControlClient {
        ControlClient::new(self.clone())
    }

    /// The configured base URL.
    #[must_use]
    pub fn base_url(&self) -> &Url {
        &self.inner.base
    }
}

/// One logical request.
pub(crate) struct Req<'a> {
    pub method: Method,
    pub segments: Vec<&'a str>,
    pub query: Vec<(&'a str, String)>,
    pub body: Option<Vec<u8>>,
    pub content_type: Option<&'static str>,
    pub headers: Vec<(&'static str, String)>,
    pub idempotency_key: Option<&'a IdempotencyKey>,
    /// Reads and read-only POSTs that may be resent after a transport error.
    pub replay_safe: bool,
}

impl<'a> Req<'a> {
    pub fn new(method: Method, segments: Vec<&'a str>) -> Self {
        let replay_safe = method == Method::GET;
        Self {
            method,
            segments,
            query: Vec::new(),
            body: None,
            content_type: None,
            headers: Vec::new(),
            idempotency_key: None,
            replay_safe,
        }
    }

    pub fn json<T: Serialize + ?Sized>(mut self, body: &T) -> Result<Self> {
        self.body = Some(
            serde_json::to_vec(body)
                .map_err(|e| Error::InvalidInput(format!("encode request: {e}")))?,
        );
        Ok(self)
    }
}

/// A response with a 2xx or 304 status.
pub(crate) struct Resp {
    pub status: StatusCode,
    pub headers: HeaderMap,
    pub body: Vec<u8>,
}

impl Resp {
    pub fn decode<T: DeserializeOwned>(&self) -> Result<T> {
        serde_json::from_slice(&self.body).map_err(|e| Error::Decode(e.to_string()))
    }

    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers.get(name).and_then(|v| v.to_str().ok())
    }
}

enum Attempt {
    Done(Resp),
    Failed(Error, Option<Duration>),
}

impl Client {
    fn url(&self, segments: &[&str], query: &[(&str, String)]) -> Result<Url> {
        for seg in segments {
            if seg.is_empty() || *seg == "." || *seg == ".." {
                return Err(Error::InvalidInput(format!(
                    "invalid path parameter {seg:?}"
                )));
            }
        }
        let mut url = self.inner.base.clone();
        url.path_segments_mut()
            .map_err(|()| Error::InvalidInput("base URL cannot carry a path".into()))?
            .pop_if_empty()
            .extend(segments);
        if !query.is_empty() {
            url.query_pairs_mut()
                .extend_pairs(query.iter().map(|(k, v)| (*k, v.as_str())));
        }
        Ok(url)
    }

    /// Sends `req` under the retry policy. Returns 2xx and 304 responses.
    pub(crate) async fn send(&self, req: Req<'_>) -> Result<Resp> {
        if req
            .body
            .as_ref()
            .is_some_and(|b| b.len() > MAX_REQUEST_BYTES)
        {
            return Err(Error::InvalidInput(format!(
                "request body exceeds the {MAX_REQUEST_BYTES} byte limit"
            )));
        }
        let url = self.url(&req.segments, &req.query)?;
        let keyed = req.idempotency_key.is_some();
        let replay_safe = req.replay_safe || keyed;
        let policy = &self.inner.retry;
        let mut attempt: u32 = 0;
        loop {
            match self.attempt(&req, &url).await {
                Attempt::Done(resp) => return Ok(resp),
                Attempt::Failed(err, retry_after) => {
                    let retry = attempt < policy.max_retries
                        && match &err {
                            Error::RateLimited { .. } => true,
                            Error::Transport(_) | Error::CommitUncertain => replay_safe,
                            Error::Api {
                                status: 503, code, ..
                            } => {
                                replay_safe
                                    && matches!(
                                        code.as_str(),
                                        "capacity"
                                            | "storage_unavailable"
                                            | "dependencies_unavailable"
                                    )
                            }
                            Error::InternalError => keyed,
                            _ => false,
                        };
                    if !retry {
                        return Err(err);
                    }
                    let delay = match retry_after {
                        Some(d) => d.min(policy.max_retry_after),
                        None => backoff(policy, attempt),
                    };
                    tokio::time::sleep(delay).await;
                    attempt += 1;
                }
            }
        }
    }

    async fn attempt(&self, req: &Req<'_>, url: &Url) -> Attempt {
        let mut rb = self
            .inner
            .http
            .request(req.method.clone(), url.clone())
            .header(AUTHORIZATION, self.inner.auth.clone())
            .header("Accept", "application/json");
        for (name, value) in &req.headers {
            rb = rb.header(*name, value);
        }
        if let Some(key) = req.idempotency_key {
            rb = rb.header("Idempotency-Key", key.as_str());
        }
        if let Some(body) = &req.body {
            rb = rb
                .header(CONTENT_TYPE, req.content_type.unwrap_or("application/json"))
                .body(body.clone());
        }
        let resp = match rb.send().await {
            Ok(r) => r,
            Err(e) => return Attempt::Failed(Error::Transport(describe(&e)), None),
        };
        let status = resp.status();
        let headers = resp.headers().clone();
        let mut body = Vec::new();
        let mut stream = resp.bytes_stream();
        while let Some(chunk) = stream.next().await {
            match chunk {
                Ok(chunk) => {
                    if body.len() + chunk.len() > self.inner.max_response {
                        return Attempt::Failed(
                            Error::Transport(format!(
                                "response exceeds {} bytes",
                                self.inner.max_response
                            )),
                            None,
                        );
                    }
                    body.extend_from_slice(&chunk);
                }
                Err(e) => return Attempt::Failed(Error::Transport(describe(&e)), None),
            }
        }
        if status.is_success() || status == StatusCode::NOT_MODIFIED {
            return Attempt::Done(Resp {
                status,
                headers,
                body,
            });
        }
        let retry_after = headers
            .get(RETRY_AFTER)
            .and_then(|v| v.to_str().ok())
            .and_then(|v| v.trim().parse::<u64>().ok())
            .map(Duration::from_secs);
        let etag = headers.get(ETAG).and_then(|v| v.to_str().ok());
        let err = errors::from_response(status.as_u16(), etag, retry_after, &body);
        Attempt::Failed(err, retry_after)
    }
}

fn describe(e: &reqwest::Error) -> String {
    use std::error::Error as _;
    let mut msg = e.to_string();
    let mut source = e.source();
    while let Some(s) = source {
        msg.push_str(": ");
        msg.push_str(&s.to_string());
        source = s.source();
    }
    msg
}

fn backoff(policy: &RetryPolicy, attempt: u32) -> Duration {
    let full = policy
        .base_delay
        .saturating_mul(1u32 << attempt.min(16))
        .min(policy.max_delay);
    // Equal jitter: half fixed, half random.
    let half = full / 2;
    let random = uuid::Uuid::new_v4().as_u128() as u64;
    let nanos = half.as_nanos() as u64;
    let jitter = if nanos == 0 { 0 } else { random % (nanos + 1) };
    half + Duration::from_nanos(jitter)
}
