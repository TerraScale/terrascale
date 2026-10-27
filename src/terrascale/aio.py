"""Asyncio client: the same API as :mod:`terrascale.client`, awaited."""

from __future__ import annotations

import asyncio
import builtins
from collections.abc import AsyncIterator, Awaitable, Callable, Sequence
from typing import Any

import httpx

from . import _spec, models
from ._spec import Call, TransactionBuilder
from ._version import __version__
from .auth import AsyncAPIKeyTokenSource
from .client import _error, _settings
from .errors import CommitUncertainError, InvalidResponseError

__all__ = ["AsyncTerraScale", "AsyncDatabase", "AsyncCollection", "AsyncTransaction"]


class AsyncTerraScale:
    """A project-scoped asyncio TerraBase client; see :class:`terrascale.TerraScale`.

    ``api_key`` is exchanged for short-lived access tokens with ts-auth and
    refreshed automatically. ``token`` instead supplies an access token, or a
    callable returning one. Arguments default to the ``TERRASCALE_API_KEY``
    and ``TERRASCALE_PROJECT_ID`` environment variables.
    """

    def __init__(
        self,
        api_key: str | None = None,
        *,
        project: str | None = None,
        token: str | Callable[[], str | Awaitable[str]] | None = None,
        base_url: str | None = None,
        issuer: str | None = None,
        scopes: Sequence[str] | None = None,
        timeout: float = 30.0,
        max_attempts: int = _spec.MAX_ATTEMPTS,
        http_client: httpx.AsyncClient | None = None,
    ):
        api_key, self.project, self.base_url = _settings(api_key, project, token, base_url)
        self._http = http_client or httpx.AsyncClient(timeout=timeout, follow_redirects=False)
        self._owns_http = http_client is None
        self._max_attempts = max(1, max_attempts)
        self._source: AsyncAPIKeyTokenSource | None = None
        self._token = token
        if token is None:
            assert api_key is not None
            self._source = AsyncAPIKeyTokenSource(api_key, project=self.project, scopes=scopes, issuer=issuer,
                                                  max_attempts=self._max_attempts, http_client=self._http)
        self._headers = {"User-Agent": _spec.USER_AGENT_PREFIX + __version__, "Accept": "application/json"}

    # Lifecycle -----------------------------------------------------------

    async def close(self) -> None:
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
        while True:
            token = await self._bearer()
            headers = {**self._headers, **call.headers, "Authorization": f"Bearer {token}"}
            if content is not None:
                headers["Content-Type"] = call.content_type
            try:
                response = await self._http.request(call.method, url, params=call.params or None, headers=headers,
                                              content=content)
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
            if response.status_code == 401 and self._source is not None and not refreshed:
                # Refresh an ordinary token refusal once, never a permanent key refusal.
                self._source.invalidate(token)
                refreshed = True
                continue
            attempt += 1
            if call.replay_safe and attempt < self._max_attempts and _spec.retryable(err.status, err.code):
                await asyncio.sleep(_spec.backoff(attempt, err.retry_after))
                continue
            raise err

    # Databases -----------------------------------------------------------

    def database(self, name: str) -> AsyncDatabase:
        """A handle for the named database; no request is made."""
        return AsyncDatabase(self, _spec.name(name, "database"))

    async def list_databases(self, *, limit: int | None = None, cursor: str | None = None) -> models.DatabasePage:
        return await self._send(_spec.list_databases(self.project, limit, cursor))

    async def create_database(self, name: str, *, home_region: str | None = None,
                        replica_regions: Sequence[str] | None = None,
                        all_regions: bool | None = None) -> models.Database:
        """Create a database in at least two distinct serving regions.

        Regions are names such as ``"gru"``. Omitting ``replica_regions`` uses
        all current serving regions; ``home_region`` alone is not single-region
        placement. Pass ``all_regions=True`` to include future regions too.
        An explicit ``replica_regions=[]`` is rejected with HTTP 400 on create.
        """
        return await self._send(_spec.create_database(self.project, name, home_region, replica_regions, all_regions))



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

    async def update(self, *, home_region: str | None = None, replica_regions: Sequence[str] | None = None,
               all_regions: bool | None = None) -> models.Database:
        """Change region placement. Writable databases require at least two regions.

        ``replica_regions=[]`` can temporarily disable document writes with
        HTTP 409 ``placement_needs_two_regions``. It does not provide supported
        single-region placement and is not required before deletion.
        ``all_regions=True`` includes current and future serving regions.
        """
        return await self._c._send(_spec.update_database(*self._p, home_region, replica_regions, all_regions))

    async def delete(self) -> None:
        """Delete directly in the database's home region, without a placement update.

        Deletion covers every replica, including all-regions placement. HTTP 204
        confirms logical deletion and local cleanup; remote erasure is retried
        durably and may complete later.
        """
        await self._c._send(_spec.delete_database(*self._p))

    async def branch(self, name: str) -> models.Database:
        """Create an independent copy named ``name``."""
        return await self._c._send(_spec.branch_database(*self._p, name))

    async def create_collection(self, name: str) -> models.Collection:
        """Create a collection; an existing name raises ConflictError (``name_taken``)."""
        return await self._c._send(_spec.create_collection(*self._p, name))

    async def list_collections(self, *, limit: int | None = None, cursor: str | None = None) -> models.CollectionPage:
        return await self._c._send(_spec.list_collections(*self._p, limit, cursor))

    def collection(self, name: str) -> AsyncCollection:
        return AsyncCollection(self, _spec.name(name, "collection"))

    async def sql(self, statement: str) -> models.SQLResult:
        """Run a read-only ``SELECT`` (at most 1,000 rows)."""
        return await self._c._send(_spec.sql(*self._p, statement))

    async def check_sql(self, statement: str) -> dict[str, Any]:
        return await self._c._send(_spec.sql(*self._p, statement, "check"))

    async def explain_sql(self, statement: str) -> dict[str, Any]:
        return await self._c._send(_spec.sql(*self._p, statement, "explain"))

    async def execute(self, sql: str, args: Sequence[Any] | dict[str, Any] | None = None) -> models.SQLStatementResult:
        """Run one SQL statement (reads and writes) with positional or named args."""
        return (await self._c._send(_spec.execute(*self._p, [_spec.statement(sql, args)])))[0]

    async def batch(self, statements: Sequence[str | tuple[str, Any]]) -> builtins.list[models.SQLStatementResult]:
        """Run statements as one all-or-nothing transaction."""
        stmts = [_spec.statement(s, None) if isinstance(s, str) else _spec.statement(*s) for s in statements]
        return await self._c._send(_spec.execute(*self._p, stmts))

    def transaction(self) -> AsyncTransaction:
        """An atomic group of document ops across collections::

            async with db.transaction() as tx:
                tx.create("orders", {"total": 5})
                tx.patch("users", user_id, ops=[q.inc("/orders", 1)])
            tx.result  # committed on exit
        """
        return AsyncTransaction(self)


class AsyncTransaction(TransactionBuilder):
    def __init__(self, db: AsyncDatabase):
        super().__init__()
        self._db = db
        self.result: models.TransactionResult | None = None

    async def commit(self) -> models.TransactionResult:
        self.result = await self._db._c._send(self._prepare(*self._db._p))
        return self.result

    async def __aenter__(self) -> AsyncTransaction:
        return self

    async def __aexit__(self, exc_type: object, *_: object) -> None:
        if exc_type is None and not self._consumed:
            await self.commit()


class AsyncCollection:
    def __init__(self, db: AsyncDatabase, name: str):
        self._db = db
        self._c = db._c
        self.name = name

    def __repr__(self) -> str:
        return f"AsyncCollection({self._db.name!r}, {self.name!r})"

    @property
    def _p(self) -> tuple[str, str, str]:
        return self._c.project, self._db.name, self.name

    async def info(self) -> models.Collection:
        return await self._c._send(_spec.get_collection(*self._p))

    async def drop(self) -> None:
        await self._c._send(_spec.drop_collection(*self._p))

    async def get(self, id: str) -> models.Document:
        """Read a document; absent, deleted and expired ones raise NotFoundError."""
        return await self._c._send(_spec.get_document(*self._p, id))

    async def create(self, document: dict[str, Any]) -> models.WriteResult:
        """Insert with a server-generated ULID id."""
        return await self._c._send(_spec.create_document(*self._p, document))

    async def put(self, id: str, document: dict[str, Any], *, if_match: str | None = None,
            if_none_match_any: bool = False) -> models.WriteResult:
        """Create or replace the document ``id``."""
        return await self._c._send(_spec.put_document(*self._p, id, document, if_match, if_none_match_any))

    async def patch(self, id: str, *, merge: dict[str, Any] | None = None, ops: Sequence[dict[str, Any]] | None = None,
              if_match: str | None = None) -> models.WriteResult:
        """JSON merge patch (``merge``) or ordered ops (``ops``, see :mod:`terrascale.query`)."""
        return await self._c._send(_spec.patch_document(*self._p, id, merge, ops, if_match))

    async def delete(self, id: str, *, if_match: str | None = None) -> models.WriteResult:
        return await self._c._send(_spec.delete_document(*self._p, id, if_match))

    async def list(self, *, limit: int | None = None, cursor: str | None = None) -> models.Page:
        """One page in id order."""
        return await self._c._send(_spec.list_documents(*self._p, limit, cursor))

    async def query(self, filter: dict[str, Any] | None = None, *, sort: Sequence[dict[str, Any]] | None = None,
              project: Sequence[str] | None = None, limit: int | None = None, cursor: str | None = None,
              count: bool = False, aggregate: dict[str, Any] | None = None) -> models.Page:
        """One page of matches; follow ``next_cursor`` or use :meth:`pages`/:meth:`items`.

        ``aggregate`` (see :func:`terrascale.query.aggregate`) answers ``groups`` instead of items.
        """
        return await self._c._send(_spec.query_documents(*self._p, filter, sort, project, limit, cursor, count, aggregate))

    async def pages(self, filter: dict[str, Any] | None = None, *, sort: Sequence[dict[str, Any]] | None = None,
                    project: Sequence[str] | None = None, limit: int | None = None) -> AsyncIterator[models.Page]:
        cursor = None
        seen: set[str] = set()
        while True:
            page = await self.query(filter, sort=sort, project=project, limit=limit, cursor=cursor)
            if page.next_cursor:
                if page.next_cursor in seen:
                    raise InvalidResponseError(200, "invalid_response", "repeated pagination cursor")
                seen.add(page.next_cursor)
            yield page
            if not page.next_cursor:
                return
            cursor = page.next_cursor

    async def items(self, filter: dict[str, Any] | None = None, *, sort: Sequence[dict[str, Any]] | None = None,
                    project: Sequence[str] | None = None, limit: int | None = None) -> AsyncIterator[models.Document]:
        async for page in self.pages(filter, sort=sort, project=project, limit=limit):
            for item in page.items:
                yield item

    async def search(self, path: str, vector: Sequence[float], *, limit: int = 10, metric: str = "cosine",
                     filter: dict[str, Any] | None = None) -> builtins.list[models.Document]:
        """Vector search: the ``limit`` (<= 100) nearest documents, each with ``distance``."""
        from .query import near

        return (await self.query(filter, sort=[near(path, vector, metric)], limit=limit)).items

    async def get_ttl(self) -> str | None:
        """The TTL path, or None."""
        return await self._c._send(_spec.get_ttl(*self._p))

    async def set_ttl(self, path: str) -> str | None:
        """Delete documents whose ``path`` holds a past RFC 3339 UTC time
        (an hourly sweep; expired documents stay readable until then)."""
        return await self._c._send(_spec.set_ttl(*self._p, path))

    async def delete_ttl(self) -> None:
        await self._c._send(_spec.delete_ttl(*self._p))
