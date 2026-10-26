"""Storage capabilities never inherit project authentication or diagnostics."""
import base64
import hashlib
import inspect
import ssl
from dataclasses import replace

import httpx
import pytest
from test_changes import Chunks

from terrascale import file_transfer
from terrascale.file_transfer import AsyncFileTransfer, FileTransfer, FileTransferError
from terrascale.files import FileSignedUrl

DATA = b"hello"
HASH = base64.b64encode(hashlib.sha256(DATA).digest()).decode()
CAP = FileSignedUrl("01ARZ3NDEKTSV4RRFFQ69G5FAD", "https://files.example/object?sig=fixture%2Fprivate%2Btoken",
                    "2099-01-01T00:00:00Z", {})


@pytest.fixture(params=[False, True])
def transfer(request, monkeypatch):
    expected, seen, responses, clients = [], [], [], []
    def respond(r):
        method, status, headers, chunks = expected.pop(0)
        assert r.method == method and str(r.url) == CAP.url
        assert not any(h in r.headers for h in ("authorization", "proxy-authorization", "cookie", "referer"))
        assert r.headers["accept-encoding"] == "identity"
        assert r.content == (DATA if method == "PUT" else b"")
        seen.append(r)
        if isinstance(chunks, Exception):
            raise chunks
        response = httpx.Response(status, headers=headers, stream=Chunks(chunks))
        responses.append(response)
        return response
    ctor = httpx.AsyncClient if request.param else httpx.Client
    def isolated(**kwargs):
        assert kwargs["trust_env"] is False and kwargs["follow_redirects"] is False
        assert kwargs["verify"].minimum_version == ssl.TLSVersion.TLSv1_3
        assert kwargs["timeout"].connect == 10.0 and kwargs["timeout"].read == 30.0
        assert set(kwargs) == {"verify", "trust_env", "follow_redirects", "timeout"}
        client = ctor(transport=httpx.MockTransport(respond), **kwargs)
        clients.append(client)
        return client
    monkeypatch.setattr(file_transfer.httpx, "AsyncClient" if request.param else "Client", isolated)
    async def call(method, cap=CAP, *args):
        result = getattr(AsyncFileTransfer(cap) if request.param else FileTransfer(cap), method)(*args)
        return await result if inspect.isawaitable(result) else result
    yield expected, seen, call
    assert not expected and all(r.is_closed for r in responses) and all(c.is_closed for c in clients)


async def test_exact_upload_and_raw_download(transfer):
    expected, seen, call = transfer
    headers = {"Content-Length": "5", "Content-Type": "text/plain", "x-amz-checksum-sha256": HASH}
    expected.append(("PUT", 201, {}, [AssertionError("upload response must not be read")]))
    assert await call("put_bytes", replace(CAP, headers=headers), DATA) is None
    assert seen[0].headers["content-length"] == "5" and seen[0].headers["content-type"] == "text/plain"
    expected.append(("GET", 200, {**headers, "content-encoding": "gzip"}, [DATA[:2], DATA[2:]]))
    assert await call("get_bytes", replace(CAP, headers={"x-storage-token": "fixture-private"})) == DATA
    assert seen[1].headers["x-storage-token"] == "fixture-private"


@pytest.mark.parametrize("changes", [{"url": "http://files.example/object"},
    {"url": "https://user:fixture-private@files.example/object"}, {"url": "https://files.example/object#private"},
    {"headers": {"Authorization": "fixture-private"}}, {"headers": {"Cookie": "fixture-private"}},
    {"headers": {"Host": "elsewhere.example"}}, {"headers": {"Transfer-Encoding": "chunked"}},
    {"headers": {"X-Storage": "fixture-private\r\ninjected: value"}},
    {"headers": {"Content-Type": "text/plain", "content-type": "application/json"}},
    {"headers": {"Content-Length": "4"}}, {"headers": {"x-amz-checksum-sha256": "wrong"}}])
async def test_invalid_upload_grant_never_dispatches(transfer, changes):
    _, seen, call = transfer
    with pytest.raises(ValueError) as error:
        await call("put_bytes", replace(CAP, **changes), DATA)
    assert str(error.value) == "invalid storage capability or upload bytes" and error.value.__context__ is None
    assert not seen


@pytest.mark.parametrize("method,status", [("PUT", 302), ("PUT", 403), ("PUT", 500), ("GET", 302), ("GET", 206), ("GET", 500)])
async def test_refusals_do_not_follow_retry_or_echo(transfer, method, status):
    expected, seen, call = transfer
    expected.append((method, status, {"Location": "https://other.example/?sig=fixture-private"},
                     [AssertionError("refusal body must not be read")]))
    with pytest.raises(FileTransferError) as error:
        await call("put_bytes", CAP, DATA) if method == "PUT" else await call("get_bytes")
    assert error.value.status == status and error.value.may_have_applied == (method == "PUT" and status >= 500)
    assert "fixture" not in str(error.value) + repr(error.value) and len(seen) == 1


@pytest.mark.parametrize("headers,chunks", [({"content-length": "4"}, [DATA]), ({"content-length": "6"}, [DATA]),
    ({"content-length": "104857601"}, []), ({"x-amz-checksum-sha256": HASH}, [b"wrong"]),
    ({"x-amz-checksum-sha256": "invalid"}, []), ({}, [b"x" * 1048576] * 101)])
async def test_download_length_checksum_and_hard_cap(transfer, headers, chunks):
    expected, seen, call = transfer
    expected.append(("GET", 200, headers, chunks))
    with pytest.raises(FileTransferError) as error:
        await call("get_bytes")
    assert not error.value.may_have_applied and len(seen) == 1


async def test_transport_and_bad_data_are_safe(transfer):
    expected, seen, call = transfer
    for data in (None, b"", "fixture-private", b"x" * 104857601):
        with pytest.raises(ValueError):
            await call("put_bytes", CAP, data)
    assert not seen
    for method in ("PUT", "GET"):
        expected.append((method, 200, {}, httpx.ReadError("https://files.example/?sig=fixture-private")))
        with pytest.raises(FileTransferError) as error:
            await call("put_bytes", CAP, DATA) if method == "PUT" else await call("get_bytes")
        assert error.value.status == 0 and error.value.may_have_applied == (method == "PUT")
        assert "fixture" not in str(error.value) and error.value.__context__ is None
    assert inspect.signature(FileTransfer.put_bytes) == inspect.signature(AsyncFileTransfer.put_bytes)
    assert inspect.signature(FileTransfer.get_bytes) == inspect.signature(AsyncFileTransfer.get_bytes)


async def test_signed_download_hash_and_interrupted_body(transfer):
    expected, seen, call = transfer
    expected.append(("GET", 200, {}, [DATA]))
    assert await call("get_bytes", replace(CAP, headers={"x-amz-checksum-sha256": HASH})) == DATA
    expected.append(("GET", 200, {}, [DATA, httpx.ReadError("fixture-private")]))
    with pytest.raises(FileTransferError) as error:
        await call("get_bytes")
    assert error.value.__context__ is None and "fixture" not in str(error.value) and len(seen) == 2
