"""Typed errors. Every server error is an :class:`APIError` carrying the
stable ``code``; common codes map to subclasses so callers can catch them."""

from __future__ import annotations

from typing import Any

from .ids import is_valid_id

__all__ = [
    "TerraScaleError",
    "TokenError",
    "APIError",
    "InvalidRequestError",
    "InvalidResponseError",
    "AuthenticationError",
    "KeyRevokedError",
    "KeyExpiredError",
    "PermissionDeniedError",
    "NotFoundError",
    "ConflictError",
    "UniqueViolationError",
    "RateLimitError",
    "ServiceUnavailableError",
    "CommitUncertainError",
    "SqlError",
    "SqlErrorCodes",
    "error_for",
]


class TerraScaleError(Exception):
    """Base class of every error raised by this SDK."""


class TokenError(TerraScaleError):
    """The ts-auth token endpoint refused the API key (RFC 6749 section 5.2),
    for example 401 ``invalid_client`` for an unknown, wrong or revoked key.
    ``request_id`` is the response's canonical ULID, or None."""

    def __init__(self, status: int, code: str, description: str = "", retry_after: float | None = None,
                 *, request_id: str | None = None):
        self.status = status
        self.code = code
        self.description = description
        self.retry_after = retry_after
        self.request_id: str | None = request_id if is_valid_id(request_id) else None
        msg = f"token endpoint answered {status} {code}".rstrip()
        if description:
            msg += f": {description}"
        super().__init__(msg)


class WebhookVerificationError(TerraScaleError):
    def __init__(self, code: str, message: str):
        self.code = code
        super().__init__(message)


class APIError(TerraScaleError):
    """A TerraBase error or an undecodable success response.
    ``request_id`` is the response's canonical ULID, or None."""

    def __init__(
        self,
        status: int,
        code: str,
        message: str = "",
        *,
        details: list[dict[str, Any]] | None = None,
        retry_after: float | None = None,
        region: str | None = None,
        request_id: str | None = None,
        statement: int | None = None,
        sqlstate: str | None = None,
    ):
        self.status = status
        self.code = code
        self.message = message
        self.details = details or []
        self.retry_after = retry_after
        self.region = region
        self.request_id: str | None = request_id if is_valid_id(request_id) else None
        self.statement = statement
        self.sqlstate = sqlstate
        text = f"{status} {code}"
        if message:
            text += f": {message}"
        super().__init__(text)


class InvalidResponseError(APIError):
    """The server response could not be decoded according to the API contract."""


class InvalidRequestError(APIError):
    """400: the request or cursor is invalid."""


class AuthenticationError(APIError):
    """401: the access token is missing, expired or invalid."""


class KeyRevokedError(AuthenticationError):
    """401 key_revoked: permanent; replace the key without retry or exchange."""


class KeyExpiredError(AuthenticationError):
    """401 key_expired: permanent; replace the key without retry or exchange."""


class PermissionDeniedError(APIError):
    """403: the token lacks a scope, e.g. ``forbidden_scope``."""


class NotFoundError(APIError):
    """404: unknown database or route."""


class ConflictError(APIError):
    """409: e.g. ``name_taken``."""


class UniqueViolationError(ConflictError):
    """409 ``unique_violation``: nothing was written."""


class RateLimitError(APIError):
    """429: rate limited, quota exceeded or capacity; see ``retry_after``."""


class ServiceUnavailableError(APIError):
    """503: transient unavailability."""


class CommitUncertainError(APIError):
    """The write may have committed: no response after sending, an uncoded 5xx,
    or commit_uncertain, write_outcome_unknown or internal_error.
    Re-read the state before deciding what to do; never blindly replay."""


class SqlErrorCodes:
    """TerraScale SQL error codes, the same on REST, the WebSocket and the PostgreSQL wire."""

    SQL_ERROR = "sql_error"
    SYNTAX_ERROR = "syntax_error"
    UNDEFINED_TABLE = "undefined_table"
    UNDEFINED_COLUMN = "undefined_column"
    DUPLICATE_OBJECT = "duplicate_object"
    PRIMARY_KEY_VIOLATION = "primary_key_violation"
    UNIQUE_VIOLATION = "unique_violation"
    NOT_NULL_VIOLATION = "not_null_violation"
    CHECK_VIOLATION = "check_violation"
    FOREIGN_KEY_VIOLATION = "foreign_key_violation"
    CONSTRAINT_VIOLATION = "constraint_violation"
    FORBIDDEN_SCOPE = "forbidden_scope"
    SQL_SANDBOX = "sql_sandbox"
    SERIALIZATION_CONFLICT = "serialization_conflict"
    ROW_CONFLICT = "row_conflict"
    TOO_LARGE = "too_large"
    QUERY_CANCELED = "query_canceled"
    TRANSACTION_ROLLED_BACK = "transaction_rolled_back"
    TRANSACTION_ABORTED = "transaction_aborted"
    READ_ONLY_TRANSACTION = "read_only_transaction"
    QUOTA_EXCEEDED = "quota_exceeded"
    INVALID_REQUEST = "invalid_request"
    TRANSACTION_STATE = "transaction_state"
    TENANT_BUSY = "tenant_busy"
    UNAVAILABLE = "unavailable"
    UNAUTHORIZED = "unauthorized"
    INTERNAL_ERROR = "internal_error"



class SqlError(APIError):
    """A refused SQL statement; the whole request rolled back. ``code`` is one of
    :class:`SqlErrorCodes`, ``statement`` the zero-based failing statement and
    ``sqlstate`` its PostgreSQL SQLSTATE."""

    @property
    def is_transient(self) -> bool:
        return self.code in (SqlErrorCodes.SERIALIZATION_CONFLICT, SqlErrorCodes.TENANT_BUSY)


_BY_CODE: dict[str, type[APIError]] = {
    "key_revoked": KeyRevokedError,
    "key_expired": KeyExpiredError,
    "unique_violation": UniqueViolationError,
}
_BY_STATUS: dict[int, type[APIError]] = {
    400: InvalidRequestError,
    401: AuthenticationError,
    403: PermissionDeniedError,
    404: NotFoundError,
    409: ConflictError,
    429: RateLimitError,
    503: ServiceUnavailableError,
}


def error_for(status: int, body: Any, headers: Any = None, *, mutation: bool = False) -> APIError:
    """Build the typed error for a response status and decoded JSON body."""
    err = body.get("error") if isinstance(body, dict) else None
    if not isinstance(err, dict):
        err = {}
    raw_code = err.get("code")
    code: str = raw_code if isinstance(raw_code, str) and raw_code else f"http_{status}"
    retry_after = None
    if headers is not None:
        retry_after = parse_retry_after(headers.get("retry-after"))
    statement = err.get("statement") if type(err.get("statement")) is int else None
    raw_state = err.get("sqlstate")
    sqlstate = raw_state if isinstance(raw_state, str) and raw_state else None
    cls = _BY_CODE.get(code) or _BY_STATUS.get(status) or APIError
    if statement is not None and raw_code and isinstance(raw_state, str):
        cls = SqlError
    elif mutation and (code in ("commit_uncertain", "write_outcome_unknown", "internal_error")
                       or 500 <= status < 600 and code == f"http_{status}"):
        cls = CommitUncertainError
    return cls(
        status,
        code,
        err.get("message") or "",
        details=err.get("details") if isinstance(err.get("details"), list) else None,
        retry_after=retry_after,
        region=err.get("region"),
        request_id=response_request_id(headers),
        statement=statement,
        sqlstate=sqlstate,
    )


def response_request_id(headers: Any) -> str | None:
    """Read one canonical ULID for correlation only, never authorization.

    httpx combines repeated header fields with commas, which fails validation.
    Count case-insensitively for plain mappings too; never keep arbitrary text.
    """
    if headers is None:
        return None
    values = [value for name, value in headers.items() if name.lower() == "x-request-id"]
    if len(values) != 1:
        return None
    value = values[0]
    return value if isinstance(value, str) and len(value) == 26 and is_valid_id(value) else None


def parse_retry_after(value: str | None) -> float | None:
    """Parse delta-seconds Retry-After; HTTP-dates and junk give None."""
    if not value:
        return None
    try:
        seconds = float(value.strip())
    except ValueError:
        return None
    return seconds if seconds >= 0 else None
