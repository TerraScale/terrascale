"""Immutable file reservations and metadata; signed capabilities stay private."""

from __future__ import annotations

import base64
import re
from dataclasses import dataclass, field
from typing import Any

import httpx

from ._spec import Call
from .backup_config import _redacted
from .errors import TerraScaleError
from .ids import validate_id
from .rules import _policy_call
from .storage import AsyncStorage, Storage


def _size(value: Any) -> int:
    if isinstance(value, bool) or not isinstance(value, (int, str)):
        raise ValueError("file size must be an integer or decimal string")
    if isinstance(value, str) and re.fullmatch(r"0|[1-9][0-9]*", value) is None:
        raise ValueError("file size must be a canonical decimal integer")
    size = int(value)
    if not 0 <= size <= 9223372036854775807:
        raise ValueError("file size must fit nonnegative int64")
    return size


def _checksum(value: str) -> str:
    try:
        decoded = base64.b64decode(value, validate=True)
        if len(decoded) != 32 or base64.b64encode(decoded).decode() != value:
            raise ValueError("invalid digest")
    except (ValueError, TypeError):
        raise ValueError("sha256 must be canonical base64 of a 32-byte SHA-256 digest") from None
    return value


@dataclass(frozen=True)
class FileUpload:
    name: str
    content_type: str
    size: int
    sha256: str
    metadata: dict[str, Any] = field(default_factory=dict, repr=False)

    def body(self) -> dict[str, Any]:
        if not isinstance(self.name, str) or not self.name or not isinstance(self.content_type, str) or not self.content_type:
            raise ValueError("file name and content_type must be nonempty strings")
        if not isinstance(self.size, int) or not 1 <= _size(self.size) <= 104857600:
            raise ValueError("upload size must be an integer from 1 to 104857600")
        if not isinstance(self.metadata, dict):
            raise ValueError("file metadata must be an object")
        return {"name": self.name, "contentType": self.content_type, "size": self.size,
                "sha256": _checksum(self.sha256), "metadata": self.metadata}


@dataclass(frozen=True)
class FileObject:
    id: str
    name: str
    content_type: str
    size: int
    sha256: str
    metadata: Any = field(repr=False)
    status: str
    upload_expires_at: str
    delete_after: str | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> FileObject:
        return cls(validate_id(b["id"], "file"), b["name"], b["contentType"], _size(b["size"]), _checksum(b["sha256"]),
                   b["metadata"], b["status"], b["uploadExpiresAt"], b.get("deleteAfter"))


@dataclass(frozen=True)
class FileObjectPage:
    items: list[FileObject]
    next_cursor: str | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> FileObjectPage:
        if not isinstance(b["items"], list):
            raise ValueError("file items must be an array")
        cursor = b["next"]
        return cls([FileObject.from_json(x) for x in b["items"]],
                   validate_id(cursor, "file cursor") if cursor is not None else None)


@dataclass(frozen=True)
class FileSignedUrl:
    id: str
    url: str = field(repr=False)
    expires_at: str
    headers: dict[str, str] = field(repr=False)

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> FileSignedUrl:
        try:
            url = httpx.URL(b["url"])
        except (httpx.InvalidURL, TypeError, ValueError):
            raise ValueError("invalid signed capability URL") from None
        if not isinstance(b["url"], str) or url.scheme != "https" or not url.host or url.userinfo or url.fragment:
            raise ValueError("signed capability must be an HTTPS URL without userinfo or fragment")
        headers = b["headers"]
        if not isinstance(headers, dict) or any(not isinstance(k, str) or not isinstance(v, str) for k, v in headers.items()):
            raise ValueError("capability headers must map strings to strings")
        return cls(validate_id(b["id"], "file"), b["url"], b["expiresAt"], dict(headers))


class _Requests:
    def __init__(self, project: str, bucket: str):
        self._path = ["v1", "projects", validate_id(project, "project"), "buckets", validate_id(bucket, "bucket"), "files"]

    def _list(self, after: str | None) -> Call:
        return _policy_call("GET", self._path, lambda r: FileObjectPage.from_json(r.json()), 200,
                            params={} if after is None else {"after": validate_id(after, "file cursor")})

    def _upload(self, request: FileUpload) -> Call:
        return _policy_call("POST", [*self._path, "upload"], lambda r: FileSignedUrl.from_json(r.json()), 201,
                            body=request.body())

    def _file(self, method: str, file: str, operation: str | None = None) -> Call:
        file = validate_id(file, "file")
        def parse(r: httpx.Response) -> FileObject | FileSignedUrl:
            result = FileSignedUrl.from_json(r.json()) if operation == "download" else FileObject.from_json(r.json())
            if result.id != file:
                raise ValueError("file acknowledgement identity mismatch")
            return result
        return _policy_call(method, [*self._path, file, *([operation] if operation else [])], parse,
                            204 if method == "DELETE" else 200)


class BucketFiles(_Requests):
    def __init__(self, storage: Storage, bucket: str):
        super().__init__(storage._client.project, bucket)
        self._storage = storage

    def _send(self, call: Call) -> Any:
        try:
            return self._storage._send(call)
        except TerraScaleError as error:
            raise _redacted(error) from None

    def list_bucket_files(self, *, after: str | None = None) -> FileObjectPage:
        return self._send(self._list(after))

    def create_file_upload(self, request: FileUpload) -> FileSignedUrl:
        return self._send(self._upload(request))

    def complete_file_upload(self, file: str) -> FileObject:
        return self._send(self._file("POST", file, "complete"))

    def create_file_download(self, file: str) -> FileSignedUrl:
        return self._send(self._file("POST", file, "download"))

    def delete_file(self, file: str) -> None:
        self._send(self._file("DELETE", file))


class AsyncBucketFiles(_Requests):
    def __init__(self, storage: AsyncStorage, bucket: str):
        super().__init__(storage._client.project, bucket)
        self._storage = storage

    async def _send(self, call: Call) -> Any:
        try:
            return await self._storage._send(call)
        except TerraScaleError as error:
            raise _redacted(error) from None

    async def list_bucket_files(self, *, after: str | None = None) -> FileObjectPage:
        return await self._send(self._list(after))

    async def create_file_upload(self, request: FileUpload) -> FileSignedUrl:
        return await self._send(self._upload(request))

    async def complete_file_upload(self, file: str) -> FileObject:
        return await self._send(self._file("POST", file, "complete"))

    async def create_file_download(self, file: str) -> FileSignedUrl:
        return await self._send(self._file("POST", file, "download"))

    async def delete_file(self, file: str) -> None:
        await self._send(self._file("DELETE", file))
