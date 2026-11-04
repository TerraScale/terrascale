"""Streaming export fixtures validate framing, closure and explicit resumption."""
import inspect
import json
from types import SimpleNamespace

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import AsyncTerraScale, CommitUncertainError, ConflictError, TerraScale
from terrascale.exports import AsyncDatabaseExports, DatabaseExports, ExportRun, IncompleteExportError

ID = "01ARZ3NDEKTSV4RRFFQ69G5FAD"
LINE = json.dumps({"collection": "people", "id": ID, "document": {"name": "Ada"}}).encode() + b"\n"
RUN = {"startedAt": "2026-10-08T03:00:00Z", "finishedAt": "2026-10-08T03:00:04Z", "status": "succeeded",
       "key": "exports/fixture.ndjson", "documents": "9007199254740993", "bytes": 184320, "error": None}
SCHEDULE = {"cron": "0 3 * * *", "updatedAt": "2026-10-07T12:00:00Z", "lastRun": RUN}


class Chunks(httpx.SyncByteStream, httpx.AsyncByteStream):
    def __init__(self, chunks):
        self.chunks = chunks
    def __iter__(self):
        for chunk in self.chunks:
            if isinstance(chunk, Exception):
                raise chunk
            yield chunk
    async def __aiter__(self):
        for chunk in self:
            yield chunk


@pytest.fixture(params=[False, True])
async def exports(request):
    expected, responses = [], []
    def respond(r):
        method, suffix, params, body, status, result = expected.pop(0)
        assert r.method == method and r.url.path == f"/v1/projects/{PROJECT}/databases/main" + suffix
        assert _route_ok(r.method, r.url.path) and dict(r.url.params) == params
        assert (json.loads(r.content) if r.content else None) == body
        if isinstance(result, Exception):
            raise result
        response = httpx.Response(status, stream=Chunks(result), headers={"content-type": "application/x-ndjson"}) if (
            suffix == "/export" and status == 200) else httpx.Response(status, json=result)
        responses.append(response)
        return response
    transport = httpx.MockTransport(respond)
    http = httpx.AsyncClient(transport=transport) if request.param else httpx.Client(transport=transport)
    client = (AsyncTerraScale if request.param else TerraScale)(token="fixture-only", project=PROJECT, http_client=http)
    handle = (AsyncDatabaseExports if request.param else DatabaseExports)(client.database("main"))
    async def call(method, *args, **kwargs):
        value = getattr(handle, method)(*args, **kwargs)
        if method == "iter_export":
            return [x async for x in value] if request.param else list(value)
        return await value if inspect.isawaitable(value) else value
    yield expected, call, handle, request.param
    assert expected == [] and all(r.is_closed for r in responses)
    if request.param:
        await http.aclose()
    else:
        http.close()


async def test_export_stream_and_schedule_contract(exports):
    expected, call, _, _ = exports
    expected.append(("GET", "/export", {}, None, 200, [LINE[:17], LINE[17:]]))
    records = await call("iter_export")
    assert records[0].collection == "people" and records[0].id == ID and records[0].document == {"name": "Ada"}
    expected.append(("GET", "/export", {"after_collection": "people", "after_id": ID}, None, 200, []))
    assert await call("iter_export", after_collection="people", after_id=ID) == []
    for method, verb, args, body, result in [("get_export_schedule", "GET", (), None, SCHEDULE),
        ("set_export_schedule", "PUT", ("0 3 * * *",), {"cron": "0 3 * * *"}, SCHEDULE),
        ("set_export_schedule", "PUT", (None,), {"cron": None}, {"cron": None, "updatedAt": None, "lastRun": None})]:
        expected.append((verb, "/export-schedule", {}, body, 200, result))
        schedule = await call(method, *args)
        assert schedule.cron == result["cron"] and schedule.updated_at == result["updatedAt"]
        assert schedule.last_run == (ExportRun.from_json(RUN) if result["lastRun"] else None)
    assert ExportRun.from_json(RUN).documents == 9007199254740993


@pytest.mark.parametrize("tail", [b"{", b"x" * 4194305, httpx.RemoteProtocolError("fixture: final chunk missing")])
async def test_incomplete_stream_retains_last_emitted_resume_key(exports, tail):
    expected, _, handle, asynchronous = exports
    expected.append(("GET", "/export", {}, None, 200, [LINE, tail]))
    iterator = handle.iter_export()
    try:
        first = await anext(iterator) if asynchronous else next(iterator)
        assert first.id == ID
        with pytest.raises(IncompleteExportError) as error:
            await anext(iterator) if asynchronous else next(iterator)
        assert error.value.after_collection == "people" and error.value.after_id == ID
    finally:
        await iterator.aclose() if asynchronous else iterator.close()


async def test_export_validation_and_schedule_errors(exports):
    expected, call, _, _ = exports
    with pytest.raises(ValueError):
        await call("iter_export", after_id=ID)
    for status, result, error in [(409, {"error": {"code": "backup_destination_required"}}, ConflictError),
                                 (200, {}, CommitUncertainError), (201, SCHEDULE, CommitUncertainError),
                                 (200, httpx.ReadError("fixture: schedule response lost"), CommitUncertainError)]:
        expected.append(("PUT", "/export-schedule", {}, {"cron": "0 3 * * *"}, status, result))
        with pytest.raises(error):
            await call("set_export_schedule", "0 3 * * *")


def test_export_signatures_match():
    for method in ("iter_export", "get_export_schedule", "set_export_schedule"):
        sync, aio = inspect.signature(getattr(DatabaseExports, method)), inspect.signature(getattr(AsyncDatabaseExports, method))
        assert sync.parameters == aio.parameters
        if method != "iter_export":
            assert sync.return_annotation == aio.return_annotation


async def test_export_refreshes_first_party_token_once(exports):
    expected, call, handle, asynchronous = exports
    invalidated = []
    def token():
        return "fixture-fresh" if invalidated else "fixture-stale"
    async def async_token():
        return token()
    handle._client._source = SimpleNamespace(token=async_token if asynchronous else token, invalidate=invalidated.append)
    expected.extend([("GET", "/export", {}, None, 401, {"error": {"code": "invalid_key"}}),
                     ("GET", "/export", {}, None, 200, [])])
    assert await call("iter_export") == [] and invalidated == ["fixture-stale"]
    handle._client._source = None
    expected.append(("GET", "/export", {}, None, 409, {"error": {"code": "fixture_conflict"}}))
    with pytest.raises(ConflictError) as error:
        await call("iter_export")
    assert error.value.code == "fixture_conflict"


async def test_caller_can_close_a_partial_export(exports):
    expected, _, handle, asynchronous = exports
    expected.append(("GET", "/export", {}, None, 200, [LINE, LINE]))
    iterator = handle.iter_export()
    assert (await anext(iterator) if asynchronous else next(iterator)).id == ID
    await iterator.aclose() if asynchronous else iterator.close()
