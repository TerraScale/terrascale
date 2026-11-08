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
from concurrent.futures import Future
from typing import Any
from urllib.parse import quote_plus

import httpx

from . import _spec, models
from .errors import TerraScaleError, TokenError, parse_retry_after, response_request_id
from .ids import validate_id

__all__ = ["DEFAULT_ISSUER", "SCOPES", "APIKeyTokenSource", "AsyncAPIKeyTokenSource"]

DEFAULT_ISSUER = "https://auth.terrascale.tech/terrascale"
SCOPES = ("databases:read", "databases:write", "sql:read", "sql:write")

_KEY = re.compile(r"^[0-7][0-9A-HJKMNP-TV-Z]{25}_[0-9a-hjkmnp-tv-z]{52}$")
# Token endpoint answers worth another exchange: issuing a token changes no
# state, so overload and gateway failures are retried with backoff.
_RETRY_STATUS = frozenset({429, 500, 502, 503, 504})


def _scoped_key_call(api_key: str | None, issuer: str | None, project: str, database: str,
                     access: str, ttl_seconds: int | None) -> _spec.Call:
    if api_key is None:
        raise ValueError("create_scoped_key requires the client's api_key")
    exchange = _Exchange(api_key, project, None, issuer)
    if access not in ("read", "read_write"):
        raise ValueError("access must be 'read' or 'read_write'")
    body: dict[str, Any] = {"project": project, "database": validate_id(database, "database"), "access": access}
    if ttl_seconds is not None:
        body["ttl_seconds"] = _spec.ttl(ttl_seconds, 2_592_000)
    origin = str(httpx.URL(exchange.endpoint).copy_with(path="/", query=None, fragment=None)).rstrip("/")
    return _spec.Call("POST", ["v1", "terrascale", "api-keys", "self", "scoped-keys"],
                      lambda r: models.ScopedKey.from_json(r.json()), headers=exchange.headers(), body=body,
                      success=(201,), base_url=origin)


def _rotate_key_call(api_key: str | None, issuer: str | None, key_id: str, grace_seconds: int) -> _spec.Call:
    if api_key is None:
        raise ValueError("rotate_api_key requires the client's api_key")
    if not isinstance(grace_seconds, int) or isinstance(grace_seconds, bool) or not 0 <= grace_seconds <= 86400:
        raise ValueError("grace_seconds must be 0 to 86400 seconds")
    exchange = _Exchange(api_key, None, None, issuer)
    origin = str(httpx.URL(exchange.endpoint).copy_with(path="/", query=None, fragment=None)).rstrip("/")
    return _spec.Call("POST", ["v1", "terrascale", "api-keys", validate_id(key_id, "key id"), "rotate"],
                      lambda r: models.RotatedApiKey.from_json(r.json()), headers=exchange.headers(),
                      body={"previous_secret_ttl": grace_seconds}, base_url=origin)


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
        self.expires_at = 0.0
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
        if self.token and time.monotonic() < self.expires_at:
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
        if not ok or not math.isfinite(lifetime) or lifetime <= 0 or started + lifetime <= time.monotonic():
            # The body may hold credentials: never include it.
            raise TokenError(response.status_code, "invalid_token_response", "not a Bearer access_token with expires_in",
                             request_id=response_request_id(response.headers))
        self.token = token
        # Refresh a minute early (or at half-life for short tokens); the
        # lifetime counts from before the request.
        self.refresh_at = started + lifetime - min(60.0, lifetime / 2)
        self.expires_at = started + lifetime
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
        self._flight: Future[str] | None = None
        self._thread: threading.Thread | None = None
        self._closed = False
        self._owns_http = http_client is None
        self._max_attempts = max(1, max_attempts)

    def token(self) -> str:
        with self._lock:
            if self._closed:
                raise RuntimeError("token source is closed")
            cached = self._x.cached()
            if cached and time.monotonic() < self._x.refresh_at:
                return cached
            flight = self._flight
            if flight is None:
                flight = Future()
                self._flight = flight
                self._thread = threading.Thread(target=self._renew, args=(flight,), daemon=True)
                self._thread.start()
            # Refresh never holds the state lock during network I/O. A valid
            # token remains usable until its actual expiry, even on failure.
            if cached:
                return cached
        return flight.result()

    def _renew(self, flight: Future[str]) -> None:
        try:
            attempt = 0
            while True:
                attempt += 1
                started = time.monotonic()
                response: httpx.Response | None = None
                transport: Exception | None = None
                try:
                    response = self._http.send(self._x.request(), auth=None, follow_redirects=False)
                except (httpx.TransportError, httpx.DecodingError) as exc:
                    transport = exc
                with self._lock:
                    delay = self._x.retry_delay(response, attempt, self._max_attempts)
                    if isinstance(transport, httpx.DecodingError):
                        delay = None
                    if delay is None:
                        if transport is not None:
                            raise _transport_error(attempt, transport) from transport
                        assert response is not None
                        token = self._x.accept(response, started)
                        flight.set_result(token)
                        return
                time.sleep(delay)
        except Exception as exc:
            with self._lock:
                self._x.refresh_at = min(self._x.expires_at, time.monotonic() + 1.0)
                flight.set_exception(exc)
        finally:
            with self._lock:
                self._flight = None

    def close(self) -> None:
        """Finish this source's bounded renewal before releasing its HTTP client."""
        with self._lock:
            self._closed = True
            thread = self._thread
        if thread is not None:
            thread.join()
        if self._owns_http:
            self._http.close()

    def reject(self, error: TerraScaleError) -> None:
        """Drop a permanently refused token and stop exchanging this key."""
        with self._lock:
            self._x.reject(error)

    def invalidate(self, token: str) -> None:
        """Drop token after a resource server refused it (401)."""
        with self._lock:
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
        self._flight: asyncio.Task[str] | None = None
        self._closed = False
        self._owns_http = http_client is None
        self._max_attempts = max(1, max_attempts)

    async def token(self) -> str:
        if self._closed:
            raise RuntimeError("token source is closed")
        cached = self._x.cached()
        if cached and time.monotonic() < self._x.refresh_at:
            return cached
        if self._flight is None:
            self._flight = asyncio.create_task(self._renew())
            # Background failures are observed; missing/expired callers still
            # receive the same result. Cancelling one waiter cannot cancel it.
            self._flight.add_done_callback(lambda task: None if task.cancelled() else task.exception())
        if cached:
            return cached
        return await asyncio.shield(self._flight)

    async def _renew(self) -> str:
        try:
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
        except Exception:
            self._x.refresh_at = min(self._x.expires_at, time.monotonic() + 1.0)
            raise
        finally:
            self._flight = None

    async def close(self) -> None:
        """Cancel this source's renewal before releasing its HTTP client."""
        self._closed = True
        if self._flight is not None:
            self._flight.cancel()
            try:
                await self._flight
            except asyncio.CancelledError:
                pass
        if self._owns_http:
            await self._http.aclose()

    def reject(self, error: TerraScaleError) -> None:
        """Drop a permanently refused token and stop exchanging this key."""
        self._x.reject(error)

    def invalidate(self, token: str) -> None:
        self._x.invalidate(token)
