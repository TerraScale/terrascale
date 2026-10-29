"""An in-memory HTTP fake of ts-auth's token endpoint and the TerraBase
document API, for httpx.MockTransport. It never runs a TerraBase binary.
It also records every request so tests can assert the wire format."""

from __future__ import annotations

import base64
import json
import math
import re
from typing import Any
from urllib.parse import unquote, unquote_plus

import httpx

from terrascale import new_id

API_KEY = "01JABCDEFGHJKMNPQRSTVWXYZ0" + "_" + "a" * 52
PROJECT = "01J" + "A" * 23
BASE = "https://api.test"
ISSUER = "https://auth.test/terrascale"


class Fake:
    def __init__(self) -> None:
        self.requests: list[httpx.Request] = []
        self.tokens_issued = 0
        self.expires_in = 3600
        self.reject_tokens: set[str] = set()
        self.fail_next: list[tuple[int, dict[str, Any], dict[str, str]]] = []
        self.databases: dict[str, dict[str, Any]] = {}
        # collection -> id -> (version, document)
        self.docs: dict[str, dict[str, tuple[str, dict[str, Any]]]] = {}
        self.ttl: dict[str, str | None] = {}
        self.offset = 0

    def transport(self) -> httpx.MockTransport:
        return httpx.MockTransport(self.handle)

    # helpers ---------------------------------------------------------------

    def _version(self) -> str:
        self.offset += 1
        return f"tbv1-{self.offset}"

    def _write(self, coll: str, doc_id: str, version: str, document: dict[str, Any] | None = None) -> dict[str, Any]:
        out: dict[str, Any] = {"id": doc_id, "version": version, "offset": str(self.offset), "durability": "local"}
        if document is not None:
            out["document"] = document
        return out

    @staticmethod
    def _err(status: int, code: str, headers: dict[str, str] | None = None) -> httpx.Response:
        return httpx.Response(status, json={"error": {"code": code, "message": code.replace("_", " ")}},
                              headers=headers or {})

    # dispatch --------------------------------------------------------------

    def handle(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        if request.url.host == "auth.test":
            return self._token(request)
        auth = request.headers.get("authorization", "")
        if not auth.startswith("Bearer ") or auth[7:] in self.reject_tokens or auth[7:] == API_KEY:
            return self._err(401, "invalid_key")
        if self.fail_next:
            status, body, headers = self.fail_next.pop(0)
            return httpx.Response(status, json=body, headers=headers)
        path = unquote(request.url.raw_path.split(b"?")[0].decode())
        m = re.fullmatch(r"/v1/projects/([^/]+)/databases(?:/([^/]+))?(/.*)?", path)
        if not m or m.group(1) != PROJECT:
            return self._err(404, "unknown_route")
        db, rest = m.group(2), m.group(3) or ""
        body = json.loads(request.content) if request.content else None
        if db is None:
            return self._databases(request, body)
        if rest == "":
            return self._database(request, db, body)
        if rest == "/collections":
            if request.method == "POST":
                name = body["name"]
                if name in self.docs:
                    return self._err(409, "name_taken")
                self.docs[name] = {}
                return httpx.Response(201, json={"name": name})
            return httpx.Response(200, json={"items": [{"name": n} for n in sorted(self.docs)], "next_cursor": None})
        if rest.startswith("/sql"):
            return self._sql(rest, body)
        if rest == "/transactions":
            return self._tx(body)
        m = re.fullmatch(r"/collections/([^/]+)(/.*)?", rest)
        if not m:
            return self._err(404, "unknown_route")
        coll, sub = m.group(1), m.group(2) or ""
        if coll not in self.docs:
            return self._err(404, "unknown_collection")
        if sub == "":
            if request.method == "DELETE":
                del self.docs[coll]
                return httpx.Response(200, json={"name": coll})
            return httpx.Response(200, json={"name": coll, "documents": len(self.docs[coll])})
        if sub == "/ttl":
            if request.method == "PUT":
                self.ttl[coll] = body["path"]
            elif request.method == "DELETE":
                self.ttl[coll] = None
                return httpx.Response(200, json={"path": None})
            return httpx.Response(200, json={"path": self.ttl.get(coll)})
        if sub == "/query":
            return self._query(coll, body)
        if sub == "/documents":
            if request.method == "POST":
                doc_id = new_id()
                version = self._version()
                self.docs[coll][doc_id] = (version, body)
                return httpx.Response(201, json=self._write(coll, doc_id, version))
            return self._page(sorted(self.docs[coll].items()), request.url.params)
        if sub == "/documents/by-key":
            return self._doc(request, coll, request.url.params["id"], body)
        return self._err(404, "unknown_route")

    def _token(self, request: httpx.Request) -> httpx.Response:
        form = dict(x.split("=", 1) for x in request.content.decode().split("&"))
        basic = base64.b64decode(request.headers["authorization"].split(" ", 1)[1]).decode()
        client, secret = (unquote_plus(x) for x in basic.split(":", 1))
        if form.get("grant_type") != "client_credentials" or secret != API_KEY or client != API_KEY[:26]:
            return httpx.Response(401, json={"error": "invalid_client", "error_description": "unknown key"})
        self.tokens_issued += 1
        return httpx.Response(200, json={"access_token": f"tok-{self.tokens_issued}", "token_type": "Bearer",
                                         "expires_in": self.expires_in, "scope": "documents:read"})

    def _databases(self, request: httpx.Request, body: Any) -> httpx.Response:
        if request.method == "POST":
            name = body["name"]
            if name in self.databases:
                return self._err(409, "name_taken")
            self.databases[name] = {
                "id": new_id(), "projectId": PROJECT, "name": name, "branchedFrom": None,
                "createdAt": "2026-10-23T00:00:00Z", "region": body.get("homeRegion") or "gru",
                "homeRegion": body.get("homeRegion") or "gru", "replicaRegions": body.get("replicaRegions") or [],
                "allRegions": bool(body.get("allRegions")),
            }
            return httpx.Response(201, json=self.databases[name])
        return httpx.Response(200, json={"items": list(self.databases.values()), "next": None})

    def _database(self, request: httpx.Request, name: str, body: Any) -> httpx.Response:
        if name not in self.databases:
            return self._err(404, "unknown_database")
        if request.method == "PATCH":
            for src, dst in (("homeRegion", "homeRegion"), ("replicaRegions", "replicaRegions"), ("allRegions", "allRegions")):
                if src in body:
                    self.databases[name][dst] = body[src]
        elif request.method == "DELETE":
            if self.databases[name]["replicaRegions"]:
                return self._err(409, "database_replicated")
            del self.databases[name]
            return httpx.Response(204)
        return httpx.Response(200, json=self.databases[name])

    def _doc(self, request: httpx.Request, coll: str, doc_id: str, body: Any) -> httpx.Response:
        current = self.docs[coll].get(doc_id)
        if_match = request.headers.get("if-match", "").strip('"')
        if if_match and (current is None or current[0] != if_match):
            return self._err(412, "precondition_failed")
        if request.headers.get("if-none-match") == "*" and current is not None:
            return self._err(412, "precondition_failed")
        if request.method == "GET":
            if current is None:
                return self._err(404, "not_found")
            return httpx.Response(200, json={"id": doc_id, "version": current[0], "state": "value", "document": current[1]},
                                  headers={"ETag": f'"{current[0]}"'})
        if request.method == "PUT":
            version = self._version()
            self.docs[coll][doc_id] = (version, body)
            return httpx.Response(201 if current is None else 200, json=self._write(coll, doc_id, version))
        if request.method == "DELETE":
            if current is None:
                return self._err(404, "not_found")
            del self.docs[coll][doc_id]
            return httpx.Response(200, json=self._write(coll, doc_id, self._version()))
        if request.method == "PATCH":
            if current is None:
                return self._err(404, "not_found")
            doc = dict(current[1])
            ctype = request.headers["content-type"]
            if ctype == "application/merge-patch+json":
                for k, v in body.items():
                    if v is None:
                        doc.pop(k, None)
                    else:
                        doc[k] = v
            elif ctype == "application/vnd.terrabase.ops+json":
                for op in body["ops"]:
                    key = op["path"].lstrip("/")
                    if op["op"] == "set":
                        doc[key] = op["value"]
                    elif op["op"] == "unset":
                        doc.pop(key, None)
                    elif op["op"] == "inc":
                        doc[key] = doc.get(key, 0) + op["by"]
                    elif op["op"] == "append":
                        doc[key] = [*doc.get(key, []), op["value"]]
            else:
                return self._err(415, "unsupported_media_type")
            version = self._version()
            self.docs[coll][doc_id] = (version, doc)
            return httpx.Response(200, json=self._write(coll, doc_id, version, doc))
        return self._err(405, "method_not_allowed")

    def _page(self, entries: list[tuple[str, tuple[str, dict[str, Any]]]], params: Any) -> httpx.Response:
        limit = int(params.get("limit") or 50)
        start = int(params.get("cursor") or 0)
        chunk = entries[start:start + limit]
        nxt = str(start + limit) if start + limit < len(entries) else None
        items = [{"id": i, "version": v, "state": "value", "document": d} for i, (v, d) in chunk]
        return httpx.Response(200, json={"items": items, "next_cursor": nxt})

    def _query(self, coll: str, body: dict[str, Any]) -> httpx.Response:
        entries = sorted(self.docs[coll].items())
        f = body.get("filter")
        if f:
            entries = [e for e in entries if _match(f, e[0], e[1][1])]
        if "aggregate" in body:
            return httpx.Response(200, json={"items": [], "next_cursor": None, "groups": _groups(body["aggregate"], entries)})
        sort = body.get("sort") or []
        if sort and "near" in sort[0]:
            near = sort[0]["near"]
            key = near["path"].lstrip("/")
            scored = []
            for doc_id, (v, d) in entries:
                if "lat" in near:
                    dist = _meters(d.get(key), near["lat"], near["lon"])
                    if dist is None:
                        continue
                else:
                    dist = sum((a - b) ** 2 for a, b in zip(d.get(key, []), near["vector"], strict=False)) ** 0.5
                scored.append({"id": doc_id, "version": v, "state": "value", "document": d, "distance": dist})
            scored.sort(key=lambda x: x["distance"])
            return httpx.Response(200, json={"items": scored[: body.get("limit", 10)], "next_cursor": None})
        for k in reversed(sort):
            key = k["path"].lstrip("/")
            entries.sort(key=lambda e, key=key: e[1][1].get(key), reverse=k["order"] == "desc")
        resp = self._page(entries, {"limit": body.get("limit"), "cursor": body.get("cursor")})
        if body.get("count"):
            data = json.loads(resp.content)
            data["count"] = {"value": str(len(entries)), "exact": True}
            return httpx.Response(200, json=data)
        return resp

    def _sql(self, rest: str, body: dict[str, Any]) -> httpx.Response:
        if rest == "/sql":
            coll = re.search(r"FROM (\w+)", body["sql"], re.I)
            rows = [{"id": i, "value": d} for i, (_, d) in sorted(self.docs.get(coll.group(1) if coll else "", {}).items())]
            return httpx.Response(200, json={"matched": len(rows), "truncated": False, "rows": rows})
        if rest in ("/sql/check", "/sql/explain"):
            return httpx.Response(200, json={"ok": True, "plan": "SCAN"})
        if rest == "/sql/execute":
            results = [{"columns": [{"name": "n", "decltype": None}],
                        "rows": [[len(s.get("args") or s.get("named") or [])]], "rows_affected": 0,
                        "last_insert_rowid": None} for s in body["statements"]]
            return httpx.Response(200, json={"results": results})
        return self._err(404, "unknown_route")

    def _tx(self, body: dict[str, Any]) -> httpx.Response:
        results = []
        for i, op in enumerate(body["ops"]):
            coll = self.docs.setdefault(op["collection"], {})
            if op["op"] in ("create", "put"):
                doc_id = op.get("id") or new_id()
                version = self._version()
                coll[doc_id] = (version, op["document"])
                results.append({"id": doc_id, "version": version})
            elif op["op"] == "assert":
                if op.get("absent") and op["id"] in coll:
                    return httpx.Response(409, json={"error": {"code": "assertion_failed", "op": i}})
                results.append({"asserted": True})
            elif op["op"] == "assert_count":
                results.append({"asserted": True, "count": str(len(coll))})
            else:
                results.append({})
        return httpx.Response(200, json={"offset": str(self.offset), "durability": "local", "results": results})


def _match(f: dict[str, Any], doc_id: str, doc: dict[str, Any]) -> bool:
    (op, arg), = f.items()
    if op == "and":
        return all(_match(x, doc_id, doc) for x in arg)
    if op == "or":
        return any(_match(x, doc_id, doc) for x in arg)
    if op == "not":
        return not _match(arg, doc_id, doc)
    path, value = arg if isinstance(arg, list) else (arg, None)
    actual = doc_id if path == "$id" else doc.get(path.lstrip("/"))
    if op == "exists":
        return path.lstrip("/") in doc
    if op == "eq":
        return bool(actual == value)
    if op == "ne":
        return bool(actual != value)
    if op == "gt":
        return actual is not None and actual > value
    if op == "gte":
        return actual is not None and actual >= value
    if op == "lt":
        return actual is not None and actual < value
    if op == "lte":
        return actual is not None and actual <= value
    if op == "in":
        return actual in value
    if op == "prefix":
        return isinstance(actual, str) and actual.startswith(value)
    if op == "contains":
        return isinstance(actual, list) and value in actual
    if op == "within":
        if "meters" in value:
            dist = _meters(actual, value["lat"], value["lon"])
            return dist is not None and dist <= value["meters"]
        if not isinstance(actual, dict) or not value["south"] <= actual["lat"] <= value["north"]:
            return False
        west, east, lon = value["west"], value["east"], actual["lon"]
        return bool(west <= lon <= east if west <= east else lon >= west or lon <= east)
    raise AssertionError(op)


def _meters(point: Any, lat: float, lon: float) -> float | None:
    if not isinstance(point, dict) or not {"lat", "lon"} <= point.keys():
        return None
    p1, p2 = math.radians(point["lat"]), math.radians(lat)
    h = math.sin((p2 - p1) / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(math.radians(lon - point["lon"]) / 2) ** 2
    return 2 * 6371008.8 * math.asin(math.sqrt(h))


def _groups(agg: dict[str, Any], entries: list[tuple[str, tuple[str, dict[str, Any]]]]) -> list[dict[str, Any]]:
    buckets: dict[str, list[dict[str, Any]]] = {}
    for doc_id, (_, d) in entries:
        key = [doc_id if p == "$id" else d.get(p.lstrip("/")) for p in agg.get("group_by", [])]
        buckets.setdefault(json.dumps(key), []).append(d)
    groups = []
    for key, docs in sorted(buckets.items()):
        g: dict[str, Any] = {"key": json.loads(key), "count": str(len(docs))}
        for name, fold in (("sum", sum), ("avg", lambda xs: sum(xs) / len(xs)), ("min", min), ("max", max)):
            if name in agg:
                nums = {p: [d[p.lstrip("/")] for d in docs if isinstance(d.get(p.lstrip("/")), (int, float))]
                        for p in agg[name]}
                g[name] = {p: fold(xs) if xs else (0 if name == "sum" else None) for p, xs in nums.items()}
        groups.append(g)
    return groups
