from __future__ import annotations

from collections.abc import AsyncIterator, Iterator

import httpx
import pytest
from fake import API_KEY, BASE, ISSUER, PROJECT, Fake

from terrascale import AsyncTerraScale, TerraScale


@pytest.fixture
def fake() -> Fake:
    return Fake()


@pytest.fixture
def ts(fake: Fake) -> Iterator[TerraScale]:
    http = httpx.Client(transport=fake.transport())
    with TerraScale(API_KEY, project=PROJECT, base_url=BASE, issuer=ISSUER, http_client=http) as client:
        yield client


@pytest.fixture
async def ats(fake: Fake) -> AsyncIterator[AsyncTerraScale]:
    http = httpx.AsyncClient(transport=fake.transport())
    async with AsyncTerraScale(API_KEY, project=PROJECT, base_url=BASE, issuer=ISSUER, http_client=http) as client:
        yield client
