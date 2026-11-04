"""Adversarial HTTP contracts, exercised through both client surfaces."""
import inspect
from typing import Any

import httpx
import pytest
from fake import PROJECT

from terrascale import (
    AsyncTerraScale,
    CommitUncertainError,
    TerraScale,
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


@pytest.mark.parametrize("change", [{"rows": {}}, {"rows": [False]}, {"rows": [[1]]}, {"columns": {}},
                                  {"rows_affected": False}, {"rows_affected": 1.5}, {"last_insert_rowid": True},
                                  {"columns": [{"name": False, "decltype": None}]},
                                  {"columns": [{"name": "n", "decltype": False}]}])
async def test_sql_response_types(sdk: Any, change: dict[str, Any]) -> None:
    ts, _, responses = sdk
    responses.append(httpx.Response(200, json={"results": [{"columns": [], "rows": [], "rows_affected": 0,
                                                          "last_insert_rowid": None, **change}]}))
    with pytest.raises(CommitUncertainError):
        await resolve(ts.database("main").execute("INSERT INTO t VALUES (1)"))
