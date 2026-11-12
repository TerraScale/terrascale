"""Asyncio client: the same API as :mod:`terrascale.client`, awaited."""

from __future__ import annotations

import asyncio
import builtins
from collections.abc import AsyncIterator, Awaitable, Callable, Sequence
from typing import Any, TypeVar, overload

import httpx

from . import _spec, models
from . import backups as _backups
from . import sql as _sql
from ._spec import Call
from ._version import __version__
from .auth import (
    AsyncAPIKeyTokenSource,
    _rotate_key_call,
    _scoped_key_call,
    create_api_key_call,
    create_project_client_call,
    delete_project_client_call,
    delete_provider_call,
    delete_user_call,
    disable_user_call,
    enable_user_call,
    export_users_call,
    get_settings_call,
    get_user_call,
    list_api_keys_call,
    list_project_clients_call,
    list_sessions_call,
    list_users_call,
    revoke_api_key_call,
    revoke_session_call,
    rotate_project_client_secret_call,
    set_provider_call,
    update_project_client_call,
    update_settings_call,
)
from .client import _error, _settings
from .errors import CommitUncertainError, InvalidResponseError, SqlError, SqlErrorCodes
from .session import AsyncSqlSession
from .sql import Args, Row, Statement, StatementResult

__all__ = ["AsyncTerraScale", "AsyncDatabase"]

T = TypeVar("T")


class AsyncTerraScale:
    """A project-scoped asyncio TerraBase client; see :class:`terrascale.TerraScale`.

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
        token: str | Callable[[], str | Awaitable[str]] | None = None,
        base_url: str | None = None,
        region: str | None = None,
        issuer: str | None = None,
        scopes: Sequence[str] | None = None,
        timeout: float = 30.0,
        max_attempts: int = _spec.MAX_ATTEMPTS,
        http_client: httpx.AsyncClient | None = None,
    ):
        api_key, self.project, self.base_url = _settings(api_key, project, token, base_url, issuer)
        self._http = http_client or httpx.AsyncClient(timeout=timeout, follow_redirects=False, http2=True)
        self._owns_http = http_client is None
        self._max_attempts = max(1, max_attempts)
        self._source: AsyncAPIKeyTokenSource | None = None
        self._token = token
        self._api_key = api_key
        self._issuer = issuer
        if token is None:
            assert api_key is not None
            self._source = AsyncAPIKeyTokenSource(api_key, project=self.project, scopes=scopes, issuer=issuer,
                                                  max_attempts=self._max_attempts, http_client=self._http)
        self._headers = {"User-Agent": _spec.USER_AGENT_PREFIX + __version__, "Accept": "application/json"}
        if region is not None and (not isinstance(region, str) or region.strip()):
            self._headers[_spec.REGION_PIN] = _spec.region(region)

    # Lifecycle -----------------------------------------------------------

    async def close(self) -> None:
        if self._source is not None:
            await self._source.close()
        if self._owns_http:
            await self._http.aclose()

    async def __aenter__(self) -> AsyncTerraScale:
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.close()

    # Transport -----------------------------------------------------------

    async def _bearer(self) -> str:
        if self._source is not None:
            return await self._source.token()
        if callable(self._token):
            token = self._token()
            return await token if isinstance(token, Awaitable) else token
        return str(self._token)

    async def _send(self, call: Call) -> Any:
        url = call.url(self.base_url)
        content = call.content()
        refreshed = False
        attempt = 0
        conflicts = 0
        while True:
            direct = "Authorization" in call.headers
            token = "" if direct or call.anonymous else await self._bearer()
            headers = dict(call.headers) if direct else {**self._headers, **call.headers}
            if not direct and not call.anonymous:
                headers["Authorization"] = f"Bearer {token}"
            if content is not None:
                headers["Content-Type"] = call.content_type
            try:
                if direct:
                    request = httpx.Request(call.method, url, headers=headers, content=content)
                    response = await self._http.send(request, auth=None, follow_redirects=False)
                else:
                    response = await self._http.request(
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
                await asyncio.sleep(_spec.backoff(attempt, None))
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
            if isinstance(err, SqlError) and err.code == SqlErrorCodes.SERIALIZATION_CONFLICT and conflicts < 2:
                conflicts += 1
                await asyncio.sleep(_spec.backoff(conflicts, None))
                continue
            attempt += 1
            if call.replay_safe and attempt < self._max_attempts and _spec.retryable(err.status, err.code):
                await asyncio.sleep(_spec.backoff(attempt, err.retry_after))
                continue
            raise err

    async def _stream(self, call: Call) -> AsyncIterator[bytes]:
        """Send a replay-safe GET and yield its body in chunks; the response closes when the iterator does."""
        url = call.url(self.base_url)
        attempt = 0
        refreshed = False
        while True:
            token = await self._bearer()
            headers = {**self._headers, **call.headers, "Authorization": f"Bearer {token}"}
            request = httpx.Request(call.method, url, headers=headers)
            try:
                response = await self._http.send(request, auth=None, stream=True, follow_redirects=False)
            except httpx.TransportError as exc:
                attempt += 1
                if attempt >= self._max_attempts:
                    raise _spec.transport_failure(call.method, attempt, exc) from exc
                await asyncio.sleep(_spec.backoff(attempt, None))
                continue
            if 200 <= response.status_code < 300:
                break
            err = _error(response)
            await response.aclose()
            if response.status_code == 401 and err.code in {"key_revoked", "key_expired"}:
                if self._source is not None:
                    self._source.reject(err)
                raise err
            if response.status_code == 401 and self._source is not None and not refreshed:
                self._source.invalidate(token)
                refreshed = True
                continue
            attempt += 1
            if attempt < self._max_attempts and _spec.retryable(err.status, err.code):
                await asyncio.sleep(_spec.backoff(attempt, err.retry_after))
                continue
            raise err
        try:
            async for chunk in response.aiter_bytes():
                yield chunk
        finally:
            await response.aclose()

    # Databases -----------------------------------------------------------

    def database(self, name: str) -> AsyncDatabase:
        """A handle by current database name or immutable ULID; no request is made."""
        return AsyncDatabase(self, _spec.database_selector(name))

    async def list_databases(self, *, limit: int | None = None, cursor: str | None = None) -> models.DatabasePage:
        return await self._send(_spec.list_databases(self.project, limit, cursor))

    async def list_regions(self) -> models.Regions:
        """The anonymous ordered serving-region catalog, sent without a bearer token."""
        return await self._send(_spec.list_regions())

    async def rename_database(self, database_id: str, name: str) -> models.Database:
        """Rename by immutable ULID or current name with ``databases:write``; return metadata.

        See docs/database-rename.md for producer availability and conflict handling.
        ID, data, keys and placement stay unchanged. Invalid names return
        400 (or local ValueError); known same-region duplicates return 409.
        After convergence the old name has no alias and may be claimed again.
        Update name-based connection strings and handles; ID-based strings remain stable. Reconcile uncertain failures
        before explicitly retrying this mutation.
        """
        return await self._send(_spec.rename_database(self.project, database_id, name))

    async def create_database(
        self,
        name: str,
        *,
        ttl_seconds: int | None = None,
        regions: Sequence[str] | None = None,
        all_regions: bool | None = None,
    ) -> models.Database:
        """Create in the selected regions, or the answering region; TTL expiry deletes the database and backups."""
        return await self._send(_spec.create_database(self.project, name, ttl_seconds, regions, all_regions))

    async def fork_database(
        self, source: str, name: str, *, point_in_time: str | None = None, ttl_seconds: int | None = None,
    ) -> models.Database:
        """Fork the current database state, or an RFC 3339 point in its restore window, with an independent TTL."""
        operation = await self._send(_spec.fork_database(self.project, source, name, point_in_time, ttl_seconds))
        if point_in_time is None:
            return operation
        done = await _backups._send_or_timeout_async(self._send, self.project, source, operation)
        return await self._send(_spec.get_database(self.project, done.database))

    async def set_database_ttl(self, database: str, ttl_seconds: int) -> models.Database:
        """Set expiry in 60 to 31,536,000 seconds; the database and backups are deleted on expiry."""
        return await self._send(_spec.set_database_ttl(self.project, database, ttl_seconds))

    async def set_database_placement(
        self,
        database: str,
        *,
        regions: Sequence[str] | None = None,
        all_regions: bool | None = None,
    ) -> models.Database:
        """Set every region holding the database, or all regions; placement refusals are never automatically retried."""
        return await self._send(_spec.set_database_placement(self.project, database, regions, all_regions))

    async def clear_database_ttl(self, database: str) -> models.Database:
        """Remove the database's expiry."""
        return await self._send(_spec.clear_database_ttl(self.project, database))

    async def delete_database(self, database: str) -> None:
        """Delete the database and backups; a region outside its placement returns 421 wrong_region."""
        await self._send(_spec.delete_database(self.project, database))

    async def delete_database_if_id(self, database: str, expected_id: str) -> None:
        """Delete only when the name still resolves to expected_id; a changed identity answers 409."""
        await self._send(_spec.delete_database_if_id(self.project, database, expected_id))

    def export_database(self, database: str) -> AsyncIterator[bytes]:
        """Stream the database as one SQL dump (BEGIN … COMMIT) with ``sql:read``.

        The region that orders the database's writes serves the export; consume the iterator to
        read the dump and close it when done.
        """
        return self._stream(_spec.export_database(self.project, _spec.database_selector(database)))

    async def create_scoped_key(
        self, database: str, access: str, *, project: str | None = None, ttl_seconds: int | None = None,
    ) -> models.ScopedKey:
        """Mint a key for one database ULID, using this client's API key; the default lifetime is 3600 seconds."""
        return await self._send(_scoped_key_call(
            self._api_key, self._issuer, project if project is not None else self.project, database, access, ttl_seconds))

    async def rotate_api_key(self, key_id: str, grace_seconds: int = 0) -> models.RotatedApiKey:
        """Issue a new secret for the caller's API key id; the previous one keeps working for grace_seconds (0 to 86400)."""
        return await self._send(_rotate_key_call(self._api_key, self._issuer, key_id, grace_seconds))

    async def create_api_key(
        self, organization: str, name: str, scopes: Sequence[str], *,
        project: str | None = None, database: str | None = None, expires_at: str | None = None,
        allowed_cidrs: Sequence[str] | None = None,
    ) -> models.ApiKeySecret:
        """Create an organization key, or a project key with ``project``; the whole key string is answered once."""
        return await self._send(create_api_key_call(
            self._issuer, organization, project, name, scopes, database, expires_at, allowed_cidrs))

    async def list_api_keys(
        self, organization: str, *, project: str | None = None, limit: int | None = None, cursor: str | None = None,
    ) -> models.ApiKeyPage:
        """List an organization's keys, or a project's, in creation order; never secrets."""
        return await self._send(list_api_keys_call(self._issuer, organization, project, limit, cursor))

    async def revoke_api_key(self, organization: str, key: str, *, project: str | None = None) -> None:
        """Revoke a key at once; tokens already issued live out their 15 minutes."""
        await self._send(revoke_api_key_call(self._issuer, organization, project, key))

    async def create_project_client(
        self, name: str, client_type: str, redirect_uris: Sequence[str], *, step_up_proof: str,
        project: str | None = None,
    ) -> models.ProjectClient:
        """Register a public or confidential OAuth client; a confidential secret is answered once."""
        return await self._send(create_project_client_call(
            self._issuer, project if project is not None else self.project, name, client_type, redirect_uris,
            step_up_proof))

    async def list_project_clients(self, *, project: str | None = None) -> models.ProjectClientPage:
        """List every registered client of the project in one page, without secrets."""
        return await self._send(list_project_clients_call(self._issuer, project if project is not None else self.project))

    async def update_project_client(
        self, client_id: str, name: str, redirect_uris: Sequence[str], *, step_up_proof: str,
        project: str | None = None,
    ) -> models.ProjectClient:
        """Rename a client and replace its redirect URIs; the id, type and secret stay unchanged."""
        return await self._send(update_project_client_call(
            self._issuer, project if project is not None else self.project, client_id, name, redirect_uris,
            step_up_proof))

    async def rotate_project_client_secret(
        self, client_id: str, *, step_up_proof: str, grace_seconds: int | None = None, project: str | None = None,
    ) -> models.ProjectClientSecret:
        """Issue a new secret for a confidential client; the previous one keeps working for grace_seconds (0 to 86400)."""
        return await self._send(rotate_project_client_secret_call(
            self._issuer, project if project is not None else self.project, client_id, grace_seconds, step_up_proof))

    async def delete_project_client(self, client_id: str, *, project: str | None = None) -> None:
        """Delete a registered client."""
        await self._send(delete_project_client_call(self._issuer, project if project is not None else self.project,
                                                    client_id))

    async def get_settings(self, *, project: str | None = None) -> models.ProjectSettings:
        """Read the project's end-user sign-in settings; viewer access with the caller's sign-in token."""
        return await self._send(get_settings_call(self._issuer, project if project is not None else self.project))

    async def update_settings(self, update: models.ProjectSettingsUpdate, *, project: str | None = None,
                             ) -> models.ProjectSettings:
        """Update settings; omitted parts stay unchanged and admin access is required."""
        return await self._send(
            update_settings_call(self._issuer, project if project is not None else self.project, update))

    async def list_users(self, *, search: str | None = None, limit: int | None = None, after: str | None = None,
                         project: str | None = None) -> models.ProjectUserPage:
        """List the project's end users in creation order; ``search`` matches an email substring."""
        return await self._send(list_users_call(self._issuer, project if project is not None else self.project,
                                                search, limit, after))

    async def get_user(self, user_id: str, *, project: str | None = None) -> models.ProjectUser:
        """Read one end user by id."""
        return await self._send(get_user_call(self._issuer, project if project is not None else self.project,
                                              user_id))

    async def disable_user(self, user_id: str, *, project: str | None = None) -> models.ProjectUser:
        """Disable a user; every session ends and refresh tokens stop at once."""
        return await self._send(disable_user_call(self._issuer, project if project is not None else self.project,
                                                  user_id))

    async def enable_user(self, user_id: str, *, project: str | None = None) -> models.ProjectUser:
        """Re-enable a disabled user."""
        return await self._send(enable_user_call(self._issuer, project if project is not None else self.project,
                                                 user_id))

    async def delete_user(self, user_id: str, *, project: str | None = None) -> None:
        """Delete a user's account; the address can sign up again."""
        await self._send(delete_user_call(self._issuer, project if project is not None else self.project, user_id))

    async def export_users(self, *, limit: int | None = None, after: str | None = None,
                           project: str | None = None) -> models.ProjectUserExportPage:
        """Fetch one audited directory-export page; pass the previous page's ``next_cursor`` as ``after``."""
        return await self._send(export_users_call(self._issuer, project if project is not None else self.project,
                                                  limit, after))

    async def set_provider(self, provider: str, update: models.ProjectProviderUpdate, *,
                           project: str | None = None) -> models.ProjectSettings:
        """Configure a federated sign-in provider (google, github, apple or oidc); an omitted secret is kept."""
        return await self._send(set_provider_call(self._issuer, project if project is not None else self.project,
                                                  provider, update))

    async def delete_provider(self, provider: str, *, project: str | None = None) -> models.ProjectSettings:
        """Remove a federated sign-in provider; the answered settings no longer carry it."""
        return await self._send(delete_provider_call(self._issuer, project if project is not None else self.project,
                                                     provider))

    async def list_sessions(self, *, user_id: str | None = None, limit: int | None = None,
                            after: str | None = None, project: str | None = None) -> models.ProjectSessionPage:
        """List live sessions, optionally of one user, in creation order."""
        return await self._send(list_sessions_call(self._issuer, project if project is not None else self.project,
                                                   user_id, limit, after))

    async def revoke_session(self, session_id: str, *, project: str | None = None) -> None:
        """Revoke one session; the session is signed out at once."""
        await self._send(revoke_session_call(self._issuer, project if project is not None else self.project,
                                             session_id))


class AsyncDatabase:
    def __init__(self, client: AsyncTerraScale, name: str):
        self._c = client
        self.name = name

    def __repr__(self) -> str:
        return f"AsyncDatabase({self.name!r})"

    @property
    def _p(self) -> tuple[str, str]:
        return self._c.project, self.name

    async def info(self) -> models.Database:
        return await self._c._send(_spec.get_database(*self._p))

    async def list_database_logs(self, status: str | None = None) -> models.DatabaseRequestLogs:
        """The newest 100 recorded requests within 24 hours; ``status`` filters by HTTP status class.
        Recording is best effort, without a cursor or export guarantee."""
        return await self._c._send(_spec.list_database_logs(*self._p, status))


    async def regions(self) -> models.DatabaseRegions:
        """Read selected regions and writer-observed pull progress with databases:read; observations are not acknowledgements."""
        return await self._c._send(_spec.get_database_regions(*self._p))

    async def delete(self) -> None:
        """Delete in the region that orders the database's writes. HTTP 204 confirms logical deletion and local cleanup;
        remote erasure is retried durably and may complete later."""
        await self._c._send(_spec.delete_database(*self._p))

    async def branch(self, name: str) -> models.Database:
        """Create an independent copy named ``name``."""
        return await self._c._send(_spec.branch_database(*self._p, name))

    async def branch_if_id(self, name: str, expected_id: str) -> models.Database:
        """Branch only when the name still resolves to expected_id; a changed identity answers 409."""
        return await self._c._send(_spec.branch_database_if_id(*self._p, name, expected_id))

    async def delete_if_id(self, expected_id: str) -> None:
        """Delete only when the name still resolves to expected_id; a changed identity answers 409."""
        await self._c._send(_spec.delete_database_if_id(*self._p, expected_id))

    @overload
    async def query(self, sql: str, args: Args = None) -> builtins.list[Row]: ...

    @overload
    async def query(self, sql: str, args: Args, mapper: Callable[..., T]) -> builtins.list[T]: ...

    async def query(self, sql: str, args: Args = None, mapper: Callable[..., T] | None = None) -> builtins.list[Any]:
        """Run one statement and return its rows, or ``mapper(**row)`` for each row."""
        result = await self.execute(sql, args)
        return result.rows if mapper is None else result.as_(mapper)

    async def execute(self, sql: str | Statement, args: Args = None) -> StatementResult:
        """Run one statement, reads and writes, with positional or named arguments."""
        return (await self._c._send(_spec.execute(*self._p, _sql.statements([_sql.statement(sql, args)]))))[0]

    async def batch(self, statements: Sequence[str | tuple[str, Args] | Statement]) -> builtins.list[StatementResult]:
        """Run 1 to 256 statements as one all-or-nothing transaction; one result per statement."""
        return await self._c._send(_spec.execute(*self._p, _sql.statements(statements)))

    def connect(self) -> AsyncSqlSession:
        """A SQL WebSocket session: ``await db.connect()`` or ``async with db.connect() as session``."""
        return AsyncSqlSession(self)
