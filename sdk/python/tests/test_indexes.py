"""Index fixtures from the production OpenAPI, including v1 recommendation outcomes."""

import inspect
import json

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import APIError, AsyncTerraScale, CommitUncertainError, TerraScale
from terrascale.indexes import AsyncDatabaseIndexes, DatabaseIndexes, IndexRequest

ROOT = f"/v1/projects/{PROJECT}/databases/main"
INDEX = {"name": "people_email", "collection": "people", "fields": ["email"], "state": "active"}
VECTOR = INDEX | {"fields": ["embedding"], "kind": "vector", "dimensions": "3", "state": "building"}
RECOMMENDATION = {"id": "people:profile.email", "collection": "people", "fields": ["profile.email"],
                  "reason": "observed queries", "queries": str(2**60 + 1)}
CASES = [
    ("list_indexes", (), {"collection": "people & team"}, "GET", "/indexes", 200, None,
     {"items": [INDEX, VECTOR, INDEX | {"dimensions": None}]}),
    ("create_index", (IndexRequest("people", ["embedding"], kind="vector", dimensions=3),), {}, "POST", "/indexes", 201,
     {"collection": "people", "fields": ["embedding"], "kind": "vector", "dimensions": 3}, VECTOR),
    ("drop_index", ("people_email",), {}, "DELETE", "/indexes/people_email", 204, None, None),
    ("list_index_recommendations", (), {}, "GET", "/index-recommendations", 200, None, {"items": [RECOMMENDATION]}),
    ("apply_index_recommendation", (RECOMMENDATION["id"],), {}, "POST",
     "/index-recommendations/people:profile.email/apply", 201, None, {"status": "applied", "index": INDEX}),
    ("apply_index_recommendation", (RECOMMENDATION["id"],), {}, "POST",
     "/index-recommendations/people:profile.email/apply", 200, None, {"status": "already-applied", "index": INDEX}),
]


async def invoke(asynchronous, handler, method, *args, **kwargs):
    transport = httpx.MockTransport(handler)
    if asynchronous:
        async with httpx.AsyncClient(transport=transport) as http:
            client = AsyncTerraScale(token="fixture-only", project=PROJECT, http_client=http)
            return await getattr(AsyncDatabaseIndexes(client.database("main")), method)(*args, **kwargs)
    with httpx.Client(transport=transport) as http:
        client = TerraScale(token="fixture-only", project=PROJECT, http_client=http)
        return getattr(DatabaseIndexes(client.database("main")), method)(*args, **kwargs)


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("method,args,kwargs,verb,suffix,status,body,response", CASES)
async def test_index_contract(asynchronous, method, args, kwargs, verb, suffix, status, body, response):
    def respond(r):
        assert r.method == verb and r.url.path == ROOT + suffix and _route_ok(r.method, r.url.path)
        assert dict(r.url.params) == (kwargs if method == "list_indexes" else {})
        assert r.headers["authorization"] == "Bearer fixture-only"
        assert (json.loads(r.content) if r.content else None) == body
        if method == "apply_index_recommendation":
            assert b"people%3Aprofile.email" in r.url.raw_path
        return httpx.Response(status) if status == 204 else httpx.Response(status, json=response)
    result = await invoke(asynchronous, respond, method, *args, **kwargs)
    if method == "list_indexes":
        assert result[0].kind == "btree" and result[0].dimensions is None and result[0] == result[2]
        assert result[1].state == "building" and result[1].dimensions == 3
    elif method == "create_index":
        assert result.kind == "vector" and result.dimensions == 3
    elif method == "list_index_recommendations":
        assert result[0].queries == 2**60 + 1 and result[0].id == RECOMMENDATION["id"]
    elif method == "apply_index_recommendation":
        assert result.status == response["status"] and result.index.name == INDEX["name"]
    else:
        assert result is None


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("method,args,kwargs,verb,suffix,status,body,response", [x for x in CASES if x[3] != "GET"])
@pytest.mark.parametrize("failure", ["lost", "server", "malformed"])
async def test_mutations_uncertain_without_replay(asynchronous, method, args, kwargs, verb, suffix,
                                                 status, body, response, failure):
    requests = []
    def respond(r):
        requests.append(r)
        if failure == "lost":
            raise httpx.ReadError("fixture-only", request=r)
        return httpx.Response(500 if failure == "server" else status, json={})
    with pytest.raises(CommitUncertainError):
        await invoke(asynchronous, respond, method, *args, **kwargs)
    assert len(requests) == 1


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("method,response", [("list_indexes", {"items": []}),
                                           ("list_index_recommendations", {"items": []})])
async def test_read_replay(asynchronous, method, response, monkeypatch):
    requests = []
    monkeypatch.setattr("terrascale._spec.backoff", lambda *_: 0)
    def respond(r):
        requests.append(r)
        if len(requests) == 1:
            raise httpx.ReadError("fixture-only", request=r)
        return httpx.Response(200, json=response)
    await invoke(asynchronous, respond, method)
    assert len(requests) == 2


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("method,args,response", [
    ("list_indexes", (), {"items": {}}),
    ("list_indexes", (), {"items": [VECTOR | {"dimensions": 3.1}]}),
])
async def test_malformed_read(asynchronous, method, args, response):
    with pytest.raises(APIError) as error:
        await invoke(asynchronous, lambda _: httpx.Response(200, json=response), method, *args)
    assert error.value.code == "invalid_response"


@pytest.mark.parametrize("index_request", [IndexRequest("people", []), IndexRequest("people", ["_id"]),
                                    IndexRequest("people", ["email", "email"]), IndexRequest("people", ["email"], name="BAD"),
                                    IndexRequest("people", ["v"], kind="vector", dimensions=True),
                                    IndexRequest("people", ["a", "b"], kind="vector", dimensions=3),
                                    IndexRequest("people", ["email"], dimensions=3)])
def test_invalid_requests(index_request):
    with pytest.raises(ValueError):
        index_request.body()


def test_index_twins_and_default_request():
    methods = {x for x in dir(DatabaseIndexes) if not x.startswith("_")}
    assert methods == {x for x in dir(AsyncDatabaseIndexes) if not x.startswith("_")}
    for method in methods:
        assert inspect.signature(getattr(DatabaseIndexes, method)) == inspect.signature(getattr(AsyncDatabaseIndexes, method))
    assert IndexRequest("people", ["email"]).body() == {"collection": "people", "fields": ["email"], "kind": "btree"}
