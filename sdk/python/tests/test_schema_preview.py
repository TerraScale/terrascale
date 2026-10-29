"""The 111th data route: copy-local, read-only schema preview over HTTP fakes."""

import inspect
import json
from contextlib import asynccontextmanager

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import APIError, AsyncTerraScale, CommitUncertainError, TerraScale
from terrascale.schema_preview import AsyncCollectionSchemaPreview, CollectionSchemaPreview

PATH = f"/v1/projects/{PROJECT}/databases/main/collections/people/schema/preview"
CURSOR = "opaque+/%=cursor"
DIAGNOSTIC = {"id_hash": "b" * 64, "path_hash": "c" * 64, "keyword": "type"}
PAGE = {"schema_hash": "a" * 64, "frontier": "d" * 64, "examined": "2", "valid": 1, "invalid": "1",
        "complete": False, "next_cursor": CURSOR, "diagnostics": [DIAGNOSTIC]}


@asynccontextmanager
async def preview_fixture(asynchronous, respond, schema):
    transport = httpx.MockTransport(respond)
    http = httpx.AsyncClient(transport=transport) if asynchronous else httpx.Client(transport=transport)
    client = (AsyncTerraScale if asynchronous else TerraScale)(token="fixture-only", project=PROJECT, http_client=http)
    try:
        preview = (AsyncCollectionSchemaPreview if asynchronous else CollectionSchemaPreview)(client.database("main"),
                                                                                              "people", schema)
        async def page(cursor=None):
            result = preview.page(cursor=cursor)
            return await result if inspect.isawaitable(result) else result
        yield page
    finally:
        if asynchronous:
            await http.aclose()
        else:
            http.close()


@pytest.mark.parametrize("asynchronous", [False, True])
async def test_preview_contract_and_frozen_bytes(asynchronous):
    requests = []
    schema = {"type": "object", "properties": {"z": {"type": "string"}, "a": {"type": "number"}}}
    expected = json.dumps(schema, sort_keys=True, separators=(",", ":")).encode()
    def respond(r):
        assert r.method == "POST" and r.url.path == PATH and _route_ok(r.method, r.url.path)
        assert r.content == expected and r.headers["content-type"] == "application/json"
        assert r.headers["authorization"] == "Bearer fixture-only"
        assert dict(r.url.params) == ({"cursor": CURSOR} if requests else {})
        requests.append(r)
        body = PAGE if len(requests) == 1 else PAGE | {"complete": True, "next_cursor": None, "diagnostics": []}
        return httpx.Response(200, json=body)
    async with preview_fixture(asynchronous, respond, schema) as page:
        schema["properties"]["z"]["type"] = "boolean"
        first = await page()
        assert first.examined == 2 and first.valid == 1 and first.invalid == 1 and not first.complete
        assert first.schema_hash == "a" * 64 and first.frontier == "d" * 64
        assert first.diagnostics[0].id_hash == "b" * 64 and first.diagnostics[0].path_hash == "c" * 64
        assert first.diagnostics[0].keyword == "type"
        last = await page(first.next_cursor)
        assert last.complete and last.next_cursor is None and last.diagnostics == []
    assert len(requests) == 2


@pytest.mark.parametrize("asynchronous", [False, True])
async def test_preview_post_is_replay_safe(asynchronous, monkeypatch):
    requests = []
    monkeypatch.setattr("terrascale._spec.backoff", lambda *_: 0)
    def respond(r):
        requests.append(r)
        if len(requests) == 1:
            raise httpx.ReadError("fixture-only", request=r)
        assert r.content == b"false"
        return httpx.Response(200, json=PAGE)
    async with preview_fixture(asynchronous, respond, False) as page:
        await page()
    assert len(requests) == 2 and requests[0].content == requests[1].content


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("status,code", [(400, "invalid_cursor"), (409, "preview_stale"), (500, "internal_error")])
async def test_preview_errors_are_observational(asynchronous, status, code):
    requests = []
    def respond(r):
        requests.append(r)
        return httpx.Response(status, json={"error": {"code": code, "message": "fixture-only"}})
    async with preview_fixture(asynchronous, respond, {}) as page:
        with pytest.raises(APIError) as error:
            await page(CURSOR)
        assert error.value.status == status and error.value.code == code
        assert not isinstance(error.value, CommitUncertainError)
    assert len(requests) == 1


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("body,status", [(PAGE | {"complete": "false"}, 200), (PAGE | {"examined": True}, 200),
                                       (PAGE | {"invalid": -1}, 200), (PAGE | {"diagnostics": {}}, 200),
                                       (PAGE | {"diagnostics": [DIAGNOSTIC] * 17}, 200), (PAGE, 201)])
async def test_malformed_preview(asynchronous, body, status):
    async with preview_fixture(asynchronous, lambda _: httpx.Response(status, json=body), {}) as page:
        with pytest.raises(APIError) as error:
            await page()
        assert error.value.code == "invalid_response" and not isinstance(error.value, CommitUncertainError)


@pytest.mark.parametrize("asynchronous", [False, True])
async def test_preview_local_validation(asynchronous):
    requests = []
    def respond(r):
        requests.append(r)
        return httpx.Response(200, json=PAGE)
    async with preview_fixture(asynchronous, respond, {}) as page:
        with pytest.raises(ValueError):
            await page("")
    with pytest.raises(ValueError):
        async with preview_fixture(asynchronous, respond, {"description": "x" * 65536}):
            pass
    assert requests == []


def test_preview_signatures_match():
    assert inspect.signature(CollectionSchemaPreview.page) == inspect.signature(AsyncCollectionSchemaPreview.page)
