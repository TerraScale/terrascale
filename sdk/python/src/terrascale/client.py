"""Synchronous client."""

from __future__ import annotations

import builtins
import os
import time
from collections.abc import Callable, Sequence
from typing import Any, TypeVar, overload

import httpx

from . import _spec, models
from . import backups as _backups
from . import sql as _sql
from ._spec import Call
from ._version import __version__
from .auth import APIKeyTokenSource, _rotate_key_call, _scoped_key_call
from .errors import APIError, CommitUncertainError, InvalidResponseError, SqlError, SqlErrorCodes, error_for
from .ids import validate_id
from .session import SqlSession
from .sql import Args, Row, Statement, StatementResult

__all__ = ["TerraScale", "Database"]

T = TypeVar("T")


def _settings(api_key: str | None, project: str | None, token: Any, base_url: str | None) -> tuple[str | None, str, str]:
    api_key = api_key if api_key is not None else os.environ.get("TERRASCALE_API_KEY")
    project = project if project is not None else os.environ.get("TERRASCALE_PROJECT_ID")
    if not project:
        raise ValueError("project is required: pass project= or set TERRASCALE_PROJECT_ID")
    validate_id(project, "project")
    if not api_key and token is None:
        raise ValueError("an API key is required: pass api_key= or set TERRASCALE_API_KEY")
    base = (base_url or os.environ.get("TERRASCALE_BASE_URL") or _spec.DEFAULT_BASE_URL).rstrip("/")
    url = httpx.URL(base)
    if url.scheme != "https" and not (url.scheme == "http" and url.host in ("localhost", "127.0.0.1", "::1")):
        raise ValueError("base_url must be an https URL")
    return api_key, project, base


class TerraScale:
    """A project-scoped TerraBase client.

    ``api_key`` is exchanged for short-lived access tokens with ts-auth and
    refreshed automatically. ``token`` instead supplies an access token, or a
    callable returning one. Arguments default to the ``TERRASCALE_API_KEY``
    and ``TERRASCALE_PROJECT_ID`` environment variables. ``region`` pins every
    request to one region, such as ``"gru"``.
    """

    def __init__(
        self,
        api_key: str | None = None,
        *,
        project: str | None = None,
        token: str | Callable[[], str] | None = None,
        base_url: str | None = None,
        region: str | None = None,
        issuer: str | None = None,
        scopes: Sequence[str] | None = None,
        timeout: float = 30.0,
        max_attempts: int = _spec.MAX_ATTEMPTS,
        http_client: httpx.Client | None = None,
    ):
        api_key, self.project, self.base_url = _settings(api_key, project, token, base_url)
        self._http = http_client or httpx.Client(timeout=timeout, follow_redirects=False, http2=True)
        self._owns_http = http_client is None
        self._max_attempts = max(1, max_attempts)
        self._source: APIKeyTokenSource | None = None
        self._token = token
        self._api_key = api_key
        self._issuer = issuer
        if token is None:
            assert api_key is not None
            self._source = APIKeyTokenSource(api_key, project=self.project, scopes=scopes, issuer=issuer,
                                             max_attempts=self._max_attempts, http_client=self._http)
        self._headers = {"User-Agent": _spec.USER_AGENT_PREFIX + __version__, "Accept": "application/json"}
        if region is not None:
            self._headers[_spec.REGION_PIN] = _spec.region(region)

    # Lifecycle -----------------------------------------------------------

    def close(self) -> None:
        if self._source is not None:
            self._source.close()
        if self._owns_http:
            self._http.close()

    def __enter__(self) -> TerraScale:
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    # Transport -----------------------------------------------------------

    def _bearer(self) -> str:
        if self._source is not None:
            return self._source.token()
        return self._token() if callable(self._token) else str(self._token)

    def _send(self, call: Call) -> Any:
        url = call.url(self.base_url)
        content = call.content()
        refreshed = False
        attempt = 0
        conflicts = 0
        resends = 0
        while True:
            direct = "Authorization" in call.headers
            token = "" if direct else self._bearer()
            headers = dict(call.headers) if direct else {**self._headers, **call.headers, "Authorization": f"Bearer {token}"}
            if content is not None:
                headers["Content-Type"] = call.content_type
            try:
                if direct:
                    request = httpx.Request(call.method, url, headers=headers, content=content)
                    response = self._http.send(request, auth=None, follow_redirects=False)
                else:
                    response = self._http.request(
                        call.method, url, params=call.params or None, headers=headers, content=content,
                        timeout=call.timeout if call.timeout is not None else httpx.USE_CLIENT_DEFAULT)
            except (httpx.TransportError, httpx.DecodingError) as exc:
                attempt += 1
                # A connect or pool failure never reached the server, so even a write can be repeated.
                if not call.replay_safe and not isinstance(exc, _spec.NOT_SENT):
                    raise CommitUncertainError(0, "commit_uncertain", "transport failure; the write may have committed") from exc
                if isinstance(exc, httpx.DecodingError):
                    raise InvalidResponseError(0, "invalid_response", "invalid response encoding") from exc
                if attempt >= self._max_attempts:
                    raise _spec.transport_failure(call.method, attempt, exc) from exc
                time.sleep(_spec.backoff(attempt, None))
                continue
            if 200 <= response.status_code < 300:
                return call.decode(response)
            err = _error(response, mutation=not call.replay_safe)
            if response.status_code == 401 and err.code in {"key_revoked", "key_expired"}:
                if self._source is not None:
                    self._source.reject(err)
                raise err
            if response.status_code == 401 and self._source is not None and not refreshed and not direct:
                # Refresh an ordinary token refusal once, never a permanent key refusal.
                self._source.invalidate(token)
                refreshed = True
                continue
            if err.status == 503 and err.code in call.resend_codes and resends < _spec.MOVE_RESENDS:
                resends += 1
                time.sleep(err.retry_after if err.retry_after is not None else 1.0)
                continue
            if isinstance(err, SqlError) and err.code == SqlErrorCodes.SERIALIZATION_CONFLICT and conflicts < 2:
                conflicts += 1
                time.sleep(_spec.backoff(conflicts, None))
                continue
            attempt += 1
            if call.replay_safe and attempt < self._max_attempts and _spec.retryable(err.status, err.code):
                time.sleep(_spec.backoff(attempt, err.retry_after))
                continue
            raise err

    # Databases -----------------------------------------------------------

    def database(self, name: str) -> Database:
        """A handle by current database name or immutable ULID; no request is made."""
        return Database(self, _spec.database_selector(name))

    def list_databases(self, *, limit: int | None = None, cursor: str | None = None) -> models.DatabasePage:
        return self._send(_spec.list_databases(self.project, limit, cursor))

    def rename_database(self, database_id: str, name: str) -> models.Database:
        """Rename by immutable ULID or current name with ``databases:write``; return metadata.

        See docs/database-rename.md for producer availability and conflict handling.
        ID, data, keys and home region stay unchanged. Invalid names return
        400 (or local ValueError); known same-region duplicates return 409.
        After convergence the old name has no alias and may be claimed again.
        Update name-based connection strings and handles; ID-based strings remain stable. Reconcile uncertain failures
        before explicitly retrying this mutation.
        """
        return self._send(_spec.rename_database(self.project, database_id, name))

    def create_database(
        self, name: str, *, home_region: str | None = None, ttl_seconds: int | None = None,
        replica_regions: Sequence[str] | None = None, all_regions: bool | None = None,
    ) -> models.Database:
        """Create a database in ``home_region`` (a region id such as ``"gru"``), or in the region that answers."""
        return self._send(
            _spec.create_database(self.project, name, home_region, ttl_seconds, replica_regions, all_regions))

    def fork_database(
        self, source: str, name: str, *, point_in_time: str | None = None, ttl_seconds: int | None = None,
    ) -> models.Database:
        """Fork the current database state, or an RFC 3339 point in its restore window, with an independent TTL."""
        operation = self._send(_spec.fork_database(self.project, source, name, point_in_time, ttl_seconds))
        if point_in_time is None:
            return operation
        done = _backups._send_or_timeout(self._send, self.project, source, operation)
        return self._send(_spec.get_database(self.project, done.database))

    def set_database_ttl(self, database: str, ttl_seconds: int) -> models.Database:
        """Set expiry in 60 to 31,536,000 seconds; the database and backups are deleted on expiry."""
        return self._send(_spec.set_database_ttl(self.project, database, ttl_seconds))

    def set_database_placement(
        self, database: str, *, replica_regions: Sequence[str] | None = None, all_regions: bool | None = None,
    ) -> models.Database:
        """Set the replica regions, or turn all-regions mode on or off; pass exactly one."""
        return self._send(_spec.set_database_placement(self.project, database, replica_regions, all_regions))

    def move_database_home(self, database: str, home_region: str) -> models.Database:
        """Move the home to one of the database's replica regions and wait until the move is done."""
        return self._send(_spec.move_database_home(self.project, database, home_region))

    def clear_database_ttl(self, database: str) -> models.Database:
        """Remove the database's expiry."""
        return self._send(_spec.clear_database_ttl(self.project, database))

    def delete_database(self, database: str) -> None:
        """Delete by name or ULID in the database's home region."""
        self._send(_spec.delete_database(self.project, database))

    def create_scoped_key(
        self, database: str, access: str, *, project: str | None = None, ttl_seconds: int | None = None,
    ) -> models.ScopedKey:
        """Mint a key for one database ULID, using this client's API key; the default lifetime is 3600 seconds."""
        return self._send(_scoped_key_call(
            self._api_key, self._issuer, project if project is not None else self.project, database, access, ttl_seconds))

    def rotate_api_key(self, key_id: str, grace_seconds: int = 0) -> models.RotatedApiKey:
        """Issue a new secret for the caller's API key id; the previous one keeps working for grace_seconds (0 to 86400)."""
        return self._send(_rotate_key_call(self._api_key, self._issuer, key_id, grace_seconds))


def _error(response: httpx.Response, *, mutation: bool = False) -> APIError:
    try:
        body = response.json()
    except ValueError:
        body = None
    if isinstance(body, dict) and isinstance(body.get("error"), str):
        body = {"error": {"code": body["error"], "message": body.get("error_description", "")}}
    return error_for(response.status_code, body, response.headers, mutation=mutation)


class Database:
    def __init__(self, client: TerraScale, name: str):
        self._c = client
        self.name = name

    def __repr__(self) -> str:
        return f"Database({self.name!r})"

    @property
    def _p(self) -> tuple[str, str]:
        return self._c.project, self.name

    def info(self) -> models.Database:
        return self._c._send(_spec.get_database(*self._p))

    def delete(self) -> None:
        """Delete in the database's home region. HTTP 204 confirms logical deletion and local cleanup;
        remote erasure is retried durably and may complete later."""
        self._c._send(_spec.delete_database(*self._p))

    def branch(self, name: str) -> models.Database:
        """Create an independent copy named ``name``."""
        return self._c._send(_spec.branch_database(*self._p, name))

    @overload
    def query(self, sql: str, args: Args = None) -> builtins.list[Row]: ...

    @overload
    def query(self, sql: str, args: Args, mapper: Callable[..., T]) -> builtins.list[T]: ...

    def query(self, sql: str, args: Args = None, mapper: Callable[..., T] | None = None) -> builtins.list[Any]:
        """Run one statement and return its rows, or ``mapper(**row)`` for each row."""
        result = self.execute(sql, args)
        return result.rows if mapper is None else result.as_(mapper)

    def execute(self, sql: str | Statement, args: Args = None) -> StatementResult:
        """Run one statement, reads and writes, with positional or named arguments."""
        item = sql if isinstance(sql, Statement) else Statement(sql, args)
        return (self._c._send(_spec.execute(*self._p, _sql.statements([item]))))[0]

    def batch(self, statements: Sequence[str | tuple[str, Args] | Statement]) -> builtins.list[StatementResult]:
        """Run 1 to 256 statements as one all-or-nothing transaction; one result per statement."""
        return self._c._send(_spec.execute(*self._p, _sql.statements(statements)))

    def connect(self) -> SqlSession:
        """Open a SQL WebSocket session for interactive transactions; close it or use ``with``."""
        return SqlSession(self)
