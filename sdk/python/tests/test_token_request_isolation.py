"""A shared caller HTTP client must not change the credential exchange."""

from __future__ import annotations

import base64

import httpx
import pytest
from fake import API_KEY, ISSUER, PROJECT

from terrascale.auth import APIKeyTokenSource, AsyncAPIKeyTokenSource
from terrascale.errors import TokenError


@pytest.mark.parametrize("asynchronous", [False, True])
@pytest.mark.parametrize("redirect", [False, True])
async def test_exchange_ignores_shared_client_defaults(asynchronous: bool, redirect: bool) -> None:
    requests: list[httpx.Request] = []

    def exchange(request: httpx.Request) -> httpx.Response:
        requests.append(request)
        if redirect:
            return httpx.Response(302, headers={"Location": "https://other.test/credential-sink"})
        return httpx.Response(200, json={"access_token": "fixture-token", "token_type": "Bearer", "expires_in": 3600})

    options = {
        "transport": httpx.MockTransport(exchange),
        "auth": httpx.BasicAuth("unrelated-user", "unrelated-password"),
        "headers": {"X-Unrelated-Credential": "fixture-secret"},
        "cookies": {"session": "fixture-cookie"},
        "params": {"unrelated": "fixture-query"},
        "follow_redirects": True,
    }
    if asynchronous:
        async with httpx.AsyncClient(**options) as client:  # type: ignore[arg-type]
            async_source = AsyncAPIKeyTokenSource(API_KEY, project=PROJECT, issuer=ISSUER, http_client=client)
            if redirect:
                with pytest.raises(TokenError):
                    await async_source.token()
            else:
                assert await async_source.token() == "fixture-token"
    else:
        with httpx.Client(**options) as sync_client:  # type: ignore[arg-type]
            source = APIKeyTokenSource(API_KEY, project=PROJECT, issuer=ISSUER, http_client=sync_client)
            if redirect:
                with pytest.raises(TokenError):
                    source.token()
            else:
                assert source.token() == "fixture-token"
    assert len(requests) == 1
    request = requests[0]
    assert str(request.url) == ISSUER + "/oauth/token"
    expected = base64.b64encode(f"{API_KEY[:26]}:{API_KEY}".encode()).decode()
    assert request.headers["Authorization"] == "Basic " + expected
    assert "cookie" not in request.headers
    assert "x-unrelated-credential" not in request.headers
    assert request.content == f"grant_type=client_credentials&project={PROJECT}".encode()
