"""Adversarial HTTP contracts, exercised through both client surfaces."""
import inspect
import json
from typing import Any

import httpx
import pytest
from fake import API_KEY, PROJECT

from terrascale import (
    AsyncTerraScale,
    CommitUncertainError,
    InvalidResponseError,
    TerraScale,
    TerraScaleError,
    TokenError,
)


async def resolve(value: Any) -> Any:
    return await value if inspect.isawaitable(value) else value


@pytest.fixture(params=[False, True], ids=["sync", "async"])
async def sdk(request: Any) -> Any:
    calls = []
    responses = []

    def handler(req: httpx.Request) -> httpx.Response:
        calls.append(req)
        result = responses.pop(0)
        if isinstance(result, Exception):
            raise result
        return result(req) if callable(result) else result

    http_cls, cls = (httpx.AsyncClient, AsyncTerraScale) if request.param else (httpx.Client, TerraScale)
    http = http_cls(transport=httpx.MockTransport(handler))
    client = cls(project=PROJECT, token="fixture-token", http_client=http, max_attempts=2)
    yield client, calls, responses
    await resolve(http.aclose() if request.param else http.close())


@pytest.mark.parametrize("kind", ["put", "patch", "delete", "transaction", "placement", "ttl"])
async def test_lost_ack_is_never_replayed(sdk: Any, kind: str) -> None:
    ts, calls, responses = sdk
    responses.extend([httpx.ReadError("lost acknowledgement"), httpx.Response(412)])
    db = ts.database("main")
    c = db.collection("c")
    tx = db.transaction()
    tx.put("c", PROJECT, {}, if_match="v1")
    send = {"put": lambda: c.put(PROJECT, {}, if_match="v1"),
            "patch": lambda: c.patch(PROJECT, merge={}, if_match="v1"),
            "delete": lambda: c.delete(PROJECT, if_match="v1"), "transaction": tx.commit,
            "placement": lambda: db.update(home_region="gru"), "ttl": lambda: c.set_ttl("/expires")}[kind]
    with pytest.raises(CommitUncertainError):
        await resolve(send())
    assert len(calls) == 1


@pytest.mark.parametrize("body", [b"{", b"{}", b"[]", b"null", b"gzip"])
@pytest.mark.parametrize("write", [False, True])
async def test_malformed_success_is_typed(sdk: Any, body: bytes, write: bool) -> None:
    ts, calls, responses = sdk
    responses.extend([lambda _: httpx.Response(201, headers={"content-encoding": "gzip"}, content=b"not-a-gzip-stream")]
                     if body == b"gzip" else [httpx.Response(201 if write else 200, content=body)])
    c = ts.database("main").collection("c")
    with pytest.raises(CommitUncertainError if write else InvalidResponseError):
        await resolve(c.create({}) if write else c.get(PROJECT))
    assert len(calls) == 1


@pytest.mark.parametrize("status", [200, 500, 502, 503, "transport"])
async def test_context_consumes_transaction_before_dispatch(sdk: Any, status: int | str) -> None:
    ts, calls, responses = sdk
    tx = ts.database("main").transaction()
    tx.create("c", {})
    ack = {"offset": "1", "durability": "local", "results": [{"id": PROJECT, "version": "v1"}]}
    responses.append(httpx.ReadError("lost ack") if status == "transport" else
                     httpx.Response(status, json=ack if status == 200 else {"error": {"code": "internal_error"}}))
    await resolve(tx.__aenter__() if isinstance(ts, AsyncTerraScale) else tx.__enter__())
    if status == 200:
        await resolve(tx.commit())
    else:
        with pytest.raises(CommitUncertainError) as caught:
            await resolve(tx.commit())
        assert (caught.value.status, caught.value.code) == (
            (0, "commit_uncertain") if status == "transport" else (status, "internal_error"))
    await resolve(tx.__aexit__(None) if isinstance(ts, AsyncTerraScale) else tx.__exit__(None))
    with pytest.raises(TerraScaleError, match="already submitted"):
        tx.create("c", {})
    with pytest.raises(TerraScaleError, match="already submitted"):
        await resolve(tx.commit())
    assert len(calls) == 1


@pytest.mark.parametrize("failure", ["transport", "gzip"])
async def test_token_failures_stay_typed(sdk: Any, failure: str) -> None:
    ts, calls, responses = sdk
    ts = type(ts)(API_KEY, project=PROJECT, http_client=ts._http)
    responses.append((lambda _: httpx.Response(201, headers={"content-encoding": "gzip"}, content=b"not-a-gzip-stream"))
                     if failure == "gzip" else httpx.ConnectError("auth failed"))
    with pytest.raises(TokenError) as err:
        await resolve(ts.database("main").collection("c").create({}))
    assert err.value.code == "token_transport_error" and len(calls) == 1
    assert calls[0].url.path.endswith("/oauth/token")

@pytest.mark.parametrize("change", [{"id": "01ARZ3NDEKTSV4RRFFQ69G5FAV"}, {"version": None},
                                  {"offset": "invalid"}, {"durability": None}])
async def test_malformed_write_metadata_is_uncertain(sdk: Any, change: dict[str, Any]) -> None:
    ts, calls, responses = sdk
    responses.append(httpx.Response(200, json={"id": PROJECT, "version": "v1", "offset": "1",
                                              "durability": "local", **change}))
    with pytest.raises(CommitUncertainError):
        await resolve(ts.database("main").collection("c").put(PROJECT, {}))
    assert len(calls) == 1


@pytest.mark.parametrize("kind", ["database", "drop", "ttl", "collection"])
async def test_nonempty_invalid_acknowledgements(sdk: Any, kind: str) -> None:
    ts, calls, responses = sdk
    responses.append(httpx.Response(200, json={"results": [{"columns": []}]} if kind == "sql" else
                                   {"error": {"code": "internal_error"}}))
    db = ts.database("main")
    send = {"sql": lambda: db.execute("INSERT INTO t VALUES (1)"), "database": lambda: ts.create_database("main"),
            "drop": db.delete, "ttl": db.collection("c").delete_ttl, "collection": db.collection("c").drop}[kind]
    with pytest.raises(CommitUncertainError):
        await resolve(send())
    assert len(calls) == 1


async def test_submission_snapshot_survives_caller_mutation(sdk: Any) -> None:
    ts, _, responses = sdk
    tx = ts.database("main").transaction()
    tx.put("c", PROJECT, {"nested": {"n": 1}})

    def after_send(req: httpx.Request) -> httpx.Response:
        tx.ops[0]["id"] = "changed"
        tx.ops[0]["document"]["nested"]["n"] = 2
        tx.ops.clear()
        assert json.loads(req.content)["ops"] == [
            {"op": "put", "collection": "c", "id": PROJECT, "document": {"nested": {"n": 1}}}]
        return httpx.Response(200, json={"offset": "1", "durability": "local", "results": [{"id": PROJECT, "version": "v1"}]})

    responses.append(after_send)
    assert (await resolve(tx.commit())).results[0]["id"] == PROJECT
    with pytest.raises(TerraScaleError, match="already submitted"):
        await resolve(tx.commit())


@pytest.mark.parametrize("op", ["1e999", "true", "1.5"])
async def test_invalid_error_metadata_cannot_hide_uncertainty(sdk: Any, op: str) -> None:
    ts, calls, responses = sdk
    responses.append(httpx.Response(500, content='{"error":{"code":"internal_error","op":' + op + '}}'))
    with pytest.raises(CommitUncertainError) as err:
        await resolve(ts.database("main").collection("c").create({}))
    assert err.value.op is None and err.value.code == "internal_error" and len(calls) == 1


@pytest.mark.parametrize("result", [None, {"asserted": False}, {"asserted": 1},
                                   {"asserted": True, "count": False}, {"asserted": True, "count": "-1"},
                                   {"asserted": True, "count": "01"}, {"asserted": True, "count": "18446744073709551616"},
                                   {"asserted": True, "count": "1"}])
@pytest.mark.parametrize("mixed", [False, True])
@pytest.mark.parametrize("bounds", [{"lte": 0}, {"gte": 2}])
async def test_assertion_acknowledgements(sdk: Any, result: Any, mixed: bool, bounds: dict[str, int]) -> None:
    ts, _, responses = sdk
    tx = ts.database("main").transaction()
    tx.assert_count("c", **bounds)
    results = [result]
    if mixed:
        tx.put("c", PROJECT, {})
        results.append({"id": PROJECT, "version": "v1"})
    responses.append(httpx.Response(200, json={"offset": "1", "durability": "local", "results": results}))
    with pytest.raises(CommitUncertainError if mixed else InvalidResponseError):
        await resolve(tx.commit())


@pytest.mark.parametrize("method,path", [("get_ttl", False), ("get_ttl", "expires"), ("set_ttl", False),
                                       ("set_ttl", "/wrong"), ("delete_ttl", "/still-enabled")])
async def test_ttl_acknowledgements(sdk: Any, method: str, path: Any) -> None:
    ts, _, responses = sdk
    responses.append(httpx.Response(200, json={"path": path}))
    send = getattr(ts.database("main").collection("c"), method)
    with pytest.raises(InvalidResponseError if method == "get_ttl" else CommitUncertainError):
        await resolve(send("/expires") if method == "set_ttl" else send())
