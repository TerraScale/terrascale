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
/// response is [`Error::Api`]. `op` is the failing transaction op index when
/// the server names one.
#[derive(Debug, Error)]
#[non_exhaustive]
pub enum Error {
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
    /// `503 commit_uncertain`: the outcome is unknown. Retry with the same
    /// `Idempotency-Key` to learn it.
    #[error("terrabase: 503 commit_uncertain")]
    CommitUncertain,
    /// `409 idempotency_conflict`: the key was used with a different request.
    #[error("terrabase: 409 idempotency_conflict")]
    IdempotencyConflict,
    /// `409 idempotency_expired`: the key's 24 hour replay window passed.
    #[error("terrabase: 409 idempotency_expired")]
    IdempotencyExpired,
    /// `429 rate_limited`, after the retry budget was spent.
    #[error("terrabase: 429 rate_limited")]
    RateLimited { retry_after: Option<Duration> },
    /// `403 forbidden_scope`.
    #[error("terrabase: 403 forbidden_scope{}", fmt_op(*op))]
    Forbidden { op: Option<usize> },
    /// `401 invalid_key`: unknown, expired or revoked key.
    #[error("terrabase: 401 invalid_key")]
    InvalidKey,
    /// `500 internal_error`: no effect is guaranteed; retry only with an
    /// `Idempotency-Key`.
    #[error("terrabase: 500 internal_error")]
    InternalError,
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
    /// A control-plane operation finished with status `failed`.
    #[error("terrabase: operation {id} failed: {code}{}", fmt_msg(message))]
    OperationFailed {
        id: String,
        code: String,
        message: Option<String>,
    },
    /// The request was not sent or no response was read (connect, TLS,
    /// timeout, body too large). A mutation without an `Idempotency-Key`
    /// may or may not have been applied.
    #[error("terrabase: transport: {0}")]
    Transport(String),
    /// A response body exceeded the configured
    /// [`max_response_bytes`](super::ClientBuilder::max_response_bytes). The
    /// server answered, so it is not retried.
    #[error("terrabase: response exceeds {limit} bytes")]
    ResponseTooLarge { limit: usize },
    /// Transaction op `op` is invalid; nothing was sent.
    #[error("terrabase: invalid transaction op {op}: {message}")]
    InvalidTxOp { op: usize, message: String },
    /// The caller passed an invalid argument; nothing was sent.
    #[error("terrabase: invalid input: {0}")]
    InvalidInput(String),
    /// A 2xx response did not match the contract.
    #[error("terrabase: invalid response: {0}")]
    Decode(String),
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
            Self::PreconditionFailed { .. } => 412,
            Self::UniqueViolation { .. } | Self::ConflictUnresolved { .. } => 409,
            Self::IdempotencyConflict | Self::IdempotencyExpired => 409,
            Self::NotFound { .. } => 404,
            Self::HomeRegionRequired => 421,
            Self::CommitUncertain => 503,
            Self::RateLimited { .. } => 429,
            Self::Forbidden { .. } => 403,
            Self::InvalidKey => 401,
            Self::InternalError => 500,
            Self::Api { status, .. } => *status,
            Self::OperationFailed { .. }
            | Self::ResponseTooLarge { .. }
            | Self::InvalidTxOp { .. }
            | Self::Transport(_)
            | Self::InvalidInput(_)
            | Self::Decode(_) => return None,
        })
    }

    /// The server error code, for server errors.
    #[must_use]
    pub fn code(&self) -> Option<&str> {
        Some(match self {
            Self::PreconditionFailed { .. } => "precondition_failed",
            Self::UniqueViolation { .. } => "unique_violation",
            Self::NotFound { .. } => "not_found",
            Self::ConflictUnresolved { .. } => "conflict_unresolved",
            Self::HomeRegionRequired => "home_region_required",
            Self::CommitUncertain => "commit_uncertain",
            Self::IdempotencyConflict => "idempotency_conflict",
            Self::IdempotencyExpired => "idempotency_expired",
            Self::RateLimited { .. } => "rate_limited",
            Self::Forbidden { .. } => "forbidden_scope",
            Self::InvalidKey => "invalid_key",
            Self::InternalError => "internal_error",
            Self::Api { code, .. } | Self::OperationFailed { code, .. } => code,
            Self::ResponseTooLarge { .. }
            | Self::InvalidTxOp { .. }
            | Self::Transport(_)
            | Self::InvalidInput(_)
            | Self::Decode(_) => return None,
        })
    }

    /// The failing transaction op index, when the server names one.
    #[must_use]
    pub fn op(&self) -> Option<usize> {
        match self {
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

    /// True for `404 not_found`.
    #[must_use]
    pub fn is_not_found(&self) -> bool {
        matches!(self, Self::NotFound { .. })
    }

    /// True when the outcome of a mutation is unknown: `commit_uncertain`,
    /// `internal_error` or a transport failure. Retry with the same
    /// `Idempotency-Key` to learn the outcome.
    #[must_use]
    pub fn is_uncertain(&self) -> bool {
        matches!(
            self,
            Self::CommitUncertain | Self::InternalError | Self::Transport(_)
        )
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
        (409, "idempotency_conflict") => Error::IdempotencyConflict,
        (409, "idempotency_expired") => Error::IdempotencyExpired,
        (429, "rate_limited") => Error::RateLimited { retry_after },
        (403, "forbidden_scope") => Error::Forbidden { op },
        (401, "invalid_key") => Error::InvalidKey,
        (500, "internal_error") => Error::InternalError,
        _ => Error::Api {
            status,
            code,
            message,
            op,
            details: raw,
        },
    }
}
