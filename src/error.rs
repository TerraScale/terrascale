//! Typed SDK errors, mirroring the `@terrascale/sdk` result model.

use std::time::Duration;

use thiserror::Error;

/// Machine-readable failure kinds returned by [`Error`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ErrorKind {
    Authentication,
    Authorization,
    NotFound,
    Conflict,
    Validation,
    RateLimit,
    Server,
    Transport,
    InvalidResponse,
    Unavailable,
    Domain,
    /// The request landed in a region that does not host the database
    /// (HTTP 421 + `X-TerraScale-Placement`). The SDK repairs its pin from
    /// [`Error::placement`] and the caller may retry.
    RegionRedirect,
}

/// The single error type for the whole SDK surface.
#[derive(Debug, Error, Clone)]
#[error("[{kind:?}] {code}: {message}")]
pub struct Error {
    /// Stable machine-readable kind.
    pub kind: ErrorKind,
    /// Server-provided code, or a client-side equivalent.
    pub code: String,
    /// Human-readable message carrying the fix, never just the status.
    pub message: String,
    /// Optional server-provided remediation, kept separately from `message`.
    pub fix: Option<Box<str>>,
    /// HTTP status when the error came from a response.
    pub status: Option<u16>,
    /// Server request id for support correlation.
    pub request_id: Option<String>,
    /// Placement hint from a 421 response (`X-TerraScale-Placement`).
    pub placement: Option<String>,
    /// `Retry-After` delay requested by the response, seconds form only (the
    /// HTTP-date form is not parsed), stored uncapped.
    pub retry_after: Option<Duration>,
}

impl Error {
    /// Build a fully specified error.
    pub fn new(kind: ErrorKind, code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            kind,
            code: code.into(),
            message: message.into(),
            fix: None,
            status: None,
            request_id: None,
            placement: None,
            retry_after: None,
        }
    }

    /// A transport-level failure (DNS, TLS, timeout, connect).
    pub fn transport(message: impl Into<String>) -> Self {
        Self::new(ErrorKind::Transport, "transport", message)
    }

    /// A response that does not match the pinned contract.
    pub fn invalid_response(message: impl Into<String>) -> Self {
        Self::new(ErrorKind::InvalidResponse, "invalid_response", message)
    }

    /// Map an HTTP status to the documented kind.
    pub fn for_status(status: u16, code: String, message: String) -> Self {
        let kind = match status {
            400 | 422 => ErrorKind::Validation,
            401 => ErrorKind::Authentication,
            403 => ErrorKind::Authorization,
            404 => ErrorKind::NotFound,
            409 => ErrorKind::Conflict,
            421 => ErrorKind::RegionRedirect,
            429 => ErrorKind::RateLimit,
            502..=504 => ErrorKind::Unavailable,
            500..=599 => ErrorKind::Server,
            _ => ErrorKind::Domain,
        };
        Self {
            kind,
            code,
            message,
            fix: None,
            status: Some(status),
            request_id: None,
            placement: None,
            retry_after: None,
        }
    }
}

/// Convenience alias used across the SDK surface.
pub type Result<T> = std::result::Result<T, Error>;
