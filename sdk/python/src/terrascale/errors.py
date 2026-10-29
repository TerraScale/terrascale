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
    "PreconditionFailedError",
    "RateLimitError",
    "ServiceUnavailableError",
    "CommitUncertainError",
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


class APIError(TerraScaleError):
    """A TerraBase error or an undecodable success response.
    ``request_id`` is the response's canonical ULID, or None."""

    def __init__(
        self,
        status: int,
        code: str,
        message: str = "",
        *,
        op: int | None = None,
        details: list[dict[str, Any]] | None = None,
        retry_after: float | None = None,
        etag: str | None = None,
        region: str | None = None,
        request_id: str | None = None,
        statement: int | None = None,
        sqlite_code: str | None = None,
    ):
        self.status = status
        self.code = code
        self.message = message
        self.op = op
        self.details = details or []
        self.retry_after = retry_after
        self.etag = etag
        self.region = region
        self.request_id: str | None = request_id if is_valid_id(request_id) else None
        self.statement = statement
        self.sqlite_code = sqlite_code
        text = f"{status} {code}"
        if message:
            text += f": {message}"
        if op is not None:
            text += f" (op {op})"
        super().__init__(text)


class InvalidResponseError(APIError):
    """The server response could not be decoded according to the API contract."""


class InvalidRequestError(APIError):
    """400: the request, query, patch or cursor is invalid."""


class AuthenticationError(APIError):
    """401: the access token is missing, expired or invalid."""


class KeyRevokedError(AuthenticationError):
    """401 key_revoked: permanent; replace the key without retry or exchange."""


class KeyExpiredError(AuthenticationError):
    """401 key_expired: permanent; replace the key without retry or exchange."""


class PermissionDeniedError(APIError):
    """403: the token lacks a scope, e.g. ``forbidden_scope``."""


class NotFoundError(APIError):
    """404: unknown document, collection, database or route. For a deleted
    document ``etag`` holds the tombstone version, usable with ``if_match``."""


class ConflictError(APIError):
    """409: e.g. ``name_taken``, ``patch_conflict``, ``index_exists``."""


class UniqueViolationError(ConflictError):
    """409 ``unique_violation``: nothing was written."""


class PreconditionFailedError(APIError):
    """412: ``if_match`` / ``if_none_match_any`` did not hold."""


class RateLimitError(APIError):
    """429: rate limited, quota exceeded or capacity; see ``retry_after``."""


class ServiceUnavailableError(APIError):
    """503: transient unavailability."""


class CommitUncertainError(APIError):
    """The write may or may not have committed (``commit_uncertain``,
    ``write_outcome_unknown`` or a transport failure on a mutation).
    Re-read the state before deciding what to do; never blindly replay."""


_BY_CODE: dict[str, type[APIError]] = {
    "key_revoked": KeyRevokedError,
    "key_expired": KeyExpiredError,
    "unique_violation": UniqueViolationError,
    "commit_uncertain": CommitUncertainError,
    "write_outcome_unknown": CommitUncertainError,
    "precondition_failed": PreconditionFailedError,
}
_BY_STATUS: dict[int, type[APIError]] = {
    400: InvalidRequestError,
    401: AuthenticationError,
    403: PermissionDeniedError,
    404: NotFoundError,
    409: ConflictError,
    412: PreconditionFailedError,
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
    op = err.get("op")
    try:
        op = int(op) if isinstance(op, (int, str)) and not isinstance(op, bool) else None
    except (TypeError, ValueError, OverflowError):
        op = None
    retry_after = None
    etag = None
    if headers is not None:
        retry_after = parse_retry_after(headers.get("retry-after"))
        raw_etag = headers.get("etag")
        if raw_etag:
            etag = raw_etag.removeprefix("W/").strip('"')
    cls = _BY_CODE.get(code) or _BY_STATUS.get(status) or APIError
    if mutation and status >= 500:
        cls = CommitUncertainError
    return cls(
        status,
        code,
        err.get("message") or "",
        op=op,
        details=err.get("details") if isinstance(err.get("details"), list) else None,
        retry_after=retry_after,
        etag=etag,
        region=err.get("region"),
        request_id=response_request_id(headers),
        statement=err.get("statement") if type(err.get("statement")) is int else None,
        sqlite_code=err.get("sqlite_code") if isinstance(err.get("sqlite_code"), str) else None,
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
