"""Synchronous client."""

from __future__ import annotations

import builtins
import os
import time
from collections.abc import Callable, Iterator, Sequence
from typing import Any

import httpx

from . import _spec, models
from ._spec import Call, TransactionBuilder
from ._version import __version__
from .auth import APIKeyTokenSource
from .errors import APIError, CommitUncertainError, InvalidResponseError, TerraScaleError, error_for
from .ids import validate_id

__all__ = ["TerraScale", "Database", "Collection", "Transaction"]


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
    and ``TERRASCALE_PROJECT_ID`` environment variables.
    """

    def __init__(
        self,
        api_key: str | None = None,
        *,
        project: str | None = None,
        token: str | Callable[[], str] | None = None,
        base_url: str | None = None,
        issuer: str | None = None,
        scopes: Sequence[str] | None = None,
        timeout: float = 30.0,
        max_attempts: int = _spec.MAX_ATTEMPTS,
        http_client: httpx.Client | None = None,
    ):
        api_key, self.project, self.base_url = _settings(api_key, project, token, base_url)
        self._http = http_client or httpx.Client(timeout=timeout, follow_redirects=False)
        self._owns_http = http_client is None
        self._max_attempts = max(1, max_attempts)
        self._source: APIKeyTokenSource | None = None
        self._token = token
        if token is None:
            assert api_key is not None
            self._source = APIKeyTokenSource(api_key, project=self.project, scopes=scopes, issuer=issuer,
                                             http_client=self._http)
        self._headers = {"User-Agent": _spec.USER_AGENT_PREFIX + __version__, "Accept": "application/json"}

    # Lifecycle -----------------------------------------------------------

    def close(self) -> None:
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
        while True:
            token = self._bearer()
            headers = {**self._headers, **call.headers, "Authorization": f"Bearer {token}"}
            if content is not None:
                headers["Content-Type"] = call.content_type
            try:
                response = self._http.request(call.method, url, params=call.params or None, headers=headers,
                                              content=content)
            except (httpx.TransportError, httpx.DecodingError) as exc:
                attempt += 1
                if not call.replay_safe:
                    raise CommitUncertainError(0, "commit_uncertain", "transport failure; the write may have committed") from exc
                if isinstance(exc, httpx.DecodingError):
                    raise InvalidResponseError(0, "invalid_response", "invalid response encoding") from exc
                if attempt >= self._max_attempts:
                    raise TerraScaleError(f"{call.method} failed: {type(exc).__name__}") from exc
                time.sleep(_spec.backoff(attempt, None))
                continue
            if 200 <= response.status_code < 300:
                return call.decode(response)
            err = _error(response, mutation=not call.replay_safe)
            if response.status_code == 401 and self._source is not None and not refreshed:
                # An expired or revoked token was never processed: refresh once.
                self._source.invalidate(token)
                refreshed = True
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
        """Rename by immutable ULID with ``databases:write``; return metadata.

        Availability remains held pending the owning API; see docs/database-rename.md.
        The pending contract keeps ID, data, keys and placement unchanged. Invalid names return
        400 (or local ValueError); duplicate names in the project return 409.
        Old name routes return 404. Update name-based connection strings and
        handles; ID-based strings remain stable. Reconcile uncertain failures
        before explicitly retrying this mutation.
        """
        return self._send(_spec.rename_database(self.project, database_id, name))

    def create_database(self, name: str, *, home_region: str | None = None,
                        replica_regions: Sequence[str] | None = None,
                        all_regions: bool | None = None) -> models.Database:
        """Create a database in at least two distinct serving regions.

        Regions are names such as ``"gru"``. Omitting ``replica_regions`` uses
        all current serving regions; ``home_region`` alone is not single-region
        placement. Pass ``all_regions=True`` to include future regions too.
        An explicit ``replica_regions=[]`` is rejected with HTTP 400 on create.
        """
        return self._send(_spec.create_database(self.project, name, home_region, replica_regions, all_regions))


def _error(response: httpx.Response, *, mutation: bool = False) -> APIError:
    try:
        body = response.json()
    except ValueError:
        body = None
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

    def update(self, *, home_region: str | None = None, replica_regions: Sequence[str] | None = None,
               all_regions: bool | None = None) -> models.Database:
        """Change region placement. Writable databases require at least two regions.

        ``replica_regions=[]`` can temporarily disable document writes with
        HTTP 409 ``placement_needs_two_regions``. It does not provide supported
        single-region placement and is not required before deletion.
        ``all_regions=True`` includes current and future serving regions.
        """
        return self._c._send(_spec.update_database(*self._p, home_region, replica_regions, all_regions))

    def delete(self) -> None:
        """Delete directly in the database's home region, without a placement update.

        Deletion covers every replica, including all-regions placement. HTTP 204
        confirms logical deletion and local cleanup; remote erasure is retried
        durably and may complete later.
        """
        self._c._send(_spec.delete_database(*self._p))

    def branch(self, name: str) -> models.Database:
        """Create an independent copy named ``name``."""
        return self._c._send(_spec.branch_database(*self._p, name))

    def create_collection(self, name: str) -> models.Collection:
        """Create a collection; an existing name raises ConflictError (``name_taken``)."""
        return self._c._send(_spec.create_collection(*self._p, name))

    def list_collections(self, *, limit: int | None = None, cursor: str | None = None) -> models.CollectionPage:
        return self._c._send(_spec.list_collections(*self._p, limit, cursor))

    def collection(self, name: str) -> Collection:
        return Collection(self, _spec.name(name, "collection"))

    def sql(self, statement: str) -> models.SQLResult:
        """Run a read-only ``SELECT`` (at most 1,000 rows)."""
        return self._c._send(_spec.sql(*self._p, statement))

    def check_sql(self, statement: str) -> dict[str, Any]:
        return self._c._send(_spec.sql(*self._p, statement, "check"))

    def explain_sql(self, statement: str) -> dict[str, Any]:
        return self._c._send(_spec.sql(*self._p, statement, "explain"))

    def execute(self, sql: str, args: Sequence[Any] | dict[str, Any] | None = None) -> models.SQLStatementResult:
        """Run one SQL statement (reads and writes) with positional or named args."""
        return self._c._send(_spec.execute(*self._p, [_spec.statement(sql, args)]))[0]

    def batch(self, statements: Sequence[str | tuple[str, Any]]) -> builtins.list[models.SQLStatementResult]:
        """Run statements as one all-or-nothing transaction."""
        stmts = [_spec.statement(s, None) if isinstance(s, str) else _spec.statement(*s) for s in statements]
        return self._c._send(_spec.execute(*self._p, stmts))

    def transaction(self) -> Transaction:
        """An atomic group of document ops across collections::

            with db.transaction() as tx:
                tx.create("orders", {"total": 5})
                tx.patch("users", user_id, ops=[q.inc("/orders", 1)])
            tx.result  # committed on exit
        """
        return Transaction(self)


class Transaction(TransactionBuilder):
    def __init__(self, db: Database):
        super().__init__()
        self._db = db
        self.result: models.TransactionResult | None = None

    def commit(self) -> models.TransactionResult:
        self.result = self._db._c._send(self._prepare(*self._db._p))
        return self.result

    def __enter__(self) -> Transaction:
        return self

    def __exit__(self, exc_type: object, *_: object) -> None:
        if exc_type is None and not self._consumed:
            self.commit()


class Collection:
    def __init__(self, db: Database, name: str):
        self._db = db
        self._c = db._c
        self.name = name

    def __repr__(self) -> str:
        return f"Collection({self._db.name!r}, {self.name!r})"

    @property
    def _p(self) -> tuple[str, str, str]:
        return self._c.project, self._db.name, self.name

    def info(self) -> models.Collection:
        return self._c._send(_spec.get_collection(*self._p))

    def drop(self) -> None:
        self._c._send(_spec.drop_collection(*self._p))

    def get(self, id: str) -> models.Document:
        """Read a document; absent, deleted and expired ones raise NotFoundError."""
        return self._c._send(_spec.get_document(*self._p, id))

    def create(self, document: dict[str, Any]) -> models.WriteResult:
        """Insert with a server-generated ULID id."""
        return self._c._send(_spec.create_document(*self._p, document))

    def put(self, id: str, document: dict[str, Any], *, if_match: str | None = None,
            if_none_match_any: bool = False) -> models.WriteResult:
        """Create or replace the document ``id``."""
        return self._c._send(_spec.put_document(*self._p, id, document, if_match, if_none_match_any))

    def patch(self, id: str, *, merge: dict[str, Any] | None = None, ops: Sequence[dict[str, Any]] | None = None,
              if_match: str | None = None) -> models.WriteResult:
        """JSON merge patch (``merge``) or ordered ops (``ops``, see :mod:`terrascale.query`)."""
        return self._c._send(_spec.patch_document(*self._p, id, merge, ops, if_match))

    def delete(self, id: str, *, if_match: str | None = None) -> models.WriteResult:
        return self._c._send(_spec.delete_document(*self._p, id, if_match))

    def list(self, *, limit: int | None = None, cursor: str | None = None) -> models.Page:
        """One page in id order."""
        return self._c._send(_spec.list_documents(*self._p, limit, cursor))

    def query(self, filter: dict[str, Any] | None = None, *, sort: Sequence[dict[str, Any]] | None = None,
              project: Sequence[str] | None = None, limit: int | None = None, cursor: str | None = None,
              count: bool = False, aggregate: dict[str, Any] | None = None) -> models.Page:
        """One page of matches; follow ``next_cursor`` or use :meth:`pages`/:meth:`items`.

        ``aggregate`` (see :func:`terrascale.query.aggregate`) answers ``groups`` instead of items.
        """
        return self._c._send(_spec.query_documents(*self._p, filter, sort, project, limit, cursor, count, aggregate))

    def pages(self, filter: dict[str, Any] | None = None, *, sort: Sequence[dict[str, Any]] | None = None,
              project: Sequence[str] | None = None, limit: int | None = None) -> Iterator[models.Page]:
        cursor = None
        seen: set[str] = set()
        while True:
            page = self.query(filter, sort=sort, project=project, limit=limit, cursor=cursor)
            if page.next_cursor:
                if page.next_cursor in seen:
                    raise InvalidResponseError(200, "invalid_response", "repeated pagination cursor")
                seen.add(page.next_cursor)
            yield page
            if not page.next_cursor:
                return
            cursor = page.next_cursor

    def items(self, filter: dict[str, Any] | None = None, *, sort: Sequence[dict[str, Any]] | None = None,
              project: Sequence[str] | None = None, limit: int | None = None) -> Iterator[models.Document]:
        for page in self.pages(filter, sort=sort, project=project, limit=limit):
            yield from page.items

    def search(self, path: str, vector: Sequence[float], *, limit: int = 10, metric: str = "cosine",
               filter: dict[str, Any] | None = None) -> builtins.list[models.Document]:
        """Vector search: the ``limit`` (<= 100) nearest documents, each with ``distance``."""
        from .query import near

        return self.query(filter, sort=[near(path, vector, metric)], limit=limit).items

    def get_ttl(self) -> str | None:
        """The TTL path, or None."""
        return self._c._send(_spec.get_ttl(*self._p))

    def set_ttl(self, path: str) -> str | None:
        """Delete documents whose ``path`` holds a past RFC 3339 UTC time
        (an hourly sweep; expired documents stay readable until then)."""
        return self._c._send(_spec.set_ttl(*self._p, path))

    def delete_ttl(self) -> None:
        self._c._send(_spec.delete_ttl(*self._p))
