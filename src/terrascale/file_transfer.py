"""Isolated, bounded storage capability transfers; never project authentication."""

from __future__ import annotations

import base64
import hashlib
import re
import ssl
from typing import Any

import httpx

from .errors import APIError
from .files import FileSignedUrl, _checksum, _size

_MAX_BYTES = 104857600
_FORBIDDEN = {"authorization", "proxy-authorization", "cookie", "cookie2", "host", "connection", "proxy-connection",
              "transfer-encoding", "trailer", "upgrade", "te"}


class FileTransferError(APIError):
    """A redacted failure; an uncertain PUT must be reconciled before another attempt."""

    def __init__(self, status: int = 0, *, may_have_applied: bool = False):
        super().__init__(status, "file_transfer_error", "storage transfer failed")
        self.may_have_applied = may_have_applied


def _length(value: str) -> int:
    size = _size(value)
    if size > _MAX_BYTES:
        raise ValueError("file transfer exceeds 100 MiB")
    return size


def _prepare(cap: FileSignedUrl, data: bytes | None) -> dict[str, str]:
    try:
        url = httpx.URL(cap.url)
        if not isinstance(cap.url, str) or url.scheme != "https" or not url.host or url.userinfo or url.fragment:
            raise ValueError()
        headers: dict[str, str] = {}
        if not isinstance(cap.headers, dict):
            raise ValueError()
        for key, value in cap.headers.items():
            if (not isinstance(key, str) or not re.fullmatch(r"[!#$%&'*+.^_`|~0-9A-Za-z-]+", key)
                    or not isinstance(value, str) or any(c != "\t" and not 32 <= ord(c) <= 126 for c in value)):
                raise ValueError()
            key = key.lower()
            if key in headers or key in _FORBIDDEN:
                raise ValueError()
            headers[key] = value
        if data is not None and (not isinstance(data, bytes) or not 1 <= len(data) <= _MAX_BYTES):
            raise ValueError()
        if "content-length" in headers and _length(headers["content-length"]) != (len(data) if data is not None else 0):
            raise ValueError()
        if "x-amz-checksum-sha256" in headers:
            digest = _checksum(headers["x-amz-checksum-sha256"])
            if data is not None and digest != base64.b64encode(hashlib.sha256(data).digest()).decode():
                raise ValueError()
        headers.setdefault("accept-encoding", "identity")
        return headers
    except (ValueError, TypeError, AttributeError, httpx.InvalidURL):
        pass
    raise ValueError("invalid storage capability or upload bytes")


def _options() -> dict[str, Any]:
    context = httpx.create_ssl_context(trust_env=False)
    context.minimum_version = ssl.TLSVersion.TLSv1_3
    return {"verify": context, "trust_env": False, "follow_redirects": False, "timeout": httpx.Timeout(30.0, connect=10.0)}


def _download_bounds(r: httpx.Response) -> None:
    if r.status_code != 200:
        raise FileTransferError(r.status_code)
    try:
        if "content-length" in r.headers:
            _length(r.headers["content-length"])
        if "x-amz-checksum-sha256" in r.headers:
            _checksum(r.headers["x-amz-checksum-sha256"])
        return
    except (ValueError, TypeError):
        pass
    raise FileTransferError(r.status_code)


def _finish(r: httpx.Response, payload: bytearray, headers: dict[str, str]) -> bytes:
    digest = base64.b64encode(hashlib.sha256(payload).digest()).decode()
    if ("content-length" in r.headers and len(payload) != _length(r.headers["content-length"])
            or any(expected != digest for expected in (headers.get("x-amz-checksum-sha256"),
                                                       r.headers.get("x-amz-checksum-sha256")) if expected is not None)):
        raise FileTransferError(r.status_code)
    return bytes(payload)


_TRANSPORT = (httpx.HTTPError, httpx.StreamError, httpx.InvalidURL, OSError)


class FileTransfer:
    def __init__(self, capability: FileSignedUrl):
        self._cap = capability

    def put_bytes(self, data: bytes) -> None:
        if data is None:
            raise ValueError("invalid storage capability or upload bytes")
        headers = _prepare(self._cap, data)
        try:
            with httpx.Client(**_options()) as storage:
                with storage.stream("PUT", self._cap.url, headers=headers, content=data, follow_redirects=False) as r:
                    if not 200 <= r.status_code < 300:
                        raise FileTransferError(r.status_code, may_have_applied=r.status_code >= 500)
                    return
        except _TRANSPORT:
            pass
        raise FileTransferError(may_have_applied=True)

    def get_bytes(self) -> bytes:
        headers = _prepare(self._cap, None)
        try:
            with httpx.Client(**_options()) as storage:
                with storage.stream("GET", self._cap.url, headers=headers, follow_redirects=False) as r:
                    _download_bounds(r)
                    payload = bytearray()
                    for chunk in r.iter_raw():
                        if len(payload) + len(chunk) > _MAX_BYTES:
                            raise FileTransferError(r.status_code)
                        payload.extend(chunk)
                    return _finish(r, payload, headers)
        except _TRANSPORT:
            pass
        raise FileTransferError()


class AsyncFileTransfer:
    def __init__(self, capability: FileSignedUrl):
        self._cap = capability

    async def put_bytes(self, data: bytes) -> None:
        if data is None:
            raise ValueError("invalid storage capability or upload bytes")
        headers = _prepare(self._cap, data)
        try:
            async with httpx.AsyncClient(**_options()) as storage:
                async with storage.stream("PUT", self._cap.url, headers=headers, content=data, follow_redirects=False) as r:
                    if not 200 <= r.status_code < 300:
                        raise FileTransferError(r.status_code, may_have_applied=r.status_code >= 500)
                    return
        except _TRANSPORT:
            pass
        raise FileTransferError(may_have_applied=True)

    async def get_bytes(self) -> bytes:
        headers = _prepare(self._cap, None)
        try:
            async with httpx.AsyncClient(**_options()) as storage:
                async with storage.stream("GET", self._cap.url, headers=headers, follow_redirects=False) as r:
                    _download_bounds(r)
                    payload = bytearray()
                    async for chunk in r.aiter_raw():
                        if len(payload) + len(chunk) > _MAX_BYTES:
                            raise FileTransferError(r.status_code)
                        payload.extend(chunk)
                    return _finish(r, payload, headers)
        except _TRANSPORT:
            pass
        raise FileTransferError()
