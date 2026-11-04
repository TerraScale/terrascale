"""Customer-function wire contracts; all credentials and execution are fake."""

import inspect
import json

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import APIError, AsyncTerraScale, CommitUncertainError, TerraScale
from terrascale.functions import AsyncDatabaseFunctions, DatabaseFunctions, FunctionDeployment, FunctionTrigger

SOURCE = 'function handler(event) { return event.body; }'
TRIGGER = {"collection": "orders", "events": ["upsert"]}
FUNCTION = {"name": "hello", "source": SOURCE, "trigger": TRIGGER, "cron": None,
            "createdAt": "2026-10-07T12:00:00Z", "updatedAt": "2026-10-07T12:00:01Z"}
LOG = {"id": "01ARZ3NDEKTSV4RRFFQ69G5FAC", "at": FUNCTION["updatedAt"], "trigger": "http", "status": "ok",
       "durationMs": "3", "lines": ["hello Ada"], "error": None}
DELIVERY = {"id": "01ARZ3NDEKTSV4RRFFQ69G5FAB", "createdAt": FUNCTION["createdAt"], "status": "pending", "attempts": "2",
            "lastAttemptAt": None, "nextAttemptAt": FUNCTION["updatedAt"], "httpStatus": "500", "errorCode": "function_error"}


@pytest.fixture(params=[False, True])
async def functions(request):
    expected = []

    def respond(r):
        method, suffix, params, body, result, status = expected.pop(0)
        assert r.method == method and r.url.path == f"/v1/projects/{PROJECT}/databases/main/functions" + suffix
        assert _route_ok(r.method, r.url.path)
        assert dict(r.url.params) == params and (json.loads(r.content) if r.content else None) == body
        if method == "POST" and body is None:
            assert r.content == b"null" and r.headers["content-type"] == "application/json"
        assert r.headers["authorization"] == "Bearer fixture-only"
        if isinstance(result, Exception):
            raise result
        if status == 204:
            return httpx.Response(status)
        if status == 202:
            return httpx.Response(status, text=result)
        if result is None:
            return httpx.Response(status, content=b"null", headers={"content-type": "application/json"})
        return httpx.Response(status, json=result)

    transport = httpx.MockTransport(respond)
    http = httpx.AsyncClient(transport=transport) if request.param else httpx.Client(transport=transport)
    client = (AsyncTerraScale if request.param else TerraScale)(token="fixture-only", project=PROJECT, http_client=http)
    handle = (AsyncDatabaseFunctions if request.param else DatabaseFunctions)(client.database("main"))

    async def call(method, *args, **kwargs):
        value = getattr(handle, method)(*args, **kwargs)
        return await value if inspect.isawaitable(value) else value

    yield expected, call
    assert expected == []
    if request.param:
        await http.aclose()
    else:
        http.close()


async def test_function_contract(functions):
    expected, call = functions
    expected.extend([
        ("GET", "", {}, None, [FUNCTION | {"source": None}], 200),
        ("GET", "/hello", {}, None, FUNCTION, 200),
        ("PUT", "/hello", {}, {"source": SOURCE, "trigger": TRIGGER, "cron": None}, FUNCTION, 200),
        ("POST", "/hello/invoke", {"name": "Ada+/="}, {"name": "Ada"}, [1, False, {"error": "user data"}], 201),
        ("GET", "/hello/logs", {"limit": "1"}, None, [LOG], 200),
        ("GET", "/hello/deliveries", {}, None, [DELIVERY], 200),
        ("DELETE", "/hello", {}, None, None, 204),
    ])
    listed = await call("list_functions")
    assert listed[0].source is None and listed[0].name == "hello"
    function = await call("get_function", "hello")
    assert function.source == SOURCE and function.trigger == FunctionTrigger("orders", ["upsert"])
    assert function.cron is None and function.created_at == FUNCTION["createdAt"] and function.updated_at == FUNCTION["updatedAt"]
    assert await call("deploy_function", "hello", FunctionDeployment(SOURCE, FunctionTrigger("orders", ["upsert"]))) == function
    assert await call("invoke_function", "hello", {"name": "Ada"}, query={"name": "Ada+/="}) == [1, False, {"error": "user data"}]
    log = (await call("list_function_logs", "hello", limit=1))[0]
    assert log.id == LOG["id"] and log.at == LOG["at"] and log.trigger == "http" and log.status == "ok"
    assert log.duration_ms == 3 and log.lines == ["hello Ada"] and log.error is None
    delivery = (await call("list_function_deliveries", "hello"))[0]
    assert delivery.id == DELIVERY["id"] and delivery.created_at == DELIVERY["createdAt"] and delivery.status == "pending"
    assert delivery.attempts == 2 and delivery.last_attempt_at is None and delivery.next_attempt_at == FUNCTION["updatedAt"]
    assert delivery.http_status == 500 and delivery.error_code == "function_error"
    assert await call("delete_function", "hello") is None


async def test_function_empty_and_scalar_results(functions):
    expected, call = functions
    for method, suffix in [("list_functions", ""), ("list_function_logs", "/hello/logs"),
                           ("list_function_deliveries", "/hello/deliveries")]:
        expected.append(("GET", suffix, {}, None, [], 200))
        assert await call(method, *(() if not suffix else ("hello",))) == []
    for result, status in [(None, 200), (False, 200), (0, 200), ("hello", 202), (None, 204)]:
        expected.append(("POST", "/hello/invoke", {}, None, result, status))
        assert await call("invoke_function", "hello", None) == result


async def test_function_validation_and_uncertainty(functions):
    expected, call = functions
    for method, args, kwargs in [
        ("get_function", ("../other",), {}), ("list_function_logs", ("hello",), {"limit": 101}),
        ("deploy_function", ("hello", FunctionDeployment("x" * (256 * 1024 + 1))), {}),
        ("invoke_function", ("hello", "x" * (1024 * 1024)), {}),
    ]:
        with pytest.raises(ValueError):
            await call(method, *args, **kwargs)
    for method, verb, suffix, body, args in [
        ("deploy_function", "PUT", "/hello", {"source": SOURCE, "trigger": None, "cron": None}, (FunctionDeployment(SOURCE),)),
        ("invoke_function", "POST", "/hello/invoke", {}, ({},)), ("delete_function", "DELETE", "/hello", None, ()),
    ]:
        expected.append((verb, suffix, {}, body, httpx.ReadError("fixture: response lost after mutation"), 200))
        with pytest.raises(CommitUncertainError):
            await call(method, "hello", *args)
    expected.append(("GET", "", {}, None, {}, 200))
    with pytest.raises(APIError, match="invalid_response"):
        await call("list_functions")


def test_function_signatures_match():
    methods = {n for n in dir(DatabaseFunctions) if not n.startswith("_")}
    assert methods == {n for n in dir(AsyncDatabaseFunctions) if not n.startswith("_")}
    for method in methods:
        assert inspect.signature(getattr(DatabaseFunctions, method)) == inspect.signature(getattr(AsyncDatabaseFunctions, method))


def test_decoder_supports_function_shapes_after_shared_transport_refresh():
    with TerraScale(token="fixture-only", project=PROJECT) as client:
        handle = DatabaseFunctions(client.database("main"))
        assert handle._call("GET", None, lambda b: b).decode(httpx.Response(200, json=[])) == []
        invoke = handle._call("POST", "hello", lambda b: b, suffix="invoke")
        assert invoke.decode(httpx.Response(201, json={"error": "user result"})) == {"error": "user result"}
        assert invoke.decode(httpx.Response(200, json=False)) is False
        with pytest.raises(CommitUncertainError):
            invoke.decode(httpx.Response(200, content=b"{", headers={"content-type": "application/json"}))
        delete = handle._call("DELETE", "hello", lambda _: None)
        with pytest.raises(CommitUncertainError):
            delete.decode(httpx.Response(204, content=b"unexpected"))
        assert handle._call("GET", "invoke", lambda b: b).content() is None
