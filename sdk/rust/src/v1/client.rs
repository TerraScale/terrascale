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

use super::auth::{Auth, Credential};
use super::documents::Database;
use super::errors::{self, Error, Result};

/// The public TerraBase API origin used by [`Client::for_project`].
pub const DEFAULT_BASE_URL: &str = "https://api.terrascale.tech";
/// The server's request body limit (4 MiB).
pub const MAX_REQUEST_BYTES: usize = 4 << 20;
/// Default bound on one response body (64 MiB).
pub const DEFAULT_MAX_RESPONSE_BYTES: usize = 64 << 20;

/// Retry policy for the v1 client.
///
/// * `429 rate_limited` and `429 tenant_busy` are always retried (the request
///   was not admitted), after `Retry-After` capped at `max_retry_after`. A
///   `429` without a TerraBase error code is retried only when replay-safe.
///   Quota refusals such as `429 operation_allowance` are never retried.
/// * Transport errors, the transient 503s (`capacity`,
///   `storage_unavailable`, `dependencies_unavailable`, `commit_uncertain`)
///   and gateway `502`/`503`/`504` answers without a TerraBase error code
///   are retried only when the request is replay-safe: reads, queries and
///   other explicitly read-only requests.
/// * API-key token exchanges retry transport failures, `429` and gateway
///   `502`/`503`/`504` under the same budget; minting a token has no effect.
/// * `500 internal_error` is returned without retry.
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
    credential: Credential,
    root_cas: Vec<Vec<u8>>,
    timeout: Option<Duration>,
    connect_timeout: Option<Duration>,
    retry: RetryPolicy,
    max_response_bytes: usize,
    user_agent: Option<String>,
    project: Option<String>,
    access_token: bool,
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
    /// `base_url` is the TerraBase origin (HTTPS; HTTP only for loopback).
    /// `token` is a ts-auth bearer access token. Select [`Self::project`]
    /// before building, or use [`Client::for_platform`] for platform access.
    pub fn new(base_url: impl Into<String>, token: impl Into<String>) -> Self {
        Self {
            base_url: base_url.into(),
            credential: Credential::Bearer(token.into()),
            root_cas: Vec::new(),
            timeout: Some(Duration::from_secs(30)),
            connect_timeout: Some(Duration::from_secs(10)),
            retry: RetryPolicy::default(),
            max_response_bytes: DEFAULT_MAX_RESPONSE_BYTES,
            user_agent: None,
            project: None,
            access_token: false,
            region_pin: None,
        }
    }

    /// Selects the project-scoped contract with a ts-auth access token.
    #[must_use]
    pub fn project(mut self, project: impl ToString) -> Self {
        self.project = Some(project.to_string());
        self
    }

    /// Sets an explicit static endpoint instead of the public API origin.
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
        if self.project.is_none() && !self.access_token {
            return Err(Error::InvalidInput(
                "select a project or use Client::for_platform".into(),
            ));
        }
        if self
            .project
            .as_deref()
            .is_some_and(|p| !super::ids::valid_entity_id(p))
        {
            return Err(Error::InvalidInput(
                "project must be a canonical uppercase ULID".into(),
            ));
        }
        let public = self.base_url.is_empty();
        let base = parse_base_url(if public {
            DEFAULT_BASE_URL
        } else {
            &self.base_url
        })?;
        if self.region_pin.as_ref().is_some_and(|r| !valid_region(r)) {
            return Err(Error::InvalidInput(
                "region pin must be a TerraBase region id".into(),
            ));
        }
        if public && self.region_pin.is_some() {
            return Err(Error::InvalidInput(
                "a region pin requires a static endpoint".into(),
            ));
        }
        let auth = Auth::new(self.credential, self.project.as_deref())?;
        // Never send a bearer key over plain HTTP through a proxy taken from
        // the environment (HTTP_PROXY/ALL_PROXY): loopback HTTP and loopback
        // hosts always connect directly.
        let mut http = reqwest::Client::builder()
            .use_rustls_tls()
            .min_tls_version(reqwest::tls::Version::TLS_1_3)
            .redirect(reqwest::redirect::Policy::none())
            .user_agent(
                self.user_agent
                    .unwrap_or_else(|| format!("terrascale-rust/{} v1", env!("CARGO_PKG_VERSION"))),
            );
        http = http.no_proxy();
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
                project: self.project,
                region_pin: self.region_pin,
                shape_keys: Default::default(),
            }),
        })
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
    auth: Auth,
    retry: RetryPolicy,
    max_response: usize,
    project: Option<String>,
    region_pin: Option<String>,
    shape_keys: tokio::sync::Mutex<super::shapes::EpochKeys>,
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
    pub(crate) fn shape_keys(&self) -> &tokio::sync::Mutex<super::shapes::EpochKeys> {
        &self.inner.shape_keys
    }

    /// Builds a project-scoped C# TerraBase client for [`DEFAULT_BASE_URL`].
    /// Supply a ts-auth access token obtained through the authentication SDK.
    /// Use [`ClientBuilder::endpoint`] for an explicit static override.
    pub fn for_project(project: impl ToString, token: impl Into<String>) -> ClientBuilder {
        ClientBuilder::new("", token).project(project)
    }
    /// Exchanges a first-party API key for project-scoped bearer tokens.
    /// Tokens refresh automatically and the cache is shared by all clones.
    /// The API key is sent only to the fixed first-party authentication origin.
    pub fn for_project_api_key(
        project: impl ToString,
        api_key: impl Into<String>,
    ) -> ClientBuilder {
        ClientBuilder {
            credential: Credential::ApiKey(api_key.into()),
            ..ClientBuilder::new("", "").project(project)
        }
    }
    /// Builds a platform-admin client with a user access token and explicit endpoint.
    pub fn for_platform(endpoint: impl Into<String>, token: impl Into<String>) -> ClientBuilder {
        ClientBuilder {
            access_token: true,
            ..ClientBuilder::new(endpoint, token)
        }
    }
    /// Starts a [`ClientBuilder`].
    pub fn builder(base_url: impl Into<String>, token: impl Into<String>) -> ClientBuilder {
        ClientBuilder::new(base_url, token)
    }

    /// A handle by current database name or immutable ULID. Performs no I/O;
    /// the selector is validated on each request.
    #[must_use]
    pub fn database(&self, name: impl Into<String>) -> Database {
        Database::new(self.clone(), name.into())
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
    /// Reads and read-only POSTs that may be resent after a transport error.
    pub replay_safe: bool,
    /// Only public health probes omit the configured bearer credential.
    anonymous_probe: bool,
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
            replay_safe,
            anonymous_probe: false,
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
        serde_json::from_slice(&self.body).map_err(|e| self.invalid(e.to_string()))
    }

    pub fn decode_status<T: DeserializeOwned>(&self, allowed: &[StatusCode]) -> Result<T> {
        self.check_status(allowed)?;
        self.decode()
    }

    pub fn check_status(&self, allowed: &[StatusCode]) -> Result<()> {
        if allowed.contains(&self.status) {
            Ok(())
        } else if !self.status.is_success() && self.status != StatusCode::NOT_MODIFIED {
            let retry_after = self
                .header("Retry-After")
                .and_then(|value| parse_retry_after(value, std::time::SystemTime::now()));
            Err(errors::from_project_response(
                self.status.as_u16(),
                self.header("ETag"),
                retry_after,
                self.header("X-Request-ID"),
                &self.body,
            ))
        } else {
            Err(self.invalid(format!("unexpected HTTP status {}", self.status.as_u16())))
        }
    }

    pub fn invalid(&self, message: String) -> Error {
        let error = if self.mutation && !self.status.is_client_error() {
            Error::UndecodableAck {
                status: self.status.as_u16(),
                message,
            }
        } else {
            Error::Decode(message)
        };
        Error::Response {
            status: self.status.as_u16(),
            error: Box::new(error),
            body: self.body.clone(),
            request_id: self.header("X-Request-ID").map(str::to_owned),
        }
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
    /// Resolves shape routing without replaying machine-local offsets on a new endpoint.
    pub(crate) async fn shape_endpoint(&self) -> Result<(Url, Option<String>)> {
        self.inner.project.as_deref().ok_or_else(|| {
            Error::InvalidInput("shape reads require a C# TerraBase project client".into())
        })?;
        Ok((self.inner.base.clone(), self.inner.region_pin.clone()))
    }

    pub(crate) fn shape_url(
        &self,
        base: &Url,
        database: &str,
        collection: &str,
        suffix: Option<&str>,
        query: &[(&str, String)],
    ) -> Result<Url> {
        let mut segments = vec![
            "v1",
            "databases",
            database,
            "collections",
            collection,
            "shape",
        ];
        if let Some(suffix) = suffix {
            segments.push(suffix);
        }
        self.url(base, &segments, query)
    }

    /// Shape protocol owns redirects and must-refetch; preserve their headers and bytes.
    pub(crate) async fn shape_get(
        &self,
        url: &Url,
        region: Option<&str>,
        headers: &[(&'static str, String)],
    ) -> Result<Resp> {
        let mut req = Req::new(Method::GET, Vec::new());
        req.headers = headers.to_vec();
        match self
            .attempt(&req, url, region, None, true, &mut false)
            .await
        {
            Attempt::Done(resp) => Ok(resp),
            Attempt::Failed(error, _) => Err(error),
        }
    }

    pub(crate) async fn shape_retry(&self, attempt: u32, retry_after: Option<&str>) -> bool {
        let policy = &self.inner.retry;
        if attempt >= policy.max_retries {
            return false;
        }
        let delay = retry_after
            .and_then(|value| parse_retry_after(value, std::time::SystemTime::now()))
            .map(|delay| delay.min(policy.max_retry_after))
            .unwrap_or_else(|| backoff(policy, attempt));
        tokio::time::sleep(delay).await;
        true
    }

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
            if segments.first() == Some(&"v1")
                && segments.get(1) != Some(&"platform")
                && segments.get(1) != Some(&"regions")
            {
                scoped.splice(1..1, ["projects", project.as_str()]);
            }
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

    pub(crate) fn max_response(&self) -> usize {
        self.inner.max_response
    }

    pub(crate) async fn changes_response(
        &self,
        database: &str,
        query: &[(&str, String)],
        last_event_id: Option<&str>,
    ) -> Result<reqwest::Response> {
        let (origin, region) = self.shape_endpoint().await?;
        let url = self.url(&origin, &["v1", "databases", database, "changes"], query)?;
        let mut request = self
            .inner
            .http
            .get(url)
            .header("Accept", "text/event-stream")
            // The producer ends streams after 30 minutes; dropping the future/stream cancels it.
            .timeout(Duration::from_secs(31 * 60));
        if let Some(region) = &region {
            request = request.header("X-TerraScale-Region-Pin", region);
        }
        if let Some(cursor) = last_event_id {
            let cursor = HeaderValue::from_str(cursor)
                .map_err(|_| Error::InvalidInput("Last-Event-ID must be a valid header".into()))?;
            request = request.header("Last-Event-ID", cursor);
        }
        let response = self.authorized(request, false, &mut false).await?;
        let status = response.status();
        let headers = response.headers().clone();
        let valid_region = region.as_deref().is_none_or(|expected| {
            let mut values = headers.get_all("X-TerraBase-Region").iter();
            values.next().and_then(|value| value.to_str().ok()) == Some(expected)
                && values.next().is_none()
        });
        let event_stream = headers
            .get(CONTENT_TYPE)
            .and_then(|value| value.to_str().ok())
            .is_some_and(|value| {
                value
                    .split(';')
                    .next()
                    .is_some_and(|kind| kind.trim().eq_ignore_ascii_case("text/event-stream"))
            });
        if status == StatusCode::OK && valid_region && event_stream {
            return Ok(response);
        }
        let limit = self.inner.max_response.min(64 << 10);
        let mut body = Vec::new();
        let mut chunks = response.bytes_stream();
        while let Some(chunk) = chunks.next().await {
            let chunk = chunk.map_err(|error| {
                self.response_error(
                    Error::Transport(describe(&error)),
                    status,
                    &headers,
                    body.clone(),
                )
            })?;
            let room = limit.saturating_sub(body.len());
            body.extend_from_slice(&chunk[..chunk.len().min(room)]);
            if chunk.len() > room {
                return Err(self.response_error(
                    Error::ResponseTooLarge {
                        limit,
                        status: status.as_u16(),
                        may_have_applied: false,
                    },
                    status,
                    &headers,
                    body,
                ));
            }
        }
        let response = Resp {
            status,
            headers,
            body,
            mutation: false,
        };
        response.check_status(&[StatusCode::OK])?;
        Err(response
            .invalid("changes requires HTTP 200, text/event-stream and the pinned region".into()))
    }

    pub(crate) async fn probe(&self, segments: Vec<&str>) -> Result<Resp> {
        let mut req = Req::new(Method::GET, segments);
        req.anonymous_probe = true;
        let url = self.url(&self.inner.base, &req.segments, &[])?;
        match self.attempt(&req, &url, None, None, true, &mut false).await {
            Attempt::Done(response) => Ok(response),
            Attempt::Failed(error, _) => Err(error),
        }
    }

    /// Sends `req` under the retry policy. Returns 2xx and 304 responses.
    pub(crate) async fn send(&self, mut req: Req<'_>) -> Result<Resp> {
        if req.segments.first() != Some(&"v1")
            || !matches!(
                req.segments.get(1).copied(),
                Some(
                    "platform"
                        | "databases"
                        | "saved-queries"
                        | "metrics"
                        | "buckets"
                        | "backup-destination"
                        | "usage"
                        | "analytics"
                        | "replication"
                )
            )
            || (self.inner.project.is_none() && req.segments.get(1) != Some(&"platform"))
        {
            return Err(Error::InvalidInput(
                "request requires a canonical project or platform route".into(),
            ));
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
        let url = self.url(&self.inner.base, &req.segments, &req.query)?;
        let region = self.inner.region_pin.as_deref();
        // Serialized once; each attempt shares it without copying.
        let body = req.body.take().map(bytes::Bytes::from);
        let replay_safe = req.replay_safe;
        let policy = &self.inner.retry;
        let mut attempt: u32 = 0;
        let mut refreshed = false;
        loop {
            match self
                .attempt(&req, &url, region, body.clone(), false, &mut refreshed)
                .await
            {
                Attempt::Done(resp) => return Ok(resp),
                Attempt::Failed(err, retry_after) => {
                    let retry = attempt < policy.max_retries
                        && ((matches!(err, Error::Transport(_))
                            || matches!(&err, Error::Response { status, error, .. } if !(400..500).contains(status) && matches!(**error, Error::Transport(_))))
                            && replay_safe
                            || match (err.status(), err.code()) {
                                // The producer sends both codes before admission
                                // (rate limit and organization QoS budget). Quota
                                // refusals such as `operation_allowance` stay terminal.
                                (Some(429), Some("rate_limited" | "tenant_busy")) => true,
                                // An uncoded (edge) 429 grants no mutation retry.
                                (Some(429), Some("http_429")) => replay_safe,
                                // A gateway answer without a TerraBase error code
                                // never reached a handler that names its outcome.
                                (Some(status @ 502..=504), Some(code))
                                    if code.strip_prefix("http_")
                                        == Some(status.to_string().as_str()) =>
                                {
                                    replay_safe
                                }
                                (
                                    Some(503),
                                    Some(
                                        "commit_uncertain"
                                        | "capacity"
                                        | "storage_unavailable"
                                        | "dependencies_unavailable"
                                        | "storage_busy"
                                        | "not_primary"
                                        | "auth_unavailable",
                                    ),
                                ) => replay_safe,
                                _ => false,
                            });
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
        raw_status: bool,
        refreshed: &mut bool,
    ) -> Attempt {
        let mut rb = self
            .inner
            .http
            .request(req.method.clone(), url.clone())
            .header("Accept", "application/json");
        if let Some(region) = region {
            rb = rb.header("X-TerraScale-Region-Pin", region);
        }
        for (name, value) in &req.headers {
            rb = rb.header(*name, value);
        }
        if let Some(body) = body {
            rb = rb
                .header(CONTENT_TYPE, req.content_type.unwrap_or("application/json"))
                .body(body);
        }
        let resp = match self.authorized(rb, req.anonymous_probe, refreshed).await {
            Ok(r) => r,
            Err(e) => return Attempt::Failed(e, None),
        };
        let status = resp.status();
        let headers = resp.headers().clone();
        let wrong_region = self.inner.project.is_some()
            && status != StatusCode::MISDIRECTED_REQUEST
            && !(status.is_redirection() && status != StatusCode::NOT_MODIFIED)
            && region.is_some_and(|r| {
                let mut values = headers.get_all("X-TerraBase-Region").iter();
                values.next().and_then(|h| h.to_str().ok()) != Some(r) || values.next().is_some()
            });
        let mut body = Vec::new();
        let mut stream = resp.bytes_stream();
        while let Some(chunk) = stream.next().await {
            match chunk {
                Ok(chunk) => {
                    if body.len() + chunk.len() > self.inner.max_response {
                        let error = Error::ResponseTooLarge {
                            limit: self.inner.max_response,
                            status: status.as_u16(),
                            // A mutation answered with anything but a
                            // client error may have been applied.
                            may_have_applied: !req.replay_safe && !status.is_client_error(),
                        };
                        body.extend_from_slice(&chunk[..self.inner.max_response - body.len()]);
                        return Attempt::Failed(
                            self.response_error(error, status, &headers, body),
                            None,
                        );
                    }
                    body.extend_from_slice(&chunk);
                }
                Err(e) => {
                    return Attempt::Failed(
                        self.response_error(Error::Transport(describe(&e)), status, &headers, body),
                        None,
                    )
                }
            }
        }
        if wrong_region {
            let response = Resp {
                status,
                headers,
                body,
                mutation: !req.replay_safe,
            };
            return Attempt::Failed(
                response.invalid("TerraBase response region does not match the session pin".into()),
                None,
            );
        }
        if raw_status || status.is_success() || status == StatusCode::NOT_MODIFIED {
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
        let err = errors::from_project_response(
            status.as_u16(),
            etag,
            retry_after,
            headers.get("X-Request-ID").and_then(|v| v.to_str().ok()),
            &body,
        );
        Attempt::Failed(err, retry_after)
    }

    // A 401 refuses admission, so even a mutation can be sent once more.
    // Every other retry remains subject to the existing replay-safety policy.
    async fn authorized(
        &self,
        request: reqwest::RequestBuilder,
        anonymous: bool,
        refreshed: &mut bool,
    ) -> Result<reqwest::Response> {
        let transport = |e: reqwest::Error| Error::Transport(describe(&e));
        if anonymous {
            return request.send().await.map_err(transport);
        }
        let mut request = request;
        let mut rejected = None;
        loop {
            let token = self
                .inner
                .auth
                .bearer(&self.inner.http, rejected.as_ref(), &self.inner.retry)
                .await?;
            let replay = request.try_clone();
            let mut response = request
                .header(AUTHORIZATION, token.as_ref().clone())
                .send()
                .await
                .map_err(transport)?;
            if response.status() != StatusCode::UNAUTHORIZED || !self.inner.auth.refreshable() {
                return Ok(response);
            }
            let headers = response.headers().clone();
            let mut body = Vec::new();
            while let Some(chunk) = response.chunk().await.map_err(transport)? {
                if body.len() + chunk.len() > self.inner.max_response {
                    return Err(Error::ResponseTooLarge {
                        limit: self.inner.max_response,
                        status: 401,
                        may_have_applied: false,
                    });
                }
                body.extend_from_slice(&chunk);
            }
            let error = errors::from_project_response(
                401,
                None,
                None,
                headers.get("X-Request-ID").and_then(|v| v.to_str().ok()),
                &body,
            );
            let decoded = match &error {
                Error::Response { error, .. } => error.as_ref(),
                _ => &error,
            };
            let permanent = match decoded {
                Error::KeyRevoked { message } => Some(("key_revoked", message.clone())),
                Error::KeyExpired { message } => Some(("key_expired", message.clone())),
                _ => None,
            };
            if let Some((code, message)) = permanent {
                self.inner.auth.reject(code, message).await;
            } else if !*refreshed {
                if let Some(replay) = replay {
                    *refreshed = true;
                    rejected = Some(token);
                    request = replay;
                    continue;
                }
            }
            return Err(error);
        }
    }

    fn response_error(
        &self,
        error: Error,
        status: StatusCode,
        headers: &HeaderMap,
        body: Vec<u8>,
    ) -> Error {
        Error::Response {
            status: status.as_u16(),
            error: Box::new(error),
            body,
            request_id: headers
                .get("X-Request-ID")
                .and_then(|v| v.to_str().ok())
                .map(str::to_owned),
        }
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

pub(super) fn backoff(policy: &RetryPolicy, attempt: u32) -> Duration {
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
#[path = "auth_smoke.rs"]
mod auth_smoke;

#[cfg(test)]
#[path = "token_tests.rs"]
mod token_tests;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn project_routes_require_scope_and_valid_bearer_credentials() {
        let project = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
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
        assert_eq!(
            Client::for_project(project, "jwt")
                .build()
                .unwrap()
                .base_url()
                .as_str(),
            "https://api.terrascale.tech/"
        );
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
    async fn platform_clients_cannot_dispatch_unscoped_document_calls() {
        let client = Client::for_platform("http://127.0.0.1:1", "fixture-token")
            .build()
            .unwrap();
        assert!(matches!(
            client
                .database("orders")
                .collection("items")
                .get::<serde_json::Value>(ulid::Ulid::nil())
                .await,
            Err(Error::InvalidInput(_))
        ));
        let mut tx = super::super::Tx::new();
        tx.put("items", ulid::Ulid::nil(), &serde_json::json!({}), None);
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

#[path = "export_stream.rs"]
mod export_stream;
