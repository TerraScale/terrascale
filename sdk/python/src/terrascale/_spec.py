"""Transport-independent request descriptions shared by the sync and async
clients, plus the retry rule."""

from __future__ import annotations

import json
import random
import re
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from typing import Any
from urllib.parse import quote

import httpx

from . import models
from .errors import CommitUncertainError, InvalidResponseError, TerraScaleError, response_request_id
from .ids import is_valid_id, validate_id
from .sql import results as sql_results

DEFAULT_BASE_URL = "https://api.terrascale.tech"
USER_AGENT_PREFIX = "terrascale-python/"
REGION_PIN = "X-TerraScale-Region-Pin"
MAX_ATTEMPTS = 4
MAX_BACKOFF = 30.0
# 503 codes that mean "try again later". storage_busy and unavailable are the
# live API's documented 503 codes (openapi/v1.json).
TRANSIENT = frozenset({
    "rate_limited", "capacity", "storage_busy", "unavailable", "storage_unavailable", "dependencies_unavailable",
    "auth_unavailable",
})
# Gateway answers (502 Bad Gateway, 504 Gateway Timeout): a replay-safe request can be repeated.
GATEWAY = frozenset({502, 504})
# Failures raised before any byte of the request reached the server, so even
# a mutation can be repeated: the server never saw it.
NOT_SENT = (httpx.ConnectError, httpx.ConnectTimeout, httpx.PoolTimeout)


@dataclass
class Call:
    method: str
    path: Sequence[str]
    parse: Callable[[httpx.Response], Any]
    params: dict[str, str] = field(default_factory=dict)
    headers: dict[str, str] = field(default_factory=dict)
    body: Any = None
    content_type: str = "application/json"
    # Only reads and read-only queries can be replayed.
    replay_safe: bool = False
    success: tuple[int, ...] = (200,)
    base_url: str | None = None
    timeout: httpx.Timeout | None = None
    resend_codes: frozenset[str] = frozenset()
    anonymous: bool = False

    def decode(self, response: httpx.Response) -> Any:
        try:
            if response.status_code not in self.success:
                raise ValueError("unexpected success status")
            if self.parse is _none and response.content:
                raise ValueError("unexpected body on a bodyless response")
            if self.parse is not _none:
                if response.headers.get("content-type", "").startswith("text/"):
                    return self.parse(response)
                body = response.json()
                if not isinstance(body, dict) or not body or "error" in body:
                    raise ValueError("expected a nonempty object")
            return self.parse(response)
        except (ValueError, KeyError, TypeError, AttributeError, IndexError, OverflowError):
            cls = InvalidResponseError if self.replay_safe else CommitUncertainError
            raise cls(response.status_code, "invalid_response", "invalid response; reconcile writes before retrying",
                      request_id=response_request_id(response.headers)) from None

    def url(self, base_url: str) -> str:
        return (self.base_url or base_url).rstrip("/") + "/" + "/".join(quote(s, safe="") for s in self.path)

    def content(self) -> bytes | None:
        if self.body is None:
            return None
        return json.dumps(self.body, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode()


def backoff(attempt: int, retry_after: float | None) -> float:
    if retry_after is not None:
        return min(retry_after, MAX_BACKOFF)
    return min(0.25 * (2**attempt) * (0.5 + random.random()), MAX_BACKOFF)


def retryable(status: int, code: str) -> bool:
    return status == 429 or status in GATEWAY or (status == 503 and code in TRANSIENT | {"http_503"})


def transport_failure(method: str, attempts: int, exc: Exception) -> TerraScaleError:
    """The error after the last transport failure, keeping its cause readable."""
    detail = str(exc).strip()
    text = f"{method} failed after {attempts} attempt{'s' if attempts != 1 else ''}: {type(exc).__name__}"
    if detail:
        text += f": {detail}"
    if isinstance(exc, NOT_SENT):
        text += " (the request was not sent)"
    return TerraScaleError(text)


def name(value: str, what: str) -> str:
    # The server validates names; the SDK only keeps them one path segment.
    if not isinstance(value, str) or value in ("", ".", "..") or "/" in value:
        raise ValueError(f"{what} {value!r} is not a valid name")
    return value


def region(value: str) -> str:
    if not isinstance(value, str) or re.fullmatch(r"[a-z][a-z0-9-]{1,31}", value) is None:
        raise ValueError("region must be a region id such as 'gru'")
    return value


def database_name(value: str) -> str:
    if not isinstance(value, str) or re.fullmatch(r"[a-z][a-z0-9_-]{0,62}", value) is None:
        raise ValueError("database names must match ^[a-z][a-z0-9_-]{0,62}$")
    return value


def database_selector(value: str) -> str:
    return validate_id(value, "database") if is_valid_id(value) else database_name(value)


def _none(_: httpx.Response) -> None:
    return None


def _page_params(limit: int | None, cursor: str | None) -> dict[str, str]:
    params: dict[str, str] = {}
    if limit:
        params["limit"] = str(limit)
    if cursor:
        params["after"] = cursor
    return params


# Databases ---------------------------------------------------------------

def list_regions() -> Call:
    return Call("GET", ["v1", "regions"], lambda r: models.Regions.from_json(r.json()), replay_safe=True,
                anonymous=True)

def _db(project: str, db: str, *extra: str) -> list[str]:
    return ["v1", "projects", project, "databases", db, *extra]


def list_databases(project: str, limit: int | None, cursor: str | None) -> Call:
    return Call("GET", ["v1", "projects", project, "databases"], lambda r: models.DatabasePage.from_json(r.json()),
                params=_page_params(limit, cursor), replay_safe=True)


def ttl(value: int, maximum: int = 31_536_000) -> int:
    if type(value) is not int or not 60 <= value <= maximum:
        raise ValueError(f"ttl_seconds must be an integer from 60 to {maximum}")
    return value


def placement(replica_regions: Sequence[str] | None, all_regions: bool | None) -> dict[str, Any]:
    if replica_regions is not None and all_regions is not None:
        raise ValueError("pass replica_regions or all_regions, not both")
    if all_regions is not None:
        if not isinstance(all_regions, bool):
            raise ValueError("all_regions must be a bool")
        return {"allRegions": all_regions}
    if replica_regions is not None:
        if isinstance(replica_regions, str):
            raise ValueError("replica_regions must be a sequence of region ids")
        return {"replicaRegions": [region(r) for r in replica_regions]}
    return {}


def create_database(project: str, db: str, home_region: str | None, ttl_seconds: int | None = None,
                    replica_regions: Sequence[str] | None = None, all_regions: bool | None = None) -> Call:
    body: dict[str, Any] = {"name": database_name(db)}
    if home_region is not None:
        body["homeRegion"] = region(home_region)
    body.update(placement(replica_regions, all_regions))
    if ttl_seconds is not None:
        body["ttlSeconds"] = ttl(ttl_seconds)
    return Call("POST", ["v1", "projects", project, "databases"],
                lambda r: models.Database.from_json(r.json()), body=body, success=(201,))


def get_database(project: str, db: str) -> Call:
    return Call("GET", _db(project, db), lambda r: models.Database.from_json(r.json()), replay_safe=True)


def rename_database(project: str, database_id: str, new_name: str) -> Call:
    return Call("PATCH", _db(project, database_selector(database_id)),
                lambda r: models.Database.from_json(r.json()), body={"name": database_name(new_name)})


def delete_database(project: str, db: str) -> Call:
    return Call("DELETE", _db(project, database_selector(db)), _none, success=(204,))


def fork_database(project: str, source: str, db: str, point_in_time: str | None, ttl_seconds: int | None) -> Call:
    body: dict[str, Any] = {"name": database_name(db)}
    route: tuple[str, ...] = ("branches",)
    if point_in_time is not None:
        body["pointInTime"] = point_in_time
        route = ("point-in-time", "restore")
    if ttl_seconds is not None:
        body["ttlSeconds"] = ttl(ttl_seconds)
    answer = models.RestoreOperation if point_in_time is not None else models.Database
    return Call("POST", _db(project, database_selector(source), *route),
                lambda r: answer.from_json(r.json()), body=body,
                success=(202,) if point_in_time is not None else (201,))


def get_restore_operation(project: str, db: str, operation: str) -> Call:
    return Call("GET", _db(project, database_selector(db), "restores", validate_id(operation, "operation")),
                lambda r: models.RestoreOperation.from_json(r.json()), replay_safe=True)


def set_database_ttl(project: str, db: str, ttl_seconds: int) -> Call:
    return Call("PUT", _db(project, database_selector(db), "ttl"),
                lambda r: models.Database.from_json(r.json()), body={"ttlSeconds": ttl(ttl_seconds)})


def set_database_placement(project: str, db: str, replica_regions: Sequence[str] | None,
                           all_regions: bool | None) -> Call:
    body = placement(replica_regions, all_regions)
    if not body:
        raise ValueError("pass replica_regions or all_regions")
    return Call("PATCH", _db(project, database_selector(db)), lambda r: models.Database.from_json(r.json()), body=body)


MOVE_TIMEOUT = httpx.Timeout(30.0, read=None)
MOVE_RESENDS = 5


def move_database_home(project: str, db: str, home_region: str) -> Call:
    return Call("POST", _db(project, database_selector(db), "move-home"),
                lambda r: models.Database.from_json(r.json()), body={"homeRegion": region(home_region)},
                timeout=MOVE_TIMEOUT, resend_codes=frozenset({"move_unresolved"}))


def clear_database_ttl(project: str, db: str) -> Call:
    return Call("DELETE", _db(project, database_selector(db), "ttl"), lambda r: models.Database.from_json(r.json()))


def branch_database(project: str, db: str, branch: str) -> Call:
    return Call("POST", _db(project, db, "branches"),
                lambda r: models.Database.from_json(r.json()), body={"name": database_name(branch)}, success=(201,))


# SQL ---------------------------------------------------------------------

def export_database(project: str, db: str) -> Call:
    return Call("GET", _db(project, db, "export"), _none, headers={"Accept": "text/plain"}, replay_safe=True)


def execute(project: str, db: str, statements: list[dict[str, Any]]) -> Call:
    return Call("POST", _db(project, db, "sql", "execute"), lambda r: sql_results(r.json(), len(statements)),
                body={"statements": statements})
