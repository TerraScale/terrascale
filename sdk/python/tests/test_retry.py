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
    ServiceUnavailableError,
    TerraScale,
    TerraScaleError,
    TokenError,
)

TOKEN = {"access_token": "fixture-token", "token_type": "Bearer", "expires_in": 3600}
DATABASE = {"id": PROJECT, "projectId": PROJECT, "name": "main", "region": "gru", "homeRegion": "gru",
            "replicaRegions": ["fra"], "allRegions": False, "branchedFrom": None,
            "createdAt": "2026-10-27T00:00:00Z"}
ACK = {"id": PROJECT, "version": "v1", "offset": "1", "durability": "local"}


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


@pytest.mark.parametrize("status", [429, 500, 502, 503, 504])
async def test_token_exchange_retries_transient_answers(asynchronous: bool, delays: list[Any], status: int) -> None:
    steps: list[Step] = [httpx.Response(status, json={"error": "temporarily_unavailable"}, headers={"Retry-After": "3"}),
                         httpx.Response(200, json=TOKEN), httpx.Response(200, json=DATABASE)]
    result, calls = await _run(asynchronous, steps, _info, exchange=True)
    assert result.name == "main"
    assert [c.url.host for c in calls] == ["auth.test", "auth.test", "api.test"]
    assert delays == [(1, 3.0)]


async def test_token_exchange_retries_transport_failures(asynchronous: bool) -> None:
    steps: list[Step] = [httpx.ConnectError("refused"), httpx.ReadTimeout("slow auth"),
                         httpx.Response(200, json=TOKEN), httpx.Response(200, json=DATABASE)]
    result, calls = await _run(asynchronous, steps, _info, exchange=True)
    assert result.name == "main" and len(calls) == 4


async def test_token_exchange_stops_after_max_attempts(asynchronous: bool) -> None:
    steps: list[Step] = [httpx.ConnectError("refused") for _ in range(4)]
    with pytest.raises(TokenError) as caught:
        await _run(asynchronous, steps, _info, exchange=True)
    assert len(steps) == 0
    assert caught.value.code == "token_transport_error"
    assert "after 4 attempts (ConnectError: refused)" in str(caught.value)
    assert API_KEY not in str(caught.value) and API_KEY[27:] not in str(caught.value)


@pytest.mark.parametrize("status,body", [(401, {"error": "invalid_client"}), (400, {"error": "invalid_scope"})])
async def test_token_exchange_never_retries_refusals(asynchronous: bool, status: int, body: dict[str, str]) -> None:
    steps: list[Step] = [httpx.Response(status, json=body)]
    with pytest.raises(TokenError) as caught:
        await _run(asynchronous, steps, _info, exchange=True)
    assert caught.value.status == status and len(steps) == 0


@pytest.mark.parametrize("status,code", [(503, "storage_busy"), (503, "unavailable"), (502, None), (504, None)])
async def test_reads_retry_documented_transient_answers(asynchronous: bool, status: int, code: str | None) -> None:
    failure = httpx.Response(status, json={"error": {"code": code}}) if code else httpx.Response(status, content=b"<html>")
    result, calls = await _run(asynchronous, [failure, httpx.Response(200, json=DATABASE)], _info)
    assert result.name == "main" and len(calls) == 2


async def test_unknown_503_codes_are_not_retried(asynchronous: bool) -> None:
    steps: list[Step] = [httpx.Response(503, json={"error": {"code": "shape_too_large"}})]
    with pytest.raises(ServiceUnavailableError):
        await _run(asynchronous, steps, _info)
    assert len(steps) == 0


@pytest.mark.parametrize("status", [502, 503, 504])
async def test_writes_never_retry_server_answers(asynchronous: bool, status: int) -> None:
    steps: list[Step] = [httpx.Response(status, json={"error": {"code": "storage_busy"}})]
    with pytest.raises(CommitUncertainError):
        await _run(asynchronous, steps, lambda ts: ts.database("main").collection("c").create({"a": 1}))
    assert len(steps) == 0


@pytest.mark.parametrize("failure", [httpx.ConnectError("refused"), httpx.ConnectTimeout("connect"),
                                     httpx.PoolTimeout("pool")])
async def test_writes_retry_failures_that_never_reached_the_server(asynchronous: bool, failure: Exception) -> None:
    steps: list[Step] = [failure, httpx.Response(201, json=ACK)]
    result, calls = await _run(asynchronous, steps, lambda ts: ts.database("main").collection("c").create({"a": 1}))
    assert result.id == PROJECT and len(calls) == 2
    assert calls[0].content == calls[1].content


async def test_unsent_write_reports_it_was_not_sent(asynchronous: bool) -> None:
    steps: list[Step] = [httpx.ConnectError("[Errno 111] Connection refused") for _ in range(4)]
    with pytest.raises(TerraScaleError) as caught:
        await _run(asynchronous, steps, lambda ts: ts.database("main").collection("c").create({"a": 1}))
    assert not isinstance(caught.value, CommitUncertainError)
    assert str(caught.value) == ("POST failed after 4 attempts: ConnectError: [Errno 111] Connection refused"
                                 " (the request was not sent)")


@pytest.mark.parametrize("failure", [httpx.ReadError("reset"), httpx.WriteError("broken pipe"), httpx.ReadTimeout("slow")])
async def test_writes_never_replay_after_sending(asynchronous: bool, failure: Exception) -> None:
    steps: list[Step] = [failure, httpx.Response(201, json=ACK)]
    with pytest.raises(CommitUncertainError):
        await _run(asynchronous, steps, lambda ts: ts.database("main").collection("c").create({"a": 1}))
    assert len(steps) == 1


async def test_exhausted_read_keeps_the_transport_detail(asynchronous: bool) -> None:
    steps: list[Step] = [httpx.ReadTimeout("The read operation timed out") for _ in range(4)]
    with pytest.raises(TerraScaleError) as caught:
        await _run(asynchronous, steps, _info)
    assert str(caught.value) == "GET failed after 4 attempts: ReadTimeout: The read operation timed out"
    assert isinstance(caught.value.__cause__, httpx.ReadTimeout)
