"""Contract fixtures for collection policies and denying defaults."""
import inspect
import json

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import APIError, AsyncTerraScale, CommitUncertainError, TerraScale
from terrascale.rules import AsyncCollectionPermissions, CollectionPermissions, CollectionRules, OperationGrants

GRANTS = {"owner": False, "public": False, "roles": []}
POLICY = {"owner": None, "read": GRANTS, "write": GRANTS}


@pytest.fixture(params=[False, True])
async def permissions(request):
    expected = []
    def respond(r):
        method, body, status, result = expected.pop(0)
        assert r.method == method and r.url.path == f"/v1/projects/{PROJECT}/databases/main/collections/people/rules"
        assert _route_ok(r.method, r.url.path) and not r.url.params
        assert (json.loads(r.content) if r.content else None) == body
        if isinstance(result, Exception):
            raise result
        return httpx.Response(status, json=result)
    transport = httpx.MockTransport(respond)
    http = httpx.AsyncClient(transport=transport) if request.param else httpx.Client(transport=transport)
    client = (AsyncTerraScale if request.param else TerraScale)(token="fixture-only", project=PROJECT, http_client=http)
    collection = client.database("main").collection("people")
    handle = (AsyncCollectionPermissions if request.param else CollectionPermissions)(collection)
    async def call(method, *args):
        value = getattr(handle, method)(*args)
        return await value if inspect.isawaitable(value) else value
    yield expected, call
    assert expected == []
    if request.param:
        await http.aclose()
    else:
        http.close()


async def test_rules_wire_contract(permissions):
    expected, call = permissions
    expected.append(("GET", None, 200, {"rules": None}))
    assert await call("get_rules") is None
    expected.append(("PUT", POLICY, 200, {"rules": POLICY}))
    assert await call("put_rules", CollectionRules()) == CollectionRules()
    policy = {"owner": "/userId", "read": {"owner": True, "public": False, "roles": None},
              "write": {"owner": False, "public": False, "roles": ["Admin", "admin"]}}
    expected.append(("PUT", policy, 200, {"rules": policy}))
    rules = CollectionRules("/userId", OperationGrants(True, False, None), OperationGrants(roles=["Admin", "admin"]))
    assert await call("put_rules", rules) == rules
    expected.append(("GET", None, 200, {"rules": {"owner": None, "read": None, "write": None}}))
    assert await call("get_rules") == CollectionRules(None, None, None)


async def test_rules_fail_closed_and_preserve_uncertainty(permissions):
    expected, call = permissions
    for rules in [None, CollectionRules(None, None, None), CollectionRules(read=OperationGrants(owner=1)),
                  CollectionRules(read=OperationGrants(roles="Admin"))]:
        with pytest.raises(ValueError):
            await call("put_rules", rules)
    for status, result in [(200, {}), (201, {"rules": POLICY}),
                           (200, {"rules": {"owner": None, "read": {"owner": True}, "write": GRANTS}})]:
        expected.append(("GET", None, status, result))
        with pytest.raises(APIError):
            await call("get_rules")
    for status, result in [(200, {}), (500, {"error": {"code": "internal_error"}}),
                           (200, httpx.ReadError("fixture: mutation response lost"))]:
        expected.append(("PUT", POLICY, status, result))
        with pytest.raises(CommitUncertainError):
            await call("put_rules", CollectionRules())


def test_rules_signatures_and_independent_defaults():
    for method in ("get_rules", "put_rules"):
        sync, aio = getattr(CollectionPermissions, method), getattr(AsyncCollectionPermissions, method)
        assert inspect.signature(sync) == inspect.signature(aio)
    rules = CollectionRules()
    rules.read.roles.append("example")
    assert rules.write.roles == [] and CollectionRules().read.roles == []
