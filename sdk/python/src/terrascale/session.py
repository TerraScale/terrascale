"""SQL WebSocket sessions (``terrascale.sql.v2``): interactive transactions, sync and asyncio.

Outside a transaction every call runs as its own transaction; ``begin()`` opens one
that lasts at most 5 seconds. Nothing on a session is resent. When the socket drops,
an unanswered committing request closes the session with ``CommitUncertainError``;
every later call raises that error. Without a committing request, a lost open
transaction raises ``transaction_rolled_back`` until ``rollback()`` clears it.
Every answer carries the session's causal bookmark, which the database handle keeps
so later REST calls and sessions read these writes in any region.
"""

from __future__ import annotations

import asyncio
import builtins
import contextlib
import json
import math
import socket
import threading
import time
from collections.abc import Awaitable, Callable, Generator, Sequence
from typing import TYPE_CHECKING, Any, Literal, TypeVar, overload
from urllib.parse import quote

from websockets.asyncio.client import ClientConnection as AsyncConnection
from websockets.asyncio.client import connect as aconnect
from websockets.exceptions import InvalidHandshake, InvalidStatus
from websockets.protocol import State
from websockets.sync.client import ClientConnection, connect
from websockets.typing import Subprotocol

from . import _spec
from . import sql as _sql
from ._sql_connect_retry import ConnectRetry
from .errors import (
    UNCERTAIN_WRITE_CODES,
    APIError,
    CommitUncertainError,
    InvalidResponseError,
    SqlError,
    SqlErrorCodes,
    TerraScaleError,
    error_for,
    sql_error_class,
)
from .sql import Args, Row, Statement, StatementResult

if TYPE_CHECKING:
    from .aio import AsyncDatabase
    from .client import Database

__all__ = ["SUBPROTOCOL", "AsyncSqlSession", "SqlSession"]

SUBPROTOCOL = "terrascale.sql.v2"
MAX_FRAME_BYTES = 1 << 20
MAX_ANSWER_BYTES = 64 << 20
DEFAULT_FRAME_TIMEOUT = 30.0
T = TypeVar("T")
Statements = Sequence[str | tuple[str, Args] | Statement]


class SqlSessionConnectionError(TerraScaleError):
    pass


class _LostTransactionError(SqlError, SqlSessionConnectionError):
    pass


def _frame_timeout(db: Database | AsyncDatabase) -> float:
    timeout = db._c._http.timeout.read
    return DEFAULT_FRAME_TIMEOUT if timeout is None or not math.isfinite(timeout) else timeout


def _shutdown_socket(ws: ClientConnection) -> None:
    with contextlib.suppress(OSError):
        ws.socket.shutdown(socket.SHUT_RDWR)


def _proxy(url: str) -> Literal[True] | None:
    return None if url.startswith("ws://") else True


def _url(base_url: str, project: str, database: str) -> str:
    path = "/".join(quote(s, safe="") for s in ["v1", "projects", project, "databases", database, "sql", "ws"])
    scheme, rest = base_url.rstrip("/").split("://", 1)
    return ("wss" if scheme == "https" else "ws") + "://" + rest + "/" + path


def _refusal(exc: InvalidStatus) -> APIError:
    """The SDK's HTTP error for a refused upgrade, as REST raises for the same status."""
    response = exc.response
    try:
        body = json.loads(response.body or b"")
    except ValueError:
        body = None
    return error_for(response.status_code, body, response.headers)


def _retry_refusal(source: Any, token: str, err: APIError, refreshed: bool) -> bool:
    """Refresh an ordinary 401 once, like REST; never retry a permanent key refusal."""
    if err.status != 401 or source is None:
        return False
    if err.code in {"key_revoked", "key_expired"}:
        source.reject(err)
        return False
    if refreshed:
        return False
    source.invalidate(token)
    return True


def _not_opened(exc: Exception) -> SqlSessionConnectionError:
    return SqlSessionConnectionError(f"the SQL session could not connect: {type(exc).__name__}: {exc}".rstrip(": "))


def _wrong_protocol() -> InvalidResponseError:
    return InvalidResponseError(0, "invalid_response", f"TerraBase did not select the {SUBPROTOCOL} subprotocol")


def _closed(exc: BaseException) -> TerraScaleError:
    """A call in flight when the socket dropped; it is never resent."""
    if isinstance(exc, TerraScaleError):
        return exc
    if isinstance(exc, asyncio.CancelledError):
        return SqlSessionConnectionError("the SQL session closed when an in-flight call was canceled")
    text = f"the SQL session closed: {type(exc).__name__}: {exc}".rstrip(": ")
    return SqlSessionConnectionError(text)


def _handshake(db: Database | AsyncDatabase, consistency: str | None) -> dict[str, str]:
    headers = _spec.causality(consistency, None)
    known = db.bookmark
    if known:
        headers[_spec.BOOKMARK] = known
    return headers


class _State:
    """Protocol state shared by both sessions: frame ids, the transaction flag, a lost transaction and the bookmark."""

    def __init__(self, observe: Callable[[str], object]) -> None:
        self.observe = observe
        self.next_id = 0
        self.transaction = False
        self.lost = False
        self.closed = False
        self.failure: CommitUncertainError | None = None
        self.pending: dict[int, bool] = {}
        self.controls: set[int] = set()

    def check_open(self) -> None:
        if self.failure is not None:
            raise self.failure
        if self.closed:
            raise SqlSessionConnectionError("the SQL session is closed")

    def local(self, kind: str) -> bool:
        self.check_open()
        if not self.lost:
            return False
        if kind == "rollback":
            self.lost = False
            return True
        raise _LostTransactionError(
            0, SqlErrorCodes.TRANSACTION_ROLLED_BACK,
            "the SQL session dropped and TerraBase rolled back the open transaction; call rollback()", sqlstate="40000")

    def frame(self, kind: str, statements: list[dict[str, Any]] | None, consistency: str | None = None,
              bookmark: str | None = None) -> tuple[int, str]:
        mode = _spec.consistency(consistency)
        checked = _spec.bookmark(bookmark) if bookmark is not None else None
        self.next_id += 1
        body: dict[str, Any] = {"id": self.next_id, "type": kind}
        if statements is not None:
            body["statements"] = statements
        if mode is not None:
            body["consistency"] = mode
        if checked is not None:
            body["bookmark"] = checked
        text = json.dumps(body, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
        if len(text.encode()) > MAX_FRAME_BYTES:
            raise ValueError("a SQL frame is at most 1 MiB")
        return self.next_id, text

    def uncertain(self, kind: str) -> bool:
        return kind == "commit" or (kind == "execute" and (not self.transaction or bool(self.controls)))

    def sent(self, frame_id: int, kind: str) -> None:
        self.pending[frame_id] = self.uncertain(kind)
        if kind in {"begin", "commit", "rollback"}:
            self.controls.add(frame_id)

    def dropped(self) -> None:
        if any(self.pending.values()) and self.failure is None:
            self.failure = CommitUncertainError(
                0, "commit_uncertain", "the SQL session lost a committing answer; reconcile before repeating")
        if self.transaction and self.failure is None:
            self.lost = True
        self.transaction = False
        self.pending.clear()
        self.controls.clear()

    def answer(self, raw: str | bytes) -> tuple[int | None, dict[str, Any]]:
        """Decode a server frame and record its transaction flag."""
        try:
            body = json.loads(raw)
            if not isinstance(body, dict) or body.get("type") not in ("result", "error"):
                raise ValueError("unknown SQL frame")
            if type(body.get("transaction")) is not bool:
                raise ValueError("missing transaction flag")
            error = body.get("error")
            if body["type"] == "error":
                if not isinstance(error, dict) or not isinstance(error.get("code"), str):
                    raise ValueError("invalid SQL error")
                if error["code"] in UNCERTAIN_WRITE_CODES:
                    self.failure = CommitUncertainError(0, error["code"], "the server reported an uncertain write outcome")
            bookmark = body.get("bookmark")
            if bookmark is not None and not isinstance(bookmark, str):
                raise ValueError("invalid bookmark")
            if bookmark:
                self.observe(bookmark)
        except ValueError:
            raise InvalidResponseError(0, "invalid_response", "TerraBase sent an invalid SQL frame") from None
        raw_id = body.get("id")
        frame_id = raw_id if type(raw_id) is int and raw_id > 0 else None
        if frame_id is not None:
            self.pending.pop(frame_id, None)
            self.controls.discard(frame_id)
        self.transaction = body["transaction"]
        return frame_id, body


def _raise_error(body: dict[str, Any]) -> None:
    if body["type"] != "error":
        return
    err = body.get("error")
    if not isinstance(err, dict) or not isinstance(err.get("code"), str):
        raise InvalidResponseError(0, "invalid_response", "TerraBase sent an invalid SQL error frame")
    statement = err.get("statement")
    sqlstate = err.get("sqlState")
    message = err.get("message")
    raise sql_error_class(err["code"])(0, err["code"], message if isinstance(message, str) else "",
                                       statement=statement if type(statement) is int else None,
                                       sqlstate=sqlstate if isinstance(sqlstate, str) and sqlstate else None)


def _results(body: dict[str, Any], expected: int, uncertain: bool) -> list[StatementResult]:
    try:
        return _sql.results(body, expected)
    except (ValueError, KeyError, TypeError, AttributeError):
        cls = CommitUncertainError if uncertain else InvalidResponseError
        raise cls(0, "invalid_response", "invalid SQL answer; reconcile writes before retrying") from None


class SqlSession:
    """A SQL WebSocket session from :meth:`Database.connect`; close it, or use it as a context manager.

    Calls from several threads are sent one at a time."""

    def __init__(self, db: Database, *, consistency: _spec.Consistency | None = None):
        self._db = db
        self._consistency = _spec.consistency(consistency)
        self._state = _State(lambda value: db._c._bookmarks.observe(db.name, value))
        self._lock = threading.Lock()
        self._ws: ClientConnection | None = self._open()

    def _open(self) -> ClientConnection:
        self._state.check_open()
        client = self._db._c
        url = _url(client.base_url, client.project, self._db.name)
        refreshed = False
        retry = ConnectRetry(client._http.timeout.connect)
        while True:
            token = client._bearer()
            try:
                ws = connect(url, subprotocols=[Subprotocol(SUBPROTOCOL)], user_agent_header=None, max_size=MAX_ANSWER_BYTES,
                             additional_headers={**client._headers, **_handshake(self._db, self._consistency),
                                                 "Authorization": f"Bearer {token}"},
                             proxy=_proxy(url), open_timeout=retry.remaining())
            except InvalidStatus as exc:
                err = _refusal(exc)
                if _retry_refusal(client._source, token, err, refreshed):
                    refreshed = True
                    continue
                delay = retry.delay(err, exc.response.headers.get("Retry-After"))
                if delay is not None:
                    retry.wait(delay)
                    continue
                raise err from None
            except (OSError, InvalidHandshake) as exc:
                raise _not_opened(exc) from exc
            if ws.subprotocol != SUBPROTOCOL:
                ws.close()
                raise _wrong_protocol()
            return ws

    def _drop(self) -> None:
        ws, self._ws = self._ws, None
        self._state.dropped()
        if ws is not None:
            with contextlib.suppress(Exception):
                ws.close()

    def _call(self, kind: str, statements: list[dict[str, Any]] | None = None, consistency: str | None = None,
              bookmark: str | None = None) -> dict[str, Any]:
        with self._lock:
            state = self._state
            if self._ws is not None and self._ws.state is not State.OPEN:
                self._drop()
            if state.local(kind):
                return {"results": []}
            frame_id, frame = state.frame(kind, statements, consistency, bookmark)
            if self._ws is None:
                self._ws = self._open()
            ws = self._ws
            timeout = _frame_timeout(self._db)
            deadline = time.monotonic() + timeout
            state.sent(frame_id, kind)
            try:
                send_timeout = threading.Timer(timeout, _shutdown_socket, (ws,))
                send_timeout.daemon = True
                send_timeout.start()
                try:
                    ws.send(frame)
                finally:
                    send_timeout.cancel()
                while True:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise TimeoutError("timed out waiting for a SQL answer")
                    answer_id, body = state.answer(ws.recv(timeout=remaining))
                    if answer_id == frame_id or answer_id is None and body["type"] == "error":
                        break
            except BaseException as exc:
                if isinstance(exc, TimeoutError):
                    _shutdown_socket(ws)
                self._drop()
                if state.failure is not None:
                    raise state.failure from exc
                if isinstance(exc, TerraScaleError) or not isinstance(exc, Exception):
                    raise
                raise _closed(exc) from exc
            if answer_id is None or state.failure is not None:
                self._drop()
            if state.failure is not None:
                raise state.failure
            _raise_error(body)
            return body

    @property
    def in_transaction(self) -> bool:
        """Whether a transaction is open, as of the last answer."""
        return self._state.transaction

    @property
    def _needs_rollback(self) -> bool:
        return self._state.transaction or self._state.lost

    @property
    def bookmark(self) -> str | None:
        """The newest bookmark of this session's database, shared with its handle."""
        return self._db.bookmark

    @overload
    def query(self, sql: str, args: Args = None, *, consistency: _spec.Consistency | None = None,
              bookmark: str | None = None) -> builtins.list[Row]: ...

    @overload
    def query(self, sql: str, args: Args, mapper: Callable[..., T], *, consistency: _spec.Consistency | None = None,
              bookmark: str | None = None) -> builtins.list[T]: ...

    def query(self, sql: str, args: Args = None, mapper: Callable[..., T] | None = None, *,
              consistency: _spec.Consistency | None = None, bookmark: str | None = None) -> builtins.list[Any]:
        """Run one statement and return its rows, or ``mapper(**row)`` for each row."""
        result = self.execute(sql, args, consistency=consistency, bookmark=bookmark)
        return result.rows if mapper is None else result.as_(mapper)

    def execute(self, sql: str | Statement, args: Args = None, *, consistency: _spec.Consistency | None = None,
                bookmark: str | None = None) -> StatementResult:
        """Run one statement, in the open transaction or as its own."""
        return self.batch([_sql.statement(sql, args)], consistency=consistency, bookmark=bookmark)[0]

    def batch(self, statements: Statements, *, consistency: _spec.Consistency | None = None,
              bookmark: str | None = None) -> builtins.list[StatementResult]:
        """Run 1 to 256 statements, all or nothing outside a transaction; one result per statement.
        ``consistency`` is fast or strong for this frame; ``bookmark`` is merged into the session's bookmark first."""
        items = _sql.statements(statements)
        uncertain = self._state.uncertain("execute")
        return _results(self._call("execute", items, consistency, bookmark), len(items), uncertain)

    def begin(self, *, consistency: _spec.Consistency | None = None) -> None:
        """Open a transaction of at most 5 seconds; strong commits it through a region quorum."""
        self._call("begin", None, consistency)

    def commit(self) -> None:
        """Commit; after a failed statement it rolls back and raises ``transaction_rolled_back``."""
        self._call("commit")

    def rollback(self) -> None:
        self._call("rollback")

    def transaction(self, fn: Callable[[SqlSession], T], *, consistency: _spec.Consistency | None = None) -> T:
        """Begin, run ``fn(session)`` and commit; roll back and re-raise when ``fn`` raises."""
        self.begin(consistency=consistency)
        try:
            result = fn(self)
        except BaseException:
            if self._state.transaction or self._state.lost:
                with contextlib.suppress(Exception):
                    self.rollback()
            raise
        self.commit()
        return result

    def close(self) -> None:
        """Close normally; the server rolls back an open transaction. The session stays closed."""
        self._state.closed = True
        self._state.dropped()
        ws, self._ws = self._ws, None
        if ws is not None:
            ws.close()

    def __enter__(self) -> SqlSession:
        self._state.check_open()
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()


class _Link:
    """One socket of an async session with its own waiting calls."""

    def __init__(self, ws: AsyncConnection):
        self.ws = ws
        self.pending: dict[int, asyncio.Future[dict[str, Any]]] = {}
        self.reader: asyncio.Task[None] | None = None


class AsyncSqlSession:
    """The asyncio twin of :class:`SqlSession`, from :meth:`AsyncDatabase.connect`:
    ``session = await db.connect()`` or ``async with db.connect() as session``.
    Calls may overlap; answers are matched by frame id."""

    def __init__(self, db: AsyncDatabase, *, consistency: _spec.Consistency | None = None):
        self._db = db
        self._consistency = _spec.consistency(consistency)
        self._state = _State(lambda value: db._c._bookmarks.observe(db.name, value))
        self._link: _Link | None = None
        self._opening = asyncio.Lock()

    def __await__(self) -> Generator[Any, None, AsyncSqlSession]:
        return self._start().__await__()

    async def _start(self) -> AsyncSqlSession:
        self._state.check_open()
        await self._connected()
        return self

    async def _connected(self) -> _Link:
        async with self._opening:
            self._state.check_open()
            if self._link is None:
                link = _Link(await self._open())
                link.reader = asyncio.create_task(self._read(link))
                self._link = link
            return self._link

    async def _open(self) -> AsyncConnection:
        self._state.check_open()
        client = self._db._c
        url = _url(client.base_url, client.project, self._db.name)
        refreshed = False
        retry = ConnectRetry(client._http.timeout.connect)
        while True:
            token = await client._bearer()
            try:
                ws = await aconnect(url, subprotocols=[Subprotocol(SUBPROTOCOL)], user_agent_header=None,
                                    max_size=MAX_ANSWER_BYTES,
                                    additional_headers={**client._headers, **_handshake(self._db, self._consistency),
                                                        "Authorization": f"Bearer {token}"},
                                    proxy=_proxy(url), open_timeout=retry.remaining())
            except InvalidStatus as exc:
                err = _refusal(exc)
                if _retry_refusal(client._source, token, err, refreshed):
                    refreshed = True
                    continue
                delay = retry.delay(err, exc.response.headers.get("Retry-After"))
                if delay is not None:
                    await asyncio.sleep(delay)
                    continue
                raise err from None
            except (OSError, InvalidHandshake, asyncio.TimeoutError) as exc:
                raise _not_opened(exc) from exc
            if ws.subprotocol != SUBPROTOCOL:
                await ws.close()
                raise _wrong_protocol()
            return ws

    async def _read(self, link: _Link) -> None:
        try:
            while True:
                raw = await link.ws.recv()
                if self._link is not link:
                    return
                answer_id, body = self._state.answer(raw)
                if self._state.failure is not None:
                    raise self._state.failure
                waiter = link.pending.pop(answer_id, None) if answer_id is not None else None
                if waiter is not None:
                    if not waiter.done():
                        waiter.set_result(body)
                elif body["type"] == "error":
                    _raise_error(body)
        except Exception as exc:
            self._drop(link, exc)
            with contextlib.suppress(Exception):
                await link.ws.close()

    def _drop(self, link: _Link, exc: BaseException) -> TerraScaleError:
        if self._link is link:
            self._link = None
            self._state.dropped()
        error = self._state.failure or _closed(exc)
        for waiter in link.pending.values():
            if not waiter.done():
                waiter.set_exception(error)
        link.pending.clear()
        return error

    async def _call(self, kind: str, statements: list[dict[str, Any]] | None = None, consistency: str | None = None,
                    bookmark: str | None = None) -> dict[str, Any]:
        state = self._state
        if self._link is not None and self._link.ws.state is not State.OPEN:
            self._drop(self._link, SqlSessionConnectionError("the SQL session closed"))
        if state.local(kind):
            return {"results": []}
        frame_id, frame = state.frame(kind, statements, consistency, bookmark)
        link = await self._connected()
        if state.local(kind):
            return {"results": []}
        waiter = asyncio.get_running_loop().create_future()
        link.pending[frame_id] = waiter

        async def exchange() -> dict[str, Any]:
            if self._link is link:
                state.sent(frame_id, kind)
                await link.ws.send(frame)
            return await waiter

        try:
            body = await asyncio.wait_for(exchange(), _frame_timeout(self._db))
        except (Exception, asyncio.CancelledError) as exc:
            link.pending.pop(frame_id, None)
            if waiter.done() and not waiter.cancelled():
                waiter.exception()
            error = self._drop(link, exc)
            link.ws.transport.abort()
            if error is exc:
                raise
            if not isinstance(exc, asyncio.CancelledError):
                raise error from exc
            if not isinstance(error, CommitUncertainError):
                raise
        else:
            _raise_error(body)
            return body
        raise error

    @property
    def in_transaction(self) -> bool:
        """Whether a transaction is open, as of the last answer."""
        return self._state.transaction

    @property
    def bookmark(self) -> str | None:
        """The newest bookmark of this session's database, shared with its handle."""
        return self._db.bookmark

    @overload
    async def query(self, sql: str, args: Args = None, *, consistency: _spec.Consistency | None = None,
              bookmark: str | None = None) -> builtins.list[Row]: ...

    @overload
    async def query(self, sql: str, args: Args, mapper: Callable[..., T], *, consistency: _spec.Consistency | None = None,
              bookmark: str | None = None) -> builtins.list[T]: ...

    async def query(self, sql: str, args: Args = None, mapper: Callable[..., T] | None = None, *,
              consistency: _spec.Consistency | None = None, bookmark: str | None = None) -> builtins.list[Any]:
        """Run one statement and return its rows, or ``mapper(**row)`` for each row."""
        result = await self.execute(sql, args, consistency=consistency, bookmark=bookmark)
        return result.rows if mapper is None else result.as_(mapper)

    async def execute(self, sql: str | Statement, args: Args = None, *, consistency: _spec.Consistency | None = None,
                bookmark: str | None = None) -> StatementResult:
        """Run one statement, in the open transaction or as its own."""
        return (await self.batch([_sql.statement(sql, args)], consistency=consistency, bookmark=bookmark))[0]

    async def batch(self, statements: Statements, *, consistency: _spec.Consistency | None = None,
              bookmark: str | None = None) -> builtins.list[StatementResult]:
        """Run 1 to 256 statements, all or nothing outside a transaction; one result per statement.
        ``consistency`` is fast or strong for this frame; ``bookmark`` is merged into the session's bookmark first."""
        items = _sql.statements(statements)
        uncertain = self._state.uncertain("execute")
        return _results(await self._call("execute", items, consistency, bookmark), len(items), uncertain)

    async def begin(self, *, consistency: _spec.Consistency | None = None) -> None:
        """Open a transaction of at most 5 seconds; strong commits it through a region quorum."""
        await self._call("begin", None, consistency)

    async def commit(self) -> None:
        """Commit; after a failed statement it rolls back and raises ``transaction_rolled_back``."""
        await self._call("commit")

    async def rollback(self) -> None:
        await self._call("rollback")

    async def transaction(self, fn: Callable[[AsyncSqlSession], Awaitable[T]], *,
                          consistency: _spec.Consistency | None = None) -> T:
        """Begin, run ``await fn(session)`` and commit; roll back and re-raise when ``fn`` raises."""
        await self.begin(consistency=consistency)
        try:
            result = await fn(self)
        except BaseException:
            if self._state.transaction or self._state.lost:
                with contextlib.suppress(Exception):
                    await self.rollback()
            raise
        await self.commit()
        return result

    async def close(self) -> None:
        """Close normally; the server rolls back an open transaction. The session stays closed."""
        async with self._opening:
            self._state.closed = True
            link = self._link
            if link is not None:
                self._drop(link, SqlSessionConnectionError("the SQL session is closed"))
        if link is not None:
            await link.ws.close()
            if link.reader is not None:
                await link.reader

    async def __aenter__(self) -> AsyncSqlSession:
        return await self._start()

    async def __aexit__(self, *exc: object) -> None:
        await self.close()
