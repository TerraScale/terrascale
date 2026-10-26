"""Bucket metadata contracts; no object-store capabilities or network I/O."""
import inspect
import json

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import APIError, AsyncTerraScale, CommitUncertainError, TerraScale
from terrascale.rules import CollectionRules, OperationGrants
from terrascale.storage import AsyncStorage, Storage

ID = "01ARZ3NDEKTSV4RRFFQ69G5FAC"
DENY = {"owner": None, "read": {"owner": False, "public": False, "roles": []},
        "write": {"owner": False, "public": False, "roles": []}}
BUCKET = {"id": ID, "name": "avatars", "region": "gru", "rules": DENY, "future": "retained"}


@pytest.fixture(params=[False, True])
async def storage(request):
    expected = []
    def respond(r):
        method, suffix, params, body, status, result = expected.pop(0)
        assert r.method == method and r.url.path == f"/v1/projects/{PROJECT}/buckets" + suffix
        assert _route_ok(r.method, r.url.path) and dict(r.url.params) == params
        assert (json.loads(r.content) if r.content else None) == body
        if isinstance(result, Exception):
            raise result
        return httpx.Response(status, content=result) if isinstance(result, bytes) else httpx.Response(status, json=result)
    transport = httpx.MockTransport(respond)
    http = httpx.AsyncClient(transport=transport) if request.param else httpx.Client(transport=transport)
    client = (AsyncTerraScale if request.param else TerraScale)(token="fixture-only", project=PROJECT, http_client=http)
    handle = (AsyncStorage if request.param else Storage)(client)
    async def call(method, *args, **kwargs):
        value = getattr(handle, method)(*args, **kwargs)
        return await value if inspect.isawaitable(value) else value
    yield expected, call
    assert expected == []
    if request.param:
        await http.aclose()
    else:
        http.close()


async def test_bucket_metadata_contract(storage):
    expected, call = storage
    expected.append(("POST", "", {}, {"name": "avatars", "rules": DENY}, 201, BUCKET))
    bucket = await call("create_bucket", "avatars")
    assert bucket.id == ID and bucket.name == "avatars" and bucket.region == "gru"
    assert bucket.rules == CollectionRules() and bucket.raw["future"] == "retained"
    expected.append(("GET", "", {}, None, 200, {"items": [BUCKET], "next": ID}))
    page = await call("list_buckets")
    assert page.items == [bucket] and page.next_cursor == ID
    expected.append(("GET", "", {"after": ID}, None, 200, {"items": [], "next": None}))
    page = await call("list_buckets", after=page.next_cursor)
    assert page.items == [] and page.next_cursor is None
    policy = CollectionRules("/userId", OperationGrants(True, False, None), OperationGrants(roles=["Admin"]))
    expected.append(("PUT", f"/{ID}/rules", {}, {"rules": policy.body()}, 200, BUCKET | {"rules": policy.body()}))
    assert (await call("set_bucket_rules", ID, policy)).rules == policy
    expected.append(("DELETE", f"/{ID}", {}, None, 204, b""))
    assert await call("delete_bucket", ID) is None


async def test_bucket_validation_and_uncertainty(storage):
    expected, call = storage
    for method, args, kwargs in [("delete_bucket", ("../other",), {}), ("list_buckets", (), {"after": "invalid"}),
                                 ("set_bucket_rules", (ID, None), {}), ("create_bucket", ("",), {})]:
        with pytest.raises(ValueError):
            await call(method, *args, **kwargs)
    for result in [{"items": [], "next": "invalid"}, {"items": []}, {"items": {}, "next": None},
                   {"items": [BUCKET | {"rules": None}], "next": None}]:
        expected.append(("GET", "", {}, None, 200, result))
        with pytest.raises(APIError):
            await call("list_buckets")
    for method, verb, suffix, args, body, success in [
        ("create_bucket", "POST", "", ("avatars",), {"name": "avatars", "rules": DENY}, 201),
        ("set_bucket_rules", "PUT", f"/{ID}/rules", (ID, CollectionRules()), {"rules": DENY}, 200),
        ("delete_bucket", "DELETE", f"/{ID}", (ID,), None, 204),
    ]:
        for status, result in [(success, httpx.ReadError("fixture: mutation response lost")),
                               (500, {"error": {"code": "internal_error"}}), (success, b"unexpected")]:
            expected.append((verb, suffix, {}, body, status, result))
            with pytest.raises(CommitUncertainError):
                await call(method, *args)
    expected.append(("POST", "", {}, {"name": "avatars", "rules": DENY}, 200, BUCKET))
    with pytest.raises(CommitUncertainError):
        await call("create_bucket", "avatars")


def test_storage_signatures_match():
    methods = {n for n in dir(Storage) if not n.startswith("_")}
    assert methods == {n for n in dir(AsyncStorage) if not n.startswith("_")}
    for method in methods:
        assert inspect.signature(getattr(Storage, method)) == inspect.signature(getattr(AsyncStorage, method))
