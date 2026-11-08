"""Response types. Unknown response members are ignored, so newer servers
stay compatible."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from .ids import validate_id

__all__ = ["Database", "DatabasePage", "ScopedKey"]


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
    expires_at: str | None = None
    replica_regions: list[str] = field(default_factory=list)
    all_regions: bool = False

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> Database:
        if any(not isinstance(body[k], str) or not body[k] for k in ("name", "createdAt", "region", "homeRegion")):
            raise ValueError("invalid database metadata")
        if body.get("expiresAt") is not None and (not isinstance(body["expiresAt"], str) or not body["expiresAt"]):
            raise ValueError("invalid database expiry")
        replicas = body.get("replicaRegions", [])
        if not isinstance(replicas, list) or any(not isinstance(r, str) or not r for r in replicas):
            raise ValueError("invalid database replica regions")
        if not isinstance(body.get("allRegions", False), bool):
            raise ValueError("invalid database allRegions")
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
            expires_at=body.get("expiresAt"),
            replica_regions=list(replicas),
            all_regions=body.get("allRegions", False),
            raw=body,
        )


@dataclass(frozen=True)
class ScopedKey:
    key: str = field(repr=False)
    id: str
    project_id: str
    database: str
    access: str
    expires_at: str

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ScopedKey:
        if any(not isinstance(body[k], str) or not body[k] for k in ("key", "expires_at")):
            raise ValueError("invalid scoped key metadata")
        if body["access"] not in ("read", "read_write"):
            raise ValueError("invalid scoped key access")
        return cls(key=body["key"], id=validate_id(body["id"]), project_id=validate_id(body["project_id"]),
                   database=validate_id(body["database"]), access=body["access"], expires_at=body["expires_at"])


@dataclass(frozen=True)
class RotatedApiKey:
    api_key: str = field(repr=False)
    id: str
    previous_secret_expires_at: int | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> RotatedApiKey:
        if not isinstance(body.get("api_key"), str) or not body["api_key"]:
            raise ValueError("invalid rotated api key secret")
        expiry = body.get("previous_secret_expires_at")
        if expiry is not None and (not isinstance(expiry, int) or isinstance(expiry, bool)):
            raise ValueError("invalid rotated api key expiry")
        return cls(api_key=body["api_key"], id=validate_id(body["id"]),
                   previous_secret_expires_at=expiry)


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
