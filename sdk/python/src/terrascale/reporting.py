"""Typed project and database usage and analytics reports."""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any, Literal

import httpx

from ._spec import Call, name, region
from .ids import validate_id

if TYPE_CHECKING:
    from .aio import AsyncTerraScale
    from .client import TerraScale


@dataclass(frozen=True)
class UsageQuery:
    """UTC reporting buckets; omitted times retain the server's default period."""

    granularity: Literal["hour", "day"] = "hour"
    from_time: str | None = None
    to_time: str | None = None

    def params(self) -> dict[str, str]:
        if self.granularity not in ("hour", "day"):
            raise ValueError("granularity must be hour or day")
        return {k: v for k, v in {"granularity": self.granularity, "from": self.from_time, "to": self.to_time}.items()
                if v is not None}


ANALYTICS_RANGES = ("1h", "24h", "7d", "30d")


def _counts(values: Any) -> dict[str, int]:
    if not isinstance(values, dict):
        raise ValueError("expected a counter map")
    return {str(key): int(value) for key, value in values.items()}


@dataclass(frozen=True)
class AnalyticsQuery:
    """A fixed dashboard range; ``database`` narrows project analytics to one catalog id."""

    range: Literal["1h", "24h", "7d", "30d"] | None = None
    database: str | None = None
    region: str | None = None

    def params(self, *, project: bool) -> dict[str, str]:
        if self.range is not None and self.range not in ANALYTICS_RANGES:
            raise ValueError("range must be 1h, 24h, 7d or 30d")
        params: dict[str, str] = {}
        if self.range is not None:
            params["range"] = self.range
        if project and self.database is not None:
            params["database"] = validate_id(self.database, "database")
        if self.region is not None:
            params["region"] = region(self.region)
        return params


@dataclass(frozen=True)
class UsagePoint:
    start: str
    reads: int
    writes: int

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> UsagePoint:
        return cls(b["start"], int(b["reads"]), int(b["writes"]))


@dataclass(frozen=True)
class UsagePeriod:
    from_time: str
    to_time: str
    granularity: str
    reads: int
    writes: int

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> UsagePeriod:
        return cls(b["from"], b["to"], b["granularity"], int(b["reads"]), int(b["writes"]))


@dataclass(frozen=True)
class UsageMonth:
    """Billed operation units, distinct from best-effort dashboard counters."""

    month: str
    read_units: int
    write_units: int
    units: int
    organization_units: int

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> UsageMonth:
        return cls(b["month"], int(b["readUnits"]), int(b["writeUnits"]), int(b["units"]), int(b["organizationUnits"]))


@dataclass(frozen=True)
class DatabaseUsage:
    database_id: str
    storage_bytes: int
    logical_bytes: int
    reads: int
    writes: int
    period: UsagePeriod
    series: list[UsagePoint]

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> DatabaseUsage:
        return cls(b["databaseId"], int(b["storageBytes"]), int(b["logicalBytes"]),
                   int(b["reads"]), int(b["writes"]), UsagePeriod.from_json(b["period"]),
                   [UsagePoint.from_json(p) for p in b["series"]])


@dataclass(frozen=True)
class ProjectUsage:
    project_id: str
    databases: int
    storage_bytes: int
    logical_bytes: int
    reads: int
    writes: int
    period: UsagePeriod
    series: list[UsagePoint]
    items: list[DatabaseUsage]
    month: UsageMonth
    file_bytes: int | None
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> ProjectUsage:
        return cls(b["projectId"], int(b["databases"]), int(b["storageBytes"]), int(b["logicalBytes"]),
                   int(b["reads"]), int(b["writes"]), UsagePeriod.from_json(b["period"]),
                   [UsagePoint.from_json(p) for p in b["series"]], [DatabaseUsage.from_json(x) for x in b["items"]],
                   UsageMonth.from_json(b["month"]), int(b["fileBytes"]) if b.get("fileBytes") is not None else None, b)


@dataclass(frozen=True)
class LatencyPercentiles:
    p50_ms: float | None
    p95_ms: float | None
    p99_ms: float | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> LatencyPercentiles:
        values = []
        for key in ("p50Ms", "p95Ms", "p99Ms"):
            value = b.get(key)
            values.append(None if value is None else float(value))
        return cls(*values)


@dataclass(frozen=True)
class Bandwidth:
    bytes_sent: int
    bytes_received: int

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> Bandwidth:
        return cls(int(b["bytesSent"]), int(b["bytesReceived"]))


@dataclass(frozen=True)
class AnalyticsPoint:
    start: str
    requests: dict[str, int]
    operations: dict[str, int]
    errors: int
    latency: LatencyPercentiles
    storage_bytes: int | None
    client_bandwidth: Bandwidth
    replication_bandwidth: Bandwidth

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> AnalyticsPoint:
        return cls(b["start"], _counts(b["requests"]), _counts(b["operations"]), int(b["errors"]),
                   LatencyPercentiles.from_json(b["latency"]),
                   int(b["storageBytes"]) if b.get("storageBytes") is not None else None,
                   Bandwidth.from_json(b["clientBandwidth"]), Bandwidth.from_json(b["replicationBandwidth"]))


@dataclass(frozen=True)
class DatabaseAnalytics:
    database_id: str
    database_name: str
    region: str
    series: list[AnalyticsPoint]

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> DatabaseAnalytics:
        return cls(validate_id(b["databaseId"], "database"), b["databaseName"], b["region"],
                   [AnalyticsPoint.from_json(p) for p in b["series"]])


@dataclass(frozen=True)
class ProjectAnalytics:
    """Best-effort observations of client requests, operations, errors, latency and bandwidth."""

    project_id: str
    range: str
    from_time: str
    to_time: str
    bucket_seconds: int
    items: list[DatabaseAnalytics]
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> ProjectAnalytics:
        return cls(validate_id(b["projectId"], "project"), b["range"], b["from"], b["to"], int(b["bucketSeconds"]),
                   [DatabaseAnalytics.from_json(x) for x in b["items"]], b)


class _Requests:
    def __init__(self, project: str):
        self._path = ["v1", "projects", validate_id(project, "project")]

    def _call(self, database: str | None, suffix: str, parse: Callable[[httpx.Response], Any],
              params: dict[str, str] | None = None) -> Call:
        path = self._path if database is None else [*self._path, "databases", name(database, "database")]
        return Call("GET", [*path, suffix], lambda r: parse(r), params=params or {}, replay_safe=True)

    def _usage_params(self, query: UsageQuery | None) -> dict[str, str]:
        return query.params() if query is not None else {}

    def _analytics_call(self, database: str | None, query: AnalyticsQuery | None) -> Call:
        return self._call(database, "analytics", lambda r: ProjectAnalytics.from_json(r.json()),
                          query.params(project=database is None) if query is not None else {})

    def _metrics_call(self) -> Call:
        return self._call(None, "metrics", lambda r: r.text)


class Reporting(_Requests):
    """Read-only reporting through an existing synchronous project client."""

    def __init__(self, client: TerraScale):
        super().__init__(client.project)
        self._client = client

    def get_project_usage(self, query: UsageQuery | None = None) -> ProjectUsage:
        return self._client._send(self._call(None, "usage", lambda r: ProjectUsage.from_json(r.json()),
                                             self._usage_params(query)))

    def get_database_usage(self, database: str, query: UsageQuery | None = None) -> DatabaseUsage:
        return self._client._send(self._call(database, "usage", lambda r: DatabaseUsage.from_json(r.json()),
                                             self._usage_params(query)))

    def get_project_analytics(self, query: AnalyticsQuery | None = None) -> ProjectAnalytics:
        """Read project analytics; ``query.database`` narrows the series to one catalog id."""
        return self._client._send(self._analytics_call(None, query))

    def get_database_analytics(self, database: str, query: AnalyticsQuery | None = None) -> ProjectAnalytics:
        """Read one database's analytics; ``query.database`` is not part of database routes."""
        if query is not None and query.database is not None:
            raise ValueError("database is only valid on project analytics")
        return self._client._send(self._analytics_call(database, query))

    def get_project_metrics(self) -> str:
        """The project's analytics as Prometheus 0.0.4 text exposition."""
        return self._client._send(self._metrics_call())


class AsyncReporting(_Requests):
    """The asynchronous twin of Reporting, with identical arguments and results."""

    def __init__(self, client: AsyncTerraScale):
        super().__init__(client.project)
        self._client = client

    async def get_project_usage(self, query: UsageQuery | None = None) -> ProjectUsage:
        return await self._client._send(self._call(None, "usage", lambda r: ProjectUsage.from_json(r.json()),
                                                   self._usage_params(query)))

    async def get_database_usage(self, database: str, query: UsageQuery | None = None) -> DatabaseUsage:
        return await self._client._send(self._call(database, "usage", lambda r: DatabaseUsage.from_json(r.json()),
                                                   self._usage_params(query)))

    async def get_project_analytics(self, query: AnalyticsQuery | None = None) -> ProjectAnalytics:
        """Read project analytics; ``query.database`` narrows the series to one catalog id."""
        return await self._client._send(self._analytics_call(None, query))

    async def get_database_analytics(self, database: str, query: AnalyticsQuery | None = None) -> ProjectAnalytics:
        """Read one database's analytics; ``query.database`` is not part of database routes."""
        if query is not None and query.database is not None:
            raise ValueError("database is only valid on project analytics")
        return await self._client._send(self._analytics_call(database, query))

    async def get_project_metrics(self) -> str:
        """The project's analytics as Prometheus 0.0.4 text exposition."""
        return await self._client._send(self._metrics_call())
