"""API key token exchange with ts-auth.

A TerraScale API key (``<ULID>_<52 secret chars>``) is an OAuth client
credential. It is exchanged at ``<issuer>/oauth/token`` (client_credentials,
HTTP Basic) for a short-lived project access token, which TerraBase accepts
as ``Authorization: Bearer``. Sending the API key itself as a bearer fails.
"""

from __future__ import annotations

import asyncio
import base64
import math
import re
import threading
import time
from collections.abc import Sequence
from typing import Any
from urllib.parse import quote_plus

import httpx

from . import _spec
from .errors import TerraScaleError, TokenError, parse_retry_after, response_request_id
from .ids import validate_id

__all__ = ["DEFAULT_ISSUER", "SCOPES", "APIKeyTokenSource", "AsyncAPIKeyTokenSource"]

DEFAULT_ISSUER = "https://auth.terrascale.tech/terrascale"
SCOPES = ("databases:read", "databases:write", "documents:read", "documents:write")

_KEY = re.compile(r"^[0-7][0-9A-HJKMNP-TV-Z]{25}_[0-9a-hjkmnp-tv-z]{52}$")
# Token endpoint answers worth another exchange: issuing a token changes no
# state, so overload and gateway failures are retried with backoff.
_RETRY_STATUS = frozenset({429, 500, 502, 503, 504})


def _transport_error(attempts: int, exc: Exception) -> TokenError:
    detail = f"{type(exc).__name__}: {exc}".rstrip(": ")
    return TokenError(0, "token_transport_error", f"token exchange failed after {attempts} attempt{'s' if attempts != 1 else ''}"
                      f" ({detail})")


class _Exchange:
    """Validated request parts and the shared caching rule."""

    def __init__(self, api_key: str, project: str | None, scopes: Sequence[str] | None, issuer: str | None):
        if not isinstance(api_key, str) or not _KEY.fullmatch(api_key):
            # Never echo the value: it is a secret.
            raise ValueError("api_key must be a TerraScale API key: a ULID, '_' and 52 secret characters")
        for scope in scopes or ():
            if scope not in SCOPES:
                raise ValueError(f"scope {scope!r} is not one of {', '.join(SCOPES)}")
        if project is not None:
            validate_id(project, "project")
        issuer = (issuer or DEFAULT_ISSUER).rstrip("/")
        url = httpx.URL(issuer)
        loopback = url.host in ("localhost", "127.0.0.1", "::1")
        if url.scheme != "https" and not (url.scheme == "http" and loopback):
            raise ValueError("issuer must be an https URL")
        self.endpoint = issuer + "/oauth/token"
        self.form: dict[str, str] = {"grant_type": "client_credentials"}
        if project:
            self.form["project"] = project
        if scopes:
            self.form["scope"] = " ".join(scopes)
        client_id = api_key[:26]
        pair = f"{quote_plus(client_id)}:{quote_plus(api_key)}".encode()
        self._auth = "Basic " + base64.b64encode(pair).decode()
        self.token: str | None = None
        self.refresh_at = 0.0
        self.refusal: TerraScaleError | None = None

    def retry_delay(self, response: httpx.Response | None, attempt: int, max_attempts: int) -> float | None:
        """Seconds to wait before another exchange, or None to stop.
        ``response`` is None after a transport failure."""
        if attempt >= max_attempts or self.refusal is not None:
            return None
        if response is None:
            return _spec.backoff(attempt, None)
        if response.status_code in _RETRY_STATUS:
            return _spec.backoff(attempt, parse_retry_after(response.headers.get("retry-after")))
        return None

    def request(self) -> httpx.Request:
        # Direct construction excludes cookies and defaults adopted from other origins.
        return httpx.Request("POST", self.endpoint, data=self.form, headers=self.headers())

    def headers(self) -> dict[str, str]:
        return {"Authorization": self._auth, "Accept": "application/json"}

    def cached(self) -> str | None:
        if self.refusal is not None:
            raise self.refusal
        if self.token and time.monotonic() < self.refresh_at:
            return self.token
        return None

    def accept(self, response: httpx.Response, started: float) -> str:
        if self.refusal is not None:
            raise self.refusal
        if response.status_code != 200:
            try:
                body: Any = response.json()
            except ValueError:
                body = {}
            if not isinstance(body, dict):
                body = {}
            error = TokenError(
                response.status_code,
                str(body.get("error") or ""),
                str(body.get("error_description") or ""),
                parse_retry_after(response.headers.get("retry-after")),
                request_id=response_request_id(response.headers),
            )
            if error.code == "invalid_client":
                self.refusal = error
                self.token = None
            raise error
        try:
            body = response.json()
            token = body["access_token"]
            lifetime = float(body["expires_in"])
            ok = isinstance(token, str) and re.fullmatch(r"[A-Za-z0-9._~+/-]+=*", token) is not None
            ok = ok and str(body.get("token_type", "")).lower() == "bearer"
        except (ValueError, KeyError, TypeError, OverflowError):
            ok = False
        if not ok or not math.isfinite(lifetime) or lifetime <= 0:
            # The body may hold credentials: never include it.
            raise TokenError(response.status_code, "invalid_token_response", "not a Bearer access_token with expires_in",
                             request_id=response_request_id(response.headers))
        self.token = token
        # Refresh a minute early (or at half-life for short tokens); the
        # lifetime counts from before the request.
        self.refresh_at = started + lifetime - min(60.0, lifetime / 2)
        return token

    def invalidate(self, token: str) -> None:
        if self.token == token:
            self.token = None

    def reject(self, error: TerraScaleError) -> None:
        self.token = None
        self.refusal = error


class APIKeyTokenSource:
    """Thread-safe, caching source of access tokens for an API key."""

    def __init__(
        self,
        api_key: str,
        *,
        project: str | None = None,
        scopes: Sequence[str] | None = None,
        issuer: str | None = None,
        http_client: httpx.Client | None = None,
        max_attempts: int = _spec.MAX_ATTEMPTS,
    ):
        self._x = _Exchange(api_key, project, scopes, issuer)
        self._http = http_client or httpx.Client(timeout=30.0, follow_redirects=False, http2=True)
        self._lock = threading.Lock()
        self._max_attempts = max(1, max_attempts)

    def token(self) -> str:
        # One exchange at a time: concurrent callers wait for it instead of stampeding ts-auth.
        with self._lock:
            cached = self._x.cached()
            if cached:
                return cached
            attempt = 0
            while True:
                attempt += 1
                started = time.monotonic()
                try:
                    response = self._http.send(self._x.request(), auth=None, follow_redirects=False)
                except httpx.TransportError as exc:
                    delay = self._x.retry_delay(None, attempt, self._max_attempts)
                    if delay is None:
                        raise _transport_error(attempt, exc) from exc
                    time.sleep(delay)
                    continue
                except httpx.DecodingError as exc:
                    raise _transport_error(attempt, exc) from exc
                delay = self._x.retry_delay(response, attempt, self._max_attempts)
                if delay is None:
                    return self._x.accept(response, started)
                time.sleep(delay)

    def reject(self, error: TerraScaleError) -> None:
        """Drop a permanently refused token and stop exchanging this key."""
        with self._lock:
            self._x.reject(error)

    def invalidate(self, token: str) -> None:
        """Drop token after a resource server refused it (401)."""
        self._x.invalidate(token)


class AsyncAPIKeyTokenSource:
    """Asyncio counterpart of :class:`APIKeyTokenSource`."""

    def __init__(
        self,
        api_key: str,
        *,
        project: str | None = None,
        scopes: Sequence[str] | None = None,
        issuer: str | None = None,
        http_client: httpx.AsyncClient | None = None,
        max_attempts: int = _spec.MAX_ATTEMPTS,
    ):
        self._x = _Exchange(api_key, project, scopes, issuer)
        self._http = http_client or httpx.AsyncClient(timeout=30.0, follow_redirects=False, http2=True)
        self._lock: asyncio.Lock | None = None
        self._max_attempts = max(1, max_attempts)

    async def token(self) -> str:
        if self._lock is None:
            self._lock = asyncio.Lock()
        # One exchange at a time: concurrent callers wait for it instead of stampeding ts-auth.
        async with self._lock:
            cached = self._x.cached()
            if cached:
                return cached
            attempt = 0
            while True:
                attempt += 1
                started = time.monotonic()
                try:
                    response = await self._http.send(self._x.request(), auth=None, follow_redirects=False)
                except httpx.TransportError as exc:
                    delay = self._x.retry_delay(None, attempt, self._max_attempts)
                    if delay is None:
                        raise _transport_error(attempt, exc) from exc
                    await asyncio.sleep(delay)
                    continue
                except httpx.DecodingError as exc:
                    raise _transport_error(attempt, exc) from exc
                delay = self._x.retry_delay(response, attempt, self._max_attempts)
                if delay is None:
                    return self._x.accept(response, started)
                await asyncio.sleep(delay)

    def reject(self, error: TerraScaleError) -> None:
        """Drop a permanently refused token and stop exchanging this key."""
        self._x.reject(error)

    def invalidate(self, token: str) -> None:
        self._x.invalidate(token)
