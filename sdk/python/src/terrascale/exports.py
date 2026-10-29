"""Bounded NDJSON export streams and customer-bucket export schedules."""

from __future__ import annotations

import json
from collections.abc import AsyncIterator, Iterator
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any

import httpx

from ._spec import Call, name
from .errors import APIError, TerraScaleError, error_for
from .ids import validate_id
from .rules import _policy_call, _policy_failure

if TYPE_CHECKING:
    from .aio import AsyncDatabase
    from .client import Database


def _count(value: Any) -> int:
    if isinstance(value, bool) or not isinstance(value, (str, int)):
        raise ValueError("export counters must be decimal integers")
    result = int(value)
    if not 0 <= result <= 9223372036854775807 or str(result) != str(value):
        raise ValueError("export counter must fit nonnegative int64")
    return result


@dataclass(frozen=True)
class ExportLine:
    collection: str
    id: str
    document: Any = field(repr=False)


@dataclass(frozen=True)
class ExportRun:
    started_at: str
    finished_at: str
    status: str
    key: str | None
    documents: int
    bytes: int
    error: str | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> ExportRun:
        return cls(b["startedAt"], b["finishedAt"], b["status"], b["key"], _count(b["documents"]), _count(b["bytes"]), b["error"])


@dataclass(frozen=True)
class ExportSchedule:
    cron: str | None
    updated_at: str | None
    last_run: ExportRun | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> ExportSchedule:
        run = b["lastRun"]
        return cls(b["cron"], b["updatedAt"], ExportRun.from_json(run) if run is not None else None)


class IncompleteExportError(TerraScaleError):
    def __init__(self, last: ExportLine | None):
        super().__init__("export incomplete; resume explicitly from the last emitted record")
        self.after_collection = last.collection if last else None
        self.after_id = last.id if last else None


class _NDJSON:
    def __init__(self) -> None:
        self.buffer = b""
        self.last: ExportLine | None = None

    def feed(self, chunk: bytes) -> Iterator[ExportLine]:
        self.buffer += chunk
        while b"\n" in self.buffer:
            line, self.buffer = self.buffer.split(b"\n", 1)
            if len(line) > 4194304:
                raise ValueError("export record exceeds 4 MiB")
            b = json.loads(line.decode("utf-8"))
            self.last = ExportLine(name(b["collection"], "collection"), validate_id(b["id"], "document"), b["document"])
            yield self.last
        if len(self.buffer) > 4194304:
            raise ValueError("export record exceeds 4 MiB")

    def finish(self) -> None:
        if self.buffer:
            raise ValueError("export ended without a complete final line")


def _status_error(response: httpx.Response, payload: bytes) -> APIError:
    try:
        body = json.loads(payload)
    except ValueError:
        body = None
    return error_for(response.status_code, body, response.headers)


def _check_stream(response: httpx.Response) -> None:
    if (response.headers.get("content-type", "").split(";", 1)[0].strip().lower() != "application/x-ndjson"
            or response.headers.get("content-encoding", "identity").lower() != "identity"):
        raise ValueError("unexpected export content type or encoding")


class _Requests:
    def __init__(self, project: str, database: str):
        self._path = ["v1", "projects", validate_id(project, "project"), "databases", name(database, "database")]

    def _export(self, collection: str | None, document: str | None) -> Call:
        if (collection is None) != (document is None):
            raise ValueError("after_collection and after_id must be supplied together")
        params = {} if collection is None else {"after_collection": name(collection, "collection"),
                                                "after_id": validate_id(document, "document")}
        return Call("GET", [*self._path, "export"], lambda _: None, params=params, replay_safe=True)

    def _schedule(self, method: str, cron: str | None = None) -> Call:
        if cron is not None and not isinstance(cron, str):
            raise ValueError("cron must be a five-field UTC expression or null")
        return _policy_call(method, [*self._path, "export-schedule"], lambda r: ExportSchedule.from_json(r.json()), 200,
                            body={"cron": cron} if method == "PUT" else None)


class DatabaseExports(_Requests):
    def __init__(self, database: Database):
        super().__init__(*database._p)
        self._client = database._c

    def iter_export(self, *, after_collection: str | None = None, after_id: str | None = None) -> Iterator[ExportLine]:
        call, decoder = self._export(after_collection, after_id), _NDJSON()
        client = self._client
        refreshed = False
        while True:
            token = client._bearer()
            headers = {**client._headers, "Authorization": f"Bearer {token}", "Accept": "application/x-ndjson",
                       "Accept-Encoding": "identity"}
            try:
                with client._http.stream("GET", call.url(client.base_url), params=call.params, headers=headers,
                                         follow_redirects=False) as response:
                    if response.status_code == 401 and client._source is not None and not refreshed:
                        client._source.invalidate(token)
                        refreshed = True
                        continue
                    if response.status_code != 200:
                        raise _status_error(response, next(response.iter_bytes(4096), b""))
                    _check_stream(response)
                    for chunk in response.iter_bytes():
                        yield from decoder.feed(chunk)
                    decoder.finish()
                    return
            except (httpx.TransportError, ValueError, KeyError, TypeError, AttributeError):
                raise IncompleteExportError(decoder.last) from None

    def get_export_schedule(self) -> ExportSchedule:
        return self._client._send(self._schedule("GET"))

    def set_export_schedule(self, cron: str | None) -> ExportSchedule:
        call = self._schedule("PUT", cron)
        try:
            return self._client._send(call)
        except APIError as error:
            raise _policy_failure(call, error) from None


class AsyncDatabaseExports(_Requests):
    def __init__(self, database: AsyncDatabase):
        super().__init__(*database._p)
        self._client = database._c

    async def iter_export(self, *, after_collection: str | None = None, after_id: str | None = None) -> AsyncIterator[ExportLine]:
        call, decoder = self._export(after_collection, after_id), _NDJSON()
        client = self._client
        refreshed = False
        while True:
            token = await client._bearer()
            headers = {**client._headers, "Authorization": f"Bearer {token}", "Accept": "application/x-ndjson",
                       "Accept-Encoding": "identity"}
            try:
                async with client._http.stream("GET", call.url(client.base_url), params=call.params, headers=headers,
                                               follow_redirects=False) as response:
                    if response.status_code == 401 and client._source is not None and not refreshed:
                        client._source.invalidate(token)
                        refreshed = True
                        continue
                    if response.status_code != 200:
                        raise _status_error(response, await anext(response.aiter_bytes(4096), b""))
                    _check_stream(response)
                    async for chunk in response.aiter_bytes():
                        for record in decoder.feed(chunk):
                            yield record
                    decoder.finish()
                    return
            except (httpx.TransportError, ValueError, KeyError, TypeError, AttributeError):
                raise IncompleteExportError(decoder.last) from None

    async def get_export_schedule(self) -> ExportSchedule:
        return await self._client._send(self._schedule("GET"))

    async def set_export_schedule(self, cron: str | None) -> ExportSchedule:
        call = self._schedule("PUT", cron)
        try:
            return await self._client._send(call)
        except APIError as error:
            raise _policy_failure(call, error) from None
