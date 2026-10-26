---
title: Python quickstart
description: Install the TerraScale Python SDK and create, get, query, read SQL and delete TerraBase documents, sync or async.
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# Python quickstart

Install the `terrascale` package (version `0.0.1`) with **Python 3.10 or newer**. The example uses the public API at `https://api.terrascale.tech` and an existing project database.

## 1. Install the SDK

```bash
mkdir terrabase-python-quickstart
cd terrabase-python-quickstart
python3 -m venv .venv && . .venv/bin/activate
pip install "terrascale @ git+https://github.com/TerraScale/ts-python@v0.0.1"
```

The package is not on PyPI yet, so install the `v0.0.1` tag from the public [ts-python](https://github.com/TerraScale/ts-python) repository. Its only dependency is [httpx](https://www.python-httpx.org/).

## 2. Supply your project and API key

Create a database and API key in the [dashboard](https://dash.terrascale.tech/) as described in [Getting started](/ja/getting-started/#create-an-account-a-database-and-an-api-key). This example creates the `py_quickstart` collection and runs SQL, so the key needs `documents:read`, `documents:write`, `databases:read` and `databases:write`: under **More access options** choose **Full access**. With the **Read and write** key from Getting started, SQL answers `403 forbidden_scope`.

In Bash, enter the key without saving its value in shell history:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY
export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ULID'
export TERRASCALE_DATABASE_NAME='YOUR_DATABASE_NAME'
```

Copy the project ID from **Organization → Current project**. It is a canonical ULID: 26 uppercase Crockford base32 characters. Database and collection names are names, not ULIDs.

`TerraScale()` reads `TERRASCALE_API_KEY` and `TERRASCALE_PROJECT_ID`. It exchanges the whole `<key ID>_<secret>` key at `https://auth.terrascale.tech/terrascale/oauth/token`, caches the short-lived access token and refreshes it before expiry; requests carry that token as `Authorization: Bearer`. Keep keys on your server and out of git. See [authentication](/ja/guides/authentication).

## 3. Create, get, query, run SQL and delete

Save this as `quickstart.py`:

```python
import os

from terrascale import ConflictError, TerraScale
from terrascale import query as q

with TerraScale() as ts:
    db = ts.database(os.environ["TERRASCALE_DATABASE_NAME"])
    try:
        db.create_collection("py_quickstart")
    except ConflictError:
        pass  # the collection already exists
    docs = db.collection("py_quickstart")

    created = docs.create({"title": "Hello from Python", "done": False})
    doc = docs.get(created.id)
    print("read:", doc.data)

    page = docs.query(q.eq("/done", False), sort=[q.asc("/title")], limit=10)
    print("query:", len(page.items), "item(s)")

    rows = db.sql("SELECT * FROM py_quickstart LIMIT 10")
    print("sql:", rows.matched, "row(s)")

    docs.delete(doc.id, if_match=doc.version)
    print("deleted", doc.id)
```

Run it from the same shell:

```bash
python quickstart.py
```

`create` lets the server generate a canonical ULID. For a caller-chosen ID, use `terrascale.new_id()` with `docs.put(id, body, if_none_match_any=True)`. UUIDs, prefixes and lowercase IDs are rejected.

The delete passes the version returned by `get`; versions are opaque and must be passed back unchanged. A stale version raises `terrascale.PreconditionFailedError`. The program deletes only its own document and leaves the collection and database intact.

`query` returns one page. Continue with `page.next_cursor` until it is `None`, keeping the filter, sort and projection unchanged; `docs.pages(...)` and `docs.items(...)` do that for you. SQL accepts the read-only `SELECT` subset with at most 1,000 rows; `db.execute(sql, args)` and `db.batch([...])` run SQL writes.

## 4. Async

`AsyncTerraScale` has the same methods, awaited; `pages` and `items` are async iterators:

```python
import asyncio
import os

from terrascale import AsyncTerraScale


async def main() -> None:
    async with AsyncTerraScale() as ts:
        docs = ts.database(os.environ["TERRASCALE_DATABASE_NAME"]).collection("py_quickstart")
        created = await docs.create({"title": "async"})
        async for doc in docs.items(limit=100):
            print(doc.id, doc.data)
        await docs.delete(created.id)


asyncio.run(main())
```

## More features

```python
from terrascale import query as q

docs.patch(doc_id, ops=[q.inc("/views", 1), q.append("/tags", "new")], if_match=version)

hits = docs.search("/embedding", [0.1, 0.2, 0.3], limit=5)  # vector search, nearest first
docs.set_ttl("/expiresAt")  # documents expire after this RFC 3339 UTC time (hourly sweep)

with db.transaction() as tx:  # atomic across collections, committed on exit
    tx.create("orders", {"total": 5})
    tx.patch("users", user_id, ops=[q.inc("/orders", 1)])

ts.create_database("eu", home_region="fra", replica_regions=["gru"])
```

Server errors are typed: `NotFoundError`, `ConflictError`, `PreconditionFailedError`, `PermissionDeniedError`, `RateLimitError` and others derive from `terrascale.APIError`, which carries `status`, `code` and `retry_after`. Reads are retried on `429` and transient `503`; writes without `if_match` are never replayed, and a network failure during one raises `CommitUncertainError`: read the document before retrying. The [SDK README](https://github.com/TerraScale/ts-python#readme) lists every method.

</div>
