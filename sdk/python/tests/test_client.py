from __future__ import annotations

import base64

import httpx
import pytest
from fake import API_KEY, BASE, ISSUER, PROJECT, Fake

import terrascale
from terrascale import (
    CommitUncertainError,
    ConflictError,
    RateLimitError,
    TerraScale,
    TokenError,
)


def test_token_exchange_and_bearer(ts: TerraScale, fake: Fake) -> None:
    ts.create_database("main")
    token_req, api_req = fake.requests
    assert str(token_req.url) == ISSUER + "/oauth/token"
    assert token_req.headers["content-type"] == "application/x-www-form-urlencoded"
    form = token_req.content.decode()
    assert "grant_type=client_credentials" in form and f"project={PROJECT}" in form
    basic = base64.b64decode(token_req.headers["authorization"].split()[1]).decode()
    assert basic == f"{API_KEY[:26]}:{API_KEY}"
    assert API_KEY not in form  # the secret travels only in Basic auth
    assert api_req.headers["authorization"] == "Bearer tok-1"
    assert api_req.headers["user-agent"] == f"terrascale-python/{terrascale.__version__}"


def test_token_is_cached_and_refreshed_on_401(ts: TerraScale, fake: Fake) -> None:
    ts.create_database("main")
    ts.database("main").info()
    assert fake.tokens_issued == 1
    fake.reject_tokens.add("tok-1")
    ts.database("main").info()
    assert fake.tokens_issued == 2


def test_bad_api_key_is_token_error(fake: Fake) -> None:
    other = "01JABCDEFGHJKMNPQRSTVWXYZ1_" + "b" * 52
    http = httpx.Client(transport=fake.transport())
    client = TerraScale(other, project=PROJECT, base_url=BASE, issuer=ISSUER, http_client=http)
    with pytest.raises(TokenError) as e:
        client.list_databases()
    assert e.value.status == 401 and e.value.code == "invalid_client"
    assert other not in str(e.value)
    with pytest.raises(TokenError) as again:
        client.list_databases()
    assert again.value.code == "invalid_client"
    assert len(fake.requests) == 1


def test_config_validation(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("TERRASCALE_API_KEY", raising=False)
    monkeypatch.delenv("TERRASCALE_PROJECT_ID", raising=False)
    with pytest.raises(ValueError, match="project"):
        TerraScale(API_KEY)
    with pytest.raises(ValueError, match="API key"):
        TerraScale(project=PROJECT)
    with pytest.raises(ValueError, match="api_key") as e:
        TerraScale("not-a-key", project=PROJECT)
    assert "not-a-key" not in str(e.value)
    with pytest.raises(ValueError, match="https"):
        TerraScale(API_KEY, project=PROJECT, base_url="http://example.com")
    monkeypatch.setenv("TERRASCALE_API_KEY", API_KEY)
    monkeypatch.setenv("TERRASCALE_PROJECT_ID", PROJECT)
    assert TerraScale().project == PROJECT


def test_retries_reads_on_429_but_not_writes(ts: TerraScale, fake: Fake, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("terrascale.client.time.sleep", lambda s: None)
    ts.create_database("main")
    fake.fail_next.append((429, {"error": {"code": "rate_limited"}}, {"Retry-After": "1"}))
    assert ts.database("main").info().name == "main"
    fake.fail_next.append((429, {"error": {"code": "rate_limited"}}, {"Retry-After": "2"}))
    with pytest.raises(RateLimitError) as e:
        ts.create_database("other")
    assert e.value.retry_after == 2.0


def test_mutation_transport_error_is_commit_uncertain(fake: Fake) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.host == "auth.test":
            return fake.handle(request)
        raise httpx.ReadError("boom", request=request)

    http = httpx.Client(transport=httpx.MockTransport(handler))
    client = TerraScale(API_KEY, project=PROJECT, base_url=BASE, issuer=ISSUER, http_client=http)
    with pytest.raises(CommitUncertainError) as caught:
        client.database("main").collection("c").create({"a": 1})
    assert caught.value.request_id is None


def test_error_mapping() -> None:
    err = terrascale.errors.error_for(409, {"error": {"code": "unique_violation", "message": "dup", "op": "2"}})
    assert isinstance(err, terrascale.UniqueViolationError) and isinstance(err, ConflictError)
    assert err.op == 2 and str(err) == "409 unique_violation: dup (op 2)"
    err = terrascale.errors.error_for(502, b"<html>")
    assert type(err) is terrascale.APIError and err.code == "http_502"


