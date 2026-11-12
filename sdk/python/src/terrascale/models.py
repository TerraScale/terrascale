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
    "DatabaseRegions",
    "ReplicaStatus",
    "ProjectClient",
    "ProjectClientPage",
    "ProjectClientSecret",
    "ProjectDirectoryUser",
    "ProjectLinkedProvider",
    "ProjectProviderUpdate",
    "ProjectSession",
    "ProjectSessionPage",
    "ProjectSettings",
    "ProjectSettingsUpdate",
    "ProjectUser",
    "ProjectUserExportPage",
    "ProjectUserPage",
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
class ReplicaStatus:
    """One region's pull progress observed by the writer; unknown observations are None."""

    region: str
    role: str | None = None
    lag_ms: float | None = None
    last_pulled_at: str | None = None
    last_verified: str | None = None
    revision: str | None = None


@dataclass(frozen=True)
class DatabaseRegions:
    """Selected regions and writer-observed pull progress sampled at observed_at."""

    database_id: str
    observed_at: str
    regions: list[ReplicaStatus] = field(default_factory=list)

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> DatabaseRegions:
        if any(not isinstance(body[k], str) or not body[k] for k in ("observedAt",)):
            raise ValueError("invalid database regions")
        replicas = body["regions"]
        if not isinstance(replicas, list):
            raise ValueError("invalid database regions replicas")
        statuses = []
        for item in replicas:
            if not isinstance(item, dict) or not isinstance(item.get("region"), str) or not item["region"]:
                raise ValueError("invalid database regions replicas")
            role = item.get("role")
            if role is not None and (not isinstance(role, str) or not role):
                raise ValueError("invalid replica role")
            lag = item.get("lagMs")
            if lag is not None and not isinstance(lag, (int, float)):
                raise ValueError("invalid replica lagMs")
            timestamps = {}
            for name in ("lastPulledAt", "lastVerified"):
                value = item.get(name)
                if value is not None and (not isinstance(value, str) or not value):
                    raise ValueError(f"invalid replica {name}")
                timestamps[name] = value
            revision = item.get("revision")
            if revision is not None and (not isinstance(revision, str) or not revision):
                raise ValueError("invalid replica revision")
            statuses.append(
                ReplicaStatus(
                    region=item["region"],
                    role=role,
                    lag_ms=lag,
                    last_pulled_at=timestamps["lastPulledAt"],
                    last_verified=timestamps["lastVerified"],
                    revision=revision,
                )
            )
        return cls(database_id=validate_id(body["databaseId"]), observed_at=body["observedAt"], regions=statuses)


@dataclass(frozen=True)
class Database:
    """Lifecycle metadata with the answering region, complete region set and placement epoch."""

    id: str
    project_id: str
    name: str
    created_at: str
    region: str
    branched_from: str | None = None
    raw: dict[str, Any] = field(default_factory=dict, repr=False)
    previous_name: str | None = None
    conflict_of: str | None = None
    expires_at: str | None = None
    regions: list[str] = field(default_factory=list)
    all_regions: bool = False
    placement_version: int = 0

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> Database:
        if any(not isinstance(body[k], str) or not body[k] for k in ("name", "createdAt", "region")):
            raise ValueError("invalid database metadata")
        if body.get("expiresAt") is not None and (not isinstance(body["expiresAt"], str) or not body["expiresAt"]):
            raise ValueError("invalid database expiry")
        regions = body.get("regions", [])
        if not isinstance(regions, list) or any(not isinstance(r, str) or not r for r in regions):
            raise ValueError("invalid database regions")
        if len(set(regions)) != len(regions):
            raise ValueError("invalid database regions")
        if not isinstance(body.get("allRegions", False), bool):
            raise ValueError("invalid database allRegions")
        version = body["placementVersion"]
        if not isinstance(version, int) or isinstance(version, bool):
            raise ValueError("invalid database placementVersion")
        return cls(
            id=validate_id(body["id"]),
            project_id=validate_id(body["projectId"]),
            name=body["name"],
            created_at=body["createdAt"],
            region=body["region"],
            branched_from=body["branchedFrom"],
            previous_name=body.get("previousName"),
            conflict_of=validate_id(body["conflictOf"]) if body.get("conflictOf") is not None else None,
            expires_at=body.get("expiresAt"),
            regions=sorted(regions),
            all_regions=body.get("allRegions", False),
            placement_version=version,
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


def _bool(body: dict[str, Any], key: str, what: str) -> bool:
    if not isinstance(body.get(key), bool):
        raise ValueError(f"invalid {what}")
    return body[key]


def _text(body: dict[str, Any], key: str, what: str) -> str | None:
    value = body.get(key)
    if value is not None and (not isinstance(value, str) or not value):
        raise ValueError(f"invalid {what}")
    return value


def _int_or_none(body: dict[str, Any], key: str, what: str) -> int | None:
    value = body.get(key)
    if value is not None and (not isinstance(value, int) or isinstance(value, bool)):
        raise ValueError(f"invalid {what}")
    return value


@dataclass(frozen=True)
class ProjectSettingsMethods:
    """The project's enabled end-user sign-in methods."""

    passcode: bool
    password: bool
    sign_up: bool

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectSettingsMethods:
        return cls(passcode=_bool(body, "passcode", "project sign-in methods"),
                   password=_bool(body, "password", "project sign-in methods"),
                   sign_up=_bool(body, "sign_up", "project sign-in methods"))


@dataclass(frozen=True)
class ProjectSettingsBranding:
    """Hosted-page branding; every member is optional and stays unchanged when omitted."""

    name: str | None = None
    logo_url: str | None = None
    theme_color: str | None = None
    title_size: int | None = None
    default_theme: str | None = None
    footer_links: tuple[tuple[str, str], ...] = ()
    background_color: str | None = None
    text_color: str | None = None
    email_message: str | None = None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectSettingsBranding:
        links = body.get("footer_links") or []
        if not isinstance(links, list):
            raise ValueError("invalid project footer links")
        parsed = []
        for link in links:
            if not isinstance(link, dict) or not isinstance(link.get("label"), str) \
                    or not isinstance(link.get("url"), str):
                raise ValueError("invalid project footer links")
            parsed.append((link["label"], link["url"]))
        return cls(name=_text(body, "name", "project branding name"),
                   logo_url=_text(body, "logo_url", "project branding logo"),
                   theme_color=_text(body, "theme_color", "project branding color"),
                   title_size=_int_or_none(body, "title_size", "project branding title size"),
                   default_theme=_text(body, "default_theme", "project branding theme"),
                   footer_links=tuple(parsed),
                   background_color=_text(body, "background_color", "project branding background"),
                   text_color=_text(body, "text_color", "project branding text"),
                   email_message=_text(body, "email_message", "project branding email message"))


@dataclass(frozen=True)
class ProjectSettingsCounts:
    """The project's current user and OAuth client counts."""

    users: int
    clients: int

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectSettingsCounts:
        return cls(users=_int_or_none(body, "users", "project user count") or 0,
                   clients=_int_or_none(body, "clients", "project client count") or 0)


@dataclass(frozen=True)
class ProjectSettingsProvider:
    """One federated sign-in provider's configuration state."""

    provider: str
    configured: bool
    client_id: str | None
    issuer: str | None
    team_id: str | None
    key_id: str | None
    callback_url: str

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectSettingsProvider:
        for key in ("provider", "callback_url"):
            if not isinstance(body.get(key), str) or not body[key]:
                raise ValueError("invalid project provider")
        return cls(provider=body["provider"], configured=_bool(body, "configured", "project provider state"),
                   client_id=_text(body, "client_id", "project provider client id"),
                   issuer=_text(body, "issuer", "project provider issuer"),
                   team_id=_text(body, "team_id", "project provider team id"),
                   key_id=_text(body, "key_id", "project provider key id"),
                   callback_url=body["callback_url"])


@dataclass(frozen=True)
class ProjectSettingsConfiguration:
    """The project's redirect URLs and session lifetime."""

    redirect_urls: list[str]
    session_lifetime_seconds: int | None = None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectSettingsConfiguration:
        return cls(redirect_urls=_strings(body, "redirect_urls", "project redirect urls"),
                   session_lifetime_seconds=_int_or_none(body, "session_lifetime_seconds",
                                                          "project session lifetime"))


@dataclass(frozen=True)
class ProjectSettings:
    """The project's end-user authentication settings, read and updated with the operator's sign-in token."""

    project_id: str
    issuer: str
    discovery_url: str
    sign_in_url: str
    methods: ProjectSettingsMethods
    branding: ProjectSettingsBranding
    counts: ProjectSettingsCounts
    providers: list[ProjectSettingsProvider]
    configuration: ProjectSettingsConfiguration | None = None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectSettings:
        for key in ("project_id", "issuer", "discovery_url", "sign_in_url"):
            if not isinstance(body.get(key), str) or not body[key]:
                raise ValueError("invalid project settings")
        configuration = body.get("configuration")
        return cls(
            project_id=validate_id(body["project_id"]),
            issuer=body["issuer"],
            discovery_url=body["discovery_url"],
            sign_in_url=body["sign_in_url"],
            methods=ProjectSettingsMethods.from_json(body.get("methods") or {}),
            branding=ProjectSettingsBranding.from_json(body.get("branding") or {}),
            counts=ProjectSettingsCounts.from_json(body.get("counts") or {}),
            providers=[ProjectSettingsProvider.from_json(p) for p in body.get("providers") or []],
            configuration=ProjectSettingsConfiguration.from_json(configuration)
            if configuration is not None else None,
        )


@dataclass(frozen=True)
class ProjectSettingsUpdate:
    """A partial settings update; omitted parts stay unchanged."""

    methods: ProjectSettingsMethods | None = None
    branding: ProjectSettingsBranding | None = None
    configuration: ProjectSettingsConfiguration | None = None

    def to_body(self) -> dict[str, Any]:
        body: dict[str, Any] = {}
        if self.methods is not None:
            body["methods"] = {"passcode": self.methods.passcode, "password": self.methods.password,
                               "sign_up": self.methods.sign_up}
        if self.branding is not None:
            branding: dict[str, Any] = {}
            for key in ("name", "logo_url", "theme_color", "default_theme", "background_color", "text_color",
                        "email_message"):
                value = getattr(self.branding, key)
                if value is not None:
                    branding[key] = value
            if self.branding.title_size is not None:
                branding["title_size"] = self.branding.title_size
            if self.branding.footer_links:
                branding["footer_links"] = [{"label": label, "url": url}
                                            for label, url in self.branding.footer_links]
            body["branding"] = branding
        if self.configuration is not None:
            configuration: dict[str, Any] = {"redirect_urls": list(self.configuration.redirect_urls)}
            if self.configuration.session_lifetime_seconds is not None:
                configuration["session_lifetime_seconds"] = self.configuration.session_lifetime_seconds
            body["configuration"] = configuration
        return body


@dataclass(frozen=True)
class ProjectUser:
    """A project end user; disabling ends every session and stops refresh tokens at once."""

    id: str
    email: str
    created_at: str
    disabled: bool

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectUser:
        for key in ("id", "email", "created_at"):
            if not isinstance(body.get(key), str) or not body[key]:
                raise ValueError("invalid project user")
        return cls(id=validate_id(body["id"]), email=body["email"], created_at=body["created_at"],
                   disabled=_bool(body, "disabled", "project user state"))


@dataclass(frozen=True)
class ProjectUserPage:
    items: list[ProjectUser]
    next_cursor: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectUserPage:
        return cls(
            items=[ProjectUser.from_json(u) for u in body.get("items") or []],
            next_cursor=body.get("next") or None,
        )


@dataclass(frozen=True)
class ProjectLinkedProvider:
    """A federated identity linked to a project end user."""

    provider: str
    client_id: str
    subject: str

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectLinkedProvider:
        for key in ("provider", "client_id", "subject"):
            if not isinstance(body.get(key), str) or not body[key]:
                raise ValueError("invalid project linked provider")
        return cls(provider=body["provider"], client_id=body["client_id"], subject=body["subject"])


@dataclass(frozen=True)
class ProjectDirectoryUser:
    """One exported directory record: profile fields only, never credentials or sessions."""

    id: str
    email: str
    email_verified: bool
    created_at: str
    disabled: bool
    linked_providers: list[ProjectLinkedProvider] = field(default_factory=list)

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectDirectoryUser:
        for key in ("id", "email", "created_at"):
            if not isinstance(body.get(key), str) or not body[key]:
                raise ValueError("invalid project directory user")
        return cls(id=validate_id(body["id"]), email=body["email"],
                   email_verified=_bool(body, "email_verified", "project directory user"),
                   created_at=body["created_at"],
                   disabled=_bool(body, "disabled", "project directory user"),
                   linked_providers=[ProjectLinkedProvider.from_json(p)
                                     for p in body.get("linked_providers") or []])


@dataclass(frozen=True)
class ProjectUserExportPage:
    """One bounded page of the audited directory export; a null ``next_cursor`` ends the export."""

    items: list[ProjectDirectoryUser]
    created_before: str
    next_cursor: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectUserExportPage:
        if not isinstance(body.get("created_before"), str) or not body["created_before"]:
            raise ValueError("invalid project export page")
        return cls(
            items=[ProjectDirectoryUser.from_json(u) for u in body.get("items") or []],
            created_before=body["created_before"],
            next_cursor=body.get("next") or None,
        )


@dataclass(frozen=True)
class ProjectProviderUpdate:
    """A federated provider's credentials; an omitted ``client_secret`` keeps the stored one."""

    client_id: str
    client_secret: str = field(default="", repr=False)
    issuer: str | None = None
    team_id: str | None = None
    key_id: str | None = None

    def to_body(self) -> dict[str, Any]:
        body: dict[str, Any] = {"client_id": self.client_id}
        for key in ("client_secret", "issuer", "team_id", "key_id"):
            value = getattr(self, key)
            if value:
                body[key] = value
        return body


@dataclass(frozen=True)
class ProjectSession:
    """One live end-user session; revoking it signs the session out."""

    id: str
    user_id: str
    email: str
    created_at: str
    expires_at: str

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectSession:
        for key in ("id", "user_id", "email", "created_at", "expires_at"):
            if not isinstance(body.get(key), str) or not body[key]:
                raise ValueError("invalid project session")
        return cls(id=validate_id(body["id"]), user_id=validate_id(body["user_id"]), email=body["email"],
                   created_at=body["created_at"], expires_at=body["expires_at"])


@dataclass(frozen=True)
class ProjectSessionPage:
    items: list[ProjectSession]
    next_cursor: str | None

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> ProjectSessionPage:
        return cls(
            items=[ProjectSession.from_json(s) for s in body.get("items") or []],
            next_cursor=body.get("next") or None,
        )


@dataclass(frozen=True)
class DatabaseRequestLog:
    """A completed authorized request; ``route`` is a redacted template, never SQL text or keys."""

    id: str
    time: str
    region: str
    method: str
    route: str
    status: int
    latency_ms: float
    operations: int
    is_query: bool
    request_id: str | None = None
    statements: int = 0
    statement_errors: int = 0

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> DatabaseRequestLog:
        for key in ("id", "time", "region", "method", "route"):
            if not isinstance(body.get(key), str) or not body[key]:
                raise ValueError("invalid request log")
        status = body["status"]
        latency = body["latencyMs"]
        if not isinstance(status, int) or isinstance(status, bool) or not 100 <= status <= 599:
            raise ValueError("invalid request log status")
        if not isinstance(latency, (int, float)) or isinstance(latency, bool) or latency < 0:
            raise ValueError("invalid request log latency")
        operations = body["operations"]
        if not isinstance(operations, int) or isinstance(operations, bool) or operations < 0:
            raise ValueError("invalid request log operations")
        if not isinstance(body["isQuery"], bool):
            raise ValueError("invalid request log isQuery")
        request_id = body.get("requestId")
        if request_id is not None and not isinstance(request_id, str):
            raise ValueError("invalid request log requestId")
        return cls(validate_id(body["id"]), body["time"], body["region"], body["method"], body["route"],
                   status, float(latency), operations, body["isQuery"], request_id,
                   int(body.get("statements") or 0), int(body.get("statementErrors") or 0))


@dataclass(frozen=True)
class DatabaseRequestLogs:
    """The newest 100 recorded requests within 24 hours; recording is best effort, without a cursor."""

    database_id: str
    items: list[DatabaseRequestLog]
    truncated: bool
    recording_limited: bool

    @classmethod
    def from_json(cls, body: dict[str, Any]) -> DatabaseRequestLogs:
        if not isinstance(body.get("truncated"), bool) or not isinstance(body.get("recordingLimited"), bool):
            raise ValueError("invalid request logs flags")
        items = body.get("items")
        if not isinstance(items, list) or len(items) > 100:
            raise ValueError("invalid request logs items")
        return cls(validate_id(body["databaseId"]), [DatabaseRequestLog.from_json(i) for i in items],
                   body["truncated"], body["recordingLimited"])
