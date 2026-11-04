"""Retry rules through both client surfaces: token exchange, documented 503
codes, gateway answers and failures that never reached the server."""

from __future__ import annotations

import inspect
from collections.abc import Callable
from typing import Any

import httpx
import pytest
from fake import API_KEY, BASE, ISSUER, PROJECT

from terrascale import (
    AsyncTerraScale,
    CommitUncertainError,
    TerraScale,
)

TOKEN = {"access_token": "fixture-token", "token_type": "Bearer", "expires_in": 3600}
DATABASE = {"id": PROJECT, "projectId": PROJECT, "name": "main", "region": "gru", "homeRegion": "gru",
            "branchedFrom": None, "createdAt": "2026-10-27T00:00:00Z"}
RESULTS = {"results": [{"columns": [], "rows": [], "rows_affected": 1, "last_insert_rowid": None}]}


@pytest.fixture(params=[False, True], ids=["sync", "async"])
def asynchronous(request: pytest.FixtureRequest) -> bool:
    return bool(request.param)


@pytest.fixture(autouse=True)
def delays(monkeypatch: pytest.MonkeyPatch) -> list[tuple[int, float | None]]:
    seen: list[tuple[int, float | None]] = []

    def backoff(attempt: int, retry_after: float | None) -> float:
        seen.append((attempt, retry_after))
        return 0

    monkeypatch.setattr("terrascale._spec.backoff", backoff)
    return seen


Step = httpx.Response | Exception


async def _run(asynchronous: bool, steps: list[Step], action: Callable[[Any], Any], *,
               exchange: bool = False) -> tuple[Any, list[httpx.Request]]:
    calls: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        calls.append(request)
        step = steps.pop(0)
        if isinstance(step, Exception):
            raise step
        return step

    options: dict[str, Any] = {"api_key": API_KEY} if exchange else {"token": "fixture-token"}
    if asynchronous:
        async with httpx.AsyncClient(transport=httpx.MockTransport(handler)) as http:
            async with AsyncTerraScale(**options, project=PROJECT, base_url=BASE, issuer=ISSUER, http_client=http) as ts:
                result = action(ts)
                return (await result if inspect.isawaitable(result) else result), calls
    with httpx.Client(transport=httpx.MockTransport(handler)) as http:
        with TerraScale(**options, project=PROJECT, base_url=BASE, issuer=ISSUER, http_client=http) as ts:
            return action(ts), calls


def _info(ts: Any) -> Any:
    return ts.database("main").info()


@pytest.mark.parametrize("status,code", [(503, "storage_busy"), (503, "unavailable"), (502, None), (504, None)])
async def test_reads_retry_documented_transient_answers(asynchronous: bool, status: int, code: str | None) -> None:
    failure = httpx.Response(status, json={"error": {"code": code}}) if code else httpx.Response(status, content=b"<html>")
    result, calls = await _run(asynchronous, [failure, httpx.Response(200, json=DATABASE)], _info)
    assert result.name == "main" and len(calls) == 2


@pytest.mark.parametrize("failure", [httpx.ReadError("reset"), httpx.WriteError("broken pipe"), httpx.ReadTimeout("slow")])
async def test_writes_never_replay_after_sending(asynchronous: bool, failure: Exception) -> None:
    steps: list[Step] = [failure, httpx.Response(200, json=RESULTS)]
    with pytest.raises(CommitUncertainError):
        await _run(asynchronous, steps, lambda ts: ts.database("main").execute("INSERT INTO t VALUES (1)"))
    assert len(steps) == 1


