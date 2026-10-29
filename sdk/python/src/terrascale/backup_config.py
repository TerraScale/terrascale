"""Typed backup scheduling and redacted requests for credential-bearing resources."""

from __future__ import annotations

from collections.abc import Callable, Sequence
from dataclasses import dataclass
from typing import TYPE_CHECKING, Any

import httpx

from ._spec import Call, name
from .errors import APIError, CommitUncertainError, InvalidResponseError, TerraScaleError, response_request_id
from .ids import validate_id
from .tooling import _integer, _text

if TYPE_CHECKING:
    from .aio import AsyncDatabase, AsyncTerraScale
    from .client import Database, TerraScale


def _optional_text(value: Any) -> str | None:
    return None if value is None else _text(value)


def _boolean(value: Any) -> bool:
    if type(value) is not bool:
        raise ValueError("expected a boolean")
    return value


class _ConfigCall(Call):
    def __repr__(self) -> str:
        return "_ConfigCall(<redacted>)"

    def decode(self, response: httpx.Response) -> Any:
        return self.parse(response)


def _call(method: str, path: Sequence[str], parse: Callable[[Any], Any], *, body: Any = None,
          params: dict[str, str] | None = None, success: int = 200) -> Call:
    def decode(response: httpx.Response) -> Any:
        try:
            if response.status_code != success:
                raise ValueError("unexpected success status")
            if success == 204:
                if response.content:
                    raise ValueError("unexpected response body")
                return None
            return parse(response.json())
        except (ValueError, KeyError, TypeError, AttributeError, IndexError, OverflowError):
            error = InvalidResponseError if method == "GET" else CommitUncertainError
            raise error(response.status_code, "invalid_response", "invalid configuration response; reconcile writes",
                        request_id=response_request_id(response.headers)) from None
    return _ConfigCall(method, path, decode, body=body, params=params or {}, replay_safe=method == "GET", success=(success,))


def _redacted(error: TerraScaleError) -> TerraScaleError:
    # Neither arbitrary server text/codes nor chained transport errors may retain credentials.
    if not isinstance(error, APIError):
        return TerraScaleError("configuration request failed; diagnostics redacted")
    safe_codes = {"invalid_response", "invalid_request", "invalid_key", "forbidden_scope", "forbidden_role", "not_found",
                  "rate_limited", "capacity", "storage_unavailable", "dependencies_unavailable", "commit_uncertain",
                  "write_outcome_unknown", "invalid_query", "invalid_cursor", "webhook_limit", "name_taken", "conflict",
                  "unknown_database", "unknown_route", "storage_busy", "unavailable", "misdirected_request", "wrong_region"}
    code = error.code if error.code in safe_codes else f"http_{error.status}"
    return type(error)(error.status, code, "configuration request failed; reconcile uncertain writes",
                       request_id=error.request_id, retry_after=error.retry_after)


def _send(client: TerraScale, call: Call) -> Any:
    try:
        return client._send(call)
    except TerraScaleError as error:
        raise _redacted(error) from None


async def _asend(client: AsyncTerraScale, call: Call) -> Any:
    try:
        return await client._send(call)
    except TerraScaleError as error:
        raise _redacted(error) from None


@dataclass(frozen=True)
class BackupPolicy:
    full_backup_cron: str | None
    retention_days: int
    disabled: bool
    updated_at: str | None

    @classmethod
    def from_json(cls, b: dict[str, Any]) -> BackupPolicy:
        retention = _integer(b["retentionDays"], 32)
        if not 1 <= retention <= 35:
            raise ValueError("retention outside contract range")
        return cls(_optional_text(b["fullBackupCron"]), retention, _boolean(b["disabled"]),
                   _optional_text(b["updatedAt"]))


@dataclass(frozen=True)
class BackupPolicyUpdate:
    full_backup_cron: str | None
    retention_days: int
    disabled: bool = False

    def body(self) -> dict[str, Any]:
        cron = _optional_text(self.full_backup_cron)
        if type(self.retention_days) is not int or not 1 <= self.retention_days <= 35:
            raise ValueError("retention_days must be an integer from 1 to 35")
        return {"fullBackupCron": cron, "retentionDays": self.retention_days, "disabled": _boolean(self.disabled)}


class _Policies:
    def __init__(self, project: str, database: str):
        self._path = ["v1", "projects", validate_id(project, "project"), "databases", name(database, "database"), "backup-policy"]

    def _get(self) -> Call:
        return _call("GET", self._path, BackupPolicy.from_json)

    def _put(self, policy: BackupPolicyUpdate) -> Call:
        return _call("PUT", self._path, BackupPolicy.from_json, body=policy.body())


class DatabaseBackupPolicy(_Policies):
    def __init__(self, database: Database):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    def get_backup_policy(self) -> BackupPolicy:
        return _send(self._client, self._get())

    def set_backup_policy(self, policy: BackupPolicyUpdate) -> BackupPolicy:
        return _send(self._client, self._put(policy))


class AsyncDatabaseBackupPolicy(_Policies):
    def __init__(self, database: AsyncDatabase):
        super().__init__(database._c.project, database.name)
        self._client = database._c

    async def get_backup_policy(self) -> BackupPolicy:
        return await _asend(self._client, self._get())

    async def set_backup_policy(self, policy: BackupPolicyUpdate) -> BackupPolicy:
        return await _asend(self._client, self._put(policy))
