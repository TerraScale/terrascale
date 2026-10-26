"""Typed database inference, unique keys and collection validation schemas."""

from __future__ import annotations

import re
from collections.abc import Callable
from dataclasses import asdict, dataclass
from typing import TYPE_CHECKING, Any, Literal

import httpx

from ._spec import Call, name
from .errors import APIError, CommitUncertainError, response_request_id
from .ids import validate_id

if TYPE_CHECKING:
    from .aio import AsyncDatabase, AsyncTerraScale
    from .client import Database, TerraScale

Normalization = Literal["none", "lowercase", "trim_lowercase"]


def _integer(value: Any, bits: int = 64) -> int:
    if isinstance(value, bool) or not isinstance(value, (int, str)) or not re.fullmatch(r"-?(?:0|[1-9][0-9]*)", str(value)):
        raise ValueError("expected an exact integer")
    result = int(value)
    if not -(2 ** (bits - 1)) <= result < 2 ** (bits - 1):
        raise ValueError("integer outside contract range")
    return result


def _text(value: Any) -> str:
    if not isinstance(value, str):
        raise ValueError("expected text")
    return value


def _array(value: Any) -> list[Any]:
    if not isinstance(value, list):
        raise ValueError("expected an array")
    return value


def _strings(value: Any) -> list[str]:
    return [_text(x) for x in _array(value)]


@dataclass(frozen=True)
class UniqueConstraint:
    name: str
    fields: list[str]
    normalization: str = "none"

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> UniqueConstraint:
        return cls(_text(b["name"]), _strings(b["fields"]), _text(b.get("normalization", "none")))


@dataclass(frozen=True)
class UniqueField:
    name: str
    normalization: Normalization = "none"


@dataclass(frozen=True)
class UniqueIndex:
    name: str
    fields: list[str]
    normalization: Normalization = "none"


@dataclass(frozen=True)
class UniqueDeclaration:
    fields: tuple[UniqueField, ...] = ()
    indexes: tuple[UniqueIndex, ...] = ()

    def body(self) -> dict[str, Any]:
        entries: list[UniqueField | UniqueIndex] = [*self.fields, *self.indexes]
        if len(entries) > 32 or any(x.normalization not in ("none", "lowercase", "trim_lowercase") for x in entries):
            raise ValueError("at most 32 unique keys with a supported normalization")
        for index in self.indexes:
            if not 1 <= len(index.fields) <= 4 or len(set(index.fields)) != len(index.fields):
                raise ValueError("unique indexes need 1 to 4 distinct fields")
        return {"fields": [asdict(x) | {"unique": True} for x in self.fields],
                "indexes": [asdict(x) | {"unique": True} for x in self.indexes]}


@dataclass(frozen=True)
class UniqueSchema:
    constraints: list[UniqueConstraint]

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> UniqueSchema:
        return cls([UniqueConstraint.from_json(x) for x in _array(b["constraints"])])


@dataclass(frozen=True)
class SchemaField:
    path: str
    present: int
    types: dict[str, int]

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> SchemaField:
        return cls(_text(b["path"]), _integer(b["present"], 32), {_text(k): _integer(v, 32) for k, v in b["types"].items()})


@dataclass(frozen=True)
class CollectionSchema:
    collection: str
    documents: int
    sampled_documents: int
    fields: list[SchemaField]
    unique_constraints: list[UniqueConstraint] | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> CollectionSchema:
        keys = b.get("uniqueConstraints")
        return cls(_text(b["collection"]), _integer(b["documents"]), _integer(b["sampledDocuments"], 32),
                   [SchemaField.from_json(x) for x in _array(b["fields"])],
                   None if keys is None else [UniqueConstraint.from_json(x) for x in _array(keys)])


@dataclass(frozen=True)
class CollectionStats:
    name: str
    documents: int
    bytes: int

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> CollectionStats:
        return cls(_text(b["name"]), _integer(b["documents"]), _integer(b["bytes"]))


@dataclass(frozen=True)
class DatabaseSchema:
    items: list[CollectionStats]

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> DatabaseSchema:
        return cls([CollectionStats.from_json(x) for x in _array(b["items"])])


@dataclass(frozen=True)
class DeclaredSchema:
    schema: Any
    dialect: str | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> DeclaredSchema:
        dialect = b.get("dialect")
        return cls(b["schema"], None if dialect is None else _text(dialect))


class _ToolingCall(Call):
    def decode(self, response: httpx.Response) -> Any:
        return self.parse(response)


class _Requests:
    def __init__(self, project: str, database: str):
        self._path = ["v1", "projects", validate_id(project, "project"), "databases", name(database, "database")]

    def _call(self, method: str, suffix: list[str], parse: Callable[[dict[str, Any]], Any], *, body: Any = None,
              params: dict[str, str] | None = None, statuses: tuple[int, ...] = (200,)) -> Call:
        def decode(r: httpx.Response) -> Any:
            try:
                if r.status_code not in statuses:
                    raise ValueError("unexpected success status")
                if r.status_code == 204 and not r.content:
                    return None
                value = r.json()
                if not isinstance(value, dict):
                    raise ValueError("expected an object")
                return parse(value)
            except (ValueError, KeyError, TypeError, AttributeError, IndexError, OverflowError):
                error = APIError if method == "GET" else CommitUncertainError
                raise error(r.status_code, "invalid_response", "invalid tooling response; reconcile writes before retrying",
                            request_id=response_request_id(r.headers)) from None
        return _ToolingCall(method, [*self._path, *suffix], decode, body=body,
                            params=params or {}, replay_safe=method == "GET")


def _uncertain(call: Call, error: APIError) -> None:
    if not call.replay_safe and error.status >= 500 and not isinstance(error, CommitUncertainError):
        raise CommitUncertainError(error.status, error.code, "tooling write may have committed; reconcile before retrying",
                                   request_id=error.request_id) from None


def _send(client: TerraScale, call: Call) -> Any:
    try:
        return client._send(call)
    except APIError as error:
        _uncertain(call, error)
        raise


async def _asend(client: AsyncTerraScale, call: Call) -> Any:
    try:
        return await client._send(call)
    except APIError as error:
        _uncertain(call, error)
        raise


class _Schemas(_Requests):
    def _inferred(self, collection: str) -> list[str]:
        return ["schema", name(collection, "collection")]

    def _declared(self, collection: str) -> list[str]:
        return ["collections", name(collection, "collection"), "schema"]

    def _put(self, collection: str, schema: dict[str, Any] | bool) -> Call:
        if not isinstance(schema, (dict, bool)):
            raise ValueError("JSON Schema must be an object or boolean")
        call = self._call("PUT", self._declared(collection), DeclaredSchema.from_json, body=schema)
        if len(call.content() or b"") > 64 * 1024:
            raise ValueError("JSON Schema exceeds 64 KiB")
        return call


class DatabaseTooling(_Schemas):
    """Compose with an existing database; inference/uniqueness and validation are distinct."""

    def __init__(self, database: Database):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    def get_database_schema(self) -> DatabaseSchema:
        return _send(self._client, self._call("GET", ["schema"], DatabaseSchema.from_json))

    def get_collection_schema(self, collection: str) -> CollectionSchema:
        return _send(self._client, self._call("GET", self._inferred(collection), CollectionSchema.from_json))

    def declare_collection_schema(self, collection: str, declaration: UniqueDeclaration) -> UniqueSchema:
        return _send(self._client, self._call("PUT", self._inferred(collection), UniqueSchema.from_json, body=declaration.body()))

    def get_declared_collection_schema(self, collection: str) -> DeclaredSchema:
        return _send(self._client, self._call("GET", self._declared(collection), DeclaredSchema.from_json))

    def put_declared_collection_schema(self, collection: str, schema: dict[str, Any] | bool) -> DeclaredSchema:
        return _send(self._client, self._put(collection, schema))

    def delete_declared_collection_schema(self, collection: str) -> DeclaredSchema:
        return _send(self._client, self._call("DELETE", self._declared(collection), DeclaredSchema.from_json))


class AsyncDatabaseTooling(_Schemas):
    """Async twin of DatabaseTooling; uses the existing client's credentials and lifecycle."""

    def __init__(self, database: AsyncDatabase):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    async def get_database_schema(self) -> DatabaseSchema:
        return await _asend(self._client, self._call("GET", ["schema"], DatabaseSchema.from_json))

    async def get_collection_schema(self, collection: str) -> CollectionSchema:
        return await _asend(self._client, self._call("GET", self._inferred(collection), CollectionSchema.from_json))

    async def declare_collection_schema(self, collection: str, declaration: UniqueDeclaration) -> UniqueSchema:
        return await _asend(self._client, self._call("PUT", self._inferred(collection), UniqueSchema.from_json,
                                                  body=declaration.body()))

    async def get_declared_collection_schema(self, collection: str) -> DeclaredSchema:
        return await _asend(self._client, self._call("GET", self._declared(collection), DeclaredSchema.from_json))

    async def put_declared_collection_schema(self, collection: str, schema: dict[str, Any] | bool) -> DeclaredSchema:
        return await _asend(self._client, self._put(collection, schema))

    async def delete_declared_collection_schema(self, collection: str) -> DeclaredSchema:
        return await _asend(self._client, self._call("DELETE", self._declared(collection), DeclaredSchema.from_json))
