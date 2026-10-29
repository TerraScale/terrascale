//! Typed v1 errors (api-v1.md "Errors").

use std::time::Duration;

use serde::Deserialize;
use serde_json::value::RawValue;
use thiserror::Error;

use super::ids::Version;

/// Result alias for the v1 client.
pub type Result<T, E = Error> = std::result::Result<T, E>;

/// A v1 client error.
///
/// Well-known server codes map to dedicated variants; every other non-2xx
/// response is [`Error::Api`]. Project failures wrap the decoded error in
/// [`Error::Response`] to retain the HTTP diagnostics. `op` is the failing
/// transaction op index when the server names one.
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
        #[source]
        error: Box<Error>,
        /// Exact response bytes, including malformed or empty bodies. Webhook
        /// creation errors omit these bytes to protect one-time secrets.
        body: Vec<u8>,
        /// Server correlation id, preferring the HTTP header over the body.
        request_id: Option<String>,
    },
    /// `412 precondition_failed`: a failed `If-Match`, `If-None-Match: *`,
    /// `assert` or `assert_count`.
    #[error("terrabase: 412 precondition_failed{}", fmt_op(*op))]
    PreconditionFailed { op: Option<usize> },
    /// `409 unique_violation` on the named constraint.
    #[error("terrabase: 409 unique_violation (constraint {constraint}){}", fmt_op(*op))]
    UniqueViolation {
        constraint: String,
        op: Option<usize>,
    },
    /// `404 not_found`. For a deleted document `etag` holds the tombstone's
    /// version, so it can be recreated with `If-Match`.
    #[error("terrabase: 404 not_found{}", fmt_op(*op))]
    NotFound {
        etag: Option<Version>,
        op: Option<usize>,
    },
    /// `409 conflict_unresolved`: the document has concurrent siblings; resolve
    /// it with a PUT whose `If-Match` covers its version.
    #[error("terrabase: 409 conflict_unresolved{}", fmt_op(*op))]
    ConflictUnresolved { op: Option<usize> },
    /// `421 home_region_required`: the write reached a non-home region.
    #[error("terrabase: 421 home_region_required")]
    HomeRegionRequired,
    /// `503 commit_uncertain`: read state to reconcile the unknown outcome.
    #[error("terrabase: 503 commit_uncertain")]
    CommitUncertain,
    /// `429 rate_limited`, after the retry budget was spent.
    #[error("terrabase: 429 rate_limited")]
    RateLimited { retry_after: Option<Duration> },
    /// `403 forbidden_scope`.
    #[error("terrabase: 403 forbidden_scope{}", fmt_op(*op))]
    Forbidden { op: Option<usize> },
    /// `401 invalid_key`: unknown, expired or revoked key.
    #[error("terrabase: 401 invalid_key")]
    InvalidKey,
    /// `401 key_revoked`: permanent; replace the key without retry or exchange.
    #[error("terrabase: 401 key_revoked{}", fmt_msg(message))]
    KeyRevoked { message: Option<String> },
    /// `401 key_expired`: permanent; replace the key without retry or exchange.
    #[error("terrabase: 401 key_expired{}", fmt_msg(message))]
    KeyExpired { message: Option<String> },
    /// `500 internal_error`: a mutation may have taken effect.
    #[error("terrabase: 500 internal_error")]
    InternalError,
    /// A relational SQL failure from `sql/execute`: `sql_error` (400),
    /// `forbidden_scope` (403), `sql_conflict` (409) or `sql_too_large` (413).
    /// The whole request was rolled back.
    #[error("terrabase: {status} {error}")]
    Sql { status: u16, error: SqlError },
    /// `410 cursor_expired`: the changes cursor fell out of retention;
    /// restart from a fresh position.
    #[error("terrabase: 410 cursor_expired")]
    CursorExpired,
    /// Any other non-2xx response.
    #[error("terrabase: {status} {code}{}{}", fmt_msg(message), fmt_op(*op))]
    Api {
        status: u16,
        code: String,
        message: Option<String>,
        op: Option<usize>,
        /// The raw error object, for route-specific members such as
        /// `suggested_index`.
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
    /// Transaction op `op` is invalid; nothing was sent.
    #[error("terrabase: invalid transaction op {op}: {message}")]
    InvalidTxOp { op: usize, message: String },
    /// The caller passed an invalid argument; nothing was sent.
    #[error("terrabase: invalid input: {0}")]
    InvalidInput(String),
    /// A 2xx response to a read did not match the contract.
    #[error("terrabase: invalid response: {0}")]
    Decode(String),
    /// A mutation got a 2xx status but its acknowledgement did not match the
    /// contract. The write was accepted, so [`Error::is_uncertain`] is true:
    /// read back state to learn the
    /// outcome.
    #[error("terrabase: {status} acknowledgement could not be decoded: {message}")]
    UndecodableAck { status: u16, message: String },
}

/// The typed body of a relational SQL failure.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct SqlError {
    pub code: String,
    pub message: String,
    /// The 0-based index of the failing statement, when one failed.
    pub statement: Option<usize>,
    /// The SQLite extended result code, such as `SQLITE_CONSTRAINT_PRIMARYKEY`.
    pub sqlite_code: Option<String>,
}

impl std::fmt::Display for SqlError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}: {}", self.code, self.message)?;
        if let Some(statement) = self.statement {
            write!(f, " (statement {statement})")?;
        }
        if let Some(code) = &self.sqlite_code {
            write!(f, " [{code}]")?;
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

fn fmt_op(op: Option<usize>) -> String {
    op.map(|op| format!(" (op {op})")).unwrap_or_default()
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
            Self::PreconditionFailed { .. } => 412,
            Self::UniqueViolation { .. } | Self::ConflictUnresolved { .. } => 409,
            Self::NotFound { .. } => 404,
            Self::HomeRegionRequired => 421,
            Self::CommitUncertain => 503,
            Self::RateLimited { .. } => 429,
            Self::Forbidden { .. } => 403,
            Self::InvalidKey | Self::KeyRevoked { .. } | Self::KeyExpired { .. } => 401,
            Self::InternalError => 500,
            Self::CursorExpired => 410,
            Self::Api { status, .. } | Self::Sql { status, .. } => *status,
            Self::InvalidTxOp { .. }
            | Self::Transport(_)
            | Self::InvalidInput(_)
            | Self::Decode(_) => return None,
        })
    }

    /// The server error code, for server errors.
    #[must_use]
    pub fn code(&self) -> Option<&str> {
        Some(match self {
            Self::TokenExchange { code, .. } => code,
            Self::Response { error, .. } => return error.code(),
            Self::PreconditionFailed { .. } => "precondition_failed",
            Self::UniqueViolation { .. } => "unique_violation",
            Self::NotFound { .. } => "not_found",
            Self::ConflictUnresolved { .. } => "conflict_unresolved",
            Self::HomeRegionRequired => "home_region_required",
            Self::CommitUncertain => "commit_uncertain",
            Self::RateLimited { .. } => "rate_limited",
            Self::Forbidden { .. } => "forbidden_scope",
            Self::InvalidKey => "invalid_key",
            Self::KeyRevoked { .. } => "key_revoked",
            Self::KeyExpired { .. } => "key_expired",
            Self::InternalError => "internal_error",
            Self::CursorExpired => "cursor_expired",
            Self::Api { code, .. } => code,
            Self::Sql { error, .. } => &error.code,
            Self::ResponseTooLarge { .. }
            | Self::InvalidTxOp { .. }
            | Self::Transport(_)
            | Self::InvalidInput(_)
            | Self::Decode(_)
            | Self::UndecodableAck { .. } => return None,
        })
    }

    /// The failing transaction op index, when the server names one.
    #[must_use]
    pub fn op(&self) -> Option<usize> {
        match self {
            Self::Response { error, .. } => error.op(),
            Self::PreconditionFailed { op }
            | Self::UniqueViolation { op, .. }
            | Self::NotFound { op, .. }
            | Self::ConflictUnresolved { op }
            | Self::Forbidden { op }
            | Self::Api { op, .. } => *op,
            Self::InvalidTxOp { op, .. } => Some(*op),
            _ => None,
        }
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
            Self::NotFound { .. } => true,
            Self::Api { status, code, .. } => *status == 404 && code == "not_found",
            _ => false,
        }
    }

    /// True for `cursor_expired` from the changes feed, as a 410 or a stream error event.
    #[must_use]
    pub fn is_cursor_expired(&self) -> bool {
        self.code() == Some("cursor_expired")
    }

    /// Exact bytes of a project API error response, including unknown problem
    /// extensions and invalid JSON. Webhook creation errors return empty bytes;
    /// local client errors have none.
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

    /// True when a mutation may have been applied although an error was
    /// returned: `commit_uncertain`, `internal_error`, any other 5xx (a
    /// gateway or unknown server failure may follow a commit), a non-2xx
    /// status outside 4xx, a transport failure, an oversized response to a
    /// mutation that may have been applied, or an undecodable mutation
    /// acknowledgement. Only 4xx responses and local errors are certain
    /// failures. Reconcile an uncertain mutation by reading its effects.
    /// For reads, which have no effect, the flag only means the request may be
    /// retried.
    #[must_use]
    pub fn is_uncertain(&self) -> bool {
        match self {
            Self::Response { status, error, .. } => {
                !(400..500).contains(status) && error.is_uncertain()
            }
            Self::CommitUncertain
            | Self::InternalError
            | Self::Transport(_)
            | Self::UndecodableAck { .. } => true,
            Self::ResponseTooLarge {
                may_have_applied, ..
            } => *may_have_applied,
            Self::Api { status, .. } | Self::Sql { status, .. } => !(400..500).contains(status),
            _ => false,
        }
    }
}

/// Maps a project API failure without discarding its HTTP diagnostics.
pub(crate) fn from_project_response(
    status: u16,
    etag: Option<&str>,
    retry_after: Option<Duration>,
    request_id: Option<&str>,
    body: &[u8],
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
            code: text("code").unwrap_or_else(|| format!("http_{status}")),
            message: text("detail").or_else(|| text("title")),
            op: problem
                .get("op")
                .and_then(|raw| serde_json::from_str::<usize>(raw.get()).ok()),
            // The wrapper retains every member and the exact number text.
            details: None,
        }
    } else {
        from_response(status, etag, retry_after, body)
    };
    Error::Response {
        status,
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
    op: Option<usize>,
    constraint: Option<String>,
}

/// Maps a non-2xx response to an [`Error`].
pub(crate) fn from_response(
    status: u16,
    etag: Option<&str>,
    retry_after: Option<Duration>,
    body: &[u8],
) -> Error {
    let raw = serde_json::from_slice::<Envelope<'_>>(body)
        .ok()
        .and_then(|e| e.error.map(|r| r.to_owned()));
    let parsed = raw
        .as_ref()
        .and_then(|r| serde_json::from_str::<Body>(r.get()).ok());
    let (code, message, op, constraint) = match parsed {
        Some(b) => (b.code, b.message, b.op, b.constraint),
        None => (None, None, None, None),
    };
    let code = code.unwrap_or_else(|| format!("http_{status}"));
    let sql = raw
        .as_ref()
        .and_then(|r| serde_json::from_str::<SqlError>(r.get()).ok())
        .filter(|sql| sql.code.starts_with("sql_") || sql.statement.is_some());
    if let Some(error) = sql {
        return Error::Sql { status, error };
    }
    match (status, code.as_str()) {
        (412, "precondition_failed") => Error::PreconditionFailed { op },
        (409, "unique_violation") => Error::UniqueViolation {
            constraint: constraint.unwrap_or_default(),
            op,
        },
        (404, "not_found") => Error::NotFound {
            etag: etag.and_then(Version::from_etag),
            op,
        },
        (409, "conflict_unresolved") => Error::ConflictUnresolved { op },
        (421, "home_region_required") => Error::HomeRegionRequired,
        (503, "commit_uncertain") => Error::CommitUncertain,
        (429, "rate_limited") => Error::RateLimited { retry_after },
        (403, "forbidden_scope") => Error::Forbidden { op },
        (401, "invalid_key") => Error::InvalidKey,
        (401, "key_revoked") => Error::KeyRevoked { message },
        (401, "key_expired") => Error::KeyExpired { message },
        (500, "internal_error") => Error::InternalError,
        (410, "cursor_expired") => Error::CursorExpired,
        _ => Error::Api {
            status,
            code,
            message,
            op,
            details: raw,
        },
    }
}
