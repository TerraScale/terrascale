"""Typed manual indexes, collection policies and workload recommendations."""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import TYPE_CHECKING, Any, Literal

from ._spec import Call, name
from .tooling import _array, _asend, _integer, _Requests, _send, _strings, _text

if TYPE_CHECKING:
    from .aio import AsyncDatabase
    from .client import Database


@dataclass(frozen=True)
class Index:
    name: str
    collection: str
    fields: list[str]
    state: str
    kind: str = "btree"
    dimensions: int | None = None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> Index:
        dimensions = b.get("dimensions")
        return cls(_text(b["name"]), _text(b["collection"]), _strings(b["fields"]), _text(b["state"]),
                   _text(b.get("kind", "btree")), None if dimensions is None else _integer(dimensions, 32))


@dataclass(frozen=True)
class IndexRequest:
    collection: str
    fields: list[str]
    name: str | None = None
    kind: Literal["btree", "text", "vector"] = "btree"
    dimensions: int | None = None

    def body(self) -> dict[str, Any]:
        fields = _strings(self.fields)
        if not 1 <= len(fields) <= 4 or len(set(fields)) != len(fields) or any(not f or f in ("_id", "_rev") for f in fields):
            raise ValueError("indexes need 1 to 4 distinct fields, excluding _id and _rev")
        if self.kind not in ("btree", "text", "vector"):
            raise ValueError("index kind must be btree, text or vector")
        body: dict[str, Any] = {"collection": name(self.collection, "collection"), "fields": fields, "kind": self.kind}
        if self.name is not None:
            if not isinstance(self.name, str) or not re.fullmatch(r"[a-z][a-z0-9_]{0,62}", self.name):
                raise ValueError("index name must be a lowercase identifier of at most 63 characters")
            body["name"] = self.name
        if self.kind == "vector":
            if (len(fields) != 1 or isinstance(self.dimensions, bool) or not isinstance(self.dimensions, int)
                    or not 1 <= self.dimensions <= 4096):
                raise ValueError("vector indexes need one field and integer dimensions from 1 to 4096")
            body["dimensions"] = self.dimensions
        elif self.dimensions is not None:
            raise ValueError("dimensions apply only to vector indexes")
        return body


@dataclass(frozen=True)
class IndexRecommendation:
    id: str
    collection: str
    fields: list[str]
    reason: str
    queries: int

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> IndexRecommendation:
        return cls(_text(b["id"]), _text(b["collection"]), _strings(b["fields"]), _text(b["reason"]), _integer(b["queries"]))


@dataclass(frozen=True)
class AppliedRecommendation:
    status: Literal["applied", "already-applied"]
    index: Index

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> AppliedRecommendation:
        status = b["status"]
        if status not in ("applied", "already-applied"):
            raise ValueError("unknown recommendation outcome")
        return cls(status, Index.from_json(b["index"]))


class _Indexes(_Requests):
    def _list(self, collection: str | None) -> Call:
        params = {} if collection is None else {"collection": name(collection, "collection")}
        return self._call("GET", ["indexes"], lambda b: [Index.from_json(x) for x in _array(b["items"])], params=params)

    def _create(self, request: IndexRequest) -> Call:
        return self._call("POST", ["indexes"], Index.from_json, body=request.body(), statuses=(201,))

    def _drop(self, index: str) -> Call:
        def unexpected_body(_: dict[str, Any]) -> None:
            raise ValueError("index deletion must have an empty response")
        return self._call("DELETE", ["indexes", name(index, "index")], unexpected_body, statuses=(204,))

    def _recommendations(self) -> Call:
        return self._call("GET", ["index-recommendations"],
                          lambda b: [IndexRecommendation.from_json(x) for x in _array(b["items"])])

    def _apply(self, recommendation: str) -> Call:
        return self._call("POST", ["index-recommendations", name(recommendation, "recommendation"), "apply"],
                          AppliedRecommendation.from_json, statuses=(200, 201))


class DatabaseIndexes(_Indexes):
    """Compose with a database; mutations use its existing authorization and are never replayed."""

    def __init__(self, database: Database):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    def list_indexes(self, *, collection: str | None = None) -> list[Index]:
        return _send(self._client, self._list(collection))

    def create_index(self, request: IndexRequest) -> Index:
        return _send(self._client, self._create(request))

    def drop_index(self, index: str) -> None:
        _send(self._client, self._drop(index))

    def list_index_recommendations(self) -> list[IndexRecommendation]:
        return _send(self._client, self._recommendations())

    def apply_index_recommendation(self, recommendation: str) -> AppliedRecommendation:
        return _send(self._client, self._apply(recommendation))


class AsyncDatabaseIndexes(_Indexes):
    """Awaited twin of DatabaseIndexes with identical arguments and results."""

    def __init__(self, database: AsyncDatabase):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    async def list_indexes(self, *, collection: str | None = None) -> list[Index]:
        return await _asend(self._client, self._list(collection))

    async def create_index(self, request: IndexRequest) -> Index:
        return await _asend(self._client, self._create(request))

    async def drop_index(self, index: str) -> None:
        await _asend(self._client, self._drop(index))

    async def list_index_recommendations(self) -> list[IndexRecommendation]:
        return await _asend(self._client, self._recommendations())

    async def apply_index_recommendation(self, recommendation: str) -> AppliedRecommendation:
        return await _asend(self._client, self._apply(recommendation))
