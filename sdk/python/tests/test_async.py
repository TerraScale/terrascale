from __future__ import annotations

import pytest
from fake import Fake

from terrascale import AsyncTerraScale, NotFoundError


async def test_async_end_to_end(ats: AsyncTerraScale, fake: Fake) -> None:
    created = await ats.create_database("main", home_region="gru")
    assert created.home_region == "gru"
    db = ats.database("main")
    assert (await db.info()).id == created.id
    assert (await db.query("SELECT ?", [1]))[0]["n"] == 1
    assert len(await db.batch(["SELECT 1", ("SELECT ?", [1, 2])])) == 2
    branch = await db.branch("copy")
    assert branch.branched_from == created.id
    await db.delete()
    with pytest.raises(NotFoundError):
        await db.info()
    assert fake.tokens_issued == 1


async def test_async_token_callable(fake: Fake) -> None:
    import httpx
    from fake import BASE, PROJECT

    async def provider() -> str:
        return "static-token"

    http = httpx.AsyncClient(transport=fake.transport())
    async with AsyncTerraScale(project=PROJECT, token=provider, base_url=BASE, http_client=http) as client:
        await client.list_databases()
    assert fake.requests[-1].headers["authorization"] == "Bearer static-token"
