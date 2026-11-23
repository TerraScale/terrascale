"""Customer JavaScript functions, scoped to an existing database handle."""

from __future__ import annotations

import json
from collections.abc import Callable
from dataclasses import dataclass
from typing import TYPE_CHECKING, Any

import httpx

from ._spec import Call, name
from .errors import APIError, CommitUncertainError
from .ids import validate_id

if TYPE_CHECKING:
    from .aio import AsyncDatabase
    from .client import Database


@dataclass(frozen=True)
class FunctionDeployment:
    source: str
    cron: str | None = None


@dataclass(frozen=True)
class Function:
    name: str
    source: str | None
    cron: str | None
    created_at: str
    updated_at: str

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> Function:
        updated = b.get("updatedAtUtc", b.get("updatedAt"))
        created = b.get("createdAt", updated)
        if not isinstance(b.get("name"), str) or not isinstance(created, str) or not isinstance(updated, str):
            raise ValueError("invalid function")
        return cls(b["name"], b.get("source"), b.get("schedule", b.get("cron")), created, updated)


@dataclass(frozen=True)
class FunctionLog:
    id: str
    at: str
    trigger: str
    status: str
    duration_ms: int
    lines: list[str]
    error: str | None
    sql_calls: int = 0
    sql_ms: int = 0
    rows_read: int = 0
    rows_written: int = 0
    response_bytes: int = 0
    http_status: int | None = None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> FunctionLog:
        http_status = b.get("httpStatus")
        return cls(b["id"], b["at"], b["trigger"], b["status"], int(b["durationMs"]), b["lines"], b.get("error"),
                   int(b.get("sqlCalls", 0)), int(b.get("sqlMs", 0)), int(b.get("rowsRead", 0)), int(b.get("rowsWritten", 0)),
                   int(b.get("responseBytes", 0)), int(http_status) if http_status is not None else None)


@dataclass(frozen=True)
class FunctionStatsBucket:
    at: str
    invocations: int
    errors: int
    p50_ms: int
    p95_ms: int

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> FunctionStatsBucket:
        return cls(b["at"], int(b["invocations"]), int(b["errors"]), int(b["p50Ms"]), int(b["p95Ms"]))


@dataclass(frozen=True)
class FunctionStats:
    window: str
    invocations: int
    errors: int
    p50_ms: int
    p95_ms: int
    p99_ms: int
    max_ms: int
    sql_calls: int
    rows_read: int
    rows_written: int
    buckets: list[FunctionStatsBucket]

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> FunctionStats:
        return cls(b["window"], int(b["invocations"]), int(b["errors"]), int(b["p50Ms"]), int(b["p95Ms"]),
                   int(b["p99Ms"]), int(b["maxMs"]), int(b["sqlCalls"]), int(b["rowsRead"]), int(b["rowsWritten"]),
                   [FunctionStatsBucket.from_json(x) for x in b["buckets"]])


class _FunctionCall(Call):
    def decode(self, response: httpx.Response) -> Any:
        return self.parse(response)

    def content(self) -> bytes | None:
        content = super().content()
        if self.method == "POST":
            content = json.dumps(self.body, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode()
            if len(content) > 1024 * 1024:
                raise ValueError("function invocation body exceeds 1 MiB")
        return content


def _function_list(body: Any) -> list[Function]:
    return [Function.from_json(item) for item in body]


class _Requests:
    def __init__(self, project: str, database: str):
        self._database = name(database, "database")
        self._path = ["v1", "projects", validate_id(project, "project"), "databases", self._database, "functions"]

    def _call(self, method: str, function: str | None, parse: Callable[[Any], Any], *, suffix: str | None = None,
              body: Any = None, params: dict[str, str] | None = None) -> Call:
        path = self._path if function is None else [*self._path, name(function, "function")]

        def decode(r: httpx.Response) -> Any:
            try:
                expected = 204 if method == "DELETE" else 200
                if not 200 <= r.status_code < 300 or (suffix != "invoke" and r.status_code != expected):
                    raise ValueError("unexpected success status")
                if method == "DELETE":
                    if r.content:
                        raise ValueError("unexpected delete body")
                    return None
                value = r.text if suffix == "invoke" and r.headers.get("content-type", "").startswith("text/") else (
                    r.json() if r.content else None)
                if method == "GET" and function is None and isinstance(value, dict):
                    value = value.get("functions")
                if method == "GET" and (function is None or suffix == "logs") and not isinstance(value, list):
                    raise ValueError("expected an array")
                return parse(value)
            except (ValueError, KeyError, TypeError, AttributeError, OverflowError) as exc:
                error = APIError if method == "GET" else CommitUncertainError
                raise error(r.status_code, "invalid_response",
                            "invalid function response; reconcile mutations before retrying") from exc

        return _FunctionCall(method, [*path, *([suffix] if suffix else [])], decode,
                             body=body, params=params or {}, replay_safe=method == "GET",
                             causal=self._database if suffix == "invoke" else None)

    def _deploy(self, function: str, deployment: FunctionDeployment) -> Call:
        if not isinstance(deployment.source, str) or not deployment.source or len(deployment.source.encode()) > 256 * 1024:
            raise ValueError("function source must contain 1 to 256 KiB of JavaScript")
        body: dict[str, Any] = {"source": deployment.source}
        if deployment.cron is not None:
            body["schedule"] = deployment.cron
        return self._call("PUT", function, Function.from_json, body=body)

    def _logs(self, function: str, limit: int | None) -> Call:
        if limit is not None and (isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 100):
            raise ValueError("log limit must be an integer from 1 to 100")
        return self._call("GET", function, lambda b: [FunctionLog.from_json(x) for x in b], suffix="logs",
                          params={} if limit is None else {"limit": str(limit)})

    def _stats(self, function: str, window: str | None) -> Call:
        if window is not None and window not in ("1h", "24h", "7d"):
            raise ValueError("stats window must be 1h, 24h or 7d")
        return self._call("GET", function, FunctionStats.from_json, suffix="stats",
                          params={} if window is None else {"window": window})


class DatabaseFunctions(_Requests):
    def __init__(self, database: Database):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    def list_functions(self) -> list[Function]:
        return self._client._send(self._call("GET", None, _function_list))

    def get_function(self, function: str) -> Function:
        return self._client._send(self._call("GET", function, Function.from_json))

    def deploy_function(self, function: str, deployment: FunctionDeployment) -> Function:
        return self._client._send(self._deploy(function, deployment))

    def delete_function(self, function: str) -> None:
        self._client._send(self._call("DELETE", function, lambda _: None))

    def invoke_function(self, function: str, body: Any, *, query: dict[str, str] | None = None) -> Any:
        return self._client._send(self._call("POST", function, lambda b: b, suffix="invoke", body=body, params=query))

    def list_function_logs(self, function: str, *, limit: int | None = None) -> list[FunctionLog]:
        return self._client._send(self._logs(function, limit))

    def get_function_stats(self, function: str, *, window: str | None = None) -> FunctionStats:
        return self._client._send(self._stats(function, window))


class AsyncDatabaseFunctions(_Requests):
    def __init__(self, database: AsyncDatabase):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    async def list_functions(self) -> list[Function]:
        return await self._client._send(self._call("GET", None, _function_list))

    async def get_function(self, function: str) -> Function:
        return await self._client._send(self._call("GET", function, Function.from_json))

    async def deploy_function(self, function: str, deployment: FunctionDeployment) -> Function:
        return await self._client._send(self._deploy(function, deployment))

    async def delete_function(self, function: str) -> None:
        await self._client._send(self._call("DELETE", function, lambda _: None))

    async def invoke_function(self, function: str, body: Any, *, query: dict[str, str] | None = None) -> Any:
        return await self._client._send(self._call("POST", function, lambda b: b, suffix="invoke", body=body, params=query))

    async def list_function_logs(self, function: str, *, limit: int | None = None) -> list[FunctionLog]:
        return await self._client._send(self._logs(function, limit))

    async def get_function_stats(self, function: str, *, window: str | None = None) -> FunctionStats:
        return await self._client._send(self._stats(function, window))
