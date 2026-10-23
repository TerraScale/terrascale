"""Response types. Unknown response members are ignored, so newer servers
stay compatible."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

__all__ = [
    "Document",
    "WriteResult",
    "Page",
    "Collection",
    "CollectionPage",
    "Database",
    "DatabasePage",
    "SQLResult",
    "SQLStatementResult",
    "TransactionResult",
]


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
        return cls(
            id=body["id"],
            version=body["version"],
            offset=str(body.get("offset", "")),
            durability=body.get("durability", ""),
            document=body.get("document"),
            created=created,
        )


@dataclass(frozen=True)
class Page:
    """One page of documents. Continue with ``next_cursor`` until it is None."""

    items: list[Document]
    next_cursor: str | None
    count: int | None = None
    count_exact: bool | None = None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> Page:
        count = body.get("count")
        return cls(
            items=[Document.from_json(d) for d in body.get("items") or []],
            next_cursor=body.get("next_cursor") or None,
            count=int(count["value"]) if isinstance(count, dict) else None,
            count_exact=count.get("exact") if isinstance(count, dict) else None,
        )


@dataclass(frozen=True)
class Collection:
    name: str
    raw: dict[str, Any] = field(default_factory=dict, repr=False)

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> Collection:
        return cls(name=body.get("name", ""), raw=body)


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
    """A managed database and its region placement."""

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

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> Database:
        return cls(
            id=body.get("id", ""),
            project_id=body.get("projectId", ""),
            name=body.get("name", ""),
            created_at=body.get("createdAt", ""),
            region=body.get("region", ""),
            home_region=body.get("homeRegion", ""),
            replica_regions=list(body.get("replicaRegions") or []),
            all_regions=bool(body.get("allRegions")),
            branched_from=body.get("branchedFrom"),
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


@dataclass(frozen=True)
class SQLStatementResult:
    """One statement of :meth:`execute`/:meth:`batch`."""

    columns: list[str]
    rows: list[list[Any]]
    rows_affected: int
    last_insert_rowid: int | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> SQLStatementResult:
        rowid = body.get("last_insert_rowid")
        return cls(
            columns=list(body.get("columns") or []),
            rows=list(body.get("rows") or []),
            rows_affected=int(body.get("rows_affected") or 0),
            last_insert_rowid=int(rowid) if rowid is not None else None,
        )


@dataclass(frozen=True)
class TransactionResult:
    """A committed transaction; ``results`` has one dict per op, in order."""

    offset: str
    durability: str
    results: list[dict[str, Any]]

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> TransactionResult:
        return cls(
            offset=str(body.get("offset", "")),
            durability=body.get("durability", ""),
            results=list(body.get("results") or []),
        )
