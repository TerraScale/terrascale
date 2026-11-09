"""Renewal must not put issuer latency on a still-valid request."""
from __future__ import annotations

import asyncio
import threading
import time
from types import SimpleNamespace

import httpx
import pytest
from fake import API_KEY, ISSUER, PROJECT

from terrascale import auth


@pytest.mark.parametrize("asynchronous", [False, True])
async def test_background_refresh_is_shared_until_actual_expiry(monkeypatch: pytest.MonkeyPatch, asynchronous: bool) -> None:
    clock = [0.0]
    monkeypatch.setattr(auth, "time", SimpleNamespace(monotonic=lambda: clock[0], sleep=time.sleep))
    calls = 0
    entered = threading.Event()
    release = threading.Event()
    async_entered = asyncio.Event()
    async_release = asyncio.Event()

    def answer() -> httpx.Response:
        lifetime = 60 if calls == 1 else 120
        return httpx.Response(200, json={"access_token": f"token-{calls}", "token_type": "Bearer", "expires_in": lifetime})

    def exchange(request: httpx.Request) -> httpx.Response:
        nonlocal calls
        calls += 1
        if calls == 2:
            entered.set()
            assert release.wait(5)
        return answer()

    async def async_exchange(request: httpx.Request) -> httpx.Response:
        nonlocal calls
        calls += 1
        if calls == 2:
            async_entered.set()
            await async_release.wait()
        return answer()

    if asynchronous:
        async with httpx.AsyncClient(transport=httpx.MockTransport(async_exchange)) as http:
            source = auth.AsyncAPIKeyTokenSource(API_KEY, project=PROJECT, issuer=ISSUER, http_client=http)
            try:
                assert await source.token() == "token-1"
                clock[0] = 30
                assert await asyncio.wait_for(source.token(), 1) == "token-1"
                await asyncio.wait_for(async_entered.wait(), 1)
                clock[0] = 59.999
                assert await asyncio.wait_for(source.token(), 1) == "token-1"
                clock[0] = 60
                waiting = asyncio.create_task(source.token())
                await asyncio.sleep(0)
                assert not waiting.done()
                async_release.set()
                assert await asyncio.wait_for(waiting, 1) == "token-2"
                assert calls == 2
            finally:
                async_release.set()
                await source.close()
    else:
        with httpx.Client(transport=httpx.MockTransport(exchange)) as sync_http:
            sync_source = auth.APIKeyTokenSource(API_KEY, project=PROJECT, issuer=ISSUER, http_client=sync_http)
            try:
                assert sync_source.token() == "token-1"
                clock[0] = 30
                assert await asyncio.wait_for(asyncio.to_thread(sync_source.token), 1) == "token-1"
                assert await asyncio.to_thread(entered.wait, 1)
                clock[0] = 59.999
                assert await asyncio.wait_for(asyncio.to_thread(sync_source.token), 1) == "token-1"
                clock[0] = 60
                waiting = asyncio.create_task(asyncio.to_thread(sync_source.token))
                await asyncio.sleep(0)
                assert not waiting.done()
                release.set()
                assert await asyncio.wait_for(waiting, 1) == "token-2"
                assert calls == 2
            finally:
                release.set()
                await asyncio.to_thread(sync_source.close)
