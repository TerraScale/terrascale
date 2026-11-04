from __future__ import annotations

import httpx
import pytest
from fake import API_KEY, BASE, ISSUER, PROJECT, Fake

from terrascale import APIError, AsyncTerraScale, KeyExpiredError, KeyRevokedError, TerraScale


@pytest.mark.parametrize("code,kind", [("key_revoked", KeyRevokedError), ("key_expired", KeyExpiredError)])
def test_permanent_key_error_drops_cache_without_refresh(code: str, kind: type[APIError]) -> None:
    fake = Fake()
    fake.fail_next = [(401, {"error": {"code": code, "message": "Replace the key."}},
                       {"www-authenticate": 'Bearer error="invalid_token"'})]
    with TerraScale(API_KEY, project=PROJECT, base_url=BASE, issuer=ISSUER,
                    http_client=httpx.Client(transport=fake.transport())) as client:
        for _ in range(2):
            with pytest.raises(kind, match="Replace the key.") as caught:
                client.list_databases()
            assert caught.value.code == code
        assert fake.tokens_issued == 1
        assert len(fake.requests) == 2  # One exchange, one refused data request.
        assert client._source is not None and client._source._x.token is None


@pytest.mark.parametrize("code,kind", [("key_revoked", KeyRevokedError), ("key_expired", KeyExpiredError)])
async def test_async_permanent_key_error_drops_cache_without_refresh(code: str, kind: type[APIError]) -> None:
    fake = Fake()
    fake.fail_next = [(401, {"error": {"code": code, "message": "Replace the key."}}, {})]
    async with AsyncTerraScale(API_KEY, project=PROJECT, base_url=BASE, issuer=ISSUER,
                              http_client=httpx.AsyncClient(transport=fake.transport())) as client:
        for _ in range(2):
            with pytest.raises(kind, match="Replace the key.") as caught:
                await client.list_databases()
            assert caught.value.code == code
        assert fake.tokens_issued == 1
        assert len(fake.requests) == 2
        assert client._source is not None and client._source._x.token is None


def test_auth_unavailable_retries_with_cached_token(ts: TerraScale, fake: Fake) -> None:
    fake.fail_next = [(503, {"error": {"code": "auth_unavailable"}}, {"retry-after": "0"})]
    assert ts.list_databases().items == []
    assert fake.tokens_issued == 1 and len(fake.requests) == 3


async def test_async_auth_unavailable_retries_with_cached_token(ats: AsyncTerraScale, fake: Fake) -> None:
    fake.fail_next = [(503, {"error": {"code": "auth_unavailable"}}, {"retry-after": "0"})]
    assert (await ats.list_databases()).items == []
    assert fake.tokens_issued == 1 and len(fake.requests) == 3
