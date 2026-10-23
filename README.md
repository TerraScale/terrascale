# TerraScale Python SDK

The Python client for [TerraScale](https://terrascale.tech) TerraBase: documents,
queries, vector search, TTL, SQL, transactions and database regions. Sync and
asyncio, built on [httpx](https://www.python-httpx.org/). Python 3.10+.

```sh
pip install terrascale
# until the first PyPI release:
pip install "git+https://github.com/TerraScale/ts-python@v0.0.1"
```

## Quickstart

Create a database and an API key in the [dashboard](https://dash.terrascale.tech/)
(**Full access** if you use SQL or create collections), then:

```sh
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
```

```python
from terrascale import TerraScale, query as q

with TerraScale() as ts:                       # reads TERRASCALE_API_KEY / TERRASCALE_PROJECT_ID
    todos = ts.database("main").collection("todos")

    res = todos.create({"title": "Ship the Python SDK", "done": False})
    doc = todos.get(res.id)
    todos.patch(doc.id, merge={"done": True}, if_match=doc.version)

    for d in todos.items(q.eq("/done", True), sort=[q.desc("/title")]):
        print(d.id, d.data)
```

The API key is exchanged with ts-auth (`https://auth.terrascale.tech`) for a
short-lived project access token, cached and refreshed automatically. Keep keys
on your server and out of git. See [examples/quickstart.py](examples/quickstart.py).

### Async

```python
from terrascale import AsyncTerraScale

async with AsyncTerraScale() as ts:
    todos = ts.database("main").collection("todos")
    res = await todos.create({"title": "async"})
    async for d in todos.items(limit=100):
        ...
```

`AsyncTerraScale` has the same methods as `TerraScale`, awaited; `pages()` and
`items()` are async iterators.

## API

| Surface | Methods |
| --- | --- |
| Client | `TerraScale(api_key=, project=, token=, base_url=)`, `database(name)`, `list_databases()`, `create_database(name, home_region=, replica_regions=, all_regions=)` |
| Database | `info()`, `update(home_region=, replica_regions=, all_regions=)`, `delete()`, `branch(name)`, `create_collection(name)`, `list_collections()`, `collection(name)` |
| Documents | `get`, `create`, `put`, `patch(merge=\|ops=)`, `delete`, `list` |
| Queries | `query(filter, sort=, project=, limit=, cursor=, count=)`, `pages(...)`, `items(...)` |
| Vector | `search(path, vector, limit=, metric="cosine"\|"l2")` or `sort=[q.near(...)]` |
| TTL | `get_ttl()`, `set_ttl("/expiresAt")`, `delete_ttl()` |
| SQL | `sql(select)`, `check_sql`, `explain_sql`, `execute(sql, args)`, `batch([...])` |
| Transactions | `with db.transaction() as tx: tx.create/put/patch/delete/assert_/assert_absent/assert_count` |

**IDs** are canonical ULIDs (`terrascale.new_id()`); the document key is the ID.
**Versions** are opaque: pass `doc.version` back as `if_match`. Use
`if_none_match_any=True` to create only when absent.

**Filters** (`terrascale.query`): `and_`, `or_`, `not_`, `eq`, `ne`, `lt`, `lte`,
`gt`, `gte`, `in_`, `nin`, `exists`, `prefix`, `contains`; paths are JSON Pointers
(`"/title"`) or `q.ID_PATH`. Sort with `q.asc`/`q.desc`. Patch ops: `q.set_`,
`q.unset`, `q.append`, `q.inc`, `q.add`, `q.remove`.

**Regions**: `create_database("eu", home_region="fra", replica_regions=["gru"])`,
`db.update(all_regions=True)`. Remove replica regions before `delete()`.

**TTL** deletes documents whose member holds a past RFC 3339 UTC time, in an
hourly sweep; expired documents stay readable until then.

## Errors

Every server error is a `terrascale.APIError` with `status`, `code`, `message`,
`op` (failing transaction op), `retry_after` and `etag`. Common cases have
subclasses: `InvalidRequestError` (400), `AuthenticationError` (401),
`PermissionDeniedError` (403), `NotFoundError` (404), `ConflictError` /
`UniqueViolationError` (409), `PreconditionFailedError` (412), `RateLimitError`
(429), `ServiceUnavailableError` (503). A refused API key raises `TokenError`.

Reads, queries and `if_match` writes are retried on 429 and transient 503s,
honouring `Retry-After`. Other writes are never replayed: a transport failure on
a write raises `CommitUncertainError`; re-read before retrying.

## Development

```sh
uv venv && uv pip install -e '.[dev]'
pytest && ruff check src tests && mypy
```

Tests run against an in-memory HTTP fake (`tests/fake.py`) and check every
request against the published route list in `contracts/openapi-routes.json`
(`python scripts/refresh-contract.py` updates it). They never start a TerraBase
server. Releases: bump `src/terrascale/_version.py`, tag `vX.Y.Z` and publish a
GitHub release for it; the publish workflow uploads to PyPI with trusted publishing.

License: Apache-2.0.
