"""An in-memory HTTP fake of ts-auth's token endpoint and the TerraBase
database and SQL API, for httpx.MockTransport. It never runs a TerraBase binary.
It also records every request so tests can assert the wire format."""

from __future__ import annotations

import base64
import json
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

    def transport(self) -> httpx.MockTransport:
        return httpx.MockTransport(self.handle)

    # helpers ---------------------------------------------------------------

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
        if rest == "/branches" and db in self.databases:
            source = self.databases[db]
            self.databases[body["name"]] = {**source, "id": new_id(), "name": body["name"], "branchedFrom": source["id"]}
            return httpx.Response(201, json=self.databases[body["name"]])
        if rest == "/sql/execute":
            return self._sql(body)
        return self._err(404, "unknown_route")

    def _token(self, request: httpx.Request) -> httpx.Response:
        form = dict(x.split("=", 1) for x in request.content.decode().split("&"))
        basic = base64.b64decode(request.headers["authorization"].split(" ", 1)[1]).decode()
        client, secret = (unquote_plus(x) for x in basic.split(":", 1))
        if form.get("grant_type") != "client_credentials" or secret != API_KEY or client != API_KEY[:26]:
            return httpx.Response(401, json={"error": "invalid_client", "error_description": "unknown key"})
        self.tokens_issued += 1
        return httpx.Response(200, json={"access_token": f"tok-{self.tokens_issued}", "token_type": "Bearer",
                                         "expires_in": self.expires_in, "scope": "databases:read"})

    def _databases(self, request: httpx.Request, body: Any) -> httpx.Response:
        if request.method == "POST":
            name = body["name"]
            if name in self.databases:
                return self._err(409, "name_taken")
            self.databases[name] = {
                "id": new_id(), "projectId": PROJECT, "name": name, "branchedFrom": None,
                "createdAt": "2026-10-23T00:00:00Z", "region": body.get("homeRegion") or "gru",
                "homeRegion": body.get("homeRegion") or "gru",
            }
            return httpx.Response(201, json=self.databases[name])
        return httpx.Response(200, json={"items": list(self.databases.values()), "next": None})

    def _database(self, request: httpx.Request, name: str, body: Any) -> httpx.Response:
        if name not in self.databases:
            return self._err(404, "unknown_database")
        if request.method == "PATCH":
            self.databases[body["name"]] = {**self.databases.pop(name), "name": body["name"]}
            return httpx.Response(200, json=self.databases[body["name"]])
        if request.method == "DELETE":
            del self.databases[name]
            return httpx.Response(204)
        return httpx.Response(200, json=self.databases[name])

    def _sql(self, body: dict[str, Any]) -> httpx.Response:
        results = [{"columns": [{"name": "n", "decltype": None}],
                    "rows": [[len(s.get("args") or s.get("named") or [])]], "rows_affected": 0,
                    "last_insert_rowid": None} for s in body["statements"]]
        return httpx.Response(200, json={"results": results})
