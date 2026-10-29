"""Import contracts over HTTP fakes, including raw parts and uncertain writes."""
import inspect
import json
from hashlib import sha256

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import APIError, AsyncTerraScale, CommitUncertainError, TerraScale
from terrascale.imports import AsyncDatabaseImports, DatabaseImports, ImportOptions, ImportRequest

ID = "01ARZ3NDEKTSV4RRFFQ69G5FAA"
PART = b'{"name":"Ada"}\n'
REQUEST = ImportRequest("ndjson", len(PART), sha256(PART).hexdigest(), ImportOptions(collection="people"))
JOB = {"id": ID, "project": PROJECT, "database": "main", "region": "gru-1", "state": "ready", "cancelRequested": False,
       "options": {"exclude": []}, "source": {"format": "ndjson", "bytes": str(len(PART)), "sha256": sha256(PART).hexdigest(),
       "receivedBytes": str(len(PART)), "staged": True, "retainedUntil": None},
       "preview": {"id": ID, "tables": [], "skipped": [], "skippedTruncated": False, "blocking": "0", "warnings": 0},
       "confirmedPreviewId": None, "progress": {"tablesDone": 0, "tablesTotal": 1, "rowsWritten": "0",
       "rowsTotal": "9007199254740993", "currentTable": None}, "validation": None, "error": None,
       "createdAt": "2026-10-12T12:00:00Z", "updatedAt": "2026-10-12T12:00:05Z", "startedAt": None, "finishedAt": None}


@pytest.fixture(params=[False, True])
async def imports(request):
    expected = []
    def respond(r):
        method, suffix, body, params, status, result = expected.pop(0)
        assert r.method == method and r.url.path == f"/v1/projects/{PROJECT}/databases/main/import" + suffix
        assert _route_ok(r.method, r.url.path) and dict(r.url.params) == params
        assert (r.content if isinstance(body, bytes) else json.loads(r.content) if r.content else None) == body
        if isinstance(body, bytes):
            assert r.headers["content-type"] == "application/octet-stream" and int(r.headers["content-length"]) == len(body)
        if isinstance(result, Exception):
            raise result
        return httpx.Response(status, json=result, headers={"x-request-id": ID})
    transport = httpx.MockTransport(respond)
    http = httpx.AsyncClient(transport=transport) if request.param else httpx.Client(transport=transport)
    client = (AsyncTerraScale if request.param else TerraScale)(token="fixture-only", project=PROJECT, http_client=http)
    handle = (AsyncDatabaseImports if request.param else DatabaseImports)(client.database("main"))
    async def call(method, *args):
        result = getattr(handle, method)(*args)
        return await result if inspect.isawaitable(result) else result
    yield expected, call
    assert expected == []
    if request.param:
        await http.aclose()
    else:
        http.close()


async def test_import_lifecycle(imports):
    expected, call = imports
    for method, args, verb, suffix, body, params, status in [
        ("create_import", (REQUEST,), "POST", "", REQUEST.body(), {}, 201),
        ("create_import", (REQUEST,), "POST", "", REQUEST.body(), {}, 200),
        ("get_import", (), "GET", "", None, {}, 200),
        ("update_import", (ImportOptions(["public.audit"]),), "PATCH", "",
         {"options": {"exclude": ["public.audit"], "collection": None}}, {}, 202),
        ("upload_import_source", (0, PART), "PUT", "/source", PART, {"offset": "0"}, 200),
        ("upload_import_source", (9223372036854775807, b"\x00\xff"), "PUT", "/source", b"\x00\xff",
         {"offset": "9223372036854775807"}, 200),
        ("start_import", (ID,), "POST", "/start", {"previewId": ID}, {}, 202),
        ("cancel_import", (), "POST", "/cancel", None, {}, 202), ("resume_import", (), "POST", "/resume", None, {}, 202),
    ]:
        expected.append((verb, suffix, body, params, status, JOB))
        assert await call(method, *args) == JOB


async def test_import_validation_and_uncertainty(imports):
    expected, call = imports
    for method, args in [("upload_import_source", (-1, PART)), ("upload_import_source", (True, PART)),
                         ("upload_import_source", (0, b"")), ("upload_import_source", (0, b"x" * 4194305)),
                         ("start_import", ("invalid",)), ("create_import", (ImportRequest("ndjson", 0, "x"),)),
                         ("create_import", (ImportRequest("ndjson", 1, "A" * 64),))]:
        with pytest.raises(ValueError):
            await call(method, *args)
    for result, status in [(httpx.ReadError("fixture: acknowledgement lost"), 202), ({}, 202), (JOB, 200),
                           ({"error": {"code": "internal_error"}}, 500)]:
        expected.append(("POST", "/cancel", None, {}, status, result))
        with pytest.raises(CommitUncertainError):
            await call("cancel_import")
    expected.append(("GET", "", None, {}, 200, {}))
    with pytest.raises(APIError) as error:
        await call("get_import")
    assert not isinstance(error.value, CommitUncertainError) and error.value.request_id == ID


def test_import_signatures_match():
    methods = {n for n in dir(DatabaseImports) if not n.startswith("_")}
    assert methods == {n for n in dir(AsyncDatabaseImports) if not n.startswith("_")}
    for method in methods:
        assert inspect.signature(getattr(DatabaseImports, method)) == inspect.signature(getattr(AsyncDatabaseImports, method))
