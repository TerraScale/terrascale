"""HTTPX attempt and connection observation for SDK parity; no transport policy changes."""

from __future__ import annotations

from typing import Any

import httpx

HOST = "api.terrascale.tech"
PIN_HEADER = "X-TerraScale-Region-Pin"
REGION_HEADER = "x-terrabase-region"


def pool(observer: Observer | None = None, *, lane: str = "sdk") -> httpx.AsyncClient:
    """The SDK's own pool settings (30 s timeout, no redirects); httpx limits, TLS and proxy defaults."""
    hooks = {} if observer is None else {"request": [observer.on_request], "response": [observer.on_response]}
    return httpx.AsyncClient(timeout=30.0, follow_redirects=False, http1=lane != "raw-h2",
                             http2=lane != "raw-h1", event_hooks=hooks)


class Observer:
    """Count every pinned attempt, including SDK retries, and validate its response."""

    def __init__(self, routes: set[tuple[str, str]], region: str, protocol: str | None = None, measured: bool = True):
        self.routes, self.region, self.protocol, self.measured = routes, region, protocol, measured
        self.connects = 0  # Pool lifetime, readiness included.
        self.window()

    def window(self) -> None:
        self.attempts = self.good = 0
        self.versions: dict[str, int] = {}

    def _pinned(self, request: httpx.Request) -> bool:
        return request.url.scheme == "https" and request.url.host == HOST and request.url.port in (None, 443) \
            and (request.method, request.url.path) in self.routes

    async def on_request(self, request: httpx.Request) -> None:
        if self._pinned(request):
            request.headers[PIN_HEADER] = self.region
            if self.measured:
                self.attempts += 1
                request.extensions["trace"] = self._trace  # httpcore: connect_tcp fires once per new connection

    async def _trace(self, event: str, _info: dict[str, Any]) -> None:
        if event == "connection.connect_tcp.started":
            self.connects += 1

    async def on_response(self, response: httpx.Response) -> None:
        if not self.measured or not self._pinned(response.request):
            return
        version = response.http_version
        self.versions[version] = self.versions.get(version, 0) + 1
        if response.status_code == 200 and response.headers.get_list(REGION_HEADER) == [self.region] \
                and self.protocol in (None, version):
            self.good += 1


def cell_rejection(ready: bool, stats: Any, observer: Observer) -> str | None:
    if not ready:
        return "readiness"
    if stats.failed:
        return "operation_error"
    if stats.stuck:
        return "drain_timeout"
    if observer.attempts != observer.good or observer.attempts != stats.started:
        return "attempt_mismatch"
    return None if stats.latencies else "empty"
