//! Typed v1 errors.

use std::time::Duration;

use serde::Deserialize;
use serde_json::value::RawValue;
use thiserror::Error;

/// Result alias for the v1 client.
pub type Result<T, E = Error> = std::result::Result<T, E>;

/// A v1 client error.
///
/// Well-known server codes map to dedicated variants; every other non-2xx
/// response is [`Error::Api`]. Project failures wrap the decoded error in
/// [`Error::Response`] to retain the HTTP diagnostics.
#[derive(Debug, Error)]
#[non_exhaustive]
pub enum Error {
    /// API-key exchange failed before the pending attempt or replay was sent;
    /// any preceding request was refused with 401. The code and reason are
    /// SDK-owned; upstream free text and transport text are omitted.
    #[error("terrabase: token exchange: {code} ({}){}", fmt_http(*status), fmt_reason(*reason))]
    TokenExchange {
        status: Option<u16>,
        code: &'static str,
        /// SDK-owned explanation: the issuer's RFC 6749 `error` code with a
        /// fixed description, or the transport failure class.
        reason: Option<&'static str>,
        /// The issuer's correlation id, when it sent one.
        request_id: Option<String>,
    },
    /// A project API failure with its complete HTTP diagnostics. The inner
    /// error retains the decoded code and typed error, when present.
    #[error("{error}")]
    Response {
        /// Actual HTTP status, independent of the response body.
        status: u16,
        /// Whether the failed request was a write.
        mutation: bool,
        #[source]
        error: Box<Error>,
        /// Exact response bytes, including malformed or empty bodies.
        body: Vec<u8>,
        /// Server correlation id, preferring the HTTP header over the body.
        request_id: Option<String>,
    },
    /// `404 not_found`.
    #[error("terrabase: 404 not_found")]
    NotFound,
    /// The request must reach the region that orders the database's writes.
    #[error("terrabase: 421 home_region_required")]
    HomeRegionRequired,
    /// `503 commit_uncertain`: read state to reconcile the unknown outcome.
    #[error("terrabase: 503 commit_uncertain")]
    CommitUncertain,
    /// `429 rate_limited`, after the retry budget was spent.
    #[error("terrabase: 429 rate_limited")]
    RateLimited { retry_after: Option<Duration> },
    /// `403 forbidden_scope`.
    #[error("terrabase: 403 forbidden_scope")]
    Forbidden,
    /// `401 invalid_key`: unknown, expired or revoked key.
    #[error("terrabase: 401 invalid_key")]
    InvalidKey,
    /// `401 key_revoked`: permanent; replace the key without retry or exchange.
    #[error("terrabase: 401 key_revoked{}", fmt_msg(message))]
    KeyRevoked { message: Option<String> },
    /// `401 key_expired`: permanent; replace the key without retry or exchange.
    #[error("terrabase: 401 key_expired{}", fmt_msg(message))]
    KeyExpired { message: Option<String> },
    /// `500 internal_error`: a write may have committed.
    #[error("terrabase: 500 internal_error")]
    InternalError,
    /// A refused SQL statement; the request rolled back. `status` is the HTTP
    /// status over REST (`sql/execute`) and `None` over a SQL session.
    #[error("terrabase: {}{error}", status.map(|s| format!("{s} ")).unwrap_or_default())]
    Sql {
        status: Option<u16>,
        error: SqlError,
    },
    /// Any other non-2xx response.
    #[error("terrabase: {status} {code}{}", fmt_msg(message))]
    Api {
        status: u16,
        code: String,
        message: Option<String>,
        /// The raw error object, for route-specific members.
        details: Option<Box<RawValue>>,
    },
    /// The request was not sent or no response was read (connect, TLS,
    /// timeout, body too large). A mutation may or may not have been applied.
    #[error("terrabase: transport: {0}")]
    Transport(String),
    /// A response body exceeded the configured
    /// [`max_response_bytes`](super::ClientBuilder::max_response_bytes). The
    /// server answered, so it is not retried. `may_have_applied` is true for
    /// a mutation whose status was not a 4xx (a success or a server error such
    /// as `commit_uncertain`): the write may have taken effect, so
    /// [`Error::is_uncertain`] is true; reconcile by reading state.
    #[error("terrabase: {status} response exceeds {limit} bytes")]
    ResponseTooLarge {
        limit: usize,
        status: u16,
        may_have_applied: bool,
    },
    /// The caller passed an invalid argument; nothing was sent.
    #[error("terrabase: invalid input: {0}")]
    InvalidInput(String),
    /// A 2xx response to a read did not match the contract.
    #[error("terrabase: invalid response: {0}")]
    Decode(String),
    /// A mutation got a 2xx status but its acknowledgement did not match the
    /// contract. The write was accepted, so [`Error::is_uncertain`] is true:
    /// read back state to learn the outcome.
    #[error("terrabase: {status} acknowledgement could not be decoded: {message}")]
    UndecodableAck { status: u16, message: String },
}

/// A refused SQL statement. The request rolled back. `code` is one of
/// [`sql_error_codes`](crate::v1::sql_error_codes).
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct SqlError {
    pub code: String,
    pub message: String,
    /// The zero-based index of the failing statement.
    pub statement: usize,
    /// The PostgreSQL SQLSTATE of `code`, such as `23505`.
    #[serde(default)]
    pub sqlstate: Option<String>,
}

impl SqlError {
    /// True when sending the same request again can succeed.
    #[must_use]
    pub fn is_transient(&self) -> bool {
        matches!(
            self.code.as_str(),
            crate::v1::sql_error_codes::SERIALIZATION_CONFLICT
                | crate::v1::sql_error_codes::TENANT_BUSY
        )
    }
}

impl std::fmt::Display for SqlError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "{}: {} (statement {})",
            self.code, self.message, self.statement
        )?;
        if let Some(sqlstate) = &self.sqlstate {
            write!(f, " [{sqlstate}]")?;
        }
        Ok(())
    }
}

fn fmt_http(status: Option<u16>) -> String {
    status.map_or_else(
        || "no HTTP response".into(),
        |status| format!("HTTP {status}"),
    )
}

fn fmt_reason(reason: Option<&str>) -> String {
    reason.map(|r| format!(": {r}")).unwrap_or_default()
}

fn fmt_msg(message: &Option<String>) -> String {
    message
        .as_ref()
        .map(|m| format!(": {m}"))
        .unwrap_or_default()
}

impl Error {
    /// The HTTP status, for server errors.
    #[must_use]
    pub fn status(&self) -> Option<u16> {
        Some(match self {
            Self::TokenExchange { status, .. } => return *status,
            Self::Response { status, .. }
            | Self::ResponseTooLarge { status, .. }
            | Self::UndecodableAck { status, .. } => *status,
            Self::NotFound => 404,
            Self::HomeRegionRequired => 421,
            Self::CommitUncertain => 503,
            Self::RateLimited { .. } => 429,
            Self::Forbidden => 403,
            Self::InvalidKey | Self::KeyRevoked { .. } | Self::KeyExpired { .. } => 401,
            Self::InternalError => 500,
            Self::Api { status, .. } => *status,
            Self::Sql { status, .. } => return *status,
            Self::Transport(_) | Self::InvalidInput(_) | Self::Decode(_) => return None,
        })
    }

    /// The server error code, for server errors.
    #[must_use]
    pub fn code(&self) -> Option<&str> {
        Some(match self {
            Self::TokenExchange { code, .. } => code,
            Self::Response { error, .. } => return error.code(),
            Self::NotFound => "not_found",
            Self::HomeRegionRequired => "home_region_required",
            Self::CommitUncertain => "commit_uncertain",
            Self::RateLimited { .. } => "rate_limited",
            Self::Forbidden => "forbidden_scope",
            Self::InvalidKey => "invalid_key",
            Self::KeyRevoked { .. } => "key_revoked",
            Self::KeyExpired { .. } => "key_expired",
            Self::InternalError => "internal_error",
            Self::Api { code, .. } => code,
            Self::Sql { error, .. } => &error.code,
            Self::ResponseTooLarge { .. }
            | Self::Transport(_)
            | Self::InvalidInput(_)
            | Self::Decode(_)
            | Self::UndecodableAck { .. } => return None,
        })
    }

    /// The typed relational SQL failure, when this is one.
    #[must_use]
    pub fn sql(&self) -> Option<&SqlError> {
        match self {
            Self::Response { error, .. } => error.sql(),
            Self::Sql { error, .. } => Some(error),
            _ => None,
        }
    }

    /// True for `404 not_found`.
    #[must_use]
    pub fn is_not_found(&self) -> bool {
        match self {
            Self::Response { status, error, .. } => *status == 404 && error.is_not_found(),
            Self::NotFound => true,
            Self::Api { status, code, .. } => *status == 404 && code == "not_found",
            _ => false,
        }
    }

    /// Exact bytes of a project API error response, including unknown problem
    /// extensions and invalid JSON; local client errors have none.
    #[must_use]
    pub fn raw_body(&self) -> Option<&[u8]> {
        match self {
            Self::Response { body, .. } => Some(body),
            _ => None,
        }
    }

    /// Server correlation id from a project API or token exchange error.
    #[must_use]
    pub fn request_id(&self) -> Option<&str> {
        match self {
            Self::Response { request_id, .. } | Self::TokenExchange { request_id, .. } => {
                request_id.as_deref()
            }
            _ => None,
        }
    }

    /// True for a write with no response after sending, an uncoded 5xx, or
    /// commit_uncertain, write_outcome_unknown or internal_error. Other coded
    /// errors and rolled-back SQL requests are definite. Response reads are
    /// never uncertain. Reconcile before repeating an uncertain write.
    #[must_use]
    pub fn is_uncertain(&self) -> bool {
        match self {
            Self::Response {
                mutation, error, ..
            } => *mutation && error.is_uncertain(),
            Self::CommitUncertain
            | Self::InternalError
            | Self::Transport(_)
            | Self::UndecodableAck { .. } => true,
            Self::ResponseTooLarge {
                may_have_applied, ..
            } => *may_have_applied,
            Self::Api { status, code, .. } => {
                matches!(
                    code.as_str(),
                    "commit_uncertain" | "write_outcome_unknown" | "internal_error"
                ) || (500..600).contains(status) && code == &format!("http_{status}")
            }
            _ => false,
        }
    }
}

/// Maps a project API failure without discarding its HTTP diagnostics.
pub(crate) fn from_project_response(
    status: u16,
    retry_after: Option<Duration>,
    request_id: Option<&str>,
    body: &[u8],
    mutation: bool,
) -> Error {
    let value = serde_json::from_slice::<std::collections::HashMap<String, &RawValue>>(body).ok();
    let problem = value.as_ref().filter(|value| {
        ["type", "title", "status", "detail", "instance"]
            .iter()
            .any(|key| value.contains_key(*key))
            && !value.contains_key("error")
    });
    let error = if let Some(problem) = problem {
        let text = |name: &str| {
            problem
                .get(name)
                .and_then(|raw| serde_json::from_str::<String>(raw.get()).ok())
        };
        // The HTTP status is authoritative; a problem's status cannot change
        // retry decisions or turn a denied request into another error kind.
        Error::Api {
            status,
            code: text("code")
                .filter(|code| !code.is_empty())
                .unwrap_or_else(|| format!("http_{status}")),
            message: text("detail").or_else(|| text("title")),
            // The wrapper retains every member and the exact number text.
            details: None,
        }
    } else {
        from_response(status, retry_after, body)
    };
    Error::Response {
        status,
        mutation,
        error: Box::new(error),
        body: body.to_vec(),
        request_id: request_id.map(str::to_owned).or_else(|| {
            problem
                .and_then(|problem| problem.get("requestId"))
                .and_then(|raw| serde_json::from_str::<String>(raw.get()).ok())
        }),
    }
}

#[derive(Deserialize)]
struct Envelope<'a> {
    #[serde(borrow)]
    error: Option<&'a RawValue>,
}

#[derive(Deserialize)]
struct Body {
    code: Option<String>,
    message: Option<String>,
}

/// Maps a non-2xx response to an [`Error`].
pub(crate) fn from_response(status: u16, retry_after: Option<Duration>, body: &[u8]) -> Error {
    let raw = serde_json::from_slice::<Envelope<'_>>(body)
        .ok()
        .and_then(|e| e.error.map(|r| r.to_owned()));
    let parsed = raw
        .as_ref()
        .and_then(|r| serde_json::from_str::<Body>(r.get()).ok());
    let (code, message) = match parsed {
        Some(b) => (b.code, b.message),
        None => (None, None),
    };
    let code = code
        .filter(|code| !code.is_empty())
        .unwrap_or_else(|| format!("http_{status}"));
    let sql = raw
        .as_ref()
        .and_then(|r| serde_json::from_str::<SqlError>(r.get()).ok());
    if let Some(error) = sql {
        return Error::Sql {
            status: Some(status),
            error,
        };
    }
    match (status, code.as_str()) {
        (404, "not_found") => Error::NotFound,
        (421, "home_region_required") => Error::HomeRegionRequired,
        (503, "commit_uncertain") => Error::CommitUncertain,
        (429, "rate_limited") => Error::RateLimited { retry_after },
        (403, "forbidden_scope") => Error::Forbidden,
        (401, "invalid_key") => Error::InvalidKey,
        (401, "key_revoked") => Error::KeyRevoked { message },
        (401, "key_expired") => Error::KeyExpired { message },
        (500, "internal_error") => Error::InternalError,
        _ => Error::Api {
            status,
            code,
            message,
            details: raw,
        },
    }
}
