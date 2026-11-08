"""Response types. Unknown response members are ignored, so newer servers
stay compatible."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from .ids import validate_id

__all__ = [
    "ApiKey",
    "ApiKeyPage",
    "ApiKeySecret",
    "Database",
    "DatabasePage",
    "ProjectClient",
    "ProjectClientPage",
    "ProjectClientSecret",
    "Regions",
    "ScopedKey",
]


@dataclass(frozen=True)
class Regions:
    """The ordered serving-region catalog; anonymous placement metadata, not a routing policy."""

    regions: list[str]

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> Regions:
        regions = body.get("regions")
        if not isinstance(regions, list) or not regions or any(not isinstance(r, str) or not r for r in regions):
            raise ValueError("invalid region catalog")
        if any(regions[i - 1] >= regions[i] for i in range(1, len(regions))):
            raise ValueError("invalid region catalog")
        return cls(list(regions))


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
class RestoreOperation:
    """A background restore; ``database`` names the restored database once completed."""

    id: str
    operation_id: str
    status: str
    kind: str
    database: str
    error_code: str | None
    error_message: str | None
    created_at: str
    finished_at: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> RestoreOperation:
        fields = ("id", "operationId", "status", "kind", "database", "createdAt")
        if any(not isinstance(body[k], str) or not body[k] for k in fields):
            raise ValueError("invalid restore operation")
        if body["status"] not in ("running", "completed", "failed"):
            raise ValueError("invalid restore operation status")
        return cls(id=validate_id(body["id"]), operation_id=validate_id(body["operationId"]), status=body["status"],
                   kind=body["kind"], database=body["database"], error_code=body.get("errorCode"),
                   error_message=body.get("errorMessage"), created_at=body["createdAt"],
                   finished_at=body.get("finishedAt"))


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


def _strings(body: dict[str, Any], key: str, what: str) -> list[str]:
    values = body.get(key)
    if not isinstance(values, list) or any(not isinstance(v, str) or not v for v in values):
        raise ValueError(f"invalid {what}")
    return list(values)


@dataclass(frozen=True)
class ApiKey:
    """An organization or project API key, never its secret."""

    id: str
    name: str
    organization_id: str
    scopes: list[str]
    created_by: str
    created_at: str
    project_id: str | None = None
    database: str | None = None
    expires_at: str | None = None
    previous_secret_expires_at: str | None = None
    last_used_at: str | None = None
    allowed_cidrs: list[str] = field(default_factory=list)

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ApiKey:
        for key in ("id", "name", "organization_id", "created_by", "created_at"):
            if not isinstance(body.get(key), str) or not body[key]:
                raise ValueError("invalid api key metadata")
        return cls(
            id=validate_id(body["id"]),
            name=body["name"],
            organization_id=validate_id(body["organization_id"]),
            scopes=_strings(body, "scopes", "api key scopes"),
            created_by=validate_id(body["created_by"]),
            created_at=body["created_at"],
            project_id=validate_id(body["project_id"]) if body.get("project_id") is not None else None,
            database=validate_id(body["database"]) if body.get("database") is not None else None,
            expires_at=body.get("expires_at"),
            previous_secret_expires_at=body.get("previous_secret_expires_at"),
            last_used_at=body.get("last_used_at"),
            allowed_cidrs=_strings(body, "allowed_cidrs", "api key allowed cidrs"),
        )


@dataclass(frozen=True)
class ApiKeySecret:
    """A created or rotated key with its whole ``<ULID>_<52 chars>`` string, answered once."""

    api_key: ApiKey
    key: str = field(repr=False)

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ApiKeySecret:
        if not isinstance(body.get("key"), str) or not body["key"]:
            raise ValueError("invalid api key secret")
        inner = body.get("api_key")
        if not isinstance(inner, dict):
            raise ValueError("invalid api key secret")
        return cls(api_key=ApiKey.from_json(inner), key=body["key"])


@dataclass(frozen=True)
class ApiKeyPage:
    items: list[ApiKey]
    next_cursor: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ApiKeyPage:
        return cls(
            items=[ApiKey.from_json(k) for k in body.get("items") or []],
            next_cursor=body.get("next") or None,
        )


@dataclass(frozen=True)
class ProjectClient:
    """A project's registered OAuth client; ``client_secret`` appears only on confidential creation."""

    client_id: str
    name: str
    type: str
    redirect_uris: list[str]
    created_at: str
    client_secret: str | None = field(default=None, repr=False)

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectClient:
        for key in ("client_id", "name", "type", "created_at"):
            if not isinstance(body.get(key), str) or not body[key]:
                raise ValueError("invalid project client metadata")
        if body["type"] not in ("public", "confidential"):
            raise ValueError("invalid project client type")
        secret = body.get("client_secret")
        if secret is not None and (not isinstance(secret, str) or not secret):
            raise ValueError("invalid project client secret")
        return cls(
            client_id=validate_id(body["client_id"]),
            name=body["name"],
            type=body["type"],
            redirect_uris=_strings(body, "redirect_uris", "project client redirect uris"),
            created_at=body["created_at"],
            client_secret=secret,
        )


@dataclass(frozen=True)
class ProjectClientSecret:
    """A rotated client secret, answered exactly once."""

    client_id: str
    client_secret: str = field(repr=False)
    previous_secret_expires_at: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectClientSecret:
        if not isinstance(body.get("client_secret"), str) or not body["client_secret"]:
            raise ValueError("invalid project client secret")
        return cls(
            client_id=validate_id(body["client_id"]),
            client_secret=body["client_secret"],
            previous_secret_expires_at=body.get("previous_secret_expires_at"),
        )


@dataclass(frozen=True)
class ProjectClientPage:
    items: list[ProjectClient]
    next_cursor: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectClientPage:
        return cls(
            items=[ProjectClient.from_json(c) for c in body.get("items") or []],
            next_cursor=body.get("next") or None,
        )
