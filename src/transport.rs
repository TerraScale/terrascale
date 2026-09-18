//! Pluggable HTTP transport. The SDK core builds typed [`Request`]s; a
//! [`HttpTransport`] sends them. Production uses [`ReqwestTransport`]; tests
//! inject a mock.

use async_trait::async_trait;
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
#[derive(Debug, Clone)]
pub struct Request {
    /// Route name for observability (e.g. `documents.create`).
    pub route_name: String,
    pub method: Method,
    pub url: String,
    pub headers: Vec<(String, String)>,
    pub body: Option<Vec<u8>>,
}

impl Request {
    /// Build a JSON request with the given serializable body.
    pub fn json(
        route_name: impl Into<String>,
        method: Method,
        url: String,
        body: &impl serde::Serialize,
    ) -> Result<Self> {
        let bytes = serde_json::to_vec(body).map_err(|e| {
            Error::invalid_response(format!("request body is not serializable: {e}"))
        })?;
        Ok(Self {
            route_name: route_name.into(),
            method,
            url,
            headers: vec![("content-type".to_owned(), "application/json".to_owned())],
            body: Some(bytes),
        })
    }
}

/// One incoming response.
#[derive(Debug, Clone)]
pub struct Response {
    pub status: u16,
    pub headers: Vec<(String, String)>,
    /// Raw body kept as shared bytes: large artifacts are not copied on
    /// transport boundaries.
    pub body: bytes::Bytes,
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

    /// Decode a JSON body against the pinned contract.
    pub fn json_body<T: DeserializeOwned>(&self) -> Result<T> {
        if self.header("content-type").is_some_and(|value| {
            value
                .split(';')
                .next()
                .is_some_and(|kind| kind.trim().eq_ignore_ascii_case("application/msgpack"))
        }) {
            return crate::reactive::decode_reactive_msgpack(&self.body);
        }

        // A 204/304 long-poll response (and a successful empty timeout body)
        // is the contract's empty response. Decode it as JSON null so the
        // reactive response's custom deserializer can turn it into `default`.
        if self.body.is_empty() {
            return serde_json::from_value(serde_json::Value::Null).map_err(|e| {
                Error::invalid_response(format!("empty response does not match the contract: {e}"))
            });
        }
        serde_json::from_slice(&self.body).map_err(|e| {
            Error::invalid_response(format!("response body does not match the contract: {e}"))
        })
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
                .build()
                .expect("HTTP client initialization"),
        }
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
        let body = response
            .bytes()
            .await
            .map_err(|e| Error::transport(format!("reading the response failed: {e}")))?;
        Ok(Response {
            status,
            headers,
            body,
        })
    }
}
