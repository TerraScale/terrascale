"""Customer JavaScript functions, scoped to an existing database handle."""

from __future__ import annotations

import json
from collections.abc import Callable
from dataclasses import asdict, dataclass
from typing import TYPE_CHECKING, Any, Literal

import httpx

from ._spec import Call, name
from .errors import APIError, CommitUncertainError
from .ids import validate_id

if TYPE_CHECKING:
    from .aio import AsyncDatabase
    from .client import Database


@dataclass(frozen=True)
class FunctionTrigger:
    collection: str
    events: list[Literal["upsert", "delete"]] | None = None


@dataclass(frozen=True)
class FunctionDeployment:
    source: str
    trigger: FunctionTrigger | None = None
    cron: str | None = None


@dataclass(frozen=True)
class Function:
    name: str
    source: str | None
    trigger: FunctionTrigger | None
    cron: str | None
    created_at: str
    updated_at: str

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> Function:
        trigger = b["trigger"]
        parsed = FunctionTrigger(trigger["collection"], trigger["events"]) if trigger is not None else None
        return cls(b["name"], b["source"], parsed,
                   b["cron"], b["createdAt"], b["updatedAt"])


@dataclass(frozen=True)
class FunctionLog:
    id: str
    at: str
    trigger: str
    status: str
    duration_ms: int
    lines: list[str]
    error: str | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> FunctionLog:
        return cls(b["id"], b["at"], b["trigger"], b["status"], int(b["durationMs"]), b["lines"], b["error"])


@dataclass(frozen=True)
class FunctionDelivery:
    id: str
    created_at: str
    status: str
    attempts: int
    last_attempt_at: str | None
    next_attempt_at: str | None
    http_status: int | None
    error_code: str | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> FunctionDelivery:
        return cls(b["id"], b["createdAt"], b["status"], int(b["attempts"]), b["lastAttemptAt"], b["nextAttemptAt"],
                   int(b["httpStatus"]) if b["httpStatus"] is not None else None, b["errorCode"])


class _FunctionCall(Call):
    # The shared object decoder cannot represent direct arrays or arbitrary invocation JSON.
    def decode(self, response: httpx.Response) -> Any:
        return self.parse(response)

    def content(self) -> bytes | None:
        content = super().content()
        if self.method == "POST":
            content = json.dumps(self.body, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode()
            if len(content) > 1024 * 1024:
                raise ValueError("function invocation body exceeds 1 MiB")
        return content


class _Requests:
    def __init__(self, project: str, database: str):
        self._path = ["v1", "projects", validate_id(project, "project"), "databases", name(database, "database"), "functions"]

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
                if method == "GET" and (function is None or suffix in ("logs", "deliveries")):
                    if not isinstance(value, list):
                        raise ValueError("expected an array")
                return parse(value)
            except (ValueError, KeyError, TypeError, AttributeError, OverflowError) as exc:
                error = APIError if method == "GET" else CommitUncertainError
                raise error(r.status_code, "invalid_response",
                            "invalid function response; reconcile mutations before retrying") from exc

        return _FunctionCall(method, [*path, *([suffix] if suffix else [])], decode,
                             body=body, params=params or {}, replay_safe=method == "GET")

    def _deploy(self, function: str, deployment: FunctionDeployment) -> Call:
        if not isinstance(deployment.source, str) or not deployment.source or len(deployment.source.encode()) > 256 * 1024:
            raise ValueError("function source must contain 1 to 256 KiB of JavaScript")
        if deployment.trigger is not None:
            name(deployment.trigger.collection, "collection")
            if deployment.trigger.events is not None and any(e not in ("upsert", "delete") for e in deployment.trigger.events):
                raise ValueError("trigger events must be upsert or delete")
        return self._call("PUT", function, Function.from_json, body=asdict(deployment))

    def _logs(self, function: str, limit: int | None) -> Call:
        if limit is not None and (isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 100):
            raise ValueError("log limit must be an integer from 1 to 100")
        return self._call("GET", function, lambda b: [FunctionLog.from_json(x) for x in b], suffix="logs",
                          params={} if limit is None else {"limit": str(limit)})


class DatabaseFunctions(_Requests):
    def __init__(self, database: Database):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    def list_functions(self) -> list[Function]:
        return self._client._send(self._call("GET", None, lambda b: [Function.from_json(x) for x in b]))

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

    def list_function_deliveries(self, function: str) -> list[FunctionDelivery]:
        return self._client._send(self._call("GET", function, lambda b: [FunctionDelivery.from_json(x) for x in b],
                                            suffix="deliveries"))


class AsyncDatabaseFunctions(_Requests):
    def __init__(self, database: AsyncDatabase):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    async def list_functions(self) -> list[Function]:
        return await self._client._send(self._call("GET", None, lambda b: [Function.from_json(x) for x in b]))

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

    async def list_function_deliveries(self, function: str) -> list[FunctionDelivery]:
        return await self._client._send(self._call("GET", function, lambda b: [FunctionDelivery.from_json(x) for x in b],
                                                  suffix="deliveries"))
