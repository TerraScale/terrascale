"""Signed capability metadata is returned without storage network requests."""
import base64
import inspect
import json

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import APIError, AsyncTerraScale, CommitUncertainError, TerraScale
from terrascale.files import AsyncBucketFiles, BucketFiles, FileObject, FileSignedUrl, FileUpload
from terrascale.storage import AsyncStorage, Storage

BUCKET, FILE = "01ARZ3NDEKTSV4RRFFQ69G5FAC", "01ARZ3NDEKTSV4RRFFQ69G5FAD"
HASH = base64.b64encode(bytes(32)).decode()
OBJECT = {"id": FILE, "name": "hello.txt", "contentType": "text/plain", "size": "5", "sha256": HASH,
          "metadata": {"userId": "fixture-subject"}, "status": "ready", "uploadExpiresAt": "2026-10-24T12:05:00Z"}
SIGNED = {"id": FILE, "url": "https://files.example/object?fixture-capability=private", "expiresAt": "2026-10-24T12:05:00Z",
          "headers": {"Content-Type": "text/plain", "Content-Length": "5", "x-amz-checksum-sha256": HASH,
                      "x-fixture-capability": "private-header"}}
UPLOAD = FileUpload("hello.txt", "text/plain", 5, HASH, OBJECT["metadata"])


@pytest.fixture(params=[False, True])
async def files(request):
    expected = []
    def respond(r):
        method, suffix, params, body, status, result = expected.pop(0)
        assert r.method == method and r.url.path == f"/v1/projects/{PROJECT}/buckets/{BUCKET}/files" + suffix
        assert r.url.host == "api.terrascale.tech" and _route_ok(r.method, r.url.path)
        assert dict(r.url.params) == params and (json.loads(r.content) if r.content else None) == body
        if isinstance(result, Exception):
            raise result
        return httpx.Response(status, content=result) if isinstance(result, bytes) else httpx.Response(status, json=result)
    transport = httpx.MockTransport(respond)
    http = httpx.AsyncClient(transport=transport) if request.param else httpx.Client(transport=transport)
    client = (AsyncTerraScale if request.param else TerraScale)(token="fixture-only", project=PROJECT, http_client=http)
    handle = AsyncBucketFiles(AsyncStorage(client), BUCKET) if request.param else BucketFiles(Storage(client), BUCKET)
    async def call(method, *args, **kwargs):
        result = getattr(handle, method)(*args, **kwargs)
        return await result if inspect.isawaitable(result) else result
    yield expected, call
    assert expected == []
    if request.param:
        await http.aclose()
    else:
        http.close()


async def test_file_lifecycle(files):
    expected, call = files
    expected.append(("POST", "/upload", {}, {"name": "hello.txt", "contentType": "text/plain", "size": 5,
                    "sha256": HASH, "metadata": OBJECT["metadata"]}, 201, SIGNED))
    capability = await call("create_file_upload", UPLOAD)
    assert capability.url == SIGNED["url"] and capability.headers == SIGNED["headers"] and capability.id == FILE
    assert capability.expires_at == SIGNED["expiresAt"] and "private" not in repr(capability)
    expected.append(("POST", f"/{FILE}/complete", {}, None, 200, OBJECT))
    obj = await call("complete_file_upload", FILE)
    assert obj.id == FILE and obj.size == 5 and obj.metadata == OBJECT["metadata"] and obj.delete_after is None
    assert obj.name == "hello.txt" and obj.content_type == "text/plain" and obj.sha256 == HASH and obj.status == "ready"
    expected.append(("GET", "", {}, None, 200, {"items": [OBJECT], "next": FILE}))
    page = await call("list_bucket_files")
    assert page.items == [obj] and page.next_cursor == FILE
    expected.append(("GET", "", {"after": FILE}, None, 200, {"items": [], "next": None}))
    assert (await call("list_bucket_files", after=page.next_cursor)).next_cursor is None
    expected.append(("POST", f"/{FILE}/download", {}, None, 200, SIGNED | {"headers": {}}))
    assert (await call("create_file_download", FILE)).headers == {}
    expected.append(("DELETE", f"/{FILE}", {}, None, 204, b""))
    assert await call("delete_file", FILE) is None


def test_upload_metadata_defaults_to_required_object():
    assert FileUpload("proof.txt", "text/plain", 1, HASH).body()["metadata"] == {}
    with pytest.raises(ValueError):
        FileUpload("proof.txt", "text/plain", 1, HASH, None).body()


async def test_capability_refusals_redact_server_diagnostics(files):
    expected, call = files
    for status in (400, 500):
        expected.append(("POST", f"/{FILE}/download", {}, None, status,
                         {"error": {"code": "private-capability", "message": SIGNED["url"],
                                    "details": [{"path": SIGNED["headers"]["x-fixture-capability"]}]}}))
        with pytest.raises(APIError) as caught:
            await call("create_file_download", FILE)
        error = caught.value
        assert error.status == status and error.code == f"http_{status}"
        assert isinstance(error, CommitUncertainError) == (status == 500)
        assert "private" not in str(error) and "private" not in repr(vars(error))


async def test_file_validation_and_uncertainty(files):
    expected, call = files
    for method, args, kwargs in [("delete_file", ("invalid",), {}), ("list_bucket_files", (), {"after": "invalid"}),
                                 ("create_file_upload", (FileUpload("x", "text/plain", True, HASH),), {}),
                                 ("create_file_upload", (FileUpload("x", "text/plain", 104857601, HASH),), {}),
                                 ("create_file_upload", (FileUpload("x", "text/plain", 1, "00" * 32),), {})]:
        with pytest.raises(ValueError):
            await call(method, *args, **kwargs)
    for method, verb, suffix, args, status in [("complete_file_upload", "POST", f"/{FILE}/complete", (FILE,), 200),
                                             ("create_file_download", "POST", f"/{FILE}/download", (FILE,), 200),
                                             ("delete_file", "DELETE", f"/{FILE}", (FILE,), 204)]:
        expected.append((verb, suffix, {}, None, status, httpx.ReadError("fixture: response lost")))
        with pytest.raises(CommitUncertainError):
            await call(method, *args)
    expected.append(("POST", f"/{FILE}/complete", {}, None, 200, OBJECT | {"id": BUCKET}))
    with pytest.raises(CommitUncertainError):
        await call("complete_file_upload", FILE)
    expected.append(("GET", "", {}, None, 200, {"items": [OBJECT | {"size": 1.5}], "next": None}))
    with pytest.raises(APIError):
        await call("list_bucket_files")
    for status, result in [(201, httpx.ReadError("fixture: reservation response lost")), (200, SIGNED),
                           (201, SIGNED | {"url": "http://files.example/fixture-capability"})]:
        expected.append(("POST", "/upload", {}, UPLOAD.body(), status, result))
        with pytest.raises(CommitUncertainError) as error:
            await call("create_file_upload", UPLOAD)
        assert "fixture-capability" not in str(error.value)


def test_file_exact_sizes_redaction_and_signatures():
    for size in [9007199254740993, "9007199254740993"]:
        assert FileObject.from_json(OBJECT | {"size": size}).size == 9007199254740993
    assert "private" not in repr(FileSignedUrl.from_json(SIGNED))
    for method in ("list_bucket_files", "create_file_upload", "complete_file_upload", "create_file_download", "delete_file"):
        assert inspect.signature(getattr(BucketFiles, method)) == inspect.signature(getattr(AsyncBucketFiles, method))


@pytest.mark.parametrize("size", [True, 1.5, "1e0", "01", -1, 9223372036854775808])
def test_invalid_metadata_sizes_are_not_coerced(size):
    with pytest.raises(ValueError):
        FileObject.from_json(OBJECT | {"size": size})


@pytest.mark.parametrize("changes", [{"url": "https://user:fixture@files.example/object"},
                                     {"url": "https://files.example/object#fragment"}, {"headers": {"Content-Length": 5}},
                                     {"id": "invalid"}])
def test_invalid_capabilities_are_rejected(changes):
    with pytest.raises(ValueError):
        FileSignedUrl.from_json(SIGNED | changes)
