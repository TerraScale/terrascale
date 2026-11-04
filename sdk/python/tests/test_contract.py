"""Every request the SDK can send must be a published TerraBase OpenAPI route
(contracts/openapi-routes.json, fetched from
https://api.terrascale.tech/openapi/v1.json). Refresh it with
scripts/refresh-contract.py when the API changes."""

from __future__ import annotations

import json
import re
from pathlib import Path

from fake import Fake

from terrascale import AsyncTerraScale, TerraScale

ROUTES = json.loads((Path(__file__).parent.parent / "contracts" / "openapi-routes.json").read_text())["routes"]
PATTERNS = [
    (r.split()[0], re.compile("^" + re.sub(r"\\\{[^}]+\\\}", "[^/]+", re.escape(r.split()[1])) + "$")) for r in ROUTES
]


def _route_ok(method: str, path: str) -> bool:
    return any(m == method and p.match(path) for m, p in PATTERNS)


def _exercise(ts: TerraScale) -> None:
    ts.create_database("main", home_region="gru")
    ts.list_databases()
    db = ts.database("main")
    db.info()
    db.execute("CREATE TABLE notes (id TEXT PRIMARY KEY)")
    db.query("SELECT ?", [1])
    db.batch(["SELECT 1", ("SELECT ?", [2])])
    db.branch("copy")
    ts.rename_database("copy", "copy2")
    ts.database("copy2").delete()
    db.delete()


def test_every_sync_request_is_a_published_route(ts: TerraScale, fake: Fake) -> None:
    _exercise(ts)
    api = [r for r in fake.requests if r.url.host != "auth.test"]
    assert len(api) == 10
    bad = [f"{r.method} {r.url.path}" for r in api if not _route_ok(r.method, r.url.path)]
    assert bad == []


async def test_async_client_has_the_same_surface() -> None:
    public = {n for n in dir(TerraScale) if not n.startswith("_")}
    assert public <= {n for n in dir(AsyncTerraScale) if not n.startswith("_")}
    from terrascale import AsyncDatabase, AsyncSqlSession, Database, SqlSession

    for sync, aio in ((Database, AsyncDatabase), (SqlSession, AsyncSqlSession)):
        assert {n for n in dir(sync) if not n.startswith("_")} <= {n for n in dir(aio) if not n.startswith("_")}
