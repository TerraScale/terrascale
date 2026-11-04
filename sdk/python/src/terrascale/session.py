"""SQL WebSocket sessions (``terrascale.sql.v1``): interactive transactions, sync and asyncio.

Outside a transaction every call runs as its own transaction; ``begin()`` opens one
that lasts at most 5 seconds. Nothing on a session is resent. When the socket drops,
calls in flight fail and the next call opens a new socket with a fresh credential; a
transaction open at the drop was rolled back by the server, so every call except
``rollback()`` raises ``transaction_rolled_back`` until ``rollback()`` clears it.
"""

from __future__ import annotations

import asyncio
import builtins
import contextlib
import json
import threading
from collections.abc import Awaitable, Callable, Generator, Sequence
from typing import TYPE_CHECKING, Any, TypeVar, overload
from urllib.parse import quote

from websockets.asyncio.client import ClientConnection as AsyncConnection
from websockets.asyncio.client import connect as aconnect
from websockets.exceptions import InvalidHandshake, InvalidStatus
from websockets.protocol import State
from websockets.sync.client import ClientConnection, connect
from websockets.typing import Subprotocol

from . import sql as _sql
from .errors import (
    APIError,
    CommitUncertainError,
    InvalidResponseError,
    SqlError,
    SqlErrorCodes,
    TerraScaleError,
    error_for,
)
from .sql import Args, Row, Statement, StatementResult

if TYPE_CHECKING:
    from .aio import AsyncDatabase
    from .client import Database

__all__ = ["SUBPROTOCOL", "AsyncSqlSession", "SqlSession"]

SUBPROTOCOL = "terrascale.sql.v1"
MAX_FRAME_BYTES = 1 << 20
T = TypeVar("T")
Statements = Sequence[str | tuple[str, Args] | Statement]


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


def _not_opened(exc: Exception) -> TerraScaleError:
    return TerraScaleError(f"the SQL session could not connect: {type(exc).__name__}: {exc}".rstrip(": "))


def _wrong_protocol() -> InvalidResponseError:
    return InvalidResponseError(0, "invalid_response", f"TerraBase did not select the {SUBPROTOCOL} subprotocol")


def _closed(exc: BaseException, uncertain: bool) -> TerraScaleError:
    """A call in flight when the socket dropped; it is never resent."""
    if isinstance(exc, TerraScaleError):
        return exc
    text = f"the SQL session closed: {type(exc).__name__}: {exc}".rstrip(": ")
    if uncertain:
        return CommitUncertainError(0, "commit_uncertain", text + "; the statements may have committed")
    return TerraScaleError(text)


class _State:
    """Protocol state shared by both sessions: frame ids, the transaction flag and a lost transaction."""

    def __init__(self) -> None:
        self.next_id = 0
        self.transaction = False
        self.lost = False
        self.closed = False

    def local(self, kind: str) -> bool:
        """Answer locally when the session is closed or lost a transaction; True means done."""
        if self.closed:
            raise TerraScaleError("the SQL session is closed")
        if not self.lost:
            return False
        if kind == "rollback":
            self.lost = False
            return True
        raise SqlError(0, SqlErrorCodes.TRANSACTION_ROLLED_BACK,
                       "the SQL session dropped and TerraBase rolled back the open transaction; call rollback()",
                       sqlstate="40000")

    def frame(self, kind: str, statements: list[dict[str, Any]] | None) -> tuple[int, str]:
        self.next_id += 1
        body: dict[str, Any] = {"id": self.next_id, "type": kind}
        if statements is not None:
            body["statements"] = statements
        text = json.dumps(body, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
        if len(text.encode()) > MAX_FRAME_BYTES:
            raise ValueError("a SQL frame is at most 1 MiB")
        return self.next_id, text

    def uncertain(self, kind: str) -> bool:
        return kind == "commit" or (kind == "execute" and not self.transaction)

    def dropped(self) -> None:
        if self.transaction:
            self.lost = True
        self.transaction = False

    def answer(self, raw: str | bytes) -> tuple[int | None, dict[str, Any]]:
        """Decode a server frame and record its transaction flag."""
        try:
            body = json.loads(raw)
            if not isinstance(body, dict) or body.get("type") not in ("result", "error"):
                raise ValueError("unknown SQL frame")
            if type(body.get("transaction")) is not bool:
                raise ValueError("missing transaction flag")
        except ValueError:
            raise InvalidResponseError(0, "invalid_response", "TerraBase sent an invalid SQL frame") from None
        self.transaction = body["transaction"]
        frame_id = body.get("id")
        return (frame_id if type(frame_id) is int else None), body


def _raise_error(body: dict[str, Any]) -> None:
    if body["type"] != "error":
        return
    err = body.get("error")
    if not isinstance(err, dict) or not isinstance(err.get("code"), str):
        raise InvalidResponseError(0, "invalid_response", "TerraBase sent an invalid SQL error frame")
    statement = err.get("statement")
    sqlstate = err.get("sqlstate")
    message = err.get("message")
    raise SqlError(0, err["code"], message if isinstance(message, str) else "",
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

    def __init__(self, db: Database):
        self._db = db
        self._state = _State()
        self._lock = threading.Lock()
        self._ws: ClientConnection | None = self._open()

    def _open(self) -> ClientConnection:
        client = self._db._c
        url = _url(client.base_url, client.project, self._db.name)
        refreshed = False
        while True:
            token = client._bearer()
            try:
                ws = connect(url, subprotocols=[Subprotocol(SUBPROTOCOL)], user_agent_header=None, max_size=None,
                             additional_headers={**client._headers, "Authorization": f"Bearer {token}"})
            except InvalidStatus as exc:
                err = _refusal(exc)
                if _retry_refusal(client._source, token, err, refreshed):
                    refreshed = True
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

    def _call(self, kind: str, statements: list[dict[str, Any]] | None = None) -> dict[str, Any]:
        with self._lock:
            state = self._state
            if self._ws is not None and self._ws.state is not State.OPEN:
                self._drop()
            if state.local(kind):
                return {"results": []}
            frame_id, frame = state.frame(kind, statements)
            uncertain = state.uncertain(kind)
            if self._ws is None:
                self._ws = self._open()
            ws = self._ws
            try:
                ws.send(frame)
                while True:
                    answer_id, body = state.answer(ws.recv())
                    if answer_id == frame_id or answer_id is None and body["type"] == "error":
                        break
            except Exception as exc:
                self._drop()
                raise _closed(exc, uncertain) from exc
            if answer_id is None:
                self._drop()
            _raise_error(body)
            return body

    @property
    def in_transaction(self) -> bool:
        """Whether a transaction is open, as of the last answer."""
        return self._state.transaction

    @overload
    def query(self, sql: str, args: Args = None) -> builtins.list[Row]: ...

    @overload
    def query(self, sql: str, args: Args, mapper: Callable[..., T]) -> builtins.list[T]: ...

    def query(self, sql: str, args: Args = None, mapper: Callable[..., T] | None = None) -> builtins.list[Any]:
        """Run one statement and return its rows, or ``mapper(**row)`` for each row."""
        result = self.execute(sql, args)
        return result.rows if mapper is None else result.as_(mapper)

    def execute(self, sql: str | Statement, args: Args = None) -> StatementResult:
        """Run one statement, in the open transaction or as its own."""
        return self.batch([sql if isinstance(sql, Statement) else Statement(sql, args)])[0]

    def batch(self, statements: Statements) -> builtins.list[StatementResult]:
        """Run 1 to 256 statements, all or nothing outside a transaction; one result per statement."""
        items = _sql.statements(statements)
        uncertain = self._state.uncertain("execute")
        return _results(self._call("execute", items), len(items), uncertain)

    def begin(self) -> None:
        """Open a transaction of at most 5 seconds."""
        self._call("begin")

    def commit(self) -> None:
        """Commit; after a failed statement it rolls back and raises ``transaction_rolled_back``."""
        self._call("commit")

    def rollback(self) -> None:
        self._call("rollback")

    def transaction(self, fn: Callable[[SqlSession], T]) -> T:
        """Begin, run ``fn(session)`` and commit; roll back and re-raise when ``fn`` raises."""
        self.begin()
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
        ws, self._ws = self._ws, None
        if ws is not None:
            ws.close()

    def __enter__(self) -> SqlSession:
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

    def __init__(self, db: AsyncDatabase):
        self._db = db
        self._state = _State()
        self._link: _Link | None = None
        self._opening = asyncio.Lock()

    def __await__(self) -> Generator[Any, None, AsyncSqlSession]:
        return self._start().__await__()

    async def _start(self) -> AsyncSqlSession:
        if self._state.closed:
            raise TerraScaleError("the SQL session is closed")
        await self._connected()
        return self

    async def _connected(self) -> _Link:
        async with self._opening:
            if self._link is None:
                link = _Link(await self._open())
                link.reader = asyncio.create_task(self._read(link))
                self._link = link
            return self._link

    async def _open(self) -> AsyncConnection:
        client = self._db._c
        url = _url(client.base_url, client.project, self._db.name)
        refreshed = False
        while True:
            token = await client._bearer()
            try:
                ws = await aconnect(url, subprotocols=[Subprotocol(SUBPROTOCOL)], user_agent_header=None, max_size=None,
                                    additional_headers={**client._headers, "Authorization": f"Bearer {token}"})
            except InvalidStatus as exc:
                err = _refusal(exc)
                if _retry_refusal(client._source, token, err, refreshed):
                    refreshed = True
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
                answer_id, body = self._state.answer(await link.ws.recv())
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

    def _drop(self, link: _Link, exc: Exception) -> None:
        if self._link is link:
            self._link = None
            self._state.dropped()
        for waiter in link.pending.values():
            if not waiter.done():
                waiter.set_exception(exc)
        link.pending.clear()

    async def _call(self, kind: str, statements: list[dict[str, Any]] | None = None) -> dict[str, Any]:
        state = self._state
        if state.local(kind):
            return {"results": []}
        frame_id, frame = state.frame(kind, statements)
        uncertain = state.uncertain(kind)
        link = await self._connected()
        waiter = asyncio.get_running_loop().create_future()
        link.pending[frame_id] = waiter
        try:
            await link.ws.send(frame)
            body = await waiter
        except Exception as exc:
            if not isinstance(exc, TerraScaleError):
                self._drop(link, exc)
            raise _closed(exc, uncertain) from exc
        finally:
            link.pending.pop(frame_id, None)
        _raise_error(body)
        return body

    @property
    def in_transaction(self) -> bool:
        """Whether a transaction is open, as of the last answer."""
        return self._state.transaction

    @overload
    async def query(self, sql: str, args: Args = None) -> builtins.list[Row]: ...

    @overload
    async def query(self, sql: str, args: Args, mapper: Callable[..., T]) -> builtins.list[T]: ...

    async def query(self, sql: str, args: Args = None, mapper: Callable[..., T] | None = None) -> builtins.list[Any]:
        """Run one statement and return its rows, or ``mapper(**row)`` for each row."""
        result = await self.execute(sql, args)
        return result.rows if mapper is None else result.as_(mapper)

    async def execute(self, sql: str | Statement, args: Args = None) -> StatementResult:
        """Run one statement, in the open transaction or as its own."""
        return (await self.batch([sql if isinstance(sql, Statement) else Statement(sql, args)]))[0]

    async def batch(self, statements: Statements) -> builtins.list[StatementResult]:
        """Run 1 to 256 statements, all or nothing outside a transaction; one result per statement."""
        items = _sql.statements(statements)
        uncertain = self._state.uncertain("execute")
        return _results(await self._call("execute", items), len(items), uncertain)

    async def begin(self) -> None:
        """Open a transaction of at most 5 seconds."""
        await self._call("begin")

    async def commit(self) -> None:
        """Commit; after a failed statement it rolls back and raises ``transaction_rolled_back``."""
        await self._call("commit")

    async def rollback(self) -> None:
        await self._call("rollback")

    async def transaction(self, fn: Callable[[AsyncSqlSession], Awaitable[T]]) -> T:
        """Begin, run ``await fn(session)`` and commit; roll back and re-raise when ``fn`` raises."""
        await self.begin()
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
        self._state.closed = True
        link, self._link = self._link, None
        if link is not None:
            await link.ws.close()
            if link.reader is not None:
                await link.reader

    async def __aenter__(self) -> AsyncSqlSession:
        return await self._start()

    async def __aexit__(self, *exc: object) -> None:
        await self.close()
