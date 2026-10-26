"""Transport-independent request descriptions shared by the sync and async
clients, plus the retry rule."""

from __future__ import annotations

import json
import random
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from typing import Any
from urllib.parse import quote

import httpx

from . import models
from .errors import CommitUncertainError, InvalidResponseError, TerraScaleError, response_request_id
from .ids import validate_id

DEFAULT_BASE_URL = "https://api.terrascale.tech"
USER_AGENT_PREFIX = "terrascale-python/"
MAX_ATTEMPTS = 4
MAX_BACKOFF = 30.0
TRANSIENT = frozenset({
    "rate_limited", "capacity", "storage_unavailable", "dependencies_unavailable", "changes_busy", "auth_unavailable",
})


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

    def decode(self, response: httpx.Response) -> Any:
        try:
            if response.status_code not in self.success:
                raise ValueError("unexpected success status")
            if self.parse is _none and response.content:
                raise ValueError("unexpected body on a bodyless response")
            if self.parse is not _none:
                body = response.json()
                if not isinstance(body, dict) or not body or "error" in body:
                    raise ValueError("expected a nonempty object")
            result = self.parse(response)
            if isinstance(result, models.WriteResult) and self.params.get("id", result.id) != result.id:
                raise ValueError("acknowledgement identity mismatch")
            return result
        except (ValueError, KeyError, TypeError, AttributeError, IndexError, OverflowError):
            cls = InvalidResponseError if self.replay_safe else CommitUncertainError
            raise cls(response.status_code, "invalid_response", "invalid response; reconcile writes before retrying",
                      request_id=response_request_id(response.headers)) from None

    def url(self, base_url: str) -> str:
        return base_url.rstrip("/") + "/" + "/".join(quote(s, safe="") for s in self.path)

    def content(self) -> bytes | None:
        if self.body is None:
            return None
        return json.dumps(self.body, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode()


def backoff(attempt: int, retry_after: float | None) -> float:
    if retry_after is not None:
        return min(retry_after, MAX_BACKOFF)
    return min(0.25 * (2**attempt) * (0.5 + random.random()), MAX_BACKOFF)


def retryable(status: int, code: str) -> bool:
    return status == 429 or (status == 503 and code in TRANSIENT | {"http_503"})


def name(value: str, what: str) -> str:
    # The server validates names; the SDK only keeps them one path segment.
    if not isinstance(value, str) or value in ("", ".", "..") or "/" in value:
        raise ValueError(f"{what} {value!r} is not a valid name")
    return value


def _json(r: httpx.Response) -> Any:
    return r.json() if r.content else None


def _none(_: httpx.Response) -> None:
    return None


def _discard(_: Any) -> None:
    return None


def _ttl(r: httpx.Response, expected: object = ...) -> str | None:
    path = r.json()["path"]
    if path is not None and (not isinstance(path, str) or not path.startswith("/")) or expected is not ... and path != expected:
        raise ValueError("invalid TTL acknowledgement")
    return path


def _write(r: httpx.Response) -> models.WriteResult:
    return models.WriteResult.from_json(r.json(), created=r.status_code == 201)


def _conditions(if_match: str | None, if_none_match_any: bool) -> dict[str, str]:
    if if_match and if_none_match_any:
        raise ValueError("if_match and if_none_match_any are mutually exclusive")
    if if_match:
        return {"If-Match": f'"{if_match}"'}
    if if_none_match_any:
        return {"If-None-Match": "*"}
    return {}


def _page_params(limit: int | None, cursor: str | None, key: str = "cursor") -> dict[str, str]:
    params: dict[str, str] = {}
    if limit:
        params["limit"] = str(limit)
    if cursor:
        params[key] = cursor
    return params


# Databases ---------------------------------------------------------------

def _placement(home_region: str | None, replica_regions: Sequence[str] | None, all_regions: bool | None) -> dict[str, Any]:
    body: dict[str, Any] = {}
    if home_region is not None:
        body["homeRegion"] = home_region
    if replica_regions is not None:
        body["replicaRegions"] = list(replica_regions)
    if all_regions is not None:
        body["allRegions"] = all_regions
    return body


def list_databases(project: str, limit: int | None, cursor: str | None) -> Call:
    return Call("GET", ["v1", "projects", project, "databases"], lambda r: models.DatabasePage.from_json(r.json()),
                params=_page_params(limit, cursor, "after"), replay_safe=True)


def create_database(project: str, db: str, home_region: str | None, replica_regions: Sequence[str] | None,
                    all_regions: bool | None) -> Call:
    body = {"name": name(db, "database"), **_placement(home_region, replica_regions, all_regions)}
    return Call("POST", ["v1", "projects", project, "databases"],
                lambda r: models.Database.from_json(r.json()), body=body, success=(201,))


def get_database(project: str, db: str) -> Call:
    return Call("GET", ["v1", "projects", project, "databases", db], lambda r: models.Database.from_json(r.json()),
                replay_safe=True)


def update_database(project: str, db: str, home_region: str | None, replica_regions: Sequence[str] | None,
                    all_regions: bool | None) -> Call:
    body = _placement(home_region, replica_regions, all_regions)
    if not body:
        raise ValueError("update needs home_region, replica_regions or all_regions")
    return Call("PATCH", ["v1", "projects", project, "databases", db], lambda r: models.Database.from_json(r.json()),
                body=body)


def delete_database(project: str, db: str) -> Call:
    return Call("DELETE", ["v1", "projects", project, "databases", db], _none, success=(204,))


def branch_database(project: str, db: str, branch: str) -> Call:
    return Call("POST", ["v1", "projects", project, "databases", db, "branches"],
                lambda r: models.Database.from_json(r.json()), body={"name": name(branch, "branch")}, success=(201,))


# Collections -------------------------------------------------------------

def _db(project: str, db: str, *extra: str) -> list[str]:
    return ["v1", "projects", project, "databases", db, *extra]


def create_collection(project: str, db: str, collection: str) -> Call:
    return Call("POST", _db(project, db, "collections"), lambda r: models.Collection.from_json(r.json()),
                body={"name": name(collection, "collection")}, success=(201,))


def list_collections(project: str, db: str, limit: int | None, cursor: str | None) -> Call:
    return Call("GET", _db(project, db, "collections"), lambda r: models.CollectionPage.from_json(r.json()),
                params=_page_params(limit, cursor), replay_safe=True)


def get_collection(project: str, db: str, collection: str) -> Call:
    return Call("GET", _db(project, db, "collections", collection), lambda r: models.Collection.from_json(r.json()),
                replay_safe=True)


def drop_collection(project: str, db: str, collection: str) -> Call:
    return Call("DELETE", _db(project, db, "collections", collection),
                lambda r: _discard(models.Collection.from_json(r.json())))


def get_ttl(project: str, db: str, collection: str) -> Call:
    return Call("GET", _db(project, db, "collections", collection, "ttl"), lambda r: _ttl(r), replay_safe=True)


def set_ttl(project: str, db: str, collection: str, path: str) -> Call:
    if not isinstance(path, str) or not path.startswith("/"):
        raise ValueError("ttl path must be a JSON Pointer such as '/expiresAt'")
    return Call("PUT", _db(project, db, "collections", collection, "ttl"), lambda r: _ttl(r, path),
                body={"path": path})


def delete_ttl(project: str, db: str, collection: str) -> Call:
    return Call("DELETE", _db(project, db, "collections", collection, "ttl"), lambda r: _discard(_ttl(r, None)))


# Documents ---------------------------------------------------------------

def _docs(project: str, db: str, collection: str, *extra: str) -> list[str]:
    return _db(project, db, "collections", collection, "documents", *extra)


def _check_body(document: Any) -> Any:
    if not isinstance(document, dict):
        raise TypeError("a document must be a JSON object (dict)")
    return document


def get_document(project: str, db: str, collection: str, doc_id: str) -> Call:
    return Call("GET", _docs(project, db, collection, "by-key"), lambda r: models.Document.from_json(r.json()),
                params={"id": validate_id(doc_id)}, replay_safe=True)


def create_document(project: str, db: str, collection: str, document: Any) -> Call:
    return Call("POST", _docs(project, db, collection), _write, body=_check_body(document), success=(201,))


def put_document(project: str, db: str, collection: str, doc_id: str, document: Any, if_match: str | None,
                 if_none_match_any: bool) -> Call:
    return Call("PUT", _docs(project, db, collection, "by-key"), _write, params={"id": validate_id(doc_id)},
                headers=_conditions(if_match, if_none_match_any), body=_check_body(document), success=(200, 201))


def patch_document(project: str, db: str, collection: str, doc_id: str, merge: dict[str, Any] | None,
                   ops: Sequence[dict[str, Any]] | None, if_match: str | None) -> Call:
    if (merge is None) == (ops is None):
        raise ValueError("patch needs exactly one of merge or ops")
    if merge is not None:
        body, content_type = _check_body(merge), "application/merge-patch+json"
    else:
        if not ops:
            raise ValueError("ops needs at least one operation")
        body, content_type = {"ops": list(ops)}, "application/vnd.terrabase.ops+json"
    return Call("PATCH", _docs(project, db, collection, "by-key"), _write, params={"id": validate_id(doc_id)},
                headers=_conditions(if_match, False), body=body, content_type=content_type)


def delete_document(project: str, db: str, collection: str, doc_id: str, if_match: str | None) -> Call:
    return Call("DELETE", _docs(project, db, collection, "by-key"), _write, params={"id": validate_id(doc_id)},
                headers=_conditions(if_match, False))


def list_documents(project: str, db: str, collection: str, limit: int | None, cursor: str | None) -> Call:
    return Call("GET", _docs(project, db, collection), lambda r: models.Page.from_json(r.json()),
                params=_page_params(limit, cursor), replay_safe=True)


def query_documents(project: str, db: str, collection: str, filter: dict[str, Any] | None,
                    sort: Sequence[dict[str, Any]] | None, project_paths: Sequence[str] | None, limit: int | None,
                    cursor: str | None, count: bool, aggregate: dict[str, Any] | None = None) -> Call:
    body: dict[str, Any] = {}
    if filter is not None:
        body["filter"] = filter
    if aggregate is not None:
        if sort or project_paths or cursor or count:
            raise ValueError("an aggregate query takes no sort, project, cursor or count")
        body["aggregate"] = aggregate
    if sort:
        if any("near" in k for k in sort) and (len(sort) != 1 or cursor):
            raise ValueError("a near sort must be the only sort key and has no cursor")
        body["sort"] = list(sort)
    if project_paths:
        body["project"] = list(project_paths)
    if limit:
        body["limit"] = limit
    if cursor:
        body["cursor"] = cursor
    if count:
        body["count"] = True
    return Call("POST", _db(project, db, "collections", collection, "query"),
                lambda r: models.Page.from_json(r.json()), body=body, replay_safe=True)


# SQL ---------------------------------------------------------------------

def sql(project: str, db: str, statement: str, endpoint: str | None = None) -> Call:
    path = _db(project, db, "sql", endpoint) if endpoint else _db(project, db, "sql")
    parse = (lambda r: models.SQLResult.from_json(r.json())) if endpoint is None else _json
    return Call("POST", path, parse, body={"sql": statement}, replay_safe=True)


def execute(project: str, db: str, statements: Sequence[dict[str, Any]]) -> Call:
    if not statements:
        raise ValueError("execute needs at least one statement")
    return Call("POST", _db(project, db, "sql", "execute"),
                lambda r: [models.SQLStatementResult.from_json(x) for x in _result_count(r.json(), len(statements))["results"]],
                body={"statements": list(statements)})


def statement(sql_text: str, args: Sequence[Any] | dict[str, Any] | None) -> dict[str, Any]:
    stmt: dict[str, Any] = {"sql": sql_text}
    if isinstance(args, dict):
        stmt["named"] = args
    elif args is not None:
        stmt["args"] = [int(a) if isinstance(a, bool) else a for a in args]
    return stmt


# Transactions ------------------------------------------------------------

def transaction(project: str, db: str, ops: Sequence[dict[str, Any]]) -> Call:
    if not ops:
        raise ValueError("a transaction needs at least one op")
    snapshot = json.dumps(ops, allow_nan=False)  # Immutable, detached from caller-owned lists and nested documents.
    return Call("POST", _db(project, db, "transactions"),
                lambda r: models.TransactionResult.from_json(_transaction_ack(r.json(), json.loads(snapshot))),
                body={"ops": json.loads(snapshot)}, replay_safe=all(o["op"] in ("assert", "assert_count") for o in ops))


def _transaction_ack(body: dict[str, Any], ops: Sequence[dict[str, Any]]) -> dict[str, Any]:
    _result_count(body, len(ops))
    for op, result in zip(ops, body["results"], strict=True):
        if not isinstance(result, dict):
            raise ValueError("invalid transaction result")
        if op["op"] in ("assert", "assert_count"):
            if result.get("asserted") is not True:
                raise ValueError("missing successful assertion")
            count = result.get("count")
            if op["op"] == "assert_count":
                if not isinstance(count, str) or not count.isascii() or not count.isdecimal():
                    raise ValueError("invalid assertion count")
                value = int(count)
                if str(value) != count or not op.get("gte", 0) <= value <= min(op.get("lte", (1 << 64) - 1), (1 << 64) - 1):
                    raise ValueError("assertion count outside submitted bounds")
            elif count is not None:
                raise ValueError("unexpected assertion count")
        else:
            models.WriteResult.from_json({**result, "offset": body["offset"], "durability": body["durability"]})
            if op.get("id", result["id"]) != result["id"]:
                raise ValueError("transaction acknowledgement identity mismatch")
    return body


def _result_count(body: dict[str, Any], count: int) -> dict[str, Any]:
    if not isinstance(body["results"], list) or len(body["results"]) != count:
        raise ValueError("acknowledgement result count mismatch")
    return body


class TransactionBuilder:
    """Collects up to 1024 ops; the client commits them atomically."""

    def __init__(self) -> None:
        self.ops: list[dict[str, Any]] = []
        self._consumed = False

    def _pending(self) -> None:
        if self._consumed:
            raise TerraScaleError("transaction already submitted; create a new transaction")

    def _prepare(self, project: str, db: str) -> Call:
        self._pending()
        call = transaction(project, db, self.ops)
        call.content()  # Local validation must succeed before consuming the transaction.
        self._consumed = True
        return call

    def _add(self, op: dict[str, Any]) -> TransactionBuilder:
        self._pending()
        if len(self.ops) >= 1024:
            raise ValueError("a transaction holds at most 1024 ops")
        self.ops.append(op)
        return self

    def create(self, collection: str, document: dict[str, Any], id: str | None = None) -> TransactionBuilder:
        op: dict[str, Any] = {"op": "create", "collection": collection, "document": _check_body(document)}
        if id is not None:
            op["id"] = validate_id(id)
        return self._add(op)

    def put(self, collection: str, id: str, document: dict[str, Any], *, if_match: str | None = None,
            if_none_match_any: bool = False) -> TransactionBuilder:
        op = {"op": "put", "collection": collection, "id": validate_id(id), "document": _check_body(document)}
        if if_match and if_none_match_any:
            raise ValueError("if_match and if_none_match_any are mutually exclusive")
        if if_match:
            op["if_match"] = if_match
        if if_none_match_any:
            op["if_none_match"] = "*"
        return self._add(op)

    def patch(self, collection: str, id: str, *, merge: dict[str, Any] | None = None,
              ops: Sequence[dict[str, Any]] | None = None, if_match: str | None = None) -> TransactionBuilder:
        if (merge is None) == (ops is None):
            raise ValueError("patch needs exactly one of merge or ops")
        op: dict[str, Any] = {"op": "patch", "collection": collection, "id": validate_id(id)}
        if merge is not None:
            op["merge"] = _check_body(merge)
        else:
            op["ops"] = list(ops or [])
        if if_match:
            op["if_match"] = if_match
        return self._add(op)

    def delete(self, collection: str, id: str, *, if_match: str | None = None) -> TransactionBuilder:
        op = {"op": "delete", "collection": collection, "id": validate_id(id)}
        if if_match:
            op["if_match"] = if_match
        return self._add(op)

    def assert_(self, collection: str, id: str, filter: dict[str, Any]) -> TransactionBuilder:
        return self._add({"op": "assert", "collection": collection, "id": validate_id(id), "filter": filter})

    def assert_absent(self, collection: str, id: str) -> TransactionBuilder:
        return self._add({"op": "assert", "collection": collection, "id": validate_id(id), "absent": True})

    def assert_count(self, collection: str, filter: dict[str, Any] | None = None, *, gte: int | None = None,
                     lte: int | None = None) -> TransactionBuilder:
        if gte is None and lte is None:
            raise ValueError("assert_count needs gte or lte")
        op: dict[str, Any] = {"op": "assert_count", "collection": collection}
        if filter is not None:
            op["filter"] = filter
        if gte is not None:
            op["gte"] = gte
        if lte is not None:
            op["lte"] = lte
        return self._add(op)
