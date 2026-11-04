"""Exercise the default HTTPX transports using HTTPcore's in-memory network fakes.

Only the individual pool's socket backend is replaced. HTTPX/HTTPcore still
encode requests, negotiate the fake ALPN result, parse responses and reuse
connections. There are no sockets, global patches or TerraBase processes.
"""

from __future__ import annotations

import json
import ssl
from typing import Any

import h2.config
import h2.connection
import h2.events
import httpcore
import pytest
from fake import API_KEY, PROJECT

from terrascale import AsyncTerraScale, TerraScale
from terrascale.auth import APIKeyTokenSource, AsyncAPIKeyTokenSource

BASE = "https://pool.test"
TOKEN = {"access_token": "tok-1", "token_type": "Bearer", "expires_in": 3600}
PAGE = {"items": [], "next_cursor": None}


class ALPNContext(ssl.SSLContext):
    def set_alpn_protocols(self, protocols: list[str]) -> None:
        self.offered = protocols
        super().set_alpn_protocols(protocols)


def responses(http2: bool, bodies: list[dict[str, Any]]) -> list[bytes]:
    if not http2:
        result = []
        for body in bodies:
            data = json.dumps(body).encode()
            result.append(b"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                          + str(len(data)).encode() + b"\r\n\r\n" + data)
        return result

    client = h2.connection.H2Connection()
    server = h2.connection.H2Connection(h2.config.H2Configuration(client_side=False))
    client.initiate_connection()
    server.initiate_connection()
    server.receive_data(client.data_to_send())
    result = [server.data_to_send()]
    for index, body in enumerate(bodies):
        stream = 2 * index + 1
        client.send_headers(stream, [(":method", "GET"), (":scheme", "https"),
                                     (":authority", "pool.test"), (":path", "/")], end_stream=True)
        server.receive_data(client.data_to_send())
        server.send_headers(stream, [(":status", "200"), ("content-type", "application/json")])
        server.send_data(stream, json.dumps(body).encode(), end_stream=True)
        result.append(server.data_to_send())
    return result


class Backend(httpcore.MockBackend):
    def __init__(self, buffer: list[bytes], http2: bool):
        super().__init__(buffer, http2=http2)
        self.connections = 0
        self.writes: list[bytes] = []
        self.ssl_context = ALPNContext(ssl.PROTOCOL_TLS_CLIENT)

    def connect_tcp(self, *args: Any, **kwargs: Any) -> Any:
        self.connections += 1
        stream = super().connect_tcp(*args, **kwargs)
        original = stream.write

        def write(buffer: bytes, timeout: float | None = None) -> None:
            self.writes.append(buffer)
            original(buffer, timeout)

        stream.write = write
        return stream


class AsyncBackend(httpcore.AsyncMockBackend):
    def __init__(self, buffer: list[bytes], http2: bool):
        super().__init__(buffer, http2=http2)
        self.connections = 0
        self.writes: list[bytes] = []
        self.ssl_context = ALPNContext(ssl.PROTOCOL_TLS_CLIENT)

    async def connect_tcp(self, *args: Any, **kwargs: Any) -> Any:
        self.connections += 1
        stream = await super().connect_tcp(*args, **kwargs)
        original = stream.write

        async def write(buffer: bytes, timeout: float | None = None) -> None:
            self.writes.append(buffer)
            await original(buffer, timeout)

        stream.write = write
        return stream


def bind(http: Any, backend: Backend | AsyncBackend) -> None:
    # This per-instance boundary avoids global patches and external proxy I/O.
    http._mounts.clear()
    http._transport._pool._ssl_context = backend.ssl_context
    http._transport._pool._network_backend = backend


def check(backend: Backend | AsyncBackend, http2: bool, exchanges: int, reads: int) -> None:
    assert backend.connections == 1
    assert "h2" in backend.ssl_context.offered
    assert "http/1.1" in backend.ssl_context.offered
    data = b"".join(backend.writes)
    if http2:
        assert data.startswith(b"PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n")
        server = h2.connection.H2Connection(h2.config.H2Configuration(client_side=False))
        server.initiate_connection()
        headers = [dict(event.headers) for event in server.receive_data(data)
                   if isinstance(event, h2.events.RequestReceived)]
        assert len(headers) == exchanges + reads
        assert sum(header[b"authorization"].startswith(b"Basic ") for header in headers) == exchanges
        assert sum(header[b"authorization"] == b"Bearer tok-1" for header in headers) == reads
    else:
        assert data.startswith(b"POST /terrascale/oauth/token HTTP/1.1\r\n")
        assert data.lower().count(b"authorization: basic ") == exchanges
        assert data.lower().count(b"authorization: bearer tok-1\r\n") == reads


@pytest.mark.parametrize("http2", [True, False])
def test_sync_default_transport_pools_auth_and_api(http2: bool) -> None:
    backend = Backend(responses(http2, [TOKEN, PAGE, PAGE, PAGE]), http2)
    with TerraScale(API_KEY, project=PROJECT, base_url=BASE, issuer=BASE + "/terrascale") as client:
        bind(client._http, backend)
        for _ in range(3):
            assert client.list_databases().items == []
    check(backend, http2, exchanges=1, reads=3)


@pytest.mark.parametrize("http2", [True, False])
async def test_async_default_transport_pools_auth_and_api(http2: bool) -> None:
    backend = AsyncBackend(responses(http2, [TOKEN, PAGE, PAGE, PAGE]), http2)
    async with AsyncTerraScale(API_KEY, project=PROJECT, base_url=BASE, issuer=BASE + "/terrascale") as client:
        bind(client._http, backend)
        for _ in range(3):
            assert (await client.list_databases()).items == []
    check(backend, http2, exchanges=1, reads=3)


@pytest.mark.parametrize("http2", [True, False])
def test_sync_standalone_token_source_reuses_connection(http2: bool) -> None:
    backend = Backend(responses(http2, [TOKEN, TOKEN]), http2)
    source = APIKeyTokenSource(API_KEY, project=PROJECT, issuer=BASE + "/terrascale")
    with source._http:
        bind(source._http, backend)
        assert source.token() == "tok-1"
        source.invalidate("tok-1")
        assert source.token() == "tok-1"
    check(backend, http2, exchanges=2, reads=0)


@pytest.mark.parametrize("http2", [True, False])
async def test_async_standalone_token_source_reuses_connection(http2: bool) -> None:
    backend = AsyncBackend(responses(http2, [TOKEN, TOKEN]), http2)
    source = AsyncAPIKeyTokenSource(API_KEY, project=PROJECT, issuer=BASE + "/terrascale")
    async with source._http:
        bind(source._http, backend)
        assert await source.token() == "tok-1"
        source.invalidate("tok-1")
        assert await source.token() == "tok-1"
    check(backend, http2, exchanges=2, reads=0)
