"""Transport-independent request descriptions shared by the sync and async
clients, plus the retry rule."""

from __future__ import annotations

import base64
import json
import random
import re
import threading
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field
from typing import Any, Literal
from urllib.parse import quote

import httpx

from . import models
from .errors import CommitUncertainError, InvalidResponseError, SerializationConflictError, TerraScaleError, response_request_id
from .ids import is_valid_id, validate_id
from .sql import results as sql_results

DEFAULT_BASE_URL = "https://api.terrascale.tech"
USER_AGENT_PREFIX = "terrascale-python/"
REGION_PIN = "X-TerraScale-Region-Pin"
BOOKMARK = "TerraScale-Bookmark"
CONSISTENCY = "TerraScale-Consistency"
Consistency = Literal["fast", "strong"]
CONSISTENCY_MODES: tuple[str, ...] = ("fast", "strong")
MAX_BOOKMARK_LENGTH = 1024
MAX_BOOKMARK_REGION_BYTES = 64
BOOKMARK_FORMAT = 1
MAX_ATTEMPTS = 4
MAX_BACKOFF = 30.0
API_SERVICE = ("api.terrascale.tech", "the TerraBase API")
AUTH_SERVICE = ("auth.terrascale.tech", "ts-auth")
DEFAULT_ISSUER = "https://auth.terrascale.tech/terrascale"
_ORIGIN_CHARACTERS = frozenset("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-.:[]/")


def _digits(text: str) -> bool:
    return text != "" and all(c in "0123456789" for c in text)


def _ipv4(host: str) -> bool:
    octets = host.split(".")
    return len(octets) == 4 and all(
        _digits(octet) and len(octet) <= 3 and not (len(octet) > 1 and octet[0] == "0") and int(octet) <= 255
        for octet in octets)


def _ipv6(text: str) -> list[int] | None:
    halves = text.split("::")
    if not text or len(halves) > 2:
        return None
    parts = [half.split(":") if half else [] for half in halves]
    count = sum(len(half) for half in parts)
    if len(parts) == 1 and count != 8 or len(parts) == 2 and count > 7:
        return None
    if not all(1 <= len(group) <= 4 and all(c in "0123456789abcdef" for c in group) for half in parts for group in half):
        return None
    tail = parts[1] if len(parts) == 2 else []
    return [int(group, 16) for group in parts[0] + ["0"] * (8 - count) + tail]


def _name(host: str) -> bool:
    if len(host) > 253 or host in ("loopback", "0.0.0.0"):
        return False
    labels = host.split(".")
    if not all(0 < len(label) <= 63 and label[0] != "-" and label[-1] != "-" and not label.startswith("xn--")
               and all(c in "abcdefghijklmnopqrstuvwxyz0123456789-" for c in label) for label in labels):
        return False
    last = labels[-1]
    numeric = _digits(last) or last.startswith("0x") and all(c in "0123456789abcdef" for c in last[2:])
    return _ipv4(host) if numeric else True


def _loopback(host: str) -> bool:
    if host == "localhost" or host.startswith("127.") and _ipv4(host):
        return True
    return host.startswith("[") and _ipv6(host[1:-1]) == [0, 0, 0, 0, 0, 0, 0, 1]


def _strict(raw: object) -> tuple[str, str, str] | None:
    if not isinstance(raw, str) or len(raw) > 2048 or not all(c in _ORIGIN_CHARACTERS for c in raw):
        return None
    scheme, split, rest = raw.lower().partition("://")
    rest = rest.removesuffix("/")
    if not split or scheme not in ("https", "http") or "/" in rest:
        return None
    host, after = rest, ""
    if rest.startswith("["):
        end = rest.find("]")
        groups = _ipv6(rest[1:end]) if end >= 0 else None
        if groups is None or groups in ([0] * 8, [0, 0, 0, 0, 0, 0xFFFF, 0, 0]):
            return None
        host, after = rest[:end + 1], rest[end + 1:]
    elif ":" in rest:
        host, after = rest[:rest.index(":")], rest[rest.index(":"):]
    if not host.startswith("[") and not _name(host):
        return None
    if not after:
        return scheme, host, ""
    port = after[1:]
    if after[0] != ":" or not _digits(port) or len(port) > 5 or port[0] == "0" or int(port) > 65535:
        return None
    return scheme, host, "" if (scheme, port) in (("https", "443"), ("http", "80")) else port


def _serves(service: tuple[str, str], host: str) -> bool:
    if host != "terrascale.tech" and not host.endswith(".terrascale.tech"):
        return True
    own = service[0]
    label = host.removesuffix("." + own)
    return host == own or label != host and label != "" and "." not in label


def _sent_as(origin: str) -> tuple[str, int | None] | None:
    try:
        parsed = httpx.URL(origin)
        return parsed.host, parsed.port
    except (httpx.InvalidURL, UnicodeError):
        return None


def service_origin(raw: str, what: str, service: tuple[str, str]) -> str:
    checked = _strict(raw)
    if checked is None:
        raise ValueError(f"{what} must be exactly scheme://host[:port], such as https://{service[0]}")
    scheme, host, port = checked
    if scheme == "http" and not _loopback(host):
        raise ValueError(f"{what} must use https; plain http is allowed only for localhost, 127.0.0.0/8 and [::1]")
    if not _serves(service, host):
        raise ValueError(f"{what} names a TerraScale host that does not serve {service[1]}")
    origin = f"{scheme}://{host}" + (f":{port}" if port else "")
    if _sent_as(origin) != (host.strip("[]"), int(port) if port else None):
        raise ValueError(f"{what} is read as another host or port by the HTTP client")
    return origin


def issuer_parts(issuer: str | None) -> tuple[str, str]:
    scheme, _, rest = (issuer or DEFAULT_ISSUER).partition("://")
    authority, _, tenant = rest.partition("/")
    base = service_origin(f"{scheme}://{authority}", "issuer", AUTH_SERVICE)
    if tenant != "terrascale" and not is_valid_id(tenant):
        raise ValueError("issuer must be a ts-auth origin followed by one tenant segment: terrascale or a project id")
    return base, "/" + tenant


TRANSIENT = frozenset({
    "rate_limited", "capacity", "storage_busy", "unavailable", "storage_unavailable", "dependencies_unavailable",
    "auth_unavailable",
})
GATEWAY = frozenset({502, 504})
NOT_SENT = (httpx.ConnectError, httpx.ConnectTimeout, httpx.PoolTimeout)


def consistency(value: str | None) -> Consistency | None:
    if value is None:
        return None
    if value == "fast":
        return "fast"
    if value == "strong":
        return "strong"
    raise ValueError("consistency must be 'fast' or 'strong'")


def _invalid_bookmark() -> ValueError:
    return ValueError("bookmark must be one TerraScale-Bookmark value returned by TerraScale")


def _read_varint(data: bytes, at: int) -> tuple[int, int]:
    value = 0
    for index in range(10):
        if at + index >= len(data):
            break
        current = data[at + index]
        if index == 9 and current > 1:
            break
        value |= (current & 0x7F) << (7 * index)
        if current < 0x80:
            return value, at + index + 1
    raise _invalid_bookmark()


def _write_varint(out: bytearray, value: int) -> None:
    while value >= 0x80:
        out.append((value & 0x7F) | 0x80)
        value >>= 7
    out.append(value)


def decode_bookmark(text: str) -> dict[str, int]:
    if not isinstance(text, str) or len(text) > MAX_BOOKMARK_LENGTH or re.fullmatch(r"[A-Za-z0-9_-]*", text) is None:
        raise _invalid_bookmark()
    if not text:
        return {}
    try:
        data = base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))
    except ValueError:
        raise _invalid_bookmark() from None
    if not data or data[0] != BOOKMARK_FORMAT:
        raise _invalid_bookmark()
    count, at = _read_varint(data, 1)
    if count > len(data) - at:
        raise _invalid_bookmark()
    entries: dict[str, int] = {}
    previous: str | None = None
    for _ in range(count):
        length, at = _read_varint(data, at)
        if length == 0 or length > MAX_BOOKMARK_REGION_BYTES or length > len(data) - at:
            raise _invalid_bookmark()
        try:
            region = data[at:at + length].decode()
        except UnicodeDecodeError:
            raise _invalid_bookmark() from None
        at += length
        sequence, at = _read_varint(data, at)
        if sequence == 0 or sequence >= 1 << 63 or previous is not None and region <= previous:
            raise _invalid_bookmark()
        entries[region] = sequence
        previous = region
    if at != len(data):
        raise _invalid_bookmark()
    return entries


def encode_bookmark(entries: Mapping[str, int]) -> str:
    if not entries:
        return ""
    out = bytearray([BOOKMARK_FORMAT])
    _write_varint(out, len(entries))
    for region in sorted(entries):
        raw = region.encode()
        _write_varint(out, len(raw))
        out += raw
        _write_varint(out, entries[region])
    text = base64.urlsafe_b64encode(bytes(out)).decode().rstrip("=")
    if len(text) > MAX_BOOKMARK_LENGTH:
        raise _invalid_bookmark()
    return text


def bookmark(value: str) -> str:
    decode_bookmark(value)
    return value


def merge_bookmarks(current: str | None, observed: str) -> str:
    merged = decode_bookmark(current) if current else {}
    for region, sequence in decode_bookmark(observed).items():
        merged[region] = max(merged.get(region, 0), sequence)
    return encode_bookmark(merged)


class Bookmarks:
    def __init__(self) -> None:
        self._known: dict[str, str] = {}
        self._lock = threading.Lock()

    def get(self, database: str) -> str | None:
        with self._lock:
            return self._known.get(database)

    def snapshot(self) -> dict[str, str]:
        with self._lock:
            return dict(self._known)

    def observe(self, database: str, value: str | None) -> str | None:
        with self._lock:
            current = self._known.get(database)
            if value is None:
                return current
            merged = merge_bookmarks(current, value)
            if merged:
                self._known[database] = merged
            return merged or current


def causality(consistency_mode: str | None, bookmark_value: str | None) -> dict[str, str]:
    headers: dict[str, str] = {}
    mode = consistency(consistency_mode)
    if mode is not None:
        headers[CONSISTENCY] = mode
    if bookmark_value is not None:
        headers[BOOKMARK] = bookmark(bookmark_value)
    return headers


@dataclass(frozen=True, slots=True)
class Next:
    """What a client does with one refused response."""

    kind: Literal["reject", "refresh", "sleep", "raise"]
    seconds: float = 0.0
    attempt: int = 0
    conflicts: int = 0


def after_transport(call: Call, exc: Exception, attempt: int, max_attempts: int) -> float:
    """Seconds to wait, or the terminal error for this transport failure. ``attempt`` is already incremented."""
    if not call.replay_safe and not isinstance(exc, NOT_SENT):
        raise CommitUncertainError(0, "commit_uncertain", "transport failure; the write may have committed") from exc
    if isinstance(exc, httpx.DecodingError):
        raise InvalidResponseError(0, "invalid_response", "invalid response encoding") from exc
    if attempt >= max_attempts:
        raise transport_failure(call.method, attempt, exc) from exc
    return backoff(attempt, None)


def after_status(call: Call, err: Any, *, attempt: int, conflicts: int, max_attempts: int, refreshed: bool,
                 direct: bool, has_source: bool, project_key: bool) -> Next:
    """Classify a non-2xx response. SQL conflict retries stop after two; pass ``conflicts`` >= 2 to disable them."""
    if isinstance(err, CommitUncertainError):
        return Next("raise")
    if err.status == 401 and err.code in {"key_revoked", "key_expired"}:
        return Next("reject")
    if err.status == 401 and has_source and not refreshed and not direct and not project_key:
        return Next("refresh")
    if isinstance(err, SerializationConflictError) and conflicts < 2:
        conflicts += 1
        return Next("sleep", backoff(conflicts, None), attempt, conflicts)
    attempt += 1
    if call.replay_safe and attempt < max_attempts and retryable(err.status, err.code):
        return Next("sleep", backoff(attempt, err.retry_after), attempt, conflicts)
    return Next("raise", attempt=attempt, conflicts=conflicts)


def project_bearer(api_key: str | None, call: Call) -> str | None:
    """An auth route addressed by project id is sent with the API key, not an exchanged access token."""
    if (api_key and call.base_url is not None and len(call.path) > 1 and call.path[0] == "v1"
            and is_valid_id(call.path[1])):
        return api_key
    return None


def bearer_token(value: object) -> str:
    if not isinstance(value, str) or re.fullmatch(r"[!-~]+", value) is None:
        raise ValueError("invalid bearer token")
    return value


def request_headers(base: Mapping[str, str], call: Call, bookmarks: Bookmarks, *, direct: bool, token: str,
                    has_body: bool) -> dict[str, str]:
    headers = dict(call.headers) if direct else {**base, **call.headers, **call.causal_headers(bookmarks)}
    if not direct and not call.anonymous:
        headers["Authorization"] = f"Bearer {token}"
    if has_body:
        headers["Content-Type"] = call.content_type
    if any(not isinstance(value, str) or re.fullmatch(r"(?:[!-~](?:[\t -~]*[!-~])?)?", value) is None
           for value in headers.values()):
        raise ValueError("invalid HTTP header value")
    return headers


@dataclass
class Call:
    method: str
    path: Sequence[str]
    parse: Callable[[httpx.Response], Any]
    params: dict[str, str] = field(default_factory=dict)
    headers: dict[str, str] = field(default_factory=dict)
    body: Any = None
    content_type: str = "application/json"
    replay_safe: bool = False
    success: tuple[int, ...] = (200,)
    base_url: str | None = None
    timeout: httpx.Timeout | None = None
    anonymous: bool = False
    causal: str | None = None
    body_bookmark: bool = False

    def causal_headers(self, bookmarks: Bookmarks) -> dict[str, str]:
        if self.causal is None:
            return {}
        known = bookmarks.get(self.causal)
        supplied = self.headers.get(BOOKMARK)
        if supplied is not None:
            known = merge_bookmarks(known, supplied)
        return {BOOKMARK: known} if known else {}

    def _json_bookmark(self, response: httpx.Response) -> str | None:
        if not self.body_bookmark or not response.content:
            return None
        if not response.headers.get("content-type", "").startswith("application/json"):
            return None
        try:
            body = response.json()
        except ValueError:
            return None
        if not isinstance(body, dict) or "bookmark" not in body or body["bookmark"] is None:
            return None
        if not isinstance(body["bookmark"], str):
            raise ValueError("invalid bookmark")
        return body["bookmark"] or None

    def accept(self, response: httpx.Response, bookmarks: Bookmarks) -> Any:
        if self.causal is not None:
            values = response.headers.get_list(BOOKMARK)
            try:
                if len(values) > 1:
                    raise ValueError("repeated bookmark")
                bookmarks.observe(self.causal, values[0] if values else self._json_bookmark(response))
            except ValueError:
                cls = InvalidResponseError if self.replay_safe else CommitUncertainError
                raise cls(response.status_code, "invalid_response", "invalid bookmark; reconcile writes before retrying",
                          request_id=response_request_id(response.headers)) from None
        return self.decode(response)

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


def placement(regions: Sequence[str] | None, all_regions: bool | None) -> dict[str, Any]:
    if regions is not None and all_regions is not None:
        raise ValueError("pass regions or all_regions, not both")
    if all_regions is not None:
        if not isinstance(all_regions, bool):
            raise ValueError("all_regions must be a bool")
        return {"allRegions": all_regions}
    if regions is not None:
        if isinstance(regions, str) or not 1 <= len(regions) <= 9:
            raise ValueError("regions must be a sequence of one to nine region ids")
        checked = [region(r) for r in regions]
        if len(set(checked)) != len(checked):
            raise ValueError("regions must not contain duplicates")
        return {"regions": checked}
    return {}


def create_database(
    project: str, db: str, ttl_seconds: int | None = None, regions: Sequence[str] | None = None, all_regions: bool | None = None,
    default_consistency: str | None = None,
) -> Call:
    body: dict[str, Any] = {"name": database_name(db)}
    body.update(placement(regions, all_regions))
    mode = consistency(default_consistency)
    if mode is not None:
        body["defaultConsistency"] = mode
    if ttl_seconds is not None:
        body["ttlSeconds"] = ttl(ttl_seconds)
    return Call("POST", ["v1", "projects", project, "databases"],
                lambda r: models.Database.from_json(r.json()), body=body, success=(201,))


def get_database(project: str, db: str) -> Call:
    return Call("GET", _db(project, db), lambda r: models.Database.from_json(r.json()), replay_safe=True)


LOG_STATUS_CLASSES = ("2xx", "3xx", "4xx", "5xx")


def list_database_logs(project: str, db: str, status: str | None) -> Call:
    if status is not None and status not in LOG_STATUS_CLASSES:
        raise ValueError("status must be 2xx, 3xx, 4xx or 5xx")
    return Call("GET", _db(project, database_selector(db), "logs"),
                lambda r: models.DatabaseRequestLogs.from_json(r.json()),
                params={"status": status} if status else {}, replay_safe=True)


def get_database_regions(project: str, db: str) -> Call:
    return Call("GET", _db(project, db, "regions"), lambda r: models.DatabaseRegions.from_json(r.json()),
                replay_safe=True)


def rename_database(project: str, database_id: str, new_name: str) -> Call:
    return Call("PATCH", _db(project, database_selector(database_id)),
                lambda r: models.Database.from_json(r.json()), body={"name": database_name(new_name)})


def delete_database(project: str, db: str) -> Call:
    return Call("DELETE", _db(project, database_selector(db)), _none, success=(204,))


def delete_database_if_id(project: str, db: str, expected_id: str) -> Call:
    return Call("DELETE", _db(project, database_selector(db), "if-id", validate_id(expected_id, "expected database ID")),
                _none, success=(204,))


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


def set_database_placement(project: str, db: str, regions: Sequence[str] | None, all_regions: bool | None) -> Call:
    body = placement(regions, all_regions)
    if not body:
        raise ValueError("pass regions or all_regions")
    return Call("PATCH", _db(project, database_selector(db)), lambda r: models.Database.from_json(r.json()), body=body)


def clear_database_ttl(project: str, db: str) -> Call:
    return Call("DELETE", _db(project, database_selector(db), "ttl"), lambda r: models.Database.from_json(r.json()))


def branch_database(project: str, db: str, branch: str) -> Call:
    return Call("POST", _db(project, db, "branches"),
                lambda r: models.Database.from_json(r.json()), body={"name": database_name(branch)}, success=(201,))


def branch_database_if_id(project: str, db: str, branch: str, expected_id: str) -> Call:
    return Call("POST", _db(project, db, "branches", "if-id", validate_id(expected_id, "expected database ID")),
                lambda r: models.Database.from_json(r.json()), body={"name": database_name(branch)}, success=(201,))



def export_database(project: str, db: str) -> Call:
    return Call("GET", _db(project, db, "export"), _none, headers={"Accept": "text/plain"}, replay_safe=True)


def execute(project: str, db: str, statements: list[dict[str, Any]], consistency_mode: str | None = None,
            bookmark_value: str | None = None) -> Call:
    return Call("POST", _db(project, db, "sql", "execute"), lambda r: sql_results(r.json(), len(statements)),
                headers=causality(consistency_mode, bookmark_value), body={"statements": statements}, causal=db,
                body_bookmark=True)


def read_table_shape(project: str, db: str, table: str) -> Call:
    return Call("GET", _db(project, db, "tables", table, "shape"), lambda r: r, replay_safe=True)


def connect_shape_hints(project: str, db: str, table: str) -> Call:
    return Call("GET", _db(project, db, "tables", table, "shape", "ws"), lambda r: r, replay_safe=True)
