"""Production webhook contract fixtures, never a real receiver or signing key."""

import base64
import inspect
import json
import traceback

import httpx
import pytest
from fake import PROJECT
from test_contract import _route_ok

from terrascale import APIError, AsyncTerraScale, CommitUncertainError, TerraScale
from terrascale.webhooks import AsyncCollectionWebhooks, CollectionWebhooks

ID = "01ARZ3NDEKTSV4RRFFQ69G5FAA"
SECRET = base64.urlsafe_b64encode(b"x" * 32).decode().rstrip("=")
URL = "https://receiver.example/events?key=fixture-only"
WEBHOOK = {"id": ID, "url": URL, "createdAt": "2026-10-26T00:00:00Z", "status": "feed_gap"}
DELIVERY = {"id": ID, "createdAt": WEBHOOK["createdAt"], "status": "pending", "attempts": str(2**60 + 1),
            "lastAttemptAt": None, "nextAttemptAt": None, "httpStatus": None, "errorCode": None}
CASES = [("create_webhook", (URL,), {}, "POST", "", 201, WEBHOOK | {"secret": SECRET}),
         ("list_webhooks", (), {"limit": 1, "after": ID}, "GET", "", 200, [WEBHOOK]),
         ("delete_webhook", (ID,), {}, "DELETE", "/" + ID, 204, None),
         ("list_webhook_deliveries", (ID,), {"limit": 100, "after": ID}, "GET", "/" + ID + "/deliveries", 200, [DELIVERY])]


async def invoke(asynchronous, handler, method, *args, **kwargs):
    transport = httpx.MockTransport(handler)
    if asynchronous:
        async with httpx.AsyncClient(transport=transport) as http:
            client = AsyncTerraScale(token="fixture-only", project=PROJECT, http_client=http)
            return await getattr(AsyncCollectionWebhooks(client.database("main").collection("people")), method)(*args, **kwargs)
    with httpx.Client(transport=transport) as http:
        client = TerraScale(token="fixture-only", project=PROJECT, http_client=http)
        return getattr(CollectionWebhooks(client.database("main").collection("people")), method)(*args, **kwargs)


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("method,args,kwargs,verb,suffix,status,response", CASES)
async def test_webhook_contract(asynchronous, method, args, kwargs, verb, suffix, status, response):
    def respond(r):
        assert r.method == verb and _route_ok(r.method, r.url.path)
        assert r.url.path == f"/v1/projects/{PROJECT}/databases/main/collections/people/webhooks" + suffix
        assert dict(r.url.params) == {k: str(v) for k, v in kwargs.items()}
        assert r.headers["authorization"] == "Bearer fixture-only"
        assert (json.loads(r.content) if r.content else None) == ({"url": URL} if verb == "POST" else None)
        return httpx.Response(204) if status == 204 else httpx.Response(status, json=response)
    result = await invoke(asynchronous, respond, method, *args, **kwargs)
    assert SECRET not in repr(result) and URL not in repr(result)
    if method == "create_webhook":
        assert result.secret == SECRET and result.url == URL and result.status == "feed_gap"
    elif method == "list_webhooks":
        assert result[0].id == ID and not hasattr(result[0], "secret")
    elif method == "list_webhook_deliveries":
        assert result[0].attempts == 2**60 + 1 and result[0].http_status is None and result[0].error_code is None


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("method,args", [("create_webhook", (URL,)), ("delete_webhook", (ID,))])
@pytest.mark.parametrize("failure", ["transport", "server", "malformed"])
async def test_uncertain_and_redacted(asynchronous, method, args, failure):
    requests = []
    def respond(r):
        requests.append(r)
        if failure == "transport":
            raise httpx.ReadError(SECRET + URL, request=r)
        status = 500 if failure == "server" else 204 if method.startswith("delete") else 201
        return httpx.Response(status, json={"error": {"code": SECRET, "message": URL}}, headers={"X-Request-ID": PROJECT})
    with pytest.raises(CommitUncertainError) as error:
        await invoke(asynchronous, respond, method, *args)
    diagnostics = repr(vars(error.value)) + "".join(traceback.format_exception(error.value))
    assert len(requests) == 1 and SECRET not in diagnostics and URL not in diagnostics


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("body", [{"items": []}, None, [DELIVERY | {"attempts": True}], [DELIVERY | {"httpStatus": 200.1}],
                                 [DELIVERY | {"attempts": 2**63}], [DELIVERY | {"lastAttemptAt": 0}]])
async def test_delivery_array_and_number_validation(asynchronous, body):
    with pytest.raises(APIError) as error:
        await invoke(asynchronous, lambda _: httpx.Response(200, content=json.dumps(body)), "list_webhook_deliveries", ID)
    assert error.value.code == "invalid_response"


@pytest.mark.parametrize("asynchronous", [False, True])
async def test_reads_retry_and_decode_populated_delivery(asynchronous, monkeypatch):
    monkeypatch.setattr("terrascale._spec.backoff", lambda *_: 0)
    requests = []
    def respond(r):
        requests.append(r)
        if len(requests) == 1:
            raise httpx.ReadError("fixture-only", request=r)
        assert not r.url.query
        return httpx.Response(200, json=[])
    assert await invoke(asynchronous, respond, "list_webhooks") == [] and len(requests) == 2
    populated = DELIVERY | {"httpStatus": "503", "errorCode": "http_error", "lastAttemptAt": WEBHOOK["createdAt"]}
    result = await invoke(asynchronous, lambda _: httpx.Response(200, json=[populated]), "list_webhook_deliveries", ID)
    assert result[0].http_status == 503 and result[0].error_code == "http_error"
    assert result[0].last_attempt_at == WEBHOOK["createdAt"]


@pytest.mark.parametrize("method,args,kwargs", [("list_webhooks", (), {"limit": True}), ("list_webhooks", (), {"limit": 101}),
                                             ("list_webhooks", (), {"after": "invalid"}),
                                             ("create_webhook", ("http://receiver.example/events",), {}),
                                             ("create_webhook", ("https://user:password@receiver.example",), {}),
                                             ("delete_webhook", ("invalid",), {})])
async def test_invalid_input_never_sends(method, args, kwargs):
    with pytest.raises(ValueError):
        await invoke(False, lambda _: pytest.fail("unexpected HTTP request"), method, *args, **kwargs)


def test_webhook_twins():
    methods = {x for x in dir(CollectionWebhooks) if not x.startswith("_")}
    assert methods == {x for x in dir(AsyncCollectionWebhooks) if not x.startswith("_")}
    for method in methods:
        assert inspect.signature(getattr(CollectionWebhooks, method)) == inspect.signature(
            getattr(AsyncCollectionWebhooks, method))


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("body", [WEBHOOK | {"secret": SECRET, "status": True}, WEBHOOK | {"secret": "bad-fixture-key"},
                                 WEBHOOK | {"secret": SECRET + "="}])
async def test_invalid_creation_ack_redacts_key(asynchronous, body):
    with pytest.raises(CommitUncertainError) as error:
        await invoke(asynchronous, lambda _: httpx.Response(201, json=body), "create_webhook", URL)
    assert SECRET not in "".join(traceback.format_exception(error.value))


@pytest.mark.parametrize("asynchronous", [False, True])
async def test_definite_rejection_preserves_only_safe_diagnostics(asynchronous):
    body = {"error": {"code": "invalid_request", "message": SECRET, "details": [{"value": SECRET}]}}
    with pytest.raises(APIError) as error:
        await invoke(asynchronous, lambda _: httpx.Response(400, json=body, headers={"X-Request-ID": PROJECT}),
                     "create_webhook", URL)
    assert not isinstance(error.value, CommitUncertainError) and error.value.code == "invalid_request"
    assert error.value.request_id == PROJECT and error.value.details == []
    assert SECRET not in repr(vars(error.value)) + "".join(traceback.format_exception(error.value))
