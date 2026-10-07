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

use super::atlas::AtlasResolver;
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
    project: Option<uuid::Uuid>,
    atlas: Option<String>,
    region_pin: Option<String>,
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
    /// data key for documents, a control key for [`ControlClient`]. Calling
    /// [`Self::project`] selects project paths and accepts a bearer access token.
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
            project: None,
            atlas: None,
            region_pin: None,
        }
    }

    /// Selects the C# TerraBase project-scoped contract. `token` is an
    /// already exchanged ts-auth access token; legacy producer credentials
    /// are accepted for explicitly configured producer qualification.
    #[must_use]
    pub fn project(mut self, project: uuid::Uuid) -> Self {
        self.project = Some(project);
        self
    }

    /// Overrides Atlas for local integration. Production discovery uses
    /// only the canonical Atlas origin; no bearer credential is sent to it.
    #[must_use]
    pub fn atlas_base_url(mut self, url: impl Into<String>) -> Self {
        self.atlas = Some(url.into());
        self
    }

    /// Sets an explicit static endpoint, bypassing Atlas discovery.
    #[must_use]
    pub fn endpoint(mut self, url: impl Into<String>) -> Self {
        self.base_url = url.into();
        self
    }

    /// Pins an explicitly configured endpoint to its TerraBase region id.
    #[must_use]
    pub fn region_pin(mut self, region: impl Into<String>) -> Self {
        self.region_pin = Some(region.into());
        self
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
        let discovery = self.base_url.is_empty();
        let base = parse_base_url(if discovery {
            "https://atlas.terrascale.tech"
        } else {
            &self.base_url
        })?;
        if discovery && self.project.is_none() {
            return Err(Error::InvalidInput(
                "Atlas routing requires a project".into(),
            ));
        }
        if self.region_pin.as_ref().is_some_and(|r| !valid_region(r)) {
            return Err(Error::InvalidInput(
                "region pin must be a TerraBase region id".into(),
            ));
        }
        if (discovery || self.atlas.is_some()) && self.region_pin.is_some() {
            return Err(Error::InvalidInput(
                "a region pin requires a static endpoint".into(),
            ));
        }
        if self.atlas.is_some() && !discovery {
            return Err(Error::InvalidInput(
                "Atlas override requires Atlas routing".into(),
            ));
        }
        if self.project.is_some() {
            if self.token.is_empty() || !self.token.bytes().all(|b| b.is_ascii_graphic()) {
                return Err(Error::InvalidInput(
                    "bearer token must be nonempty without whitespace or control characters".into(),
                ));
            }
        } else if !valid_token(&self.token) {
            return Err(Error::InvalidInput(
                "token must be a tbk1.<22-char id>.<43-char secret> key".into(),
            ));
        }
        let mut auth = HeaderValue::from_str(&format!("Bearer {}", self.token))
            .map_err(|_| Error::InvalidInput("token is not a valid header value".into()))?;
        auth.set_sensitive(true);
        // Never send a bearer key over plain HTTP through a proxy taken from
        // the environment (HTTP_PROXY/ALL_PROXY): loopback HTTP and loopback
        // hosts always connect directly.
        let direct = self.project.is_some() || base.scheme() == "http" || is_loopback(&base);
        let mut http = reqwest::Client::builder()
            .use_rustls_tls()
            .min_tls_version(reqwest::tls::Version::TLS_1_3)
            .redirect(reqwest::redirect::Policy::none())
            .user_agent(
                self.user_agent
                    .unwrap_or_else(|| format!("terrascale-rust/{} v1", env!("CARGO_PKG_VERSION"))),
            );
        if direct {
            http = http.no_proxy();
        }
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
        let atlas = if discovery {
            let atlas = parse_base_url(
                self.atlas
                    .as_deref()
                    .unwrap_or("https://atlas.terrascale.tech"),
            )?;
            if atlas.path() != "/" && !atlas.path().is_empty()
                || !(is_loopback(&atlas) || atlas.as_str() == "https://atlas.terrascale.tech/")
            {
                return Err(Error::InvalidInput(
                    "Atlas must be canonical or an explicit loopback origin".into(),
                ));
            }
            Some(AtlasResolver::new(http.clone(), atlas))
        } else {
            None
        };
        Ok(Client {
            inner: Arc::new(Inner {
                http,
                base,
                auth,
                retry: self.retry,
                max_response: self.max_response_bytes,
                project: self.project.map(|p| p.to_string()),
                atlas,
                region_pin: self.region_pin,
            }),
        })
    }

    /// Builds a [`ControlClient`]; the token must be a control key.
    pub fn build_control(self) -> Result<ControlClient> {
        Ok(ControlClient::new(self.build()?))
    }
}

pub(crate) fn valid_region(region: &str) -> bool {
    !region.is_empty()
        && region.len() <= 63
        && region.as_bytes()[0].is_ascii_lowercase()
        && !region.ends_with('-')
        && region
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
}

pub(crate) fn valid_token(token: &str) -> bool {
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

fn is_loopback(url: &Url) -> bool {
    match url.host() {
        Some(url::Host::Domain(d)) => d == "localhost",
        Some(url::Host::Ipv4(ip)) => IpAddr::V4(ip).is_loopback(),
        Some(url::Host::Ipv6(ip)) => IpAddr::V6(ip).is_loopback(),
        None => false,
    }
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
            if !is_loopback(&url) {
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
    project: Option<String>,
    atlas: Option<AtlasResolver>,
    region_pin: Option<String>,
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
    /// Builds a project-scoped C# TerraBase client using Atlas by default.
    /// Supply a ts-auth access token obtained through the authentication SDK.
    /// Use [`ClientBuilder::endpoint`] for an explicit static override.
    pub fn for_project(project: uuid::Uuid, token: impl Into<String>) -> ClientBuilder {
        ClientBuilder::new("", token).project(project)
    }
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
    /// The request was a mutation, so a 2xx means it was applied.
    pub mutation: bool,
}

impl Resp {
    pub fn decode<T: DeserializeOwned>(&self) -> Result<T> {
        serde_json::from_slice(&self.body).map_err(|e| {
            if self.mutation && self.status.is_success() {
                Error::UndecodableAck {
                    status: self.status.as_u16(),
                    message: e.to_string(),
                }
            } else {
                Error::Decode(e.to_string())
            }
        })
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
    pub(crate) async fn send_project(&self, req: Req<'_>) -> Result<Resp> {
        if self.inner.project.is_none() {
            return Err(Error::InvalidInput(
                "this operation requires a C# TerraBase project client".into(),
            ));
        }
        self.send(req).await
    }

    fn url(&self, base: &Url, segments: &[&str], query: &[(&str, String)]) -> Result<Url> {
        for seg in segments {
            if seg.is_empty() || *seg == "." || *seg == ".." {
                return Err(Error::InvalidInput(format!(
                    "invalid path parameter {seg:?}"
                )));
            }
        }
        let mut url = base.clone();
        let mut scoped = segments.to_vec();
        if let Some(project) = &self.inner.project {
            scoped.splice(1..1, ["projects", project.as_str()]);
        }
        url.path_segments_mut()
            .map_err(|()| Error::InvalidInput("base URL cannot carry a path".into()))?
            .pop_if_empty()
            .extend(scoped);
        if !query.is_empty() {
            url.query_pairs_mut()
                .extend_pairs(query.iter().map(|(k, v)| (*k, v.as_str())));
        }
        Ok(url)
    }

    /// Sends `req` under the retry policy. Returns 2xx and 304 responses.
    pub(crate) async fn send(&self, mut req: Req<'_>) -> Result<Resp> {
        if self.inner.project.is_some() {
            if req.segments.get(..2) != Some(&["v1", "databases"][..]) {
                return Err(Error::InvalidInput(
                    "the legacy dashboard control plane is unavailable for a project client".into(),
                ));
            }
            if req.idempotency_key.is_some() {
                return Err(Error::InvalidInput("C# TerraBase does not support Idempotency-Key; an uncertain mutation must be reconciled by reading its state".into()));
            }
        }
        if req
            .body
            .as_ref()
            .is_some_and(|b| b.len() > MAX_REQUEST_BYTES)
        {
            return Err(Error::InvalidInput(format!(
                "request body exceeds the {MAX_REQUEST_BYTES} byte limit"
            )));
        }
        let project = self
            .inner
            .project
            .as_deref()
            .map(|p| uuid::Uuid::parse_str(p).expect("validated project"));
        let database = req.segments.get(2).copied();
        let mut origin = self.inner.base.clone();
        let mut region = self.inner.region_pin.clone();
        if let Some(atlas) = &self.inner.atlas {
            let endpoint = atlas
                .resolve(project.expect("Atlas project"), database, false)
                .await?;
            origin = endpoint.api;
            region = Some(endpoint.region);
        }
        let mut url = self.url(&origin, &req.segments, &req.query)?;
        // Serialized once; each attempt shares it without copying.
        let body = req.body.take().map(bytes::Bytes::from);
        let keyed = req.idempotency_key.is_some();
        let replay_safe = req.replay_safe || keyed;
        let policy = &self.inner.retry;
        let mut attempt: u32 = 0;
        let mut refreshed = false;
        loop {
            if attempt > 0 {
                if let Some(atlas) = &self.inner.atlas {
                    let endpoint = atlas
                        .resolve(project.expect("Atlas project"), database, false)
                        .await?;
                    url = self.url(&endpoint.api, &req.segments, &req.query)?;
                    region = Some(endpoint.region);
                }
            }
            match self
                .attempt(&req, &url, region.as_deref(), body.clone())
                .await
            {
                Attempt::Done(resp) => return Ok(resp),
                Attempt::Failed(err, retry_after) => {
                    let wrong_route = err
                        .status()
                        .is_some_and(|s| s == 421 || (300..400).contains(&s));
                    if wrong_route && !refreshed {
                        if let Some(atlas) = &self.inner.atlas {
                            let endpoint = atlas
                                .resolve(project.expect("Atlas project"), database, true)
                                .await;
                            if req.replay_safe {
                                let endpoint = endpoint?;
                                url = self.url(&endpoint.api, &req.segments, &req.query)?;
                                region = Some(endpoint.region);
                                refreshed = true;
                                continue;
                            }
                            // Refresh for the next call, but a mutation goes once.
                        }
                    }
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

    async fn attempt(
        &self,
        req: &Req<'_>,
        url: &Url,
        region: Option<&str>,
        body: Option<bytes::Bytes>,
    ) -> Attempt {
        let mut rb = self
            .inner
            .http
            .request(req.method.clone(), url.clone())
            .header(AUTHORIZATION, self.inner.auth.clone())
            .header("Accept", "application/json");
        if let Some(region) = region {
            rb = rb.header("X-TerraScale-Region-Pin", region);
        }
        for (name, value) in &req.headers {
            rb = rb.header(*name, value);
        }
        if let Some(key) = req.idempotency_key {
            rb = rb.header("Idempotency-Key", key.as_str());
        }
        if let Some(body) = body {
            rb = rb
                .header(CONTENT_TYPE, req.content_type.unwrap_or("application/json"))
                .body(body);
        }
        let resp = match rb.send().await {
            Ok(r) => r,
            Err(e) => return Attempt::Failed(Error::Transport(describe(&e)), None),
        };
        let status = resp.status();
        let headers = resp.headers().clone();
        if self.inner.project.is_some()
            && status != StatusCode::MISDIRECTED_REQUEST
            && !(status.is_redirection() && status != StatusCode::NOT_MODIFIED)
            && region.is_some_and(|r| {
                let mut values = headers.get_all("X-TerraBase-Region").iter();
                values.next().and_then(|h| h.to_str().ok()) != Some(r) || values.next().is_some()
            })
        {
            let message = "TerraBase response region does not match the session pin".into();
            return Attempt::Failed(
                if req.replay_safe || status.is_client_error() {
                    Error::Decode(message)
                } else {
                    Error::UndecodableAck {
                        status: status.as_u16(),
                        message,
                    }
                },
                None,
            );
        }
        let mut body = Vec::new();
        let mut stream = resp.bytes_stream();
        while let Some(chunk) = stream.next().await {
            match chunk {
                Ok(chunk) => {
                    if body.len() + chunk.len() > self.inner.max_response {
                        return Attempt::Failed(
                            Error::ResponseTooLarge {
                                limit: self.inner.max_response,
                                status: status.as_u16(),
                                // A mutation answered with anything but a
                                // client error may have been applied.
                                may_have_applied: !req.replay_safe && !status.is_client_error(),
                            },
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
                mutation: !req.replay_safe,
            });
        }
        let retry_after = headers
            .get(RETRY_AFTER)
            .and_then(|v| v.to_str().ok())
            .and_then(|v| parse_retry_after(v, std::time::SystemTime::now()));
        let etag = headers.get(ETAG).and_then(|v| v.to_str().ok());
        let err = errors::from_response(status.as_u16(), etag, retry_after, &body);
        Attempt::Failed(err, retry_after)
    }
}

/// Parses `Retry-After` as delay-seconds or an HTTP-date; a past date is
/// zero. The caller caps it at `max_retry_after`.
fn parse_retry_after(value: &str, now: std::time::SystemTime) -> Option<Duration> {
    let value = value.trim();
    if let Ok(secs) = value.parse::<u64>() {
        return Some(Duration::from_secs(secs));
    }
    let at = httpdate::parse_http_date(value).ok()?;
    Some(at.duration_since(now).unwrap_or(Duration::ZERO))
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
    backoff_with(policy, attempt, uuid::Uuid::new_v4().as_u128())
}

/// Equal-jitter backoff: half of the capped exponential delay is fixed, half
/// random. Overflow-safe for any `Duration` policy values.
fn backoff_with(policy: &RetryPolicy, attempt: u32, random: u128) -> Duration {
    // Double toward the cap; a Duration saturates within 128 doublings.
    let mut full = policy.base_delay;
    for _ in 0..attempt.min(128) {
        if full >= policy.max_delay {
            break;
        }
        full = full.saturating_mul(2);
    }
    let full = full.min(policy.max_delay);
    let half = full / 2;
    // A Duration has fewer than 2^94 nanoseconds, so `span + 1` cannot overflow.
    let span = half.as_nanos();
    let jitter = if span == 0 { 0 } else { random % (span + 1) };
    // jitter <= span, so its whole seconds fit in u64.
    let jitter = Duration::new(
        (jitter / 1_000_000_000) as u64,
        (jitter % 1_000_000_000) as u32,
    );
    half.saturating_add(jitter)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn project_routes_and_credentials_are_separate_from_legacy_v1() {
        let project = uuid::Uuid::new_v4();
        let client = Client::builder("https://example.com/prefix", "already-exchanged.jwt")
            .project(project)
            .build()
            .unwrap();
        assert_eq!(client.url(&client.inner.base, &["v1", "databases", "orders", "collections", "items"], &[]).unwrap().as_str(),
            format!("https://example.com/prefix/v1/projects/{project}/databases/orders/collections/items"));
        assert!(
            Client::builder("https://example.com", "already-exchanged.jwt")
                .build()
                .is_err()
        );
        for token in ["", "Bearer jwt", "jwt\nheader", "é"] {
            assert!(Client::builder("https://example.com", token)
                .project(project)
                .build()
                .is_err());
        }
        assert!(!format!("{client:?}").contains("already-exchanged"));
        assert!(Client::for_project(project, "jwt")
            .atlas_base_url("https://evil.example")
            .build()
            .is_err());
        assert!(Client::for_project(project, "jwt")
            .region_pin("gru-1")
            .build()
            .is_err());
        assert!(Client::for_project(project, "jwt")
            .endpoint("http://127.0.0.1:1")
            .region_pin("gru-1")
            .build()
            .is_ok());
        assert!(Client::builder("http://127.0.0.1:1", "jwt")
            .project(project)
            .region_pin("GRU")
            .build()
            .is_err());
    }

    #[tokio::test]
    async fn project_rejects_legacy_control_and_keyed_writes_before_dispatch() {
        let client = Client::builder("http://127.0.0.1:1", "jwt")
            .project(uuid::Uuid::new_v4())
            .build()
            .unwrap();
        let err = client
            .control()
            .list_regions(&super::super::ListOptions::default())
            .await
            .unwrap_err();
        assert!(matches!(err, Error::InvalidInput(_)), "{err:?}");
        let key = IdempotencyKey::random();
        let err = client
            .database("orders")
            .collection("items")
            .put(
                uuid::Uuid::new_v4(),
                &serde_json::json!({"a":1}),
                super::super::WriteOptions::new().idempotency_key(key.clone()),
            )
            .await
            .unwrap_err();
        assert!(matches!(err, Error::InvalidInput(_)), "{err:?}");
        let mut tx = super::super::Tx::new();
        tx.put(
            "items",
            uuid::Uuid::new_v4(),
            &serde_json::json!({"a":1}),
            None,
        )
        .idempotency_key(key);
        assert!(matches!(
            client.database("orders").commit(&tx).await,
            Err(Error::InvalidInput(_))
        ));
    }

    #[test]
    fn retry_after_accepts_seconds_and_http_dates() {
        let now = httpdate::parse_http_date("Wed, 21 Oct 2015 07:28:00 GMT").unwrap();
        let p = |v| parse_retry_after(v, now);
        assert_eq!(p(" 7 "), Some(Duration::from_secs(7)));
        assert_eq!(
            p("Wed, 21 Oct 2015 07:28:30 GMT"),
            Some(Duration::from_secs(30))
        );
        assert_eq!(p("Wed, 21 Oct 2015 07:27:00 GMT"), Some(Duration::ZERO));
        assert_eq!(
            p("Wednesday, 21-Oct-15 07:29:00 GMT"),
            Some(Duration::from_secs(60))
        );
        assert_eq!(p("soon"), None);
        assert_eq!(p("-1"), None);
    }

    #[test]
    fn backoff_is_bounded_and_overflow_safe() {
        let huge = RetryPolicy {
            max_retries: 3,
            base_delay: Duration::MAX,
            max_delay: Duration::MAX,
            max_retry_after: Duration::MAX,
        };
        for random in [0, 1, u64::MAX as u128, u128::MAX] {
            for attempt in [0, 1, 16, u32::MAX] {
                let d = backoff_with(&huge, attempt, random);
                assert!(d >= Duration::MAX / 2, "{d:?}");
            }
        }
        let normal = RetryPolicy::default();
        for random in [0, u128::MAX] {
            for attempt in 0..40 {
                let d = backoff_with(&normal, attempt, random);
                assert!(d <= normal.max_delay, "{d:?}");
                assert!(
                    d >= normal
                        .base_delay
                        .saturating_mul(1 << attempt.min(16))
                        .min(normal.max_delay)
                        / 2
                );
            }
        }
        assert_eq!(
            backoff_with(
                &RetryPolicy {
                    base_delay: Duration::ZERO,
                    ..normal
                },
                3,
                7
            ),
            Duration::ZERO
        );
        // Growth continues past 16 doublings until the cap.
        let long = RetryPolicy {
            max_retries: 30,
            base_delay: Duration::from_millis(1),
            max_delay: Duration::from_secs(600),
            max_retry_after: Duration::from_secs(30),
        };
        assert_eq!(
            backoff_with(&long, 18, 0),
            Duration::from_millis(1 << 18) / 2
        );
        assert_eq!(
            backoff_with(&long, 19, 0),
            Duration::from_millis(1 << 19) / 2
        );
        assert_eq!(backoff_with(&long, 25, 0), Duration::from_secs(300));
    }
}
