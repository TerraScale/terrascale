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

DEFAULT_ISSUER = _spec.DEFAULT_ISSUER
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


PROOF_HEADER = "X-TerraScale-Step-Up-Proof"


def _origin(issuer: str | None) -> str:
    return _spec.issuer_parts(issuer)[0]


def _keys_path(organization: str, project: str | None, *extra: str) -> list[str]:
    path = ["v1", "terrascale", "organizations", validate_id(organization, "organization")]
    if project is not None:
        path += ["projects", validate_id(project, "project")]
    return [*path, "api-keys", *extra]


def create_api_key_call(issuer: str | None, organization: str, project: str | None, name: str,
                        scopes: Sequence[str], database: str | None, expires_at: str | None,
                        allowed_cidrs: Sequence[str] | None) -> _spec.Call:
    body: dict[str, Any] = {"name": name, "scopes": list(scopes)}
    if database is not None:
        body["database"] = validate_id(database, "database")
    if expires_at is not None:
        body["expires_at"] = expires_at
    if allowed_cidrs is not None:
        body["allowed_cidrs"] = list(allowed_cidrs)
    return _spec.Call("POST", _keys_path(organization, project),
                      lambda r: models.ApiKeySecret.from_json(r.json()),
                      body=body, success=(201,), base_url=_origin(issuer))


def list_api_keys_call(issuer: str | None, organization: str, project: str | None,
                       limit: int | None, cursor: str | None) -> _spec.Call:
    return _spec.Call("GET", _keys_path(organization, project),
                      lambda r: models.ApiKeyPage.from_json(r.json()),
                      params=_spec._page_params(limit, cursor), replay_safe=True, base_url=_origin(issuer))


def revoke_api_key_call(issuer: str | None, organization: str, project: str | None, key: str) -> _spec.Call:
    return _spec.Call("DELETE", _keys_path(organization, project, validate_id(key, "key id")), _spec._none,
                      success=(204,), base_url=_origin(issuer))


def rotate_api_key_call(issuer: str | None, organization: str, project: str | None, key: str, grace_seconds: int,
                        step_up_proof: str) -> _spec.Call:
    if not isinstance(grace_seconds, int) or isinstance(grace_seconds, bool) or not 0 <= grace_seconds <= 86400:
        raise ValueError("grace_seconds must be 0 to 86400 seconds")
    return _spec.Call("POST", _keys_path(organization, project, validate_id(key, "key id"), "rotate"),
                      lambda r: models.ApiKeySecret.from_json(r.json()), headers={PROOF_HEADER: step_up_proof},
                      body={"previous_secret_ttl": grace_seconds}, base_url=_origin(issuer))


def _clients_path(project: str, *extra: str) -> list[str]:
    return ["v1", validate_id(project, "project"), "clients", *extra]


def create_project_client_call(issuer: str | None, project: str, name: str, client_type: str,
                               redirect_uris: Sequence[str], step_up_proof: str, allowed_scopes: Sequence[str] | None = None,
                               allowed_audiences: Sequence[str] | None = None,
                               token_lifetime_minutes: int | None = None) -> _spec.Call:
    body: dict[str, Any] = {"name": name, "type": client_type, "redirect_uris": list(redirect_uris)}
    body.update({"allowed_scopes": allowed_scopes, "allowed_audiences": allowed_audiences,
                 "token_lifetime_minutes": token_lifetime_minutes})
    return _spec.Call("POST", _clients_path(project),
                      lambda r: models.ProjectClient.from_json(r.json()),
                      headers={PROOF_HEADER: step_up_proof}, body=body, success=(201,), base_url=_origin(issuer))


def list_project_clients_call(issuer: str | None, project: str) -> _spec.Call:
    return _spec.Call("GET", _clients_path(project),
                      lambda r: models.ProjectClientPage.from_json(r.json()), replay_safe=True,
                      base_url=_origin(issuer))


def update_project_client_call(issuer: str | None, project: str, client_id: str, name: str,
                               redirect_uris: Sequence[str], step_up_proof: str, allowed_scopes: Sequence[str] | None = None,
                               allowed_audiences: Sequence[str] | None = None,
                               token_lifetime_minutes: int | None = None) -> _spec.Call:
    body: dict[str, Any] = {"name": name, "redirect_uris": list(redirect_uris)}
    body.update({"allowed_scopes": allowed_scopes, "allowed_audiences": allowed_audiences,
                 "token_lifetime_minutes": token_lifetime_minutes})
    return _spec.Call("PUT", _clients_path(project, validate_id(client_id, "client id")),
                      lambda r: models.ProjectClient.from_json(r.json()),
                      headers={PROOF_HEADER: step_up_proof}, body=body, base_url=_origin(issuer))


def rotate_project_client_secret_call(issuer: str | None, project: str, client_id: str,
                                      grace_seconds: int | None, step_up_proof: str) -> _spec.Call:
    if grace_seconds is not None and (not isinstance(grace_seconds, int) or isinstance(grace_seconds, bool)
                                      or not 0 <= grace_seconds <= 86400):
        raise ValueError("grace_seconds must be 0 to 86400 seconds")
    body: dict[str, Any] = {} if grace_seconds is None else {"grace_seconds": grace_seconds}
    return _spec.Call("POST", _clients_path(project, validate_id(client_id, "client id"), "secret", "rotate"),
                      lambda r: models.ProjectClientSecret.from_json(r.json()),
                      headers={PROOF_HEADER: step_up_proof}, body=body, base_url=_origin(issuer))


def delete_project_client_call(issuer: str | None, project: str, client_id: str) -> _spec.Call:
    return _spec.Call("DELETE", _clients_path(project, validate_id(client_id, "client id")), _spec._none,
                      success=(204,), base_url=_origin(issuer))


def _tenant_path(project: str, *extra: str) -> list[str]:
    return ["v1", validate_id(project, "project"), *extra]


def _page_limit(limit: int | None) -> int | None:
    if limit is not None and (not isinstance(limit, int) or isinstance(limit, bool) or not 1 <= limit <= 100):
        raise ValueError("limit must be an integer between 1 and 100")
    return limit


def _cursor(after: str | None) -> str | None:
    if after is not None and (not isinstance(after, str) or not after or len(after) > 8192):
        raise ValueError("after must be a non-empty opaque cursor of at most 8192 characters")
    return after


def get_settings_call(issuer: str | None, project: str) -> _spec.Call:
    return _spec.Call("GET", _tenant_path(project, "settings"),
                      lambda r: models.ProjectSettings.from_json(r.json()), replay_safe=True, base_url=_origin(issuer))


def update_settings_call(issuer: str | None, project: str, update: models.ProjectSettingsUpdate) -> _spec.Call:
    return _spec.Call("PUT", _tenant_path(project, "settings"),
                      lambda r: models.ProjectSettings.from_json(r.json()), body=update.to_body(),
                      base_url=_origin(issuer))


def list_users_call(issuer: str | None, project: str, search: str | None, limit: int | None,
                    after: str | None) -> _spec.Call:
    params = _spec._page_params(_page_limit(limit), _cursor(after))
    if search is not None:
        if not isinstance(search, str) or not search:
            raise ValueError("search must be a non-empty email substring")
        params["q"] = search
    return _spec.Call("GET", _tenant_path(project, "users"),
                      lambda r: models.ProjectUserPage.from_json(r.json()), params=params, replay_safe=True,
                      base_url=_origin(issuer))


def export_users_call(issuer: str | None, project: str, limit: int | None, after: str | None) -> _spec.Call:
    return _spec.Call("GET", _tenant_path(project, "users", "export"),
                      lambda r: models.ProjectUserExportPage.from_json(r.json()),
                      params=_spec._page_params(_page_limit(limit), _cursor(after)), replay_safe=True,
                      base_url=_origin(issuer))


def get_user_call(issuer: str | None, project: str, user_id: str) -> _spec.Call:
    return _spec.Call("GET", _tenant_path(project, "users", validate_id(user_id, "user id")),
                      lambda r: models.ProjectUser.from_json(r.json()), replay_safe=True, base_url=_origin(issuer))


def _user_state_call(issuer: str | None, project: str, user_id: str, action: str) -> _spec.Call:
    return _spec.Call("POST", _tenant_path(project, "users", validate_id(user_id, "user id"), action),
                      lambda r: models.ProjectUser.from_json(r.json()), base_url=_origin(issuer))


def disable_user_call(issuer: str | None, project: str, user_id: str) -> _spec.Call:
    return _user_state_call(issuer, project, user_id, "disable")


def enable_user_call(issuer: str | None, project: str, user_id: str) -> _spec.Call:
    return _user_state_call(issuer, project, user_id, "enable")


def delete_user_call(issuer: str | None, project: str, user_id: str) -> _spec.Call:
    return _spec.Call("DELETE", _tenant_path(project, "users", validate_id(user_id, "user id")), _spec._none,
                      success=(204,), base_url=_origin(issuer))


PROVIDERS = ("google", "github", "apple", "oidc")


def _provider(value: str) -> str:
    if value not in PROVIDERS:
        raise ValueError(f"provider must be one of {', '.join(PROVIDERS)}")
    return value


def set_provider_call(issuer: str | None, project: str, provider: str,
                      update: models.ProjectProviderUpdate) -> _spec.Call:
    return _spec.Call("PUT", _tenant_path(project, "providers", _provider(provider)),
                      lambda r: models.ProjectSettings.from_json(r.json()), body=update.to_body(),
                      base_url=_origin(issuer))


def delete_provider_call(issuer: str | None, project: str, provider: str) -> _spec.Call:
    return _spec.Call("DELETE", _tenant_path(project, "providers", _provider(provider)),
                      lambda r: models.ProjectSettings.from_json(r.json()), base_url=_origin(issuer))


def list_sessions_call(issuer: str | None, project: str, user_id: str | None, limit: int | None,
                       after: str | None) -> _spec.Call:
    params = _spec._page_params(_page_limit(limit), _cursor(after))
    if user_id is not None:
        params["user"] = validate_id(user_id, "user id")
    return _spec.Call("GET", _tenant_path(project, "sessions"),
                      lambda r: models.ProjectSessionPage.from_json(r.json()), params=params, replay_safe=True,
                      base_url=_origin(issuer))


def revoke_session_call(issuer: str | None, project: str, session_id: str) -> _spec.Call:
    return _spec.Call("DELETE", _tenant_path(project, "sessions", validate_id(session_id, "session id")),
                      _spec._none, success=(204,), base_url=_origin(issuer))



def sign_out_user_call(issuer: str | None, project: str, user_id: str) -> _spec.Call:
    return _spec.Call("POST", _tenant_path(project, "users", validate_id(user_id, "user id"), "sign-out"),
                      _spec._none, success=(204,), base_url=_origin(issuer))


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
        base, tenant = _spec.issuer_parts(issuer)
        self.endpoint = base + tenant + "/oauth/token"
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
        self._http = http_client or httpx.Client(timeout=30.0, follow_redirects=False, http2=True,
                                                 mounts={"http://": httpx.HTTPTransport(http2=True)})
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
        self._http = http_client or httpx.AsyncClient(timeout=30.0, follow_redirects=False, http2=True,
                                                      mounts={"http://": httpx.AsyncHTTPTransport(http2=True)})
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

def create_project_invitation_call(issuer: str | None, project: str, email: str,
                                   redirect_url: str | None) -> _spec.Call:
    return _spec.Call("POST", _tenant_path(project, "invitations"),
                      lambda r: models.ProjectInvitation.from_json(r.json()),
                      body={"email": email, "redirect_url": redirect_url}, base_url=_origin(issuer))


def list_project_invitations_call(issuer: str | None, project: str, limit: int | None,
                                  after: str | None) -> _spec.Call:
    return _spec.Call("GET", _tenant_path(project, "invitations"),
                      lambda r: models.ProjectInvitationPage.from_json(r.json()),
                      params=_spec._page_params(_page_limit(limit), _cursor(after)), base_url=_origin(issuer))


def revoke_project_invitation_call(issuer: str | None, project: str, invitation: str) -> _spec.Call:
    return _spec.Call("DELETE", _tenant_path(project, "invitations", validate_id(invitation)),
                      lambda r: None, base_url=_origin(issuer))


def list_project_waitlist_call(issuer: str | None, project: str, limit: int | None,
                               after: str | None) -> _spec.Call:
    return _spec.Call("GET", _tenant_path(project, "waitlist"),
                      lambda r: models.ProjectWaitlistPage.from_json(r.json()),
                      params=_spec._page_params(_page_limit(limit), _cursor(after)), base_url=_origin(issuer))


def approve_project_waitlist_call(issuer: str | None, project: str, entry: str) -> _spec.Call:
    return _spec.Call("POST", _tenant_path(project, "waitlist", validate_id(entry), "approve"),
                      lambda r: None, base_url=_origin(issuer))


def reject_project_waitlist_call(issuer: str | None, project: str, entry: str) -> _spec.Call:
    return _spec.Call("POST", _tenant_path(project, "waitlist", validate_id(entry), "reject"),
                      lambda r: None, base_url=_origin(issuer))


def create_user_call(issuer: str | None, project: str, request: models.CreateProjectUser) -> _spec.Call:
    return _spec.Call("POST", _tenant_path(project, "users"), lambda r: models.ProjectUser.from_json(r.json()),
                      body=request.body(), success=(201,), base_url=_origin(issuer))


def update_user_call(issuer: str | None, project: str, user_id: str, request: models.UpdateProjectUser) -> _spec.Call:
    return _spec.Call("PATCH", _tenant_path(project, "users", validate_id(user_id, "user id")),
                      lambda r: models.ProjectUser.from_json(r.json()), body=request.body(), base_url=_origin(issuer))


def import_users_call(issuer: str | None, project: str, users: list[models.ImportProjectUser]) -> _spec.Call:
    return _spec.Call("POST", _tenant_path(project, "users", "import"),
                      lambda r: models.ImportProjectUsersResult.from_json(r.json()),
                      body={"users": [user.body() for user in users]}, base_url=_origin(issuer))

def create_user_api_key_call(issuer: str | None, project: str, user: str, name: str,
                            scopes: Sequence[str], expires_at: str | None) -> _spec.Call:
    return _spec.Call("POST", ["v1", validate_id(project, "project"), "users", validate_id(user, "user"), "api-keys"],
                      lambda r: models.UserApiKey.from_json(r.json()), success=(201,),
                      body={"name": name, "scopes": list(scopes), "expires_at": expires_at}, base_url=_origin(issuer))


def list_user_api_keys_call(issuer: str | None, project: str, user: str) -> _spec.Call:
    return _spec.Call("GET", ["v1", validate_id(project, "project"), "users", validate_id(user, "user"), "api-keys"],
                      lambda r: models.UserApiKeyPage.from_json(r.json()), base_url=_origin(issuer))


def revoke_user_api_key_call(issuer: str | None, project: str, user: str, key: str) -> _spec.Call:
    return _spec.Call("DELETE", ["v1", validate_id(project, "project"), "users", validate_id(user, "user"),
                                "api-keys", validate_id(key, "key")],
                      _spec._none, success=(204,), base_url=_origin(issuer))


def verify_user_api_key_call(issuer: str | None, project: str, key: str) -> _spec.Call:
    return _spec.Call("POST", ["v1", validate_id(project, "project"), "api-keys", "verify"],
                      lambda r: models.UserApiKeyVerification.from_json(r.json()),
                      body={"key": key}, base_url=_origin(issuer))

def set_user_api_key_policy_call(issuer: str | None, project: str, scopes: Sequence[str]) -> _spec.Call:
    return _spec.Call("PUT", ["v1", validate_id(project, "project"), "user-api-key-policy"],
                      lambda r: models.UserApiKeyPolicy.from_json(r.json()),
                      body={"allowed_scopes": list(scopes)}, base_url=_origin(issuer))
