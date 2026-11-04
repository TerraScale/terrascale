import base64
import inspect
import json
from pathlib import Path
from typing import Any

import httpx
import pytest
from fake import API_KEY
from test_contract import _route_ok

from terrascale import APIError, AsyncTerraScale, TerraScale

FIXTURE = json.loads((Path(__file__).parents[1] / "contracts/agent/databases.json").read_text())
PROJECT = FIXTURE["project"]
DATABASE = FIXTURE["database"]


async def resolve(value: Any) -> Any:
    return await value if inspect.isawaitable(value) else value


@pytest.fixture(params=[False, True], ids=["sync", "async"])
async def sdk(request: Any) -> Any:
    calls: list[httpx.Request] = []
    responses: list[httpx.Response] = []

    def handler(req: httpx.Request) -> httpx.Response:
        calls.append(req)
        return responses.pop(0)

    http_cls, cls = (httpx.AsyncClient, AsyncTerraScale) if request.param else (httpx.Client, TerraScale)
    http = http_cls(transport=httpx.MockTransport(handler), headers={"X-Unrelated-Credential": "fixture"},
                    cookies={"session": "fixture"}, follow_redirects=True)
    client = cls(API_KEY, project=PROJECT, token="fixture-token", region="gru", http_client=http)
    yield client, calls, responses, http
    await resolve(http.aclose() if request.param else http.close())


async def test_agent_database_lifecycle_matches_contract(sdk: Any) -> None:
    client, calls, responses, _ = sdk
    operations = [
        lambda: client.create_database("agent", ttl_seconds=3600),
        lambda: client.fork_database("agent", "copy", ttl_seconds=600),
        lambda: client.fork_database(DATABASE, "permanent"),
        lambda: client.fork_database(DATABASE, "restored", point_in_time="2026-11-03T23:59:00Z", ttl_seconds=60),
        lambda: client.set_database_ttl("agent", 600),
        lambda: client.clear_database_ttl(DATABASE),
        lambda: client.create_scoped_key(DATABASE, "read_write", project=PROJECT, ttl_seconds=600),
        lambda: client.create_scoped_key(DATABASE, "read"),
        lambda: client.delete_database("agent"),
    ]
    for operation, case in zip(operations, FIXTURE["cases"], strict=True):
        expected, response = case["request"], case["response"]
        responses.append(httpx.Response(response["status"], json=response["body"]) if response["body"] is not None
                         else httpx.Response(response["status"]))
        result = await resolve(operation())
        req = calls[-1]
        assert (req.method, req.url.path) == (expected["method"], expected["path"])
        assert _route_ok(req.method, req.url.path)
        assert (json.loads(req.content) if req.content else None) == expected["body"]
        if case["name"].startswith("scoped_key"):
            assert str(req.url) == "https://auth.terrascale.tech" + expected["path"]
            assert req.headers["authorization"] == "Basic " + base64.b64encode(f"{API_KEY[:26]}:{API_KEY}".encode()).decode()
            assert not {"cookie", "x-unrelated-credential", "x-terrascale-region-pin"} & set(req.headers)
            assert (result.key, result.id, result.project_id, result.database, result.access, result.expires_at) == (
                response["body"]["key"], response["body"]["id"], response["body"]["project_id"], DATABASE,
                response["body"]["access"], response["body"]["expires_at"])
            assert result.key not in repr(result)
        else:
            assert req.url.host == "api.terrascale.tech"
            assert req.headers["authorization"] == "Bearer fixture-token"
            if result is not None:
                assert (result.id, result.name, result.branched_from, result.expires_at) == (
                    response["body"]["id"], response["body"]["name"], response["body"]["branchedFrom"],
                    response["body"]["expiresAt"])
            else:
                assert case["name"] == "delete"
    assert len(calls) == len(operations)


async def test_scoped_key_refusals_and_invalid_agent_requests_are_not_replayed(sdk: Any,
                                                                            monkeypatch: pytest.MonkeyPatch) -> None:
    original, calls, responses, http = sdk
    client = type(original)(API_KEY, project=PROJECT, http_client=http)
    for error in FIXTURE["scoped_key_errors"]:
        calls.clear()
        responses.append(httpx.Response(error["status"], json=error["body"], headers=error["headers"]))
        with pytest.raises(APIError) as raised:
            await resolve(client.create_scoped_key(DATABASE, "read"))
        assert (raised.value.status, raised.value.code) == (error["status"], error["body"]["error"])
        assert raised.value.retry_after == (7 if error["status"] == 429 else None)
        assert len(calls) == 1
    calls.clear()
    responses.append(httpx.Response(302, headers={"Location": "https://other.test/credential-sink"}))
    with pytest.raises(APIError):
        await resolve(client.create_scoped_key(DATABASE, "read"))
    assert len(calls) == 1
    calls.clear()
    for invalid in (
        lambda: client.create_database("agent", ttl_seconds=59),
        lambda: client.fork_database("agent", "copy", ttl_seconds=31_536_001),
        lambda: client.set_database_ttl(DATABASE, True),
        lambda: client.create_scoped_key("agent", "read"),
        lambda: client.create_scoped_key(DATABASE, "admin"),
        lambda: client.create_scoped_key(DATABASE, "read", ttl_seconds=2_592_001),
    ):
        with pytest.raises(ValueError):
            await resolve(invalid())
    assert calls == []
    monkeypatch.delenv("TERRASCALE_API_KEY", raising=False)
    token_only = type(original)(project=PROJECT, token="fixture-token", http_client=http)
    with pytest.raises(ValueError, match="api_key"):
        await resolve(token_only.create_scoped_key(DATABASE, "read"))
    assert calls == []
