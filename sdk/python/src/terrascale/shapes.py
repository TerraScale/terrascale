"""Live table snapshots shared by synchronous and asyncio clients."""

from __future__ import annotations

import asyncio
import json
import re
from collections.abc import AsyncGenerator, Awaitable, Generator, Mapping, Sequence
from dataclasses import dataclass
from types import MappingProxyType
from typing import TYPE_CHECKING, Any, Literal

import httpx
from websockets.asyncio.client import ClientConnection, connect
from websockets.exceptions import ConnectionClosed, InvalidHandshake, InvalidStatus
from websockets.typing import Subprotocol

from . import _spec
from .errors import APIError, InvalidResponseError, error_for
from .session import _proxy, _refusal
from .sql import Column, Row, _decode

if TYPE_CHECKING:
    from .aio import AsyncTerraScale
    from .client import TerraScale

_PROTOCOL = "terrascale.shape.v1"


@dataclass(frozen=True)
class ShapeView:
    """Immutable rows keyed by decimal row id at an applied commit offset."""

    rows: Mapping[str, Row]
    status: Literal["loading", "live", "reconnecting"]
    offset: str
    provisional: frozenset[str] = frozenset()


def _invalid() -> InvalidResponseError:
    return InvalidResponseError(0, "invalid_response", "invalid shape response")


def _text(value: Any, pattern: str | None = None) -> str:
    if not isinstance(value, str) or pattern is not None and re.fullmatch(pattern, value) is None:
        raise _invalid()
    return value


def _apply(rows: dict[str, Row], ops: Any, touched: set[str] | None = None) -> None:
    if not isinstance(ops, list):
        raise _invalid()
    for op in ops:
        key = _text(op["key"], r"-?\d+")
        if op["op"] == "delete":
            rows.pop(key, None)
            if touched is not None:
                touched.discard(key)
        elif op["op"] in ("insert", "update"):
            row = op["row"]
            columns = [Column(name) for name in row]
            rows[key] = Row(columns, {c.name: i for i, c in enumerate(columns)}, [_decode(v) for v in row.values()])
            if touched is not None:
                touched.add(key)
        else:
            raise _invalid()


class AsyncShape:
    """A filtered SQL table, snapshotted and then followed in commit order."""

    def __init__(self, client: TerraScale | AsyncTerraScale, database: str, table: str, *,
                 where: str | None = None, columns: Sequence[str] | None = None):
        if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,62}", table) is None:
            raise ValueError("table must be an ordinary table name of at most 63 characters")
        self._filter: dict[str, str] = {}
        if where is not None:
            if not where.strip():
                raise ValueError("where must be a non-empty SQL condition")
            self._filter["where"] = where
        if columns is not None:
            if (isinstance(columns, str) or not columns or len(set(columns)) != len(columns)
                    or any(re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", c) is None for c in columns)):
                raise ValueError("columns must be a non-empty sequence of distinct column names")
            self._filter["columns"] = ",".join(columns)
        self._client = client
        self._call = _spec.read_table_shape(client.project, database, table)
        self._hints_call = _spec.connect_shape_hints(client.project, database, table)

    async def _headers(self) -> dict[str, str]:
        bearer = self._client._bearer()
        token = await bearer if isinstance(bearer, Awaitable) else bearer
        return {**self._client._headers, "Authorization": f"Bearer {token}"}

    async def _read(self, query: dict[str, str]) -> httpx.Response:
        result = self._client._http.request("GET", self._call.url(self._client.base_url), params=query,
                                            headers=await self._headers(), follow_redirects=False)
        response = await result if isinstance(result, Awaitable) else result
        if response.status_code not in (200, 307):
            try:
                body = response.json()
            except ValueError:
                body = None
            raise error_for(response.status_code, body, response.headers)
        return response

    async def _open(self, query: dict[str, str]) -> ClientConnection:
        url = httpx.URL(self._hints_call.url(self._client.base_url), params=query)
        address = str(url.copy_with(scheme="wss" if url.scheme == "https" else "ws"))
        try:
            ws = await connect(address, subprotocols=[Subprotocol(_PROTOCOL)], additional_headers=await self._headers(),
                               user_agent_header=None, proxy=_proxy(address), max_size=65536)
        except InvalidStatus as exc:
            raise _refusal(exc) from None
        try:
            if ws.subprotocol != _PROTOCOL or (await asyncio.wait_for(self._frame(ws), 10))["type"] != "ready":
                raise _invalid()
            return ws
        except BaseException:
            await ws.close()
            raise

    async def _frame(self, ws: ClientConnection) -> Any:
        raw = await ws.recv()
        if not isinstance(raw, str):
            raise _invalid()
        frame = json.loads(raw)
        if not isinstance(frame, dict):
            raise _invalid()
        if frame.get("type") == "error":
            code = _text(frame["code"])
            status = (401 if code in ("invalid_key", "key_expired", "key_revoked", "unauthorized") else
                      403 if code in ("forbidden_scope", "not_granted", "column_not_granted", "grant_invalid") else
                      400 if code in ("invalid_request", "unsupported_table") else
                      404 if code in ("unknown_database", "unknown_table") else
                      409 if code == "must_refetch" else 429 if code == "shape_busy" else 503)
            headers = {"retry-after": str(frame["retry_after"])} if type(frame.get("retry_after")) in (int, float) else None
            raise error_for(status, {"error": frame}, headers)
        return frame

    async def _wait(self, ws: ClientConnection, handle: str, offset: str, local: str) -> tuple[bool, str]:
        while True:
            frame = await self._frame(ws)
            if _text(frame["handle"]) != handle:
                raise error_for(409, {"error": {"code": "must_refetch"}})
            if frame["type"] == "new_data":
                if int(_text(frame["next_offset"], r"\d+")) > int(offset):
                    return False, frame["next_offset"]
            elif frame["type"] == "provisional":
                if frame["offset"] == offset and frame["local"] != local:
                    return True, offset
            else:
                raise _invalid()

    async def watch(self) -> AsyncGenerator[ShapeView, None]:
        """Yield loading, live and reconnecting views; close with ``aclose()`` when stopping early."""
        rows: dict[str, Row] = {}
        pending: list[Any] = []
        handle, offset, local = "", "-1", "0"
        cursor: str | None = None
        live = False
        hints: ClientConnection | None = None
        protocol = ""
        hinted_offset = ""
        retry_at = 0.0
        attempt = 0

        def view(status: Literal["loading", "live", "reconnecting"]) -> ShapeView:
            merged = rows.copy()
            touched: set[str] = set()
            for transaction in pending:
                _apply(merged, transaction["ops"], touched)
            return ShapeView(MappingProxyType(merged), status, offset, frozenset(touched))

        yield view("loading")
        try:
            while True:
                try:
                    query = {**self._filter, "offset": offset}
                    if handle:
                        query["handle"] = handle
                    if live and protocol == _PROTOCOL and asyncio.get_running_loop().time() >= retry_at:
                        try:
                            if hints is None:
                                hints = await self._open(query)
                            live, hinted_offset = await self._wait(hints, handle, offset, local)
                        except (APIError, OSError, asyncio.TimeoutError, InvalidHandshake, ConnectionClosed) as exc:
                            if isinstance(exc, APIError) and (exc.status in (400, 401, 403) or exc.code == "must_refetch"):
                                raise
                            if hints is not None:
                                await hints.close()
                                hints = None
                            yield view("reconnecting")
                            pause = _spec.backoff(min(attempt, 8), exc.retry_after if isinstance(exc, APIError) else None)
                            attempt += 1
                            retry_at = asyncio.get_running_loop().time() + max(1.0, pause)
                            await asyncio.sleep(pause)
                    if live and not offset.startswith("snap."):
                        query.update(live="true", local=local)
                        if cursor is not None:
                            query["cursor"] = cursor
                    response = await self._read(query)
                    if response.status_code == 307:
                        target = response.url.join(response.headers["location"])
                        if (handle or target.copy_with(query=None) != response.url.copy_with(query=None)):
                            raise _invalid()
                        handle = _text(target.params["handle"], r"h1-[a-f0-9]{16}")
                        offset = _text(target.params["offset"], r"snap\.\d+\.\d+")
                        continue
                    page = response.json()
                    page_handle = _text(page["handle"], r"h1-[a-f0-9]{16}")
                    if handle and page_handle != handle:
                        raise _invalid()
                    previous_offset, previous_local = offset, local
                    next_rows = rows.copy()
                    for change in page["changes"]:
                        _apply(next_rows, change["ops"])
                        settled = change.get("confirmed", []) + change.get("revert", [])
                        pending = [t for t in pending if t["id"] not in settled]
                    next_offset = _text(page["next_offset"], r"(?:\d+|snap\.\d+\.\d+)")
                    if type(page["up_to_date"]) is not bool:
                        raise _invalid()
                    if "provisional" in page:
                        pending = page["provisional"]
                        local = _text(page["local"], r"(?:0|[a-f0-9]{16})")
                    rows, handle, offset = next_rows, page_handle, next_offset
                    live = page["up_to_date"] or offset == hinted_offset
                    cursor = response.headers.get("tb-shape-cursor", cursor)
                    protocol = response.headers.get("tb-shape-hints", protocol)
                    attempt = 0
                    yield view("live" if live and not offset.startswith("snap.") else "loading")
                    if "live" in query and offset == previous_offset and local == previous_local:
                        await asyncio.sleep(_spec.backoff(2, None))
                except APIError as exc:
                    if hints is not None:
                        await hints.close()
                        hints = None
                    if exc.code == "must_refetch":
                        rows, pending, hinted_offset = {}, [], ""
                        handle, offset, local, cursor, live = "", "-1", "0", None, False
                        yield view("loading")
                        continue
                    if exc.code in ("key_expired", "key_revoked") and self._client._source is not None:
                        self._client._source.reject(exc)
                    if exc.status not in (408, 429, 500, 502, 503, 504):
                        raise
                    yield view("reconnecting")
                    await asyncio.sleep(_spec.backoff(min(attempt, 8), exc.retry_after))
                    attempt += 1
                except (httpx.TransportError, OSError, asyncio.TimeoutError):
                    yield view("reconnecting")
                    await asyncio.sleep(_spec.backoff(min(attempt, 8), None))
                    attempt += 1
                except (ValueError, KeyError, TypeError, AttributeError) as exc:
                    raise _invalid() from exc
        finally:
            if hints is not None:
                await hints.close()


class Shape:
    """Synchronous iteration over the shared asyncio shape subscription."""

    def __init__(self, client: TerraScale, database: str, table: str, *,
                 where: str | None = None, columns: Sequence[str] | None = None):
        self._async = AsyncShape(client, database, table, where=where, columns=columns)

    def watch(self) -> Generator[ShapeView, None, None]:
        """Yield immutable views; close with ``close()`` when stopping early."""
        loop = asyncio.new_event_loop()
        watch = self._async.watch()
        try:
            while True:
                try:
                    yield loop.run_until_complete(watch.__anext__())
                except StopAsyncIteration:
                    return
        finally:
            loop.run_until_complete(watch.aclose())
            loop.run_until_complete(loop.shutdown_asyncgens())
            loop.close()
