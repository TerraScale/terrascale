import inspect
import json
from pathlib import Path
from typing import Any

import httpx
import pytest
from fake import API_KEY
from test_contract import _route_ok

from terrascale import APIError, AsyncTerraScale, TerraScale

FIXTURES = Path(__file__).parents[2] / "dotnet/contracts/terrabase/placement"
PROJECT = "01JA0000000000000000000001"
DATABASE = "01JA0000000000000000000004"


def fixture(name: str) -> Any:
    return json.loads((FIXTURES / f"{name}.json").read_text())


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
    http = http_cls(transport=httpx.MockTransport(handler))
    yield cls(API_KEY, project=PROJECT, token="fixture-token", http_client=http), calls, responses
    await resolve(http.aclose() if request.param else http.close())


async def test_placement_matches_recorded_contract(sdk: Any) -> None:
    client, calls, responses = sdk
    cases = [
        ("create-with-replicas", lambda: client.create_database("orders", home_region="gru", replica_regions=["sjc"])),
        ("create-all-regions", lambda: client.create_database("orders", home_region="gru", all_regions=True)),
        ("set-replicas", lambda: client.set_database_placement(DATABASE, replica_regions=["fra", "sjc"])),
        ("set-all-regions", lambda: client.set_database_placement(DATABASE, all_regions=True)),
        ("move-home", lambda: client.move_database_home(DATABASE, "sjc")),
    ]
    for name, operation in cases:
        recorded = fixture(name)
        responses.append(httpx.Response(recorded["response"]["status"], json=recorded["response"]["body"]))
        result = await resolve(operation())
        req = calls[-1]
        assert (req.method, req.url.path, json.loads(req.content)) == (
            recorded["request"]["method"], recorded["request"]["path"], recorded["request"]["body"])
        assert _route_ok(req.method, req.url.path)
        body = recorded["response"]["body"]
        assert (result.home_region, result.replica_regions, result.all_regions) == (
            body["homeRegion"], body["replicaRegions"], body["allRegions"])
    assert calls[-1].extensions["timeout"]["read"] is None


async def test_placement_refusals_and_unresolved_move(sdk: Any) -> None:
    client, calls, responses = sdk
    with pytest.raises(ValueError):
        await resolve(client.set_database_placement(DATABASE, replica_regions=["sjc"], all_regions=True))
    with pytest.raises(ValueError):
        await resolve(client.set_database_placement(DATABASE))
    assert calls == []
    refused = fixture("move-home-refused")
    responses.append(httpx.Response(503, headers={"Retry-After": "0"},
                                    json={"error": {"code": "move_unresolved", "message": "settling"}}))
    responses.append(httpx.Response(400, json=refused["response"]["body"]))
    with pytest.raises(APIError) as raised:
        await resolve(client.move_database_home(DATABASE, "fra"))
    assert (raised.value.status, raised.value.code) == (400, "invalid_request")
    assert [(r.method, r.url.path, json.loads(r.content)) for r in calls] == [
        (refused["request"]["method"], refused["request"]["path"], refused["request"]["body"])] * 2
