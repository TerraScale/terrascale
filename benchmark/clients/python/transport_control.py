"""Exercise the real SDK and observation gates with synthetic HTTPX transports."""

import asyncio
import json
from types import SimpleNamespace

import httpx
from terrascale import AsyncTerraScale, new_id

from observer import HOST, PIN_HEADER, REGION_HEADER, Observer, cell_rejection


class BrokenBody(httpx.AsyncByteStream):
    async def __aiter__(self):
        yield b'{'
        raise httpx.ReadError("synthetic truncated body")


async def case(mode: str, expected: str | None) -> None:
    project, document = new_id(), new_id()
    path = f"/v1/projects/{project}/databases/fixture/collections/bench/documents/by-key"
    observer = Observer({("GET", path)}, "gru", "HTTP/2")
    exchanges = sends = 0
    body = {"id": document, "version": "fixture", "state": "value", "document": {"x": 1}}

    async def handle(request: httpx.Request) -> httpx.Response:
        nonlocal exchanges, sends
        if request.url.host == "auth.terrascale.tech":
            assert PIN_HEADER not in request.headers
            exchanges += 1
            return httpx.Response(200, json={"access_token": f"synthetic.{exchanges}",
                                           "expires_in": 3600, "token_type": "Bearer"})
        assert request.url.host == HOST and request.url.path == path
        assert request.headers[PIN_HEADER] == "gru"
        sends += 1
        headers = [(REGION_HEADER, "fra" if mode == "wrong-region" else "gru")]
        if mode == "duplicate-region":
            headers *= 2
        extensions = {"http_version": b"HTTP/1.1" if mode == "fallback" else b"HTTP/2"}
        if sends == 1 and mode in ("retry-503", "refresh-401"):
            status = 503 if mode == "retry-503" else 401
            return httpx.Response(status, headers=headers, extensions=extensions,
                                  json={"error": {"code": "unavailable" if status == 503 else "invalid_token"}})
        if sends == 1 and mode == "body-retry":
            return httpx.Response(200, headers=headers, extensions=extensions, stream=BrokenBody())
        return httpx.Response(200, headers=headers, extensions=extensions, json=body)

    async with httpx.AsyncClient(transport=httpx.MockTransport(handle),
                                event_hooks={"request": [observer.on_request],
                                             "response": [observer.on_response]}) as pool:
        client = AsyncTerraScale(new_id() + "_" + "a" * 52, project=project, http_client=pool)
        result = await client.database("fixture").collection("bench").get(document)
        assert result.raw == body
    stats = SimpleNamespace(started=1, failed=0, stuck=False, latencies=[1.0])
    reject = cell_rejection(True, stats, observer)
    assert reject == expected, (mode, reject)
    assert sends == observer.attempts
    assert exchanges == (2 if mode == "refresh-401" else 1)
    if mode == "body-retry":
        assert observer.attempts == observer.good == 2  # Both headers were 200; only one operation completed.
    print(json.dumps({"case": mode, "attempts": observer.attempts, "good": observer.good,
                      "logical_operations": 1, "auth_exchanges": exchanges, "reject": reject}))


async def cancelled_auth() -> None:
    observer = Observer({("GET", "/fixture")}, "gru")
    stopped = asyncio.Event()

    async def handle(request: httpx.Request) -> httpx.Response:
        assert request.url.host == "auth.terrascale.tech" and PIN_HEADER not in request.headers
        try:
            await asyncio.Event().wait()
        finally:
            stopped.set()
        raise AssertionError("unreachable")

    async with httpx.AsyncClient(transport=httpx.MockTransport(handle),
                                event_hooks={"request": [observer.on_request]}) as pool:
        client = AsyncTerraScale(new_id() + "_" + "a" * 52, project=new_id(), http_client=pool)
        try:
            await asyncio.wait_for(client.database("fixture").collection("bench").get(new_id()), .1)
        except TimeoutError:
            pass
        else:
            raise AssertionError("stalled auth completed")
    assert stopped.is_set() and observer.attempts == 0
    for url in ("http://api.terrascale.tech/fixture", "https://api.terrascale.tech:444/fixture",
                "https://auth.terrascale.tech/fixture", "https://api.terrascale.tech/other"):
        request = httpx.Request("GET", url)
        await observer.on_request(request)
        assert PIN_HEADER not in request.headers
    print(json.dumps({"case": "stalled-auth", "cancelled": True, "auth_pinned": False}))


async def main() -> None:
    for mode in ("normal", "wrong-region", "duplicate-region", "fallback", "retry-503", "refresh-401", "body-retry"):
        await case(mode, None if mode == "normal" else "attempt_mismatch")
    await cancelled_auth()


if __name__ == "__main__":
    asyncio.run(main())
