"""Observational, paged JSON Schema preview without changing collection validation."""

from __future__ import annotations

import json
from dataclasses import dataclass
from typing import TYPE_CHECKING, Any

import httpx

from ._spec import Call, name
from .errors import APIError, response_request_id
from .tooling import _array, _asend, _integer, _Requests, _send, _text

if TYPE_CHECKING:
    from .aio import AsyncDatabase
    from .client import Database


@dataclass(frozen=True)
class SchemaPreviewDiagnostic:
    id_hash: str
    path_hash: str
    keyword: str

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> SchemaPreviewDiagnostic:
        return cls(_text(b["id_hash"]), _text(b["path_hash"]), _text(b["keyword"]))


@dataclass(frozen=True)
class SchemaPreviewPage:
    schema_hash: str
    frontier: str
    examined: int
    valid: int
    invalid: int
    complete: bool
    next_cursor: str | None
    diagnostics: list[SchemaPreviewDiagnostic]

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> SchemaPreviewPage:
        counts = [_integer(b[k], 32) for k in ("examined", "valid", "invalid")]
        diagnostics = [SchemaPreviewDiagnostic.from_json(x) for x in _array(b["diagnostics"])]
        if not isinstance(b["complete"], bool) or min(counts) < 0 or counts[0] != counts[1] + counts[2]:
            raise ValueError("invalid preview counts or completion state")
        if counts[0] > 100 or len(diagnostics) > 16:
            raise ValueError("preview exceeds its page bounds")
        cursor = b["next_cursor"]
        return cls(_text(b["schema_hash"]), _text(b["frontier"]), counts[0], counts[1], counts[2], b["complete"],
                   None if cursor is None else _text(cursor), diagnostics)


@dataclass
class _PreviewCall(Call):
    encoded_schema: bytes = b""

    def decode(self, response: httpx.Response) -> Any:
        return self.parse(response)

    def content(self) -> bytes:
        return self.encoded_schema


class _Preview(_Requests):
    def __init__(self, project: str, database: str, collection: str, schema: dict[str, Any] | bool):
        super().__init__(project, database)
        self._path.extend(["collections", name(collection, "collection"), "schema", "preview"])
        if not isinstance(schema, (dict, bool)):
            raise ValueError("JSON Schema must be an object or boolean")
        # Freeze the bytes once: continuation is bound to the exact body, not a mutable dict.
        self._encoded = json.dumps(schema, sort_keys=True, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode()
        if len(self._encoded) > 64 * 1024:
            raise ValueError("JSON Schema exceeds 64 KiB")

    def _page(self, cursor: str | None) -> Call:
        if cursor is not None and (not isinstance(cursor, str) or not cursor):
            raise ValueError("preview cursor must be a nonempty opaque string")

        def decode(r: httpx.Response) -> SchemaPreviewPage:
            try:
                if r.status_code != 200:
                    raise ValueError("unexpected preview status")
                body = r.json()
                if not isinstance(body, dict):
                    raise ValueError("expected a preview object")
                return SchemaPreviewPage.from_json(body)
            except (ValueError, KeyError, TypeError, AttributeError, IndexError, OverflowError):
                raise APIError(r.status_code, "invalid_response", "invalid schema preview response",
                               request_id=response_request_id(r.headers)) from None

        # POST is observational: retries cannot change schemas or documents.
        return _PreviewCall("POST", self._path, decode, params={} if cursor is None else {"cursor": cursor},
                            replay_safe=True, encoded_schema=self._encoded)


class CollectionSchemaPreview(_Preview):
    """Freeze one schema and fetch preview pages through an existing database client."""

    def __init__(self, database: Database, collection: str, schema: dict[str, Any] | bool):
        super().__init__(database._c.project, database.name, collection, schema)
        self._client = database._c

    def page(self, *, cursor: str | None = None) -> SchemaPreviewPage:
        return _send(self._client, self._page(cursor))


class AsyncCollectionSchemaPreview(_Preview):
    """Async twin of CollectionSchemaPreview; no background paging or implicit apply."""

    def __init__(self, database: AsyncDatabase, collection: str, schema: dict[str, Any] | bool):
        super().__init__(database._c.project, database.name, collection, schema)
        self._client = database._c

    async def page(self, *, cursor: str | None = None) -> SchemaPreviewPage:
        return await _asend(self._client, self._page(cursor))
