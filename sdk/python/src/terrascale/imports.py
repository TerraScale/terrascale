"""One-time imports: typed wire responses and bounded, raw source parts."""

from __future__ import annotations

import re
from dataclasses import asdict, dataclass, field
from typing import TYPE_CHECKING, Any, Literal, TypedDict, cast

import httpx

from ._spec import Call, name
from .errors import APIError, CommitUncertainError, response_request_id
from .ids import validate_id

if TYPE_CHECKING:
    from .aio import AsyncDatabase
    from .client import Database

Integer = int | str

class ImportColumn(TypedDict):
    name: str
    sourceType: str
    mapping: str
    nullable: bool
    excluded: bool

class ImportConstraint(TypedDict):
    kind: str
    name: str | None
    columns: list[str]
    references: str | None
    enforced: bool

class ImportIssue(TypedDict):
    severity: str
    code: str
    column: str | None
    row: Integer | None
    message: str

class ImportTable(TypedDict):
    source: str
    collection: str
    excluded: bool
    rows: Integer
    primaryKey: list[str]
    columns: list[ImportColumn]
    constraints: list[ImportConstraint]
    issues: list[ImportIssue]

class ImportSkipped(TypedDict):
    kind: str
    name: str

class ImportPreview(TypedDict):
    id: str
    tables: list[ImportTable]
    skipped: list[ImportSkipped]
    skippedTruncated: bool
    blocking: Integer
    warnings: Integer

class ImportProgress(TypedDict):
    tablesDone: Integer
    tablesTotal: Integer
    rowsWritten: Integer
    rowsTotal: Integer
    currentTable: str | None

class ImportTableValidation(TypedDict):
    source: str
    collection: str
    sourceRows: Integer
    documents: Integer
    sourceChecksum: str
    documentChecksum: str
    match: bool

class ImportValidation(TypedDict):
    passed: bool
    tables: list[ImportTableValidation]

class ImportError(TypedDict):
    code: str
    message: str
    retryable: bool
    table: str | None
    row: Integer | None
    column: str | None

class ImportSource(TypedDict):
    format: str
    bytes: Integer
    sha256: str
    receivedBytes: Integer
    staged: bool
    retainedUntil: str | None

class _ImportCollection(TypedDict, total=False):
    collection: str | None


class ImportOptionsBody(_ImportCollection):
    exclude: list[str] | None

class ImportJob(TypedDict):
    id: str
    project: str
    database: str
    region: str
    state: str
    cancelRequested: bool
    options: ImportOptionsBody
    source: ImportSource
    preview: ImportPreview | None
    confirmedPreviewId: str | None
    progress: ImportProgress | None
    validation: ImportValidation | None
    error: ImportError | None
    createdAt: str
    updatedAt: str
    startedAt: str | None
    finishedAt: str | None


@dataclass(frozen=True)
class ImportOptions:
    exclude: list[str] = field(default_factory=list)
    collection: str | None = None


@dataclass(frozen=True)
class ImportRequest:
    format: Literal["postgres_plain", "sqlite", "ndjson"]
    source_bytes: int
    source_sha256: str
    options: ImportOptions = field(default_factory=ImportOptions)

    def body(self) -> dict[str, Any]:
        if self.format not in ("postgres_plain", "sqlite", "ndjson"):
            raise ValueError("import format must be postgres_plain, sqlite or ndjson")
        size = self.source_bytes
        if isinstance(size, bool) or not isinstance(size, int) or not 1 <= size <= 1073741824:
            raise ValueError("source_bytes must be an integer from 1 to 1073741824")
        if not isinstance(self.source_sha256, str) or re.fullmatch(r"[0-9a-f]{64}", self.source_sha256) is None:
            raise ValueError("source_sha256 must contain 64 lowercase hexadecimal characters")
        return {"format": self.format, "sourceBytes": self.source_bytes, "sourceSha256": self.source_sha256,
                "options": asdict(self.options)}


class _ImportCall(Call):
    def content(self) -> bytes | None:
        content = self.body if isinstance(self.body, bytes) else super().content()
        if content is not None and len(content) > 4194304:
            raise ValueError("import request exceeds 4 MiB")
        return content

    def decode(self, response: httpx.Response) -> Any:
        return self.parse(response)


def _failure(call: Call, error: APIError) -> APIError:
    if not call.replay_safe and error.status >= 500 and not isinstance(error, CommitUncertainError):
        return CommitUncertainError(error.status, error.code, "import mutation may have committed; inspect the job",
                                    request_id=error.request_id)
    return error


class _Requests:
    def __init__(self, project: str, database: str):
        self._path = ["v1", "projects", validate_id(project, "project"), "databases", name(database, "database"), "import"]

    def _call(self, method: str, suffix: str | None = None, body: Any = None, offset: int | None = None) -> Call:
        success: tuple[int, ...] = (200,) if method == "GET" or suffix == "source" else (202,)
        if method == "POST" and suffix is None:
            success = (200, 201)

        def parse(r: httpx.Response) -> ImportJob:
            try:
                if r.status_code not in success:
                    raise ValueError("unexpected success status")
                value = r.json()
                if not isinstance(value, dict) or not ImportJob.__required_keys__ <= value.keys():
                    raise ValueError("incomplete import job")
                return cast(ImportJob, value)
            except (ValueError, TypeError, KeyError):
                error = APIError if method == "GET" else CommitUncertainError
                raise error(r.status_code, "invalid_response", "invalid import response; inspect the job before retrying",
                            request_id=response_request_id(r.headers)) from None

        return _ImportCall(method, [*self._path, *([suffix] if suffix else [])], parse, body=body,
                           params={} if offset is None else {"offset": str(offset)}, replay_safe=method == "GET",
                           content_type="application/octet-stream" if suffix == "source" else "application/json")

    def _upload(self, offset: int, part: bytes) -> Call:
        if isinstance(offset, bool) or not isinstance(offset, int) or not 0 <= offset <= 9223372036854775807:
            raise ValueError("offset must be a nonnegative int64")
        if not isinstance(part, bytes) or not 1 <= len(part) <= 4194304:
            raise ValueError("source part must contain 1 to 4194304 bytes")
        return self._call("PUT", "source", part, offset)


class DatabaseImports(_Requests):
    def __init__(self, database: Database):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    def _send(self, call: Call) -> ImportJob:
        try:
            return self._client._send(call)
        except APIError as error:
            raise _failure(call, error) from None

    def create_import(self, request: ImportRequest) -> ImportJob:
        return self._send(self._call("POST", body=request.body()))

    def get_import(self) -> ImportJob:
        return self._send(self._call("GET"))

    def update_import(self, options: ImportOptions) -> ImportJob:
        return self._send(self._call("PATCH", body={"options": asdict(options)}))

    def upload_import_source(self, offset: int, part: bytes) -> ImportJob:
        return self._send(self._upload(offset, part))

    def start_import(self, preview_id: str) -> ImportJob:
        return self._send(self._call("POST", "start", {"previewId": validate_id(preview_id, "preview")}))

    def cancel_import(self) -> ImportJob:
        return self._send(self._call("POST", "cancel"))

    def resume_import(self) -> ImportJob:
        return self._send(self._call("POST", "resume"))


class AsyncDatabaseImports(_Requests):
    def __init__(self, database: AsyncDatabase):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    async def _send(self, call: Call) -> ImportJob:
        try:
            return await self._client._send(call)
        except APIError as error:
            raise _failure(call, error) from None

    async def create_import(self, request: ImportRequest) -> ImportJob:
        return await self._send(self._call("POST", body=request.body()))

    async def get_import(self) -> ImportJob:
        return await self._send(self._call("GET"))

    async def update_import(self, options: ImportOptions) -> ImportJob:
        return await self._send(self._call("PATCH", body={"options": asdict(options)}))

    async def upload_import_source(self, offset: int, part: bytes) -> ImportJob:
        return await self._send(self._upload(offset, part))

    async def start_import(self, preview_id: str) -> ImportJob:
        return await self._send(self._call("POST", "start", {"previewId": validate_id(preview_id, "preview")}))

    async def cancel_import(self) -> ImportJob:
        return await self._send(self._call("POST", "cancel"))

    async def resume_import(self) -> ImportJob:
        return await self._send(self._call("POST", "resume"))
