//! Pluggable HTTP transport. The SDK core builds typed [`Request`]s; a
//! [`HttpTransport`] sends them. Production uses [`ReqwestTransport`]; tests
//! inject a mock.

use async_trait::async_trait;
use futures_util::StreamExt;

pub const MAX_JSON_BYTES: usize = 4 * 1024 * 1024;

#[derive(Default)]
struct BoundedJson {
    bytes: Vec<u8>,
    exceeded: bool,
}

impl std::io::Write for BoundedJson {
    fn write(&mut self, buffer: &[u8]) -> std::io::Result<usize> {
        if self.bytes.len().saturating_add(buffer.len()) > MAX_JSON_BYTES {
            self.exceeded = true;
            return Err(std::io::Error::other("JSON capacity exceeded"));
        }
        self.bytes.extend_from_slice(buffer);
        Ok(buffer.len())
    }
    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}
use serde::de::DeserializeOwned;

use crate::error::{Error, Result};

/// HTTP method for a typed SDK request.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Method {
    Get,
    Post,
    Put,
    Patch,
    Delete,
}

impl Method {
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Get => "GET",
            Self::Post => "POST",
            Self::Put => "PUT",
            Self::Patch => "PATCH",
            Self::Delete => "DELETE",
        }
    }
}

/// One typed outgoing request.
#[derive(Clone)]
pub struct Request {
    /// Route name for observability (e.g. `documents.create`).
    pub route_name: String,
    pub method: Method,
    pub url: String,
    pub headers: Vec<(String, String)>,
    pub body: Option<Vec<u8>>,
}

impl std::fmt::Debug for Request {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("Request")
            .field("route_name", &self.route_name)
            .field("method", &self.method)
            .field("url", &self.url)
            .field(
                "headers",
                &format_args!("<{} headers, authorization redacted>", self.headers.len()),
            )
            .field(
                "body",
                &format_args!("<{} bytes>", self.body.as_ref().map_or(0, Vec::len)),
            )
            .finish()
    }
}

impl Request {
    /// Build a JSON request with the given serializable body.
    pub fn json(
        route_name: impl Into<String>,
        method: Method,
        url: String,
        body: &impl serde::Serialize,
    ) -> Result<Self> {
        let mut output = BoundedJson::default();
        serde_json::to_writer(&mut output, body).map_err(|error| {
            if output.exceeded {
                Error::new(
                    crate::ErrorKind::Validation,
                    "request_capacity",
                    "JSON request exceeds 4 MiB.",
                )
            } else {
                Error::new(
                    crate::ErrorKind::Validation,
                    "invalid_request",
                    format!("Request body is not serializable: {error}"),
                )
            }
        })?;
        Ok(Self {
            route_name: route_name.into(),
            method,
            url,
            headers: vec![("content-type".to_owned(), "application/json".to_owned())],
            body: Some(output.bytes),
        })
    }
}

/// One incoming response.
#[derive(Clone)]
pub struct Response {
    pub status: u16,
    pub headers: Vec<(String, String)>,
    /// Raw body kept as shared bytes: large artifacts are not copied on
    /// transport boundaries.
    pub body: bytes::Bytes,
}

impl std::fmt::Debug for Response {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("Response")
            .field("status", &self.status)
            .field(
                "headers",
                &format_args!("<{} headers, authorization redacted>", self.headers.len()),
            )
            .field("body", &format_args!("<{} bytes>", self.body.len()))
            .finish()
    }
}

impl Response {
    /// Build a JSON response (used by mocks and tests).
    #[must_use]
    pub fn json(status: u16, body: &impl serde::Serialize) -> Self {
        Self {
            status,
            headers: vec![("content-type".to_owned(), "application/json".to_owned())],
            body: bytes::Bytes::from(serde_json::to_vec(body).unwrap_or_default()),
        }
    }

    /// Case-insensitive header lookup.
    #[must_use]
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(name))
            .map(|(_, v)| v.as_str())
    }

    /// Decode a JSON body against the pinned contract. Failures retain HTTP
    /// context without echoing response values in their diagnostics.
    pub fn json_body<T: DeserializeOwned>(&self) -> Result<T> {
        if self.body.len() > MAX_JSON_BYTES {
            return Err(self.invalid_response("JSON response exceeds 4 MiB."));
        }
        if self.body.is_empty() {
            return serde_json::from_value(serde_json::Value::Null)
                .map_err(|_| self.invalid_response("empty response does not match the contract."));
        }
        serde_json::from_slice(&self.body).map_err(|error| {
            // Serde's Display can echo arbitrary response strings, including
            // minted credentials. Only its category and position are safe.
            let category = match error.classify() {
                serde_json::error::Category::Io => "I/O",
                serde_json::error::Category::Syntax => "syntax",
                serde_json::error::Category::Data => "data",
                serde_json::error::Category::Eof => "end-of-input",
            };
            self.invalid_response(format!(
                "response body does not match the contract: {category} error at line {}, column {}.",
                error.line(),
                error.column()
            ))
        })
    }

    pub(crate) fn invalid_response(&self, message: impl Into<String>) -> Error {
        let mut error = Error::invalid_response(message);
        error.status = Some(self.status);
        error.request_id = self.header("x-request-id").map(str::to_owned);
        error
    }
}

/// Sends [`Request`]s. Implement this to inject a custom HTTP stack.
#[async_trait]
pub trait HttpTransport: Send + Sync + std::fmt::Debug {
    /// Send one request and return the raw response without following redirects.
    async fn send(&self, request: Request) -> Result<Response>;
}

/// Production transport backed by `reqwest` + rustls.
#[derive(Debug, Clone)]
pub struct ReqwestTransport {
    inner: reqwest::Client,
}

impl ReqwestTransport {
    /// Build with rustls and automatic redirects disabled.
    #[must_use]
    pub fn new() -> Self {
        Self {
            inner: reqwest::Client::builder()
                .redirect(reqwest::redirect::Policy::none())
                .connect_timeout(std::time::Duration::from_secs(5))
                .build()
                .expect("HTTP client initialization"),
        }
    }

    /// Build an HTTPS transport with additional PEM root certificates.
    /// Certificate chains and hostnames remain verified; redirects are disabled.
    pub fn with_root_certificates(pem: &[u8]) -> Result<Self> {
        let invalid_certificate = || {
            Error::new(
                crate::ErrorKind::Validation,
                "invalid_root_certificate",
                "Invalid PEM root certificate bundle.",
            )
        };
        let certificates =
            reqwest::Certificate::from_pem_bundle(pem).map_err(|_| invalid_certificate())?;
        if certificates.is_empty() {
            return Err(invalid_certificate());
        }
        let mut builder = reqwest::Client::builder()
            .https_only(true)
            .redirect(reqwest::redirect::Policy::none())
            .connect_timeout(std::time::Duration::from_secs(5));
        for certificate in certificates {
            builder = builder.add_root_certificate(certificate);
        }
        let inner = builder.build().map_err(|_| invalid_certificate())?;
        Ok(Self { inner })
    }
}

impl Default for ReqwestTransport {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait]
impl HttpTransport for ReqwestTransport {
    async fn send(&self, request: Request) -> Result<Response> {
        let method = match request.method {
            Method::Get => reqwest::Method::GET,
            Method::Post => reqwest::Method::POST,
            Method::Put => reqwest::Method::PUT,
            Method::Patch => reqwest::Method::PATCH,
            Method::Delete => reqwest::Method::DELETE,
        };
        let mut builder = self.inner.request(method, request.url);
        for (name, value) in &request.headers {
            builder = builder.header(name.as_str(), value.as_str());
        }
        if let Some(body) = request.body {
            builder = builder.body(body);
        }
        let response = builder
            .send()
            .await
            .map_err(|e| Error::transport(format!("request failed: {e}")))?;
        let status = response.status().as_u16();
        let headers: Vec<(String, String)> = response
            .headers()
            .iter()
            .map(|(k, v)| {
                (
                    k.as_str().to_owned(),
                    v.to_str().unwrap_or_default().to_owned(),
                )
            })
            .collect();
        let mut stream = response.bytes_stream();
        let mut body = bytes::BytesMut::new();
        while let Some(chunk) = stream.next().await {
            let chunk =
                chunk.map_err(|e| Error::transport(format!("reading the response failed: {e}")))?;
            if body.len().saturating_add(chunk.len()) > MAX_JSON_BYTES {
                return Err(Response {
                    status,
                    headers,
                    body: body.freeze(),
                }
                .invalid_response("JSON response exceeds 4 MiB."));
            }
            body.extend_from_slice(&chunk);
        }
        let body = body.freeze();
        Ok(Response {
            status,
            headers,
            body,
        })
    }
}
