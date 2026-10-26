from __future__ import annotations

import base64
import json

import httpx
import pytest
from fake import API_KEY, BASE, ISSUER, PROJECT, Fake

import terrascale
from terrascale import (
    AuthenticationError,
    CommitUncertainError,
    ConflictError,
    NotFoundError,
    PreconditionFailedError,
    RateLimitError,
    TerraScale,
    TokenError,
    new_id,
)
from terrascale import query as q


def test_token_exchange_and_bearer(ts: TerraScale, fake: Fake) -> None:
    ts.create_database("main")
    token_req, api_req = fake.requests
    assert str(token_req.url) == ISSUER + "/oauth/token"
    assert token_req.headers["content-type"] == "application/x-www-form-urlencoded"
    form = token_req.content.decode()
    assert "grant_type=client_credentials" in form and f"project={PROJECT}" in form
    basic = base64.b64decode(token_req.headers["authorization"].split()[1]).decode()
    assert basic == f"{API_KEY[:26]}:{API_KEY}"
    assert API_KEY not in form  # the secret travels only in Basic auth
    assert api_req.headers["authorization"] == "Bearer tok-1"
    assert api_req.headers["user-agent"] == f"terrascale-python/{terrascale.__version__}"


def test_token_is_cached_and_refreshed_on_401(ts: TerraScale, fake: Fake) -> None:
    ts.create_database("main")
    ts.database("main").info()
    assert fake.tokens_issued == 1
    fake.reject_tokens.add("tok-1")
    ts.database("main").info()
    assert fake.tokens_issued == 2


def test_persistent_401_raises(ts: TerraScale, fake: Fake) -> None:
    fake.reject_tokens.update({"tok-1", "tok-2"})
    with pytest.raises(AuthenticationError) as e:
        ts.list_databases()
    assert e.value.code == "invalid_key"


def test_bad_api_key_is_token_error(fake: Fake) -> None:
    other = "01JABCDEFGHJKMNPQRSTVWXYZ1_" + "b" * 52
    http = httpx.Client(transport=fake.transport())
    client = TerraScale(other, project=PROJECT, base_url=BASE, issuer=ISSUER, http_client=http)
    with pytest.raises(TokenError) as e:
        client.list_databases()
    assert e.value.status == 401 and e.value.code == "invalid_client"
    assert other not in str(e.value)
    with pytest.raises(TokenError) as again:
        client.list_databases()
    assert again.value.code == "invalid_client"
    assert len(fake.requests) == 1


def test_config_validation(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("TERRASCALE_API_KEY", raising=False)
    monkeypatch.delenv("TERRASCALE_PROJECT_ID", raising=False)
    with pytest.raises(ValueError, match="project"):
        TerraScale(API_KEY)
    with pytest.raises(ValueError, match="API key"):
        TerraScale(project=PROJECT)
    with pytest.raises(ValueError, match="api_key") as e:
        TerraScale("not-a-key", project=PROJECT)
    assert "not-a-key" not in str(e.value)
    with pytest.raises(ValueError, match="https"):
        TerraScale(API_KEY, project=PROJECT, base_url="http://example.com")
    monkeypatch.setenv("TERRASCALE_API_KEY", API_KEY)
    monkeypatch.setenv("TERRASCALE_PROJECT_ID", PROJECT)
    assert TerraScale().project == PROJECT


def test_databases_and_regions(ts: TerraScale) -> None:
    db = ts.create_database("main", home_region="gru", replica_regions=["sjc"])
    assert (db.name, db.home_region, db.replica_regions) == ("main", "gru", ["sjc"])
    assert [d.name for d in ts.list_databases().items] == ["main"]
    handle = ts.database("main")
    with pytest.raises(ConflictError) as e:
        handle.delete()
    assert e.value.code == "database_replicated"
    assert handle.update(replica_regions=[]).replica_regions == []
    assert handle.update(all_regions=True).all_regions
    handle.update(all_regions=False)
    handle.delete()
    with pytest.raises(NotFoundError):
        handle.info()
    with pytest.raises(ValueError):
        handle.update()


def test_documents_crud(ts: TerraScale, fake: Fake) -> None:
    ts.create_database("main")
    db = ts.database("main")
    db.create_collection("todos")
    with pytest.raises(ConflictError):
        db.create_collection("todos")
    assert [c.name for c in db.list_collections().items] == ["todos"]
    todos = db.collection("todos")

    created = todos.create({"title": "Ship", "done": False})
    assert created.created and terrascale.is_valid_id(created.id)
    doc = todos.get(created.id)
    assert doc.data == {"title": "Ship", "done": False} and doc.state == "value"
    get_req = fake.requests[-1]
    assert get_req.url.path.endswith("/collections/todos/documents/by-key")
    assert get_req.url.params["id"] == created.id

    patched = todos.patch(created.id, merge={"done": True}, if_match=doc.version)
    assert fake.requests[-1].headers["if-match"] == f'"{doc.version}"'
    assert fake.requests[-1].headers["content-type"] == "application/merge-patch+json"
    with pytest.raises(PreconditionFailedError):
        todos.patch(created.id, merge={"done": False}, if_match=doc.version)

    todos.patch(created.id, ops=[q.inc("/views", 2), q.append("/tags", "a"), q.unset("/done")],
                if_match=patched.version)
    assert fake.requests[-1].headers["content-type"] == "application/vnd.terrabase.ops+json"
    assert todos.get(created.id).data == {"title": "Ship", "views": 2, "tags": ["a"]}

    my_id = new_id()
    assert todos.put(my_id, {"title": "Mine"}, if_none_match_any=True).created
    assert fake.requests[-1].headers["if-none-match"] == "*"
    with pytest.raises(PreconditionFailedError):
        todos.put(my_id, {"title": "Again"}, if_none_match_any=True)

    todos.delete(my_id)
    with pytest.raises(NotFoundError) as e:
        todos.get(my_id)
    assert e.value.code == "not_found" and e.value.status == 404

    with pytest.raises(ValueError):
        todos.get("not-a-ulid")
    with pytest.raises(ValueError):
        todos.patch(created.id)
    with pytest.raises(TypeError):
        todos.create(["not", "an", "object"])  # type: ignore[arg-type]


def test_queries_and_paging(ts: TerraScale, fake: Fake) -> None:
    ts.create_database("main")
    db = ts.database("main")
    db.create_collection("items")
    items = db.collection("items")
    for i in range(7):
        items.create({"n": i, "even": i % 2 == 0, "name": f"item-{i}"})

    page = items.query(q.and_(q.eq("/even", True), q.gte("/n", 2)), sort=[q.desc("/n")], limit=10, count=True)
    assert [d.data["n"] for d in page.items] == [6, 4, 2]
    assert page.count == 3 and page.count_exact
    body = json.loads(fake.requests[-1].content)
    assert body == {"filter": {"and": [{"eq": ["/even", True]}, {"gte": ["/n", 2]}]},
                    "sort": [{"path": "/n", "order": "desc"}], "limit": 10, "count": True}

    pages = list(items.pages(q.prefix("/name", "item-"), limit=3))
    assert [len(p.items) for p in pages] == [3, 3, 1]
    assert sorted(d.data["n"] for d in items.items(limit=2)) == list(range(7))
    assert len(items.list(limit=5).items) == 5
    assert items.query(q.in_("/n", [1, 3])).items[0].data["n"] in (1, 3)

    with pytest.raises(ValueError):
        q.eq("n", 1)  # paths are JSON Pointers
    with pytest.raises(TypeError):
        q.eq("/n", {"x": 1})


def test_vector_search(ts: TerraScale, fake: Fake) -> None:
    ts.create_database("main")
    db = ts.database("main")
    db.create_collection("vecs")
    vecs = db.collection("vecs")
    vecs.create({"label": "far", "embedding": [10.0, 10.0]})
    vecs.create({"label": "near", "embedding": [1.0, 0.9]})
    hits = vecs.search("/embedding", [1.0, 1.0], limit=2, metric="l2")
    assert [h.data["label"] for h in hits] == ["near", "far"]
    assert hits[0].distance is not None and hits[0].distance < hits[1].distance  # type: ignore[operator]
    body = json.loads(fake.requests[-1].content)
    assert body["sort"] == [{"near": {"path": "/embedding", "vector": [1.0, 1.0], "metric": "l2"}}]
    with pytest.raises(ValueError):
        q.near("/e", [0.0, 0.0])
    with pytest.raises(ValueError):
        vecs.query(sort=[q.near("/e", [1.0]), q.asc("/n")])


def test_geo_within_and_near(ts: TerraScale, fake: Fake) -> None:
    ts.create_database("main")
    db = ts.database("main")
    db.create_collection("places")
    places = db.collection("places")
    places.create({"name": "se", "loc": {"lat": -23.5505, "lon": -46.6333}})
    places.create({"name": "paulista", "loc": {"lat": -23.5614, "lon": -46.6559}})
    places.create({"name": "rio", "loc": {"lat": -22.9068, "lon": -43.1729}})
    places.create({"name": "fiji", "loc": {"lat": -17.7, "lon": 178.0}})
    places.create({"name": "nowhere"})

    near_se = places.query(q.within_radius("/loc", -23.5505, -46.6333, 5000))
    assert sorted(d.data["name"] for d in near_se.items) == ["paulista", "se"]
    assert json.loads(fake.requests[-1].content) == {
        "filter": {"within": ["/loc", {"lat": -23.5505, "lon": -46.6333, "meters": 5000}]}}
    crossing = places.query(q.not_(q.within_box("/loc", -20, 170, -10, -170)))
    assert json.loads(fake.requests[-1].content)["filter"] == {
        "not": {"within": ["/loc", {"south": -20, "west": 170, "north": -10, "east": -170}]}}
    assert "fiji" not in [d.data.get("name") for d in crossing.items]

    hits = places.query(sort=[q.geo_near("/loc", -23.5505, -46.6333)], limit=3).items
    assert [h.data["name"] for h in hits] == ["se", "paulista", "rio"]
    assert hits[0].distance == 0 and 2000 < hits[1].distance < 3000  # type: ignore[operator]
    assert json.loads(fake.requests[-1].content)["sort"] == [{"near": {"path": "/loc", "lat": -23.5505, "lon": -46.6333}}]

    with pytest.raises(ValueError):
        q.within_radius("/loc", 91, 0, 10)
    with pytest.raises(ValueError):
        q.within_radius("/loc", 0, 0, 20_000_001)
    with pytest.raises(ValueError):
        places.query(sort=[q.geo_near("/loc", 0, 0), q.asc("/name")])


def test_aggregate_groups(ts: TerraScale, fake: Fake) -> None:
    ts.create_database("main")
    db = ts.database("main")
    db.create_collection("orders")
    orders = db.collection("orders")
    for status, amount in (("paid", 10), ("paid", 30.5), ("open", 5), ("open", None)):
        orders.create({"status": status, "amount": amount})

    agg = q.aggregate(["/status"], sum=["/amount"], avg=["/amount"], min=["/amount"], max=["/amount"])
    page = orders.query(q.exists("/status"), aggregate=agg, limit=10)
    assert json.loads(fake.requests[-1].content) == {
        "filter": {"exists": "/status"}, "limit": 10,
        "aggregate": {"group_by": ["/status"], "sum": ["/amount"], "avg": ["/amount"], "min": ["/amount"], "max": ["/amount"]}}
    assert page.items == [] and page.groups is not None
    by_key = {g.key[0]: g for g in page.groups}
    assert by_key["paid"].count == 2 and by_key["paid"].sum == {"/amount": 40.5} and by_key["paid"].max["/amount"] == 30.5
    assert by_key["open"].count == 2 and by_key["open"].avg == {"/amount": 5.0}
    total = orders.query(aggregate=q.aggregate()).groups
    assert total is not None and len(total) == 1 and total[0].key == [] and total[0].count == 4
    assert orders.query().groups is None

    with pytest.raises(ValueError):
        q.aggregate(["/a", "/b", "/c", "/d", "/e"])
    with pytest.raises(ValueError):
        q.aggregate(sum=["/a", "/a"])
    with pytest.raises(ValueError):
        orders.query(aggregate=agg, sort=[q.asc("/status")])
    with pytest.raises(ValueError):
        orders.query(aggregate=agg, count=True)


def test_ttl(ts: TerraScale, fake: Fake) -> None:
    ts.create_database("main")
    db = ts.database("main")
    db.create_collection("sessions")
    sessions = db.collection("sessions")
    assert sessions.get_ttl() is None
    assert sessions.set_ttl("/expiresAt") == "/expiresAt"
    assert json.loads(fake.requests[-1].content) == {"path": "/expiresAt"}
    assert sessions.get_ttl() == "/expiresAt"
    sessions.delete_ttl()
    assert sessions.get_ttl() is None
    with pytest.raises(ValueError):
        sessions.set_ttl("expiresAt")


def test_sql(ts: TerraScale, fake: Fake) -> None:
    ts.create_database("main")
    db = ts.database("main")
    db.create_collection("todos")
    db.collection("todos").create({"title": "x"})
    result = db.sql("SELECT * FROM todos LIMIT 10")
    assert result.matched == 1 and result.rows[0]["value"] == {"title": "x"} and not result.truncated
    assert db.check_sql("SELECT 1")["ok"]
    assert db.explain_sql("SELECT 1")["plan"] == "SCAN"
    res = db.execute("INSERT INTO t VALUES (?, ?)", [1, True])
    assert json.loads(fake.requests[-1].content) == {"statements": [{"sql": "INSERT INTO t VALUES (?, ?)", "args": [1, 1]}]}
    assert res.rows == [[2]]
    out = db.batch(["SELECT 1", ("SELECT :a", {"a": 1})])
    assert len(out) == 2
    assert json.loads(fake.requests[-1].content)["statements"][1] == {"sql": "SELECT :a", "named": {"a": 1}}


def test_transaction(ts: TerraScale, fake: Fake) -> None:
    ts.create_database("main")
    db = ts.database("main")
    uid = new_id()
    with db.transaction() as tx:
        tx.create("orders", {"total": 5})
        tx.put("users", uid, {"orders": 1}, if_none_match_any=True)
        tx.assert_count("orders", q.gte("/total", 0), gte=0)
    assert tx.result is not None and len(tx.result.results) == 3
    body = json.loads(fake.requests[-1].content)
    assert [o["op"] for o in body["ops"]] == ["create", "put", "assert_count"]
    assert body["ops"][1]["if_none_match"] == "*"

    tx2 = db.transaction().assert_absent("users", uid).delete("users", uid)
    with pytest.raises(ConflictError) as e:
        tx2.commit()
    assert e.value.op == 0

    with pytest.raises(RuntimeError):
        with db.transaction() as tx3:
            tx3.create("orders", {"total": 1})
            raise RuntimeError("abort")
    assert tx3.result is None


def test_retries_reads_on_429_but_not_writes(ts: TerraScale, fake: Fake, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("terrascale.client.time.sleep", lambda s: None)
    ts.create_database("main")
    fake.fail_next.append((429, {"error": {"code": "rate_limited"}}, {"Retry-After": "1"}))
    assert ts.database("main").info().name == "main"
    fake.fail_next.append((429, {"error": {"code": "rate_limited"}}, {"Retry-After": "2"}))
    with pytest.raises(RateLimitError) as e:
        ts.create_database("other")
    assert e.value.retry_after == 2.0


def test_mutation_transport_error_is_commit_uncertain(fake: Fake) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.host == "auth.test":
            return fake.handle(request)
        raise httpx.ConnectError("boom", request=request)

    http = httpx.Client(transport=httpx.MockTransport(handler))
    client = TerraScale(API_KEY, project=PROJECT, base_url=BASE, issuer=ISSUER, http_client=http)
    with pytest.raises(CommitUncertainError) as caught:
        client.database("main").collection("c").create({"a": 1})
    assert caught.value.request_id is None


def test_error_mapping() -> None:
    err = terrascale.errors.error_for(409, {"error": {"code": "unique_violation", "message": "dup", "op": "2"}})
    assert isinstance(err, terrascale.UniqueViolationError) and isinstance(err, ConflictError)
    assert err.op == 2 and str(err) == "409 unique_violation: dup (op 2)"
    err = terrascale.errors.error_for(502, b"<html>")
    assert type(err) is terrascale.APIError and err.code == "http_502"


def test_ids() -> None:
    ids = [new_id() for _ in range(1000)]
    assert ids == sorted(ids) and len(set(ids)) == 1000
    assert all(terrascale.is_valid_id(i) for i in ids)
    assert not terrascale.is_valid_id("01jabc")
