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
from .ids import validate_id

DEFAULT_BASE_URL = "https://api.terrascale.tech"
USER_AGENT_PREFIX = "terrascale-python/"
MAX_ATTEMPTS = 4
MAX_BACKOFF = 30.0
TRANSIENT = frozenset({"rate_limited", "capacity", "storage_unavailable", "dependencies_unavailable", "changes_busy"})


@dataclass
class Call:
    method: str
    path: Sequence[str]
    parse: Callable[[httpx.Response], Any]
    params: dict[str, str] = field(default_factory=dict)
    headers: dict[str, str] = field(default_factory=dict)
    body: Any = None
    content_type: str = "application/json"
    # A replay cannot apply twice: reads, queries and conditional writes.
    replay_safe: bool = False

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


def _page_params(limit: int | None, cursor: str | None) -> dict[str, str]:
    params: dict[str, str] = {}
    if limit:
        params["limit"] = str(limit)
    if cursor:
        params["cursor"] = cursor
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
                params=_page_params(limit, cursor), replay_safe=True)


def create_database(project: str, db: str, home_region: str | None, replica_regions: Sequence[str] | None,
                    all_regions: bool | None) -> Call:
    body = {"name": name(db, "database"), **_placement(home_region, replica_regions, all_regions)}
    return Call("POST", ["v1", "projects", project, "databases"], lambda r: models.Database.from_json(r.json()), body=body)


def get_database(project: str, db: str) -> Call:
    return Call("GET", ["v1", "projects", project, "databases", db], lambda r: models.Database.from_json(r.json()),
                replay_safe=True)


def update_database(project: str, db: str, home_region: str | None, replica_regions: Sequence[str] | None,
                    all_regions: bool | None) -> Call:
    body = _placement(home_region, replica_regions, all_regions)
    if not body:
        raise ValueError("update needs home_region, replica_regions or all_regions")
    return Call("PATCH", ["v1", "projects", project, "databases", db], lambda r: models.Database.from_json(r.json()),
                body=body, replay_safe=True)


def delete_database(project: str, db: str) -> Call:
    return Call("DELETE", ["v1", "projects", project, "databases", db], _none)


def branch_database(project: str, db: str, branch: str) -> Call:
    return Call("POST", ["v1", "projects", project, "databases", db, "branches"],
                lambda r: models.Database.from_json(r.json()), body={"name": name(branch, "branch")})


# Collections -------------------------------------------------------------

def _db(project: str, db: str, *extra: str) -> list[str]:
    return ["v1", "projects", project, "databases", db, *extra]


def create_collection(project: str, db: str, collection: str) -> Call:
    return Call("POST", _db(project, db, "collections"), lambda r: models.Collection.from_json(r.json()),
                body={"name": name(collection, "collection")})


def list_collections(project: str, db: str, limit: int | None, cursor: str | None) -> Call:
    return Call("GET", _db(project, db, "collections"), lambda r: models.CollectionPage.from_json(r.json()),
                params=_page_params(limit, cursor), replay_safe=True)


def get_collection(project: str, db: str, collection: str) -> Call:
    return Call("GET", _db(project, db, "collections", collection), lambda r: models.Collection.from_json(r.json()),
                replay_safe=True)


def drop_collection(project: str, db: str, collection: str) -> Call:
    return Call("DELETE", _db(project, db, "collections", collection), _none)


def get_ttl(project: str, db: str, collection: str) -> Call:
    return Call("GET", _db(project, db, "collections", collection, "ttl"), lambda r: r.json().get("path"),
                replay_safe=True)


def set_ttl(project: str, db: str, collection: str, path: str) -> Call:
    if not isinstance(path, str) or not path.startswith("/"):
        raise ValueError("ttl path must be a JSON Pointer such as '/expiresAt'")
    return Call("PUT", _db(project, db, "collections", collection, "ttl"), lambda r: r.json().get("path"),
                body={"path": path}, replay_safe=True)


def delete_ttl(project: str, db: str, collection: str) -> Call:
    return Call("DELETE", _db(project, db, "collections", collection, "ttl"), _none, replay_safe=True)


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
    return Call("POST", _docs(project, db, collection), _write, body=_check_body(document))


def put_document(project: str, db: str, collection: str, doc_id: str, document: Any, if_match: str | None,
                 if_none_match_any: bool) -> Call:
    return Call("PUT", _docs(project, db, collection, "by-key"), _write, params={"id": validate_id(doc_id)},
                headers=_conditions(if_match, if_none_match_any), body=_check_body(document),
                replay_safe=bool(if_match))


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
                headers=_conditions(if_match, False), body=body, content_type=content_type,
                replay_safe=bool(if_match))


def delete_document(project: str, db: str, collection: str, doc_id: str, if_match: str | None) -> Call:
    return Call("DELETE", _docs(project, db, collection, "by-key"), _write, params={"id": validate_id(doc_id)},
                headers=_conditions(if_match, False), replay_safe=bool(if_match))


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
                lambda r: [models.SQLStatementResult.from_json(x) for x in r.json().get("results") or []],
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
    safe = all(o["op"] in ("assert", "assert_count") or o.get("if_match") for o in ops)
    return Call("POST", _db(project, db, "transactions"), lambda r: models.TransactionResult.from_json(r.json()),
                body={"ops": list(ops)}, replay_safe=safe)


class TransactionBuilder:
    """Collects up to 1024 ops; the client commits them atomically."""

    def __init__(self) -> None:
        self.ops: list[dict[str, Any]] = []

    def _add(self, op: dict[str, Any]) -> TransactionBuilder:
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
