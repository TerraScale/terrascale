"""Response types. Unknown response members are ignored, so newer servers
stay compatible."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from .ids import validate_id

__all__ = ["Database", "DatabasePage"]


@dataclass(frozen=True)
class Database:
    """A managed database in its home region, with optional cross-region name conflict metadata."""

    id: str
    project_id: str
    name: str
    created_at: str
    region: str
    home_region: str
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
