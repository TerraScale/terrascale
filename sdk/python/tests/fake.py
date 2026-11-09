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
        self.api_keys: dict[str, dict[str, Any]] = {}
        self.clients: dict[str, dict[str, Any]] = {}
        self.users: dict[str, dict[str, Any]] = {
            "01J" + "V" * 23: {"id": "01J" + "V" * 23, "email": "ada@example.com",
                               "created_at": "2026-11-04T00:00:00Z", "disabled": False},
        }

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
            path = unquote(request.url.path)
            if re.fullmatch(r"/v1/terrascale/api-keys/[^/]+/rotate", path):
                return self._rotate_key(request, path)
            m = re.fullmatch(r"/v1/terrascale/organizations/([^/]+)(?:/projects/([^/]+))?/api-keys(?:/([^/]+))?", path)
            if m:
                return self._api_keys(request, m.group(1), m.group(2), m.group(3))
            m = re.fullmatch(r"/v1/([^/]+)/clients(?:/([^/]+))?(/secret/rotate)?", path)
            if m:
                return self._clients(request, m.group(1), m.group(2), m.group(3) == "/secret/rotate")
            if path == f"/v1/{PROJECT}/settings":
                return self._settings(request)
            if path == f"/v1/{PROJECT}/users/export":
                return self._users_export(request)
            m = re.fullmatch(rf"/v1/{PROJECT}/users(?:/([^/]+))?(/disable|/enable)?", path)
            if m:
                return self._users(request, m.group(1), m.group(2))
            return self._token(request)
        path = unquote(request.url.raw_path.split(b"?")[0].decode())
        path = unquote(request.url.raw_path.split(b"?")[0].decode())
        auth = request.headers.get("authorization", "")
        if path == "/v1/regions":
            if request.method != "GET" or auth:
                return self._err(405, "unknown_route")
            return httpx.Response(200, json={"regions": ["gru", "sjc"]})
        if not auth.startswith("Bearer ") or auth[7:] in self.reject_tokens or auth[7:] == API_KEY:
            return self._err(401, "invalid_key")
        if self.fail_next:
            status, body, headers = self.fail_next.pop(0)
            return httpx.Response(status, json=body, headers=headers)
        m = re.fullmatch(r"/v1/projects/([^/]+)/databases(?:/([^/]+))?(/.*)?", path)
        if m and m.group(1) != PROJECT:
            return self._err(404, "unknown_route")
        if m is None:
            m2 = re.fullmatch(r"/v1/projects/([^/]+)/(analytics|metrics)", path)
            if not m2 or m2.group(1) != PROJECT or request.method != "GET":
                return self._err(404, "unknown_route")
            if m2.group(2) == "metrics":
                return httpx.Response(200, text="terrabase_requests_total 1\n",
                                      headers={"content-type": "text/plain; version=0.0.4; charset=utf-8"})
            return httpx.Response(200, json=self._analytics("01J" + "B" * 23))
        db, rest = m.group(2), m.group(3) or ""
        body = json.loads(request.content) if request.content else None
        if db is None:
            return self._databases(request, body)
        if rest == "":
            return self._database(request, db, body)
        if rest == "/usage":
            return httpx.Response(200, json=self._usage("01J" + "C" * 23))
        if rest == "/regions":
            return httpx.Response(200, json={"databaseId": "01J" + "C" * 23, "homeRegion": "gru",
                                             "observedAt": "2026-11-08T00:00:00Z",
                                             "replicas": [{"region": "sjc", "lagMs": 12.5,
                                                           "lastPulledAt": "2026-11-08T00:00:00Z",
                                                           "lastVerified": None, "revision": "abc"}]})
        if rest == "/analytics":
            return httpx.Response(200, json=self._analytics("01J" + "C" * 23))
        if rest == "/branches" and db in self.databases:
            source = self.databases[db]
            self.databases[body["name"]] = {**source, "id": new_id(), "name": body["name"], "branchedFrom": source["id"]}
            return httpx.Response(201, json=self.databases[body["name"]])
        guard = re.fullmatch(r"/branches/if-id/([^/]+)", rest)
        if guard and db in self.databases:
            source = self.databases[db]
            if guard.group(1) != source["id"]:
                return self._err(409, "database_identity_changed")
            self.databases[body["name"]] = {**source, "id": new_id(), "name": body["name"], "branchedFrom": source["id"]}
            return httpx.Response(201, json=self.databases[body["name"]])
        plain = re.fullmatch(r"/if-id/([^/]+)", rest)
        if plain and db in self.databases:
            if plain.group(1) != self.databases[db]["id"]:
                return self._err(409, "database_identity_mismatch")
            del self.databases[db]
            return httpx.Response(204)

        if rest == "/export" and db in self.databases:
            return httpx.Response(200, content=b"BEGIN;\nINSERT INTO \"todos\" VALUES (1,'ship');\nCOMMIT;",
                                  headers={"content-type": "application/sql"})
        if rest == "/sql/execute":
            return self._sql(body)
        return self._err(404, "unknown_route")

    def _rotate_key(self, request: httpx.Request, path: str) -> httpx.Response:
        m = re.fullmatch(r"/v1/terrascale/api-keys/([^/]+)/rotate", path)
        if request.method != "POST" or not m:
            return self._err(404, "unknown_route")
        basic = base64.b64decode(request.headers["authorization"].split(" ", 1)[1]).decode()
        client, secret = (unquote_plus(x) for x in basic.split(":", 1))
        if secret != API_KEY or client != API_KEY[:26]:
            return self._err(401, "invalid_key")
        return httpx.Response(200, json={"id": m.group(1), "api_key": m.group(1) + "_" + "b" * 52,
                                         "previous_secret_expires_at": 1_788_230_400})

    def _bearer(self, request: httpx.Request) -> bool:
        return request.headers.get("authorization", "").startswith("Bearer tok-")

    @staticmethod
    def _usage(database_id: str) -> dict[str, Any]:
        return {"databaseId": database_id, "storageBytes": 1024, "logicalBytes": 1024, "reads": 3, "writes": 1,
                "period": {"from": "2026-10-07T00:00:00Z", "to": "2026-10-08T00:00:00Z", "granularity": "hour",
                           "reads": 3, "writes": 1},
                "series": [{"start": "2026-10-07T00:00:00Z", "reads": 3, "writes": 1}]}

    @staticmethod
    def _analytics(project_id: str) -> dict[str, Any]:
        return {"projectId": project_id, "range": "24h", "from": "2026-10-07T00:00:00Z",
                "to": "2026-10-08T00:00:00Z", "bucketSeconds": 3600,
                "items": [{"databaseId": "01J" + "C" * 23, "databaseName": "main", "region": "gru",
                           "series": [{"start": "2026-10-07T00:00:00Z", "requests": {"GET": 2},
                                       "operations": {"insert": 1}, "errors": 0,
                                       "latency": {"p50Ms": 1.5, "p95Ms": None, "p99Ms": None},
                                       "storageBytes": None,
                                       "clientBandwidth": {"bytesSent": 10, "bytesReceived": 20},
                                       "replicationBandwidth": {"bytesSent": 0, "bytesReceived": 0}}]}]}

    def _api_keys(self, request: httpx.Request, org: str, project: str | None, key: str | None) -> httpx.Response:
        if not self._bearer(request):
            return self._err(401, "invalid_token")
        if key is None:
            if request.method == "POST":
                if not request.headers.get("x-terrascale-step-up-proof"):
                    return self._err(403, "step_up_required")
                body = json.loads(request.content)
                key_id = new_id()
                record = {"id": key_id, "name": body["name"], "organization_id": org, "project_id": project,
                          "database": body.get("database"), "scopes": body["scopes"], "created_by": new_id(),
                          "created_at": "2026-11-04T00:00:00Z", "expires_at": body.get("expires_at"),
                          "previous_secret_expires_at": None, "last_used_at": None,
                          "allowed_cidrs": body.get("allowed_cidrs") or []}
                self.api_keys[key_id] = record
                return httpx.Response(201, json={"api_key": record, "key": key_id + "_" + "c" * 52})
            return httpx.Response(200, json={"items": list(self.api_keys.values()), "next": None})
        if request.method == "DELETE":
            if key not in self.api_keys:
                return self._err(404, "unknown_key")
            del self.api_keys[key]
            return httpx.Response(204)
        return self._err(404, "unknown_route")

    def _clients(self, request: httpx.Request, project: str, client_id: str | None, rotate: bool) -> httpx.Response:
        if not self._bearer(request):
            return self._err(401, "invalid_token")
        if client_id is None:
            if request.method == "POST":
                if not request.headers.get("x-terrascale-step-up-proof"):
                    return self._err(403, "step_up_required")
                body = json.loads(request.content)
                cid = new_id()
                record = {"client_id": cid, "name": body["name"], "type": body["type"],
                          "redirect_uris": body["redirect_uris"], "created_at": "2026-11-04T00:00:00Z"}
                if body["type"] == "confidential":
                    record["client_secret"] = cid + "_" + "d" * 52
                self.clients[cid] = record
                return httpx.Response(201, json=record)
            items = [{k: v for k, v in c.items() if k != "client_secret"} for c in self.clients.values()]
            return httpx.Response(200, json={"items": items, "next": None})
        record = self.clients.get(client_id)
        if record is None:
            return self._err(404, "unknown_client")
        if rotate:
            if request.method != "POST":
                return self._err(404, "unknown_route")
            if not request.headers.get("x-terrascale-step-up-proof"):
                return self._err(403, "step_up_required")
            body = json.loads(request.content) if request.content else {}
            return httpx.Response(200, json={"client_id": client_id, "client_secret": client_id + "_" + "e" * 52,
                                             "previous_secret_expires_at":
                                                 "2026-11-04T01:00:00Z" if body.get("grace_seconds") else None})
        if request.method == "PUT":
            if not request.headers.get("x-terrascale-step-up-proof"):
                return self._err(403, "step_up_required")
            body = json.loads(request.content)
            self.clients[client_id] = {**record, "name": body["name"], "redirect_uris": body["redirect_uris"]}
            return httpx.Response(200, json=self.clients[client_id])
        if request.method == "DELETE":
            del self.clients[client_id]
            return httpx.Response(204)
        return self._err(404, "unknown_route")

    def _settings(self, request: httpx.Request) -> httpx.Response:
        if not self._bearer(request):
            return self._err(401, "invalid_token")
        if request.method == "PUT":
            return httpx.Response(200, json={"updated": json.loads(request.content)})
        return httpx.Response(200, json={
            "project_id": PROJECT, "issuer": f"https://auth.test/{PROJECT}",
            "discovery_url": f"https://auth.test/{PROJECT}/.well-known/openid-configuration",
            "sign_in_url": f"https://auth.test/{PROJECT}/sign-in",
            "methods": {"passcode": False, "password": True, "sign_up": True},
            "branding": {"name": "Acme", "logo_url": None, "theme_color": "#0f62fe",
                         "footer_links": [{"label": "Acme", "url": "https://acme.example"}]},
            "counts": {"users": len(self.users), "clients": len(self.clients)},
            "providers": [], "configuration": {"redirect_urls": ["https://app.example/cb"],
                                               "session_lifetime_seconds": 1209600},
        })

    def _users(self, request: httpx.Request, user: str | None, action: str | None) -> httpx.Response:
        if not self._bearer(request):
            return self._err(401, "invalid_token")
        if user is None:
            if request.method != "GET":
                return self._err(404, "unknown_route")
            items = list(self.users.values())
            search = request.url.params.get("q")
            if search:
                items = [u for u in items if search in u["email"]]
            return httpx.Response(200, json={"items": items, "next": None})
        record = self.users.get(user)
        if record is None:
            return self._err(404, "unknown_user")
        if action in ("/disable", "/enable"):
            if request.method != "POST":
                return self._err(404, "unknown_route")
            record["disabled"] = action == "/disable"
            return httpx.Response(200, json=record)
        if request.method == "GET":
            return httpx.Response(200, json=record)
        if request.method == "DELETE":
            del self.users[user]
            return httpx.Response(204)
        return self._err(404, "unknown_route")

    def _users_export(self, request: httpx.Request) -> httpx.Response:
        if not self._bearer(request):
            return self._err(401, "invalid_token")
        items = [{**u, "email_verified": True, "linked_providers": []} for u in self.users.values()]
        return httpx.Response(200, json={"items": items, "next": None,
                                         "created_before": "2026-11-08T00:00:00Z"})

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
