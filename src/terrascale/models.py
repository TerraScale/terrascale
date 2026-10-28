"""Response types. Unknown response members are ignored, so newer servers
stay compatible."""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Any

from .ids import validate_id

__all__ = [
    "Document",
    "WriteResult",
    "Page",
    "Group",
    "Collection",
    "CollectionPage",
    "Database",
    "DatabasePage",
    "SQLResult",
    "SQLStatementResult",
    "SQLColumn",
    "TransactionResult",
]


def _commit_metadata(body: dict[str, Any]) -> None:
    offset, durability = body["offset"], body["durability"]
    if not isinstance(offset, (str, int)) or not str(offset).isascii() or not str(offset).isdecimal():
        raise ValueError("invalid commit offset")
    if not isinstance(durability, str) or not durability:
        raise ValueError("invalid commit durability")


@dataclass(frozen=True)
class Document:
    """A stored document. ``version`` is an opaque token: pass it back as
    ``if_match``, never parse it. ``state`` is ``"value"`` for a plain value;
    a causal read can answer ``"conflict"`` with ``siblings`` in ``raw``."""

    id: str
    version: str
    state: str
    data: Any
    distance: float | None = None
    raw: dict[str, Any] = field(default_factory=dict, repr=False)

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> Document:
        distance = body.get("distance")
        return cls(
            id=body["id"],
            version=body["version"],
            state=body.get("state", "value"),
            data=body.get("document"),
            distance=float(distance) if distance is not None else None,
            raw=body,
        )


@dataclass(frozen=True)
class WriteResult:
    """A committed write. ``offset`` is a decimal commit position."""

    id: str
    version: str
    offset: str
    durability: str
    document: Any = None
    created: bool = False

    @classmethod
    def from_json(cls, body: dict[str, Any], created: bool = False) -> WriteResult:
        _commit_metadata(body)
        if not isinstance(body["version"], str) or not body["version"]:
            raise ValueError("invalid write version")
        return cls(
            id=validate_id(body["id"]),
            version=body["version"],
            offset=str(body["offset"]),
            durability=body["durability"],
            document=body.get("document"),
            created=created,
        )


@dataclass(frozen=True)
class Group:
    """One aggregate group: ``key`` has one JSON value per ``group_by`` path;
    ``sum``/``avg``/``min``/``max`` map each requested pointer to its value
    (``None`` when the group has no numbers there; a sum is then 0)."""

    key: list[Any]
    count: int
    sum: dict[str, float | None] = field(default_factory=dict)
    avg: dict[str, float | None] = field(default_factory=dict)
    min: dict[str, float | None] = field(default_factory=dict)
    max: dict[str, float | None] = field(default_factory=dict)

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> Group:
        def folds(name: str) -> dict[str, float | None]:
            return {p: float(v) if v is not None else None for p, v in (body.get(name) or {}).items()}

        return cls(key=list(body.get("key") or []), count=int(body.get("count") or 0),
                   sum=folds("sum"), avg=folds("avg"), min=folds("min"), max=folds("max"))


@dataclass(frozen=True)
class Page:
    """One page of documents. Continue with ``next_cursor`` until it is None.
    An aggregate query answers ``groups`` and no items."""

    items: list[Document]
    next_cursor: str | None
    count: int | None = None
    count_exact: bool | None = None
    groups: list[Group] | None = None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> Page:
        count = body.get("count")
        groups = body.get("groups")
        return cls(
            items=[Document.from_json(d) for d in body.get("items") or []],
            next_cursor=body.get("next_cursor") or None,
            count=int(count["value"]) if isinstance(count, dict) else None,
            count_exact=count.get("exact") if isinstance(count, dict) else None,
            groups=[Group.from_json(g) for g in groups] if groups is not None else None,
        )


@dataclass(frozen=True)
class Collection:
    name: str
    raw: dict[str, Any] = field(default_factory=dict, repr=False)

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> Collection:
        if not isinstance(body["name"], str) or not body["name"]:
            raise ValueError("invalid collection name")
        return cls(name=body["name"], raw=body)


@dataclass(frozen=True)
class CollectionPage:
    items: list[Collection]
    next_cursor: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> CollectionPage:
        return cls(
            items=[Collection.from_json(c) for c in body.get("items") or []],
            next_cursor=body.get("next_cursor") or None,
        )


@dataclass(frozen=True)
class Database:
    """A managed database and placement, with optional cross-region name conflict metadata."""

    id: str
    project_id: str
    name: str
    created_at: str
    region: str
    home_region: str
    replica_regions: list[str]
    all_regions: bool
    branched_from: str | None = None
    raw: dict[str, Any] = field(default_factory=dict, repr=False)
    previous_name: str | None = None
    conflict_of: str | None = None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> Database:
        if any(not isinstance(body[k], str) or not body[k] for k in ("name", "createdAt", "region", "homeRegion")):
            raise ValueError("invalid database metadata")
        return cls(
            id=validate_id(body["id"]),
            project_id=validate_id(body["projectId"]),
            name=body["name"],
            created_at=body["createdAt"],
            region=body["region"],
            home_region=body["homeRegion"],
            replica_regions=list(body["replicaRegions"]),
            all_regions=bool(body["allRegions"]),
            branched_from=body["branchedFrom"],
            previous_name=body.get("previousName"),
            conflict_of=validate_id(body["conflictOf"]) if body.get("conflictOf") is not None else None,
            raw=body,
        )


@dataclass(frozen=True)
class DatabasePage:
    items: list[Database]
    next_cursor: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> DatabasePage:
        return cls(
            items=[Database.from_json(d) for d in body.get("items") or []],
            next_cursor=body.get("next") or None,
        )


@dataclass(frozen=True)
class SQLResult:
    """A read-only ``SELECT`` result: rows are ``{"id", "value"}``."""

    matched: int
    truncated: bool
    rows: list[dict[str, Any]]

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> SQLResult:
        return cls(
            matched=int(body.get("matched") or 0),
            truncated=bool(body.get("truncated")),
            rows=list(body.get("rows") or []),
        )


def _sql_integer(value: Any) -> int:
    if type(value) is not int and (not isinstance(value, str) or not re.fullmatch(r"-?(0|[1-9][0-9]*)", value)):
        raise ValueError("expected an integer")
    return int(value)


@dataclass(frozen=True)
class SQLColumn:
    """SQL column name and declared type (None for an expression)."""

    name: str
    decltype: str | None

    def __post_init__(self) -> None:
        if not isinstance(self.name, str) or self.decltype is not None and not isinstance(self.decltype, str):
            raise ValueError("invalid SQL column")


@dataclass(frozen=True)
class SQLStatementResult:
    """One statement of :meth:`execute`/:meth:`batch`."""

    columns: list[SQLColumn]
    rows: list[list[Any]]
    rows_affected: int
    last_insert_rowid: int | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> SQLStatementResult:
        rowid = body["last_insert_rowid"]
        columns, rows = body["columns"], body["rows"]
        if not isinstance(columns, list) or not isinstance(rows, list):
            raise ValueError("expected SQL columns and rows arrays")
        if any(not isinstance(row, list) or len(row) != len(columns) for row in rows):
            raise ValueError("invalid SQL row")
        affected = _sql_integer(body["rows_affected"])
        if affected < 0:
            raise ValueError("negative rows affected")
        return cls(
            columns=[SQLColumn(c["name"], c["decltype"]) for c in columns],
            rows=rows,
            rows_affected=affected,
            last_insert_rowid=_sql_integer(rowid) if rowid is not None else None,
        )


@dataclass(frozen=True)
class TransactionResult:
    """A committed transaction; ``results`` has one dict per op, in order."""

    offset: str
    durability: str
    results: list[dict[str, Any]]

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> TransactionResult:
        _commit_metadata(body)
        return cls(
            offset=str(body["offset"]),
            durability=body["durability"],
            results=list(body["results"]),
        )
