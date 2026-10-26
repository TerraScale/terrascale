"""Database snapshots and point-in-time recovery, using the client's transport."""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any, Literal

from ._spec import Call, database_name, database_selector
from .ids import validate_id

if TYPE_CHECKING:
    from .aio import AsyncDatabase
    from .client import Database

Destination = Literal["platform", "customer"]


@dataclass(frozen=True)
class Backup:
    id: str
    database_id: str
    status: str
    trigger: str
    size_bytes: int
    created_at: str
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> Backup:
        return cls(b["id"], b["databaseId"], b["status"], b["trigger"], int(b["sizeBytes"]), b["createdAt"], b)


@dataclass(frozen=True)
class BackupPage:
    items: list[Backup]
    next_cursor: str | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> BackupPage:
        return cls([Backup.from_json(x) for x in b["items"]], b["next"])


@dataclass(frozen=True)
class RestoreWindow:
    earliest: str
    latest: str

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> RestoreWindow:
        return cls(b["earliest"], b["latest"])


@dataclass(frozen=True)
class PointInTimePlan:
    window: RestoreWindow
    point_in_time: str
    snapshot_at: str
    latest_segment_at: str | None
    segment_count: int
    history_check: str

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> PointInTimePlan:
        return cls(RestoreWindow.from_json(b["window"]), b["pointInTime"], b["snapshotAt"],
                   b["latestSegmentAt"], int(b["segmentCount"]), b["historyCheck"])


@dataclass(frozen=True)
class RestoredDatabase:
    """Management acknowledgement; the server does not return a serving region."""

    id: str
    project_id: str
    name: str
    branched_from: str | None
    created_at: str
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> RestoredDatabase:
        return cls(b["id"], b["projectId"], b["name"], b["branchedFrom"], b["createdAt"], b)


class _Requests:
    def __init__(self, project: str, database: str):
        self._path = ["v1", "projects", validate_id(project, "project"), "databases", database_selector(database)]

    def _call(self, method: str, suffix: list[str], parse: Callable[[dict[str, Any]], Any], *,
              body: dict[str, Any] | None = None, params: dict[str, str] | None = None) -> Call:
        return Call(method, [*self._path, *suffix], lambda r: parse(r.json()), body=body,
                    params=params or {}, replay_safe=method == "GET" or suffix == ["point-in-time", "plan"],
                    success=(201,) if method == "POST" and suffix != ["point-in-time", "plan"] else (200,))

    def _list(self, limit: int | None, cursor: str | None) -> Call:
        params = {} if cursor is None else {"after": cursor}
        if limit is not None:
            if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 100:
                raise ValueError("limit must be an integer between 1 and 100")
            params["limit"] = str(limit)
        return self._call("GET", ["backups"], BackupPage.from_json, params=params)

    def _restore(self, backup: str, new_name: str | None) -> Call:
        body = {} if new_name is None else {"name": database_name(new_name)}
        return self._call("POST", ["backups", validate_id(backup, "backup"), "restore"], RestoredDatabase.from_json, body=body)

    def _recovery(self, operation: str, point_in_time: str, destination: Destination, new_name: str | None = None) -> Call:
        body = {"pointInTime": point_in_time, **self._destination(destination)}
        if new_name is not None:
            body["name"] = database_name(new_name)
        parse = PointInTimePlan.from_json if operation == "plan" else RestoredDatabase.from_json
        return self._call("POST", ["point-in-time", operation], parse, body=body)

    @staticmethod
    def _destination(destination: Destination) -> dict[str, str]:
        if destination not in ("platform", "customer"):
            raise ValueError("destination must be platform or customer")
        return {"destination": destination}


class DatabaseBackups(_Requests):
    """Recovery operations scoped to an existing synchronous database handle."""

    def __init__(self, database: Database):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    def create_backup(self) -> Backup:
        return self._client._send(self._call("POST", ["backups"], Backup.from_json))

    def list_backups(self, *, limit: int | None = None, cursor: str | None = None) -> BackupPage:
        return self._client._send(self._list(limit, cursor))

    def restore_backup(self, backup: str, *, name: str | None = None) -> RestoredDatabase:
        return self._client._send(self._restore(backup, name))

    def get_restore_window(self, *, destination: Destination = "platform") -> RestoreWindow:
        return self._client._send(self._call("GET", ["point-in-time"], RestoreWindow.from_json,
                                            params=self._destination(destination)))

    def plan_point_in_time(self, point_in_time: str, *, destination: Destination = "platform") -> PointInTimePlan:
        return self._client._send(self._recovery("plan", point_in_time, destination))

    def restore_point_in_time(self, point_in_time: str, *, name: str | None = None,
                              destination: Destination = "platform") -> RestoredDatabase:
        return self._client._send(self._recovery("restore", point_in_time, destination, name))


class AsyncDatabaseBackups(_Requests):
    """The asynchronous twin of DatabaseBackups; methods have identical arguments."""

    def __init__(self, database: AsyncDatabase):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    async def create_backup(self) -> Backup:
        return await self._client._send(self._call("POST", ["backups"], Backup.from_json))

    async def list_backups(self, *, limit: int | None = None, cursor: str | None = None) -> BackupPage:
        return await self._client._send(self._list(limit, cursor))

    async def restore_backup(self, backup: str, *, name: str | None = None) -> RestoredDatabase:
        return await self._client._send(self._restore(backup, name))

    async def get_restore_window(self, *, destination: Destination = "platform") -> RestoreWindow:
        return await self._client._send(self._call("GET", ["point-in-time"], RestoreWindow.from_json,
                                                  params=self._destination(destination)))

    async def plan_point_in_time(self, point_in_time: str, *, destination: Destination = "platform") -> PointInTimePlan:
        return await self._client._send(self._recovery("plan", point_in_time, destination))

    async def restore_point_in_time(self, point_in_time: str, *, name: str | None = None,
                                    destination: Destination = "platform") -> RestoredDatabase:
        return await self._client._send(self._recovery("restore", point_in_time, destination, name))
