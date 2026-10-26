from __future__ import annotations

import traceback
from collections.abc import Callable

import httpx
import pytest
from fake import API_KEY, BASE, ISSUER, PROJECT

from terrascale import APIError, AsyncTerraScale, ConflictError, TerraScale, TokenError

REQUEST_ID = "01JABCDEFGHJKMNPQRSTVWXYZ0"
OTHER_ID = "7ZZZZZZZZZZZZZZZZZZZZZZZZZ"


@pytest.fixture(params=[False, True], ids=["sync", "async"])
def asynchronous(request: pytest.FixtureRequest) -> bool:
    return bool(request.param)


async def _call(asynchronous: bool, handler: Callable[[httpx.Request], httpx.Response], *,
                exchange: bool = False, write: bool = False, max_attempts: int = 1) -> None:
    options = {"api_key": API_KEY} if exchange else {"token": "synthetic-bearer"}
    if asynchronous:
        async with httpx.AsyncClient(transport=httpx.MockTransport(handler)) as http:
            async with AsyncTerraScale(**options, project=PROJECT, base_url=BASE, issuer=ISSUER,
                                       max_attempts=max_attempts, http_client=http) as client:
                await client.create_database("example") if write else await client.list_databases()
    else:
        with httpx.Client(transport=httpx.MockTransport(handler)) as http:
            with TerraScale(**options, project=PROJECT, base_url=BASE, issuer=ISSUER,
                            max_attempts=max_attempts, http_client=http) as client:
                client.create_database("example") if write else client.list_databases()


@pytest.mark.parametrize("status,code", [
    (400, "invalid_request"), (401, "invalid_token"), (403, "forbidden_scope"),
    (404, "not_found"), (409, "unique_violation"), (412, "precondition_failed"),
    (429, "rate_limited"), (503, "storage_unavailable"), (500, "commit_uncertain"),
])
async def test_http_error_request_id(asynchronous: bool, status: int, code: str) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        assert "x-request-id" not in request.headers
        return httpx.Response(status, headers={"x-ReQuEsT-Id": REQUEST_ID, "Retry-After": "2", "ETag": '"v1"'},
                              json={"error": {"code": code, "message": "failed", "op": 2, "region": "gru"}})

    with pytest.raises(APIError) as caught:
        await _call(asynchronous, handler)
    error = caught.value
    assert error.request_id == REQUEST_ID
    assert (error.status, error.code, error.op, error.region, error.etag, error.retry_after) == (status, code, 2, "gru", "v1", 2)
    assert str(error) == f"{status} {code}: failed (op 2)"
    if code == "unique_violation":
        assert isinstance(error, ConflictError)


@pytest.mark.parametrize("values", [
    [], [""], [REQUEST_ID.lower()], ["8" + REQUEST_ID[1:]], ["I" + REQUEST_ID[1:]],
    [REQUEST_ID + "\n"], [" " + REQUEST_ID], [REQUEST_ID + " "], ["x" * 10000],
    [REQUEST_ID + ", " + OTHER_ID], [REQUEST_ID, REQUEST_ID], [REQUEST_ID, OTHER_ID],
])
async def test_rejects_missing_malformed_and_multiple_ids(asynchronous: bool, values: list[str]) -> None:
    def handler(_: httpx.Request) -> httpx.Response:
        return httpx.Response(502, headers=[("X-Request-ID", value) for value in values],
                              json={"request_id": REQUEST_ID, "error": {"request_id": REQUEST_ID}})

    with pytest.raises(APIError) as caught:
        await _call(asynchronous, handler)
    assert caught.value.request_id is None
    assert str(caught.value) == "502 http_502"


@pytest.mark.parametrize("status,body", [(502, ""), (502, "<html>synthetic-private-value</html>"),
                                         (502, "[]"), (502, "null"), (502, '{"error":{"op":1e999}}'), (200, "not-json"),
                                         (200, "[]"), (200, '{"items":["synthetic-private-value"]}')])
async def test_unreadable_response_keeps_only_safe_context(asynchronous: bool, status: int, body: str) -> None:
    def handler(_: httpx.Request) -> httpx.Response:
        return httpx.Response(status, headers={"X-Request-ID": REQUEST_ID}, content=body)

    with pytest.raises(APIError) as caught:
        await _call(asynchronous, handler)
    error = caught.value
    assert (error.status, error.request_id) == (status, REQUEST_ID)
    assert error.code == ("invalid_response" if status == 200 else "http_502")
    diagnostic = "".join(traceback.format_exception(type(error), error, error.__traceback__))
    assert "synthetic-private-value" not in diagnostic
    assert "synthetic-bearer" not in diagnostic


async def test_malformed_write_success_is_not_replayed(asynchronous: bool) -> None:
    requests = []

    def handler(request: httpx.Request) -> httpx.Response:
        requests.append(request)
        return httpx.Response(201, content="not-json", headers={"X-Request-ID": REQUEST_ID})

    with pytest.raises(APIError) as caught:
        await _call(asynchronous, handler, write=True, max_attempts=3)
    assert len(requests) == 1
    assert (caught.value.code, caught.value.request_id) == ("invalid_response", REQUEST_ID)


@pytest.mark.parametrize("last_id", [OTHER_ID, "invalid"])
async def test_retry_reports_final_response_id(asynchronous: bool, last_id: str) -> None:
    requests = []

    def handler(request: httpx.Request) -> httpx.Response:
        requests.append(request)
        return httpx.Response(429, json={"error": {"code": "rate_limited"}},
                              headers={"X-Request-ID": REQUEST_ID if len(requests) == 1 else last_id, "Retry-After": "0"})

    with pytest.raises(APIError) as caught:
        await _call(asynchronous, handler, max_attempts=2)
    assert len(requests) == 2
    assert caught.value.request_id == (OTHER_ID if last_id == OTHER_ID else None)


@pytest.mark.parametrize("status,body,code", [
    (401, '{"error":"invalid_client","error_description":"unknown key"}', "invalid_client"),
    (502, "<html>synthetic-private-value</html>", ""),
    (200, '{"access_token":"synthetic-private-value"}', "invalid_token_response"),
    (200, "not-json", "invalid_token_response"),
])
async def test_token_endpoint_request_id(asynchronous: bool, status: int, body: str, code: str) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        assert request.url.path.endswith("/oauth/token")
        return httpx.Response(status, content=body, headers={"X-Request-ID": REQUEST_ID})

    with pytest.raises(TokenError) as caught:
        await _call(asynchronous, handler, exchange=True)
    assert (caught.value.code, caught.value.request_id) == (code, REQUEST_ID)
    assert "synthetic-private-value" not in str(caught.value)
    assert API_KEY not in str(caught.value)


def test_existing_error_construction() -> None:
    assert APIError(502, "http_502").request_id is None
    assert TokenError(401, "invalid_client", "unknown", 2).request_id is None
    assert APIError(502, "http_502", request_id="invalid").request_id is None
