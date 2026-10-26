"""Typed project usage, database usage and observed workload reports."""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any, Literal

from ._spec import Call, name
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
    collections: int
    documents: int
    reads: int
    writes: int
    period: UsagePeriod
    series: list[UsagePoint]

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> DatabaseUsage:
        return cls(b["databaseId"], int(b["storageBytes"]), int(b["logicalBytes"]), int(b["collections"]),
                   int(b["documents"]), int(b["reads"]), int(b["writes"]), UsagePeriod.from_json(b["period"]),
                   [UsagePoint.from_json(p) for p in b["series"]])


@dataclass(frozen=True)
class ProjectUsage:
    project_id: str
    databases: int
    storage_bytes: int
    logical_bytes: int
    documents: int
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
                   int(b["documents"]), int(b["reads"]), int(b["writes"]), UsagePeriod.from_json(b["period"]),
                   [UsagePoint.from_json(p) for p in b["series"]], [DatabaseUsage.from_json(x) for x in b["items"]],
                   UsageMonth.from_json(b["month"]), int(b["fileBytes"]) if b.get("fileBytes") is not None else None, b)


@dataclass(frozen=True)
class QueryShape:
    id: str
    collection: str
    shape: str
    queries: int
    total_latency_ms: float
    max_latency_ms: float
    rows_scanned: int
    last_seen_at: str
    latency_buckets: list[int] | None
    p95_latency_ms: float | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> QueryShape:
        buckets, p95 = b.get("latencyBuckets"), b.get("p95LatencyMs")
        return cls(b["id"], b["collection"], b["shape"], int(b["queries"]), float(b["totalLatencyMs"]),
                   float(b["maxLatencyMs"]), int(b["rowsScanned"]), b["lastSeenAt"],
                   [int(x) for x in buckets] if buckets is not None else None, float(p95) if p95 is not None else None)


class _Requests:
    def __init__(self, project: str):
        self._path = ["v1", "projects", validate_id(project, "project")]

    def _call(self, database: str | None, suffix: str, parse: Callable[[dict[str, Any]], Any],
              query: UsageQuery | None = None) -> Call:
        path = self._path if database is None else [*self._path, "databases", name(database, "database")]
        return Call("GET", [*path, suffix], lambda r: parse(r.json()),
                    params=query.params() if query is not None else {}, replay_safe=True)

    def _shapes(self, database: str) -> Call:
        return self._call(database, "query-shapes", lambda b: [QueryShape.from_json(x) for x in b["items"]])


class Reporting(_Requests):
    """Read-only reporting through an existing synchronous project client."""

    def __init__(self, client: TerraScale):
        super().__init__(client.project)
        self._client = client

    def get_project_usage(self, query: UsageQuery | None = None) -> ProjectUsage:
        return self._client._send(self._call(None, "usage", ProjectUsage.from_json, query))

    def get_database_usage(self, database: str, query: UsageQuery | None = None) -> DatabaseUsage:
        return self._client._send(self._call(database, "usage", DatabaseUsage.from_json, query))

    def list_query_shapes(self, database: str) -> list[QueryShape]:
        return self._client._send(self._shapes(database))


class AsyncReporting(_Requests):
    """The asynchronous twin of Reporting, with identical arguments and results."""

    def __init__(self, client: AsyncTerraScale):
        super().__init__(client.project)
        self._client = client

    async def get_project_usage(self, query: UsageQuery | None = None) -> ProjectUsage:
        return await self._client._send(self._call(None, "usage", ProjectUsage.from_json, query))

    async def get_database_usage(self, database: str, query: UsageQuery | None = None) -> DatabaseUsage:
        return await self._client._send(self._call(database, "usage", DatabaseUsage.from_json, query))

    async def list_query_shapes(self, database: str) -> list[QueryShape]:
        return await self._client._send(self._shapes(database))
