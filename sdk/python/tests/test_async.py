from __future__ import annotations

import pytest
from fake import Fake

from terrascale import AsyncTerraScale, NotFoundError, new_id
from terrascale import query as q


async def test_async_end_to_end(ats: AsyncTerraScale, fake: Fake) -> None:
    db_info = await ats.create_database("main", all_regions=True)
    assert db_info.all_regions
    db = ats.database("main")
    await db.create_collection("todos")
    todos = db.collection("todos")

    created = await todos.create({"title": "Async", "n": 1})
    doc = await todos.get(created.id)
    assert doc.data["title"] == "Async"
    await todos.patch(created.id, ops=[q.inc("/n", 1)], if_match=doc.version)
    assert (await todos.get(created.id)).data["n"] == 2

    for i in range(4):
        await todos.create({"title": f"t{i}", "n": 10 + i})
    pages = [p async for p in todos.pages(q.gte("/n", 10), limit=3)]
    assert [len(p.items) for p in pages] == [3, 1]
    assert len([d async for d in todos.items(limit=2)]) == 5
    groups = (await todos.query(q.gte("/n", 10), aggregate=q.aggregate(sum=["/n"]))).groups
    assert groups is not None and groups[0].count == 4 and groups[0].sum == {"/n": 46.0}

    await todos.set_ttl("/expiresAt")
    assert await todos.get_ttl() == "/expiresAt"

    rows = await db.sql("SELECT * FROM todos")
    assert rows.matched == 5
    assert (await db.execute("SELECT ?", [1])).rows == [[1]]

    async with db.transaction() as tx:
        tx.put("todos", new_id(), {"title": "tx"})
    assert tx.result is not None

    hits = await todos.search("/v", [1.0], limit=3)
    assert len(hits) == 3

    await todos.delete(created.id)
    with pytest.raises(NotFoundError):
        await todos.get(created.id)
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
