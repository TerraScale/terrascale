"""Schema fixtures from production OpenAPI; no producer binary or credentials."""

import inspect
import json

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import APIError, AsyncTerraScale, CommitUncertainError, TerraScale
from terrascale.tooling import AsyncDatabaseTooling, DatabaseTooling, UniqueDeclaration, UniqueField, UniqueIndex

ROOT = f"/v1/projects/{PROJECT}/databases/main"
DECLARATION = UniqueDeclaration((UniqueField("email", "lowercase"),), (UniqueIndex("tenant_email", ["tenant", "email"]),))
KEY = {"name": "email_key", "fields": ["email"]}
INFERRED = {"collection": "people", "documents": str(2**60 + 1), "sampledDocuments": "1",
            "fields": [{"path": "email", "present": "1", "types": {"string": "1"}}], "uniqueConstraints": [KEY]}
SCHEMA = {"type": "object", "properties": {"email": {"type": "string"}}}
CASES = [("get_database_schema", (), "GET", "/schema", {"items": [{"name": "people", "documents": "1", "bytes": "99"}]}, None),
         ("get_collection_schema", ("people",), "GET", "/schema/people", INFERRED, None),
         ("declare_collection_schema", ("people", DECLARATION), "PUT", "/schema/people", {"constraints": [KEY]},
          {"fields": [{"name": "email", "normalization": "lowercase", "unique": True}],
           "indexes": [{"name": "tenant_email", "fields": ["tenant", "email"], "normalization": "none", "unique": True}]}),
         ("get_declared_collection_schema", ("people",), "GET", "/collections/people/schema", {"schema": None}, None),
         ("put_declared_collection_schema", ("people", SCHEMA), "PUT", "/collections/people/schema",
          {"schema": SCHEMA, "dialect": "https://json-schema.org/draft/2020-12/schema"}, SCHEMA),
         ("delete_declared_collection_schema", ("people",), "DELETE", "/collections/people/schema", {"schema": None}, None)]


async def invoke(asynchronous, handler, method, *args):
    transport = httpx.MockTransport(handler)
    if asynchronous:
        async with httpx.AsyncClient(transport=transport) as http:
            client = AsyncTerraScale(token="fixture-only", project=PROJECT, http_client=http)
            return await getattr(AsyncDatabaseTooling(client.database("main")), method)(*args)
    with httpx.Client(transport=transport) as http:
        client = TerraScale(token="fixture-only", project=PROJECT, http_client=http)
        return getattr(DatabaseTooling(client.database("main")), method)(*args)


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("method,args,verb,suffix,response,body", CASES)
async def test_schema_contract(asynchronous, method, args, verb, suffix, response, body):
    def respond(r):
        assert r.method == verb and r.url.path == ROOT + suffix and _route_ok(r.method, r.url.path)
        assert not r.url.query and r.headers["authorization"] == "Bearer fixture-only"
        assert (json.loads(r.content) if r.content else None) == body
        return httpx.Response(200, json=response)
    result = await invoke(asynchronous, respond, method, *args)
    if method == "get_database_schema":
        assert result.items[0].documents == 1 and result.items[0].bytes == 99
    elif method == "get_collection_schema":
        assert result.documents == 2**60 + 1 and result.sampled_documents == 1 and result.fields[0].types == {"string": 1}
        assert result.unique_constraints[0].normalization == "none"
    elif method == "declare_collection_schema":
        assert result.constraints[0].fields == ["email"]
    else:
        assert result.schema == response["schema"] and result.dialect == response.get("dialect")


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("case", ["lost", "server", "malformed", "status"])
async def test_writes_are_uncertain_and_never_replayed(asynchronous, case):
    requests = []
    def respond(r):
        requests.append(r)
        if case == "lost":
            raise httpx.ReadError("fixture-only", request=r)
        return httpx.Response(500 if case == "server" else 201 if case == "status" else 200, json={})
    with pytest.raises(CommitUncertainError):
        await invoke(asynchronous, respond, "put_declared_collection_schema", "people", SCHEMA)
    assert len(requests) == 1


@pytest.mark.parametrize("asynchronous", [False, True])
async def test_reads_retry_and_nullable_constraints(asynchronous, monkeypatch):
    requests = []
    monkeypatch.setattr("terrascale._spec.backoff", lambda *_: 0)
    def respond(r):
        requests.append(r)
        if len(requests) == 1:
            raise httpx.ReadError("fixture-only", request=r)
        return httpx.Response(200, json=INFERRED | {"uniqueConstraints": None})
    result = await invoke(asynchronous, respond, "get_collection_schema", "people")
    assert result.unique_constraints is None and len(requests) == 2
    with pytest.raises(APIError):
        await invoke(asynchronous, lambda _: httpx.Response(200, json=INFERRED | {"documents": 1.5}),
                     "get_collection_schema", "people")


def test_schema_twins_and_local_limits():
    for method, *_ in CASES:
        assert inspect.signature(getattr(DatabaseTooling, method)) == inspect.signature(getattr(AsyncDatabaseTooling, method))
    with TerraScale(token="fixture-only", project=PROJECT) as client:
        tools = DatabaseTooling(client.database("main"))
        for collection, schema in [("../foreign", {}), ("people", {"description": "x" * 65536})]:
            with pytest.raises(ValueError):
                tools.put_declared_collection_schema(collection, schema)
