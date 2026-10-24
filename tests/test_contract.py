"""Every request the SDK can send must be a route of the published TerraBase
OpenAPI document (contracts/openapi-routes.json, fetched from
https://api.terrascale.tech/openapi/v1.json). Refresh it with
scripts/refresh-contract.py when the API changes."""

from __future__ import annotations

import json
import re
from pathlib import Path

from fake import Fake

from terrascale import AsyncTerraScale, TerraScale, new_id
from terrascale import query as q

ROUTES = json.loads((Path(__file__).parent.parent / "contracts" / "openapi-routes.json").read_text())["routes"]
PATTERNS = [
    (r.split()[0], re.compile("^" + re.sub(r"\\\{[^}]+\\\}", "[^/]+", re.escape(r.split()[1])) + "$")) for r in ROUTES
]


def _route_ok(method: str, path: str) -> bool:
    return any(m == method and p.match(path) for m, p in PATTERNS)


def _exercise(ts: TerraScale) -> None:
    ts.create_database("main")
    ts.list_databases()
    db = ts.database("main")
    db.info()
    db.update(all_regions=False)
    db.create_collection("c")
    db.list_collections()
    c = db.collection("c")
    c.info()
    r = c.create({"a": 1})
    d = c.get(r.id)
    c.put(new_id(), {"a": 2})
    c.patch(r.id, merge={"a": 3})
    c.list()
    c.query(q.eq("/a", 3))
    c.search("/v", [1.0])
    c.query(q.within_radius("/loc", 0, 0, 10), sort=[q.geo_near("/loc", 0, 0)])
    c.query(aggregate=q.aggregate(["/a"], sum=["/a"]))
    c.set_ttl("/t")
    c.get_ttl()
    c.delete_ttl()
    db.sql("SELECT * FROM c")
    db.check_sql("SELECT 1")
    db.explain_sql("SELECT 1")
    db.execute("SELECT 1")
    db.transaction().create("c", {"a": 1}).commit()
    c.delete(d.id)
    c.drop()
    db.delete()


def test_every_sync_request_is_a_published_route(ts: TerraScale, fake: Fake) -> None:
    _exercise(ts)
    api = [r for r in fake.requests if r.url.host != "auth.test"]
    assert len(api) >= 25
    bad = [f"{r.method} {r.url.path}" for r in api if not _route_ok(r.method, r.url.path)]
    assert bad == []


async def test_async_client_has_the_same_surface() -> None:
    public = {n for n in dir(TerraScale) if not n.startswith("_")}
    assert public <= {n for n in dir(AsyncTerraScale) if not n.startswith("_")}
    from terrascale import AsyncCollection, AsyncDatabase, Collection, Database

    for sync, aio in ((Database, AsyncDatabase), (Collection, AsyncCollection)):
        assert {n for n in dir(sync) if not n.startswith("_")} <= {n for n in dir(aio) if not n.startswith("_")}
