"""Point-in-time rewind parity against an HTTP fixture of the producer contract."""

from __future__ import annotations

import inspect
import json
from typing import Any

import httpx
import pytest
from fake import API_KEY, PROJECT

from terrascale import AsyncTerraScale, TerraScale
from terrascale.backups import AsyncDatabaseBackups, DatabaseBackups

REWOUND = {
    "id": "01JAAAAAAAAAAAAAAAAAAAAAA0",
    "projectId": PROJECT,
    "name": "orders",
    "branchedFrom": None,
    "createdAt": "2026-11-08T00:00:00Z",
    "homeRegion": None,
    "replicaRegions": None,
    "placementVersion": 3,
    "allRegions": False,
}


async def resolve(value: Any) -> Any:
    return await value if inspect.isawaitable(value) else value


@pytest.fixture(params=[False, True], ids=["sync", "async"])
async def sdk(request: Any) -> Any:
    calls: list[httpx.Request] = []

    def handler(req: httpx.Request) -> httpx.Response:
        calls.append(req)
        return httpx.Response(200, json=REWOUND)

    http_cls, cls = (httpx.AsyncClient, AsyncTerraScale) if request.param else (httpx.Client, TerraScale)
    http = http_cls(transport=httpx.MockTransport(handler))
    client = cls(API_KEY, project=PROJECT, token="fixture-token", region="gru", http_client=http)
    yield client, calls
    await resolve(http.aclose() if request.param else http.close())


async def test_rewind_point_in_time_posts_the_instant_and_destination(sdk: Any) -> None:
    client, calls = sdk
    handle = client.database("orders")
    backups = AsyncDatabaseBackups(handle) if isinstance(client, AsyncTerraScale) else DatabaseBackups(handle)
    rewound = await resolve(backups.rewind_point_in_time("2026-11-07T23:59:00Z", destination="customer"))
    assert rewound.id == REWOUND["id"]
    assert rewound.name == "orders"
    assert rewound.branched_from is None
    assert len(calls) == 1
    assert calls[0].method == "POST"
    assert calls[0].url.path == f"/v1/projects/{PROJECT}/databases/orders/point-in-time/rewind"
    assert json.loads(calls[0].content) == {"pointInTime": "2026-11-07T23:59:00Z", "destination": "customer"}
